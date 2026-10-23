package backup

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
	"github.com/mihaim21/aethera-forge/agent/internal/secrets"
)

const (
	// failureTail is how much of a failed program's stderr goes into the error.
	failureTail = 2048
	// volumeMount is where helper containers see the volume.
	volumeMount = docker.HelperVolumeTarget
)

// IO is where an operation reports to: the command's log, progress and the
// masker of the secrets the command carries.
type IO struct {
	Out, Err io.Writer
	Progress func(step, message string, percent int32)
	Masker   *secrets.Masker
}

func (o IO) out() io.Writer {
	if o.Out == nil {
		return io.Discard
	}
	return o.Out
}

func (o IO) errw() io.Writer {
	if o.Err == nil {
		return io.Discard
	}
	return o.Err
}

func (o IO) progress(step, msg string) {
	if o.Progress != nil {
		o.Progress(step, msg, -1)
	}
}

// Service runs backups, restores and deletions.
type Service struct {
	Docker docker.API
	Policy *policy.Policy
	// TempDir holds downloads of objects being restored (default
	// <backup dir>/.tmp, on the same disk as the state).
	TempDir string
	// Now and ProgressEvery are test hooks.
	Now           func() time.Time
	ProgressEvery time.Duration
	// OpenStore overrides OpenStore (tests).
	OpenStore func(ctx context.Context, loc *agentv1.BackupLocation) (Store, error)
}

func (s *Service) now() time.Time {
	if s.Now != nil {
		return s.Now()
	}
	return time.Now()
}

func (s *Service) exec() *Executor { return &Executor{Docker: s.Docker, Policy: s.Policy} }

func (s *Service) store(ctx context.Context, loc *agentv1.BackupLocation) (Store, error) {
	if s.OpenStore != nil {
		return s.OpenStore(ctx, loc)
	}
	return OpenStore(ctx, s.Policy, loc)
}

func (s *Service) tempDir() string {
	if s.TempDir != "" {
		return s.TempDir
	}
	return filepath.Join(s.Policy.BackupsDir(), ".tmp")
}

// redact removes secret values from an error before it leaves the package.
func redact(m *secrets.Masker, err error) error {
	if err == nil || m.Empty() {
		return err
	}
	var e *Error
	if errors.As(err, &e) {
		cp := *e
		cp.Msg = m.String(e.Msg)
		return &cp
	}
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	if v, ok := policy.AsViolation(err); ok {
		return &policy.Violation{Code: v.Code, Msg: m.String(v.Msg)}
	}
	// Wrapped errors keep their kind (docker sentinels) but lose secret text.
	return &redactedError{msg: m.String(err.Error()), err: err}
}

type redactedError struct {
	msg string
	err error
}

func (r *redactedError) Error() string { return r.msg }
func (r *redactedError) Unwrap() error { return r.err }

func parseEncryption(enc *agentv1.BackupEncryption) ([]byte, string, error) {
	key, err := ParseKey(enc.GetKey().GetValue())
	if err != nil {
		return nil, "", Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "%v", err)
	}
	if enc.GetKeyId() == "" {
		return nil, "", Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "the backup key id is required")
	}
	return key, enc.GetKeyId(), nil
}

// ticker reports progress until stop is called.
func (s *Service) report(io IO, step string, bytes ...*atomic.Int64) (stop func()) {
	every := s.ProgressEvery
	if every <= 0 {
		every = 2 * time.Second
	}
	done := make(chan struct{})
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		t := time.NewTicker(every)
		defer t.Stop()
		for {
			select {
			case <-t.C:
				parts := make([]string, len(bytes))
				for i, b := range bytes {
					parts[i] = humanBytes(b.Load())
				}
				io.progress(step, strings.Join(parts, " / "))
			case <-done:
				return
			}
		}
	}()
	var once sync.Once
	return func() { once.Do(func() { close(done) }); <-finished }
}

func humanBytes(n int64) string {
	switch {
	case n >= 1<<30:
		return fmt.Sprintf("%.1f GiB", float64(n)/(1<<30))
	case n >= 1<<20:
		return fmt.Sprintf("%.1f MiB", float64(n)/(1<<20))
	case n >= 1<<10:
		return fmt.Sprintf("%.1f KiB", float64(n)/(1<<10))
	}
	return fmt.Sprintf("%d B", n)
}

