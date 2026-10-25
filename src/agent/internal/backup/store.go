package backup

import (
	"context"
	"errors"
	"io"
	"net/url"
	"os"
	"path/filepath"
	"time"

	"github.com/minio/minio-go/v7"
	"github.com/minio/minio-go/v7/pkg/credentials"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
)

// PartSize is the multipart part size of S3 uploads (streams of unknown length).
const PartSize = 16 << 20

// Store is one stored backup object.
type Store interface {
	// Put streams what fill writes into the object. When fill fails (or ctx
	// ends) the upload is aborted and nothing is left behind.
	Put(ctx context.Context, fill func(w io.Writer) error) error
	// Open reads the object (NOT_FOUND when it does not exist).
	Open(ctx context.Context) (io.ReadCloser, error)
	// Delete removes the object; a missing object is not an error.
	Delete(ctx context.Context) error
	// Describe names the object for logs (never contains credentials).
	Describe() string
}

// ---------------------------------------------------------------------------
// Local files

type localStore struct {
	path string
	rel  string
}

// NewLocalStore addresses a file below the agent's backup directory.
func NewLocalStore(pol *policy.Policy, loc *agentv1.LocalLocation) (Store, error) {
	p, err := pol.ResolveLocalPath(loc.GetPath())
	if err != nil {
		return nil, err
	}
	return &localStore{path: p, rel: loc.GetPath()}, nil
}

func (l *localStore) Describe() string { return "local:" + l.rel }

func (l *localStore) Put(ctx context.Context, fill func(io.Writer) error) (err error) {
	dir := filepath.Dir(l.path)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return diskErr(err)
	}
	f, err := os.CreateTemp(dir, "."+filepath.Base(l.path)+".tmp-*")
	if err != nil {
		return diskErr(err)
	}
	tmp := f.Name()
	defer func() {
		if err != nil {
			_ = f.Close()
			_ = os.Remove(tmp)
		}
	}()
	if err = fill(f); err != nil {
		return diskErr(err)
	}
	if err = ctx.Err(); err != nil {
		return err
	}
	if err = f.Sync(); err != nil {
		return diskErr(err)
	}
	if err = f.Close(); err != nil {
		return diskErr(err)
	}
	if err = os.Rename(tmp, l.path); err != nil {
		return diskErr(err)
	}
	return nil
}

func (l *localStore) Open(ctx context.Context) (io.ReadCloser, error) {
	f, err := os.Open(l.path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, Errorf(agentv1.ErrorCode_ERROR_CODE_NOT_FOUND, "backup object %s does not exist", l.Describe())
		}
		return nil, err
	}
	return f, nil
}

func (l *localStore) Delete(ctx context.Context) error {
	if err := os.Remove(l.path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}

// diskErr turns "no space left" into OUT_OF_DISK and leaves other errors alone.
func diskErr(err error) error {
	if err != nil && isDiskFull(err) {
		return Errorf(agentv1.ErrorCode_ERROR_CODE_OUT_OF_DISK, "no space left on the agent's disk")
	}
	return err
}

// ---------------------------------------------------------------------------
// S3

type s3Store struct {
	client *minio.Client
	bucket string
	key    string
	desc   string
}

// NewS3Store connects to an S3 location. The endpoint rules (https, or http
// for loopback/private hosts) are the policy's; lookup resolves host names.
func NewS3Store(ctx context.Context, pol *policy.Policy, loc *agentv1.S3Location) (Store, error) {
	if err := pol.CheckS3(ctx, loc); err != nil {
		return nil, err
	}
	u, err := url.Parse(loc.GetEndpoint())
	if err != nil {
		return nil, Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "invalid S3 endpoint")
	}
	lookup := minio.BucketLookupAuto
	if loc.GetForcePathStyle() {
		lookup = minio.BucketLookupPath
	}
	cl, err := minio.New(u.Host, &minio.Options{
		Creds:        credentials.NewStaticV4(loc.GetAccessKeyId(), loc.GetSecretAccessKey().GetValue(), ""),
		Secure:       u.Scheme == "https",
		Region:       loc.GetRegion(),
		BucketLookup: lookup,
	})
	if err != nil {
		return nil, Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "cannot create the S3 client: %v", s3Err(err))
	}
	return &s3Store{client: cl, bucket: loc.GetBucket(), key: loc.GetKey(), desc: "s3://" + loc.GetBucket() + "/" + loc.GetKey()}, nil
}