// ---------------------------------------------------------------------------
// Backup

// Run produces the source stream, encrypts it (AEB1) and uploads it, all
// streaming. On any failure or cancellation nothing is left in the target.
func (s *Service) Run(ctx context.Context, req *agentv1.BackupRun, o IO) (res *agentv1.BackupRunResult, err error) {
	defer func() { err = redact(o.Masker, err) }()
	key, keyID, err := parseEncryption(req.GetEncryption())
	if err != nil {
		return nil, err
	}
	store, err := s.store(ctx, req.GetTarget())
	if err != nil {
		return nil, err
	}
	start := s.now()
	fmt.Fprintf(o.out(), "backup %s: writing to %s\n", req.GetBackupId(), store.Describe())

	var raw, stored atomic.Int64
	sum := sha256.New()
	stop := s.report(o, "backup", &raw, &stored)
	defer stop()

	err = store.Put(ctx, func(w io.Writer) error {
		hw := &hashWriter{w: w, h: sum, n: &stored}
		enc, err := NewWriter(hw, key, keyID)
		if err != nil {
			return Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "%v", err)
		}
		if err := s.produce(ctx, req.GetSource(), &countWriter{w: enc, n: &raw}, o); err != nil {
			return err
		}
		return enc.Close()
	})
	if err != nil {
		return nil, err
	}
	stop()
	fmt.Fprintf(o.out(), "backup %s: %s read, %s stored\n", req.GetBackupId(), humanBytes(raw.Load()), humanBytes(stored.Load()))
	return &agentv1.BackupRunResult{
		BackupId: req.GetBackupId(), RawSizeBytes: raw.Load(), StoredSizeBytes: stored.Load(),
		StoredSha256: hex.EncodeToString(sum.Sum(nil)), Duration: durationpb.New(s.now().Sub(start)),
	}, nil
}

// produce writes the source stream into dst.
func (s *Service) produce(ctx context.Context, src *agentv1.BackupSource, dst io.Writer, o IO) error {
	switch k := src.GetKind().(type) {
	case *agentv1.BackupSource_Exec:
		x := k.Exec
		tail := NewTail(failureTail)
		code, err := s.exec().Run(ctx, ExecRequest{
			Container: x.GetContainer(), Argv: x.GetArgv(), Env: x.GetEnv(), User: x.GetUser(),
			Stdout: dst, Stderr: io.MultiWriter(o.errw(), tail),
		})
		if err != nil {
			return err
		}
		if code != 0 {
			return &Error{
				Code: agentv1.ErrorCode_ERROR_CODE_INTERNAL, ExitCode: int32(code),
				Msg: fmt.Sprintf("the dump program exited with code %d: %s", code, oneLine(tail.String(o.Masker, failureTail))),
			}
		}
		return nil
	case *agentv1.BackupSource_Volume:
		v := k.Volume
		if err := s.checkVolume(ctx, v.GetVolume()); err != nil {
			return err
		}
		tail := NewTail(failureTail)
		code, err := s.Docker.RunHelper(ctx, docker.HelperSpec{
			Image: helperImage(v.GetHelperImage()), Cmd: []string{"tar", "czf", "-", "-C", volumeMount, "."},
			Volume: v.GetVolume(), ReadOnly: true, Stdout: dst, Stderr: io.MultiWriter(o.errw(), tail),
		})
		if err != nil {
			return err
		}
		if code != 0 {
			return &Error{
				Code: agentv1.ErrorCode_ERROR_CODE_INTERNAL, ExitCode: int32(code),
				Msg: fmt.Sprintf("archiving volume %q failed with code %d: %s", v.GetVolume(), code, oneLine(tail.String(o.Masker, failureTail))),
			}
		}
		return nil
	}
	return Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "a backup source is required")
}

func helperImage(img string) string {
	if img == "" {
		return policy.DefaultHelperImage
	}
	return img
}

func oneLine(s string) string {
	s = strings.TrimSpace(s)
	if s == "" {
		return "(no output)"
	}
	return strings.Join(strings.Fields(strings.ReplaceAll(s, "\n", " | ")), " ")
}

// checkVolume refuses volumes an agent command must not touch: the
// installation's own, bind mounts and volumes that do not exist.
func (s *Service) checkVolume(ctx context.Context, name string) error {
	if err := policy.CheckBackupVolume(name); err != nil {
		return err
	}
	if _, err := s.Docker.VolumeInspect(ctx, name); err != nil {
		return err // NOT_FOUND for a volume that does not exist
	}
	opts, err := s.Docker.VolumeOptions(ctx, name)
	if err != nil {
		return err
	}
	if strings.Contains(opts["o"], "bind") || opts["type"] == "bind" {
		return &policy.Violation{Code: agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, Msg: fmt.Sprintf("volume %q is a bind mount of a host path and is not archived", name)}
	}
	return nil
}

// ---------------------------------------------------------------------------
// Restore

// Restore downloads the object next to the state (so a broken object never
// reaches the sink), checks its checksum and authenticity, then decrypts it
// into the sink.
func (s *Service) Restore(ctx context.Context, req *agentv1.BackupRestore, o IO) (res *agentv1.BackupRestoreResult, err error) {
	defer func() { err = redact(o.Masker, err) }()
	key, keyID, err := parseEncryption(req.GetEncryption())
	if err != nil {
		return nil, err
	}
	store, err := s.store(ctx, req.GetSource())
	if err != nil {
		return nil, err
	}
	if sink := req.GetSink(); sink == nil || sink.GetKind() == nil {
		return nil, Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "a restore sink is required")
	}
	if v := req.GetSink().GetVolume(); v != nil {
		if err := s.checkVolume(ctx, v.GetVolume()); err != nil {
			return nil, err
		}
	}
	start := s.now()
	fmt.Fprintf(o.out(), "restore %s: reading %s\n", req.GetBackupId(), store.Describe())

	path, sum, cleanup, err := s.fetch(ctx, store, o)
	if err != nil {
		return nil, err
	}
	defer cleanup()
	if want := strings.ToLower(req.GetExpectedSha256()); want != "" && want != sum {
		return nil, Errorf(agentv1.ErrorCode_ERROR_CODE_CHECKSUM_MISMATCH, "the stored object does not match the recorded checksum (stored %s..., expected %s...)", short(sum), short(want))
	}

	// Prove the whole object intact before the sink sees a byte.
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	hdr, err := NewReader(f, key)
	if err == nil && hdr.KeyID() != keyID {
		f.Close()
		return nil, Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "the backup was encrypted with key %q, not %q", hdr.KeyID(), keyID)
	}
	f.Close()
	if err != nil {
		return nil, authErr(err)
	}
	if err := s.verify(ctx, path, key); err != nil {
		return nil, err
	}

	f, err = os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	dr, err := NewReader(f, key)
	if err != nil {
		return nil, authErr(err)
	}
	var restored atomic.Int64
	stop := s.report(o, "restore", &restored)
	defer stop()
	in := io.Reader(&countReader{r: ctxReader{ctx: ctx, r: dr}, n: &restored})
	if err := s.consume(ctx, req.GetSink(), in, o); err != nil {
		return nil, err
	}
	stop()
	fmt.Fprintf(o.out(), "restore %s: %s restored\n", req.GetBackupId(), humanBytes(restored.Load()))
	return &agentv1.BackupRestoreResult{RestoredBytes: restored.Load(), Duration: durationpb.New(s.now().Sub(start))}, nil
}

func short(h string) string {
	if len(h) > 12 {
		return h[:12]
	}
	return h
}

func authErr(err error) error {
	if errors.Is(err, ErrAuth) || errors.Is(err, ErrFormat) {
		return Errorf(agentv1.ErrorCode_ERROR_CODE_CHECKSUM_MISMATCH, "%v", err)
	}
	return err
}

func (s *Service) verify(ctx context.Context, path string, key []byte) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	if _, err := Verify(ctxReader{ctx: ctx, r: f}, key); err != nil {
		return authErr(err)
	}
	return nil
}