func (s *s3Store) Describe() string { return s.desc }

// s3Err maps S3 failures to protocol errors. S3 error bodies carry the
// request, the bucket and the access key id, never the secret.
func s3Err(err error) error {
	if err == nil {
		return nil
	}
	var ce *Error
	if errors.As(err, &ce) || errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	resp := minio.ToErrorResponse(err)
	switch resp.Code {
	case "NoSuchKey", "NoSuchBucket", "NotFound":
		return Errorf(agentv1.ErrorCode_ERROR_CODE_NOT_FOUND, "S3: %s", resp.Message)
	case "AccessDenied", "InvalidAccessKeyId", "SignatureDoesNotMatch", "AllAccessDisabled", "InvalidSecurity":
		return Errorf(agentv1.ErrorCode_ERROR_CODE_PERMISSION_DENIED, "S3 refused the credentials or the operation (%s)", resp.Code)
	}
	if resp.Code != "" {
		return Errorf(agentv1.ErrorCode_ERROR_CODE_INTERNAL, "S3 error %s: %s", resp.Code, resp.Message)
	}
	return Errorf(agentv1.ErrorCode_ERROR_CODE_INTERNAL, "S3 request failed: %v", err)
}

// cancelGrace is how long an S3 upload may keep running after cancellation
// (to abort its multipart upload cleanly) before it is cut off.
const cancelGrace = 15 * time.Second

func (s *s3Store) Put(ctx context.Context, fill func(io.Writer) error) error {
	pr, pw := io.Pipe()
	// The upload runs on a context that survives cancellation: minio aborts
	// the multipart upload itself when its reader fails, which needs a live context.
	putCtx, putCancel := context.WithCancel(context.WithoutCancel(ctx))
	defer putCancel()
	stop := make(chan struct{})
	defer close(stop)
	go func() {
		select {
		case <-ctx.Done():
			_ = pr.CloseWithError(ctx.Err()) // fails the next write and the next read
			select {
			case <-time.After(cancelGrace):
				putCancel()
			case <-stop:
			}
		case <-stop:
		}
	}()

	res := make(chan error, 1)
	go func() {
		_, err := s.client.PutObject(putCtx, s.bucket, s.key, pr, -1, minio.PutObjectOptions{
			PartSize: PartSize, ContentType: "application/octet-stream",
		})
		// Unblock the producer if the upload stopped early; it sees the mapped error.
		_ = pr.CloseWithError(s3Err(err))
		res <- err
	}()

	ferr := fill(pw)
	if ferr == nil {
		ferr = ctx.Err()
	}
	if ferr != nil {
		_ = pw.CloseWithError(ferr)
	} else {
		_ = pw.Close()
	}
	perr := <-res
	if ferr != nil {
		// Whatever the cause, make sure no half-uploaded parts stay in the bucket.
		rctx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 30*time.Second)
		defer cancel()
		_ = s.client.RemoveIncompleteUpload(rctx, s.bucket, s.key)
		return ferr
	}
	return s3Err(perr)
}

func (s *s3Store) Open(ctx context.Context) (io.ReadCloser, error) {
	obj, err := s.client.GetObject(ctx, s.bucket, s.key, minio.GetObjectOptions{})
	if err != nil {
		return nil, s3Err(err)
	}
	if _, err := obj.Stat(); err != nil {
		_ = obj.Close()
		return nil, s3Err(err)
	}
	return obj, nil
}

func (s *s3Store) Delete(ctx context.Context) error {
	err := s.client.RemoveObject(ctx, s.bucket, s.key, minio.RemoveObjectOptions{})
	if err == nil {
		return nil
	}
	if minio.ToErrorResponse(err).Code == "NoSuchKey" {
		return nil
	}
	return s3Err(err)
}

// OpenStore selects the store of a location.
func OpenStore(ctx context.Context, pol *policy.Policy, loc *agentv1.BackupLocation) (Store, error) {
	switch k := loc.GetKind().(type) {
	case *agentv1.BackupLocation_S3:
		return NewS3Store(ctx, pol, k.S3)
	case *agentv1.BackupLocation_Local:
		return NewLocalStore(pol, k.Local)
	}
	return nil, Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "a backup location is required")
}