// fetch makes the object available as a local file and returns its SHA-256.
// A local backup is read in place; a remote one is downloaded to a temporary
// file that cleanup removes.
func (s *Service) fetch(ctx context.Context, store Store, o IO) (path, sum string, cleanup func(), err error) {
	cleanup = func() {}
	rc, err := store.Open(ctx)
	if err != nil {
		return "", "", cleanup, err
	}
	defer rc.Close()
	h := sha256.New()
	var n atomic.Int64

	if f, ok := rc.(*os.File); ok { // a local backup: hash in place
		if _, err := io.Copy(h, &countReader{r: ctxReader{ctx: ctx, r: f}, n: &n}); err != nil {
			return "", "", cleanup, err
		}
		return f.Name(), hex.EncodeToString(h.Sum(nil)), cleanup, nil
	}

	dir := s.tempDir()
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", "", cleanup, diskErr(err)
	}
	tmp, err := os.CreateTemp(dir, "restore-*.aeb")
	if err != nil {
		return "", "", cleanup, diskErr(err)
	}
	cleanup = func() { _ = os.Remove(tmp.Name()) }
	stop := s.report(o, "download", &n)
	_, cerr := io.Copy(io.MultiWriter(tmp, h), &countReader{r: ctxReader{ctx: ctx, r: rc}, n: &n})
	stop()
	if cerr == nil {
		cerr = tmp.Sync()
	}
	if closeErr := tmp.Close(); cerr == nil {
		cerr = closeErr
	}
	if cerr != nil {
		cleanup()
		if isDiskFull(cerr) {
			return "", "", func() {}, diskErr(cerr)
		}
		return "", "", func() {}, s3Err(cerr)
	}
	return tmp.Name(), hex.EncodeToString(h.Sum(nil)), cleanup, nil
}

// consume feeds the decrypted stream to the sink.
func (s *Service) consume(ctx context.Context, sink *agentv1.BackupSink, in io.Reader, o IO) error {
	switch k := sink.GetKind().(type) {
	case *agentv1.BackupSink_Exec:
		x := k.Exec
		tail := NewTail(failureTail)
		code, err := s.exec().Run(ctx, ExecRequest{
			Container: x.GetContainer(), Argv: x.GetArgv(), Env: x.GetEnv(), User: x.GetUser(),
			Stdin: in, Stdout: o.out(), Stderr: io.MultiWriter(o.errw(), tail),
		})
		if err != nil {
			return err
		}
		if code != 0 {
			return &Error{
				Code: agentv1.ErrorCode_ERROR_CODE_INTERNAL, ExitCode: int32(code),
				Msg: fmt.Sprintf("the restore program exited with code %d: %s", code, oneLine(tail.String(o.Masker, failureTail))),
			}
		}
		return nil
	case *agentv1.BackupSink_Volume:
		v := k.Volume
		script := "tar xzf - -C " + volumeMount
		if v.GetClearFirst() {
			// Everything, dotfiles included, but not the mount point itself.
			script = "find " + volumeMount + " -mindepth 1 -maxdepth 1 -exec rm -rf {} + && " + script
		}
		tail := NewTail(failureTail)
		code, err := s.Docker.RunHelper(ctx, docker.HelperSpec{
			Image: helperImage(v.GetHelperImage()), Cmd: []string{"sh", "-c", script},
			Volume: v.GetVolume(), ReadOnly: false, Stdin: in, Stdout: o.out(), Stderr: io.MultiWriter(o.errw(), tail),
		})
		if err != nil {
			return err
		}
		if code != 0 {
			return &Error{
				Code: agentv1.ErrorCode_ERROR_CODE_INTERNAL, ExitCode: int32(code),
				Msg: fmt.Sprintf("restoring volume %q failed with code %d: %s", v.GetVolume(), code, oneLine(tail.String(o.Masker, failureTail))),
			}
		}
		return nil
	}
	return Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "a restore sink is required")
}

// ---------------------------------------------------------------------------
// Delete

// Delete removes the stored objects; a missing object is not an error. Every
// location is attempted; the first failure is returned.
func (s *Service) Delete(ctx context.Context, req *agentv1.BackupDelete, o IO) (err error) {
	defer func() { err = redact(o.Masker, err) }()
	var first error
	for _, loc := range req.GetLocations() {
		st, err := s.store(ctx, loc)
		if err == nil {
			err = st.Delete(ctx)
		}
		if err != nil {
			if first == nil {
				first = err
			}
			continue
		}
		fmt.Fprintf(o.out(), "deleted %s\n", st.Describe())
	}
	return first
}
