package backup

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
	"github.com/mihaim21/aethera-forge/agent/internal/secrets"
)

const secretKeyB64 = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=" // 32 x 0x01, see aeb1-vector.json

func newService(t *testing.T) (*Service, *dockertest.Fake, *policy.Policy) {
	t.Helper()
	cfg := config.Default()
	cfg.StateDir = t.TempDir()
	cfg.Finalize()
	pol := policy.New(cfg)
	pol.SetSelfContainer("feedfeedfeed")
	f := dockertest.New()
	f.AddContainer(&agentv1.ContainerInfo{
		Id: "c-db", Name: "db", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING,
		Labels: map[string]string{policy.LabelManaged: "true", policy.LabelWorkload: "w1"},
	})
	return &Service{Docker: f, Policy: pol, ProgressEvery: 10 * time.Millisecond}, f, pol
}

func enc() *agentv1.BackupEncryption {
	return &agentv1.BackupEncryption{Key: &agentv1.SecretValue{Value: secretKeyB64}, KeyId: "k1"}
}

func localLoc(path string) *agentv1.BackupLocation {
	return &agentv1.BackupLocation{Kind: &agentv1.BackupLocation_Local{Local: &agentv1.LocalLocation{Path: path}}}
}

func execSource(argv ...string) *agentv1.BackupSource {
	return &agentv1.BackupSource{Kind: &agentv1.BackupSource_Exec{Exec: &agentv1.ExecDump{
		Container: "db", Argv: argv,
		Env: []*agentv1.EnvVar{{Name: "PGPASSWORD", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "hunter2-secret"}}}},
	}}}
}

func execSink(argv ...string) *agentv1.BackupSink {
	return &agentv1.BackupSink{Kind: &agentv1.BackupSink_Exec{Exec: &agentv1.ExecRestore{Container: "db", Argv: argv}}}
}

func dumpBytes(n int) []byte { return pattern(n) }

func sha(b []byte) string { h := sha256.Sum256(b); return hex.EncodeToString(h[:]) }

type pgIO struct{ out, errw bytes.Buffer }

func (p *pgIO) io(masker *secrets.Masker) IO {
	return IO{Out: &p.out, Err: &p.errw, Masker: masker}
}

func masked(req *agentv1.BackupRun) *secrets.Masker { return secrets.ForMessage(req) }

func TestRunExecDumpToLocalFile(t *testing.T) {
	svc, f, pol := newService(t)
	data := dumpBytes(200_000)
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		if spec.Container != "c-db" {
			t.Errorf("exec addressed %q, want the container id", spec.Container)
		}
		if got := strings.Join(spec.Argv, " "); got != "pg_dump -Fc" {
			t.Errorf("argv %q", got)
		}
		if len(spec.Env) != 1 || spec.Env[0] != "PGPASSWORD=hunter2-secret" {
			t.Errorf("env %v", spec.Env)
		}
		io.WriteString(spec.Stderr, "dump: warning\n")
		_, err := spec.Stdout.Write(data)
		return 0, err
	}
	req := &agentv1.BackupRun{BackupId: "b1", Source: execSource("pg_dump", "-Fc"), Target: localLoc("org/w/b1.pgdump.aeb"), Encryption: enc()}
	var p pgIO
	res, err := svc.Run(context.Background(), req, p.io(masked(req)))
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(pol.BackupsDir(), "org", "w", "b1.pgdump.aeb")
	stored, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if res.RawSizeBytes != int64(len(data)) || res.StoredSizeBytes != int64(len(stored)) || res.StoredSha256 != sha(stored) {
		t.Fatalf("result %v vs stored %d bytes %s", res, len(stored), sha(stored))
	}
	key, _ := ParseKey(secretKeyB64)
	plain, err := open(stored, key)
	if err != nil || !bytes.Equal(plain, data) {
		t.Fatalf("decrypt: %v", err)
	}
	if !strings.Contains(p.errw.String(), "dump: warning") {
		t.Fatalf("stderr did not reach the log: %q", p.errw.String())
	}
	if strings.Contains(p.out.String(), "hunter2") || strings.Contains(p.out.String(), secretKeyB64) {
		t.Fatal("secret in the log")
	}
	// No temporary file is left next to the object.
	entries, _ := os.ReadDir(filepath.Dir(path))
	if len(entries) != 1 {
		t.Fatalf("directory holds %d entries", len(entries))
	}
}

func TestRunExecDumpFailureAbortsAndRedacts(t *testing.T) {
	svc, f, pol := newService(t)
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		spec.Stdout.Write(dumpBytes(1000))
		io.WriteString(spec.Stderr, "pg_dump: error: password hunter2-secret rejected\n")
		return 3, nil
	}
	req := &agentv1.BackupRun{BackupId: "b2", Source: execSource("pg_dump"), Target: localLoc("x/b2.aeb"), Encryption: enc()}
	_, err := svc.Run(context.Background(), req, IO{Masker: masked(req)})
	var be *Error
	if !errors.As(err, &be) || be.Code != agentv1.ErrorCode_ERROR_CODE_INTERNAL || be.ExitCode != 3 {
		t.Fatalf("err = %#v", err)
	}
	if strings.Contains(err.Error(), "hunter2-secret") || !strings.Contains(err.Error(), "pg_dump: error") {
		t.Fatalf("message %q", err)
	}
	if _, serr := os.Stat(filepath.Join(pol.BackupsDir(), "x", "b2.aeb")); !os.IsNotExist(serr) {
		t.Fatal("object left behind after a failed dump")
	}
	entries, _ := os.ReadDir(filepath.Join(pol.BackupsDir(), "x"))
	if len(entries) != 0 {
		t.Fatalf("leftover files: %v", entries)
	}
}

func TestRunExecErrors(t *testing.T) {
	svc, f, _ := newService(t)
	run := func(container string) error {
		src := execSource("true")
		src.GetExec().Container = container
		_, err := svc.Run(context.Background(), &agentv1.BackupRun{Source: src, Target: localLoc("a.aeb"), Encryption: enc()}, IO{})
		return err
	}
	if err := run("missing"); !errors.Is(err, docker.ErrNotFound) {
		t.Fatalf("missing: %v", err)
	}
	f.AddContainer(&agentv1.ContainerInfo{Id: "c-off", Name: "off", State: agentv1.ContainerState_CONTAINER_STATE_EXITED,
		Labels: map[string]string{policy.LabelManaged: "true", policy.LabelWorkload: "w"}})
	if err := run("off"); !errors.Is(err, docker.ErrConflict) {
		t.Fatalf("stopped: %v", err)
	}
	f.AddContainer(&agentv1.ContainerInfo{Id: "c-foreign", Name: "foreign", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING})
	err := run("foreign")
	if v, ok := policy.AsViolation(err); !ok || v.Code != agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION {
		t.Fatalf("unmanaged: %v", err)
	}
	if len(f.Execs) != 0 {
		t.Fatal("an exec ran in a refused container")
	}
}

func TestRunBadEncryption(t *testing.T) {
	svc, _, _ := newService(t)
	for _, e := range []*agentv1.BackupEncryption{
		nil,
		{Key: &agentv1.SecretValue{Value: "bad"}, KeyId: "k"},
		{Key: &agentv1.SecretValue{Value: base64.StdEncoding.EncodeToString(make([]byte, 16))}, KeyId: "k"},
		{Key: &agentv1.SecretValue{Value: secretKeyB64}},
	} {
		_, err := svc.Run(context.Background(), &agentv1.BackupRun{Source: execSource("x"), Target: localLoc("a.aeb"), Encryption: e}, IO{})
		var be *Error
		if !errors.As(err, &be) || be.Code != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
			t.Fatalf("err = %v", err)
		}
		if strings.Contains(err.Error(), secretKeyB64) {
			t.Fatal("key in error")
		}
	}
}

func volumeSource(vol string) *agentv1.BackupSource {
	return &agentv1.BackupSource{Kind: &agentv1.BackupSource_Volume{Volume: &agentv1.VolumeArchive{Volume: vol}}}
}

func TestRunVolumeArchive(t *testing.T) {
	svc, f, pol := newService(t)
	f.Volumes["appdata"] = &agentv1.VolumeInfo{Name: "appdata"}
	data := dumpBytes(70_000)
	f.HelperFunc = func(_ context.Context, spec docker.HelperSpec) (int, error) {
		_, err := spec.Stdout.Write(data)
		return 0, err
	}
	res, err := svc.Run(context.Background(), &agentv1.BackupRun{BackupId: "v1", Source: volumeSource("appdata"), Target: localLoc("v1.tar.gz.aeb"), Encryption: enc()}, IO{})
	if err != nil {
		t.Fatal(err)
	}
	h := f.Helpers[0]
	if h.Image != "busybox:1.37" || h.Volume != "appdata" || !h.ReadOnly || strings.Join(h.Cmd, " ") != "tar czf - -C /data ." {
		t.Fatalf("helper %+v", h)
	}
	stored, _ := os.ReadFile(filepath.Join(pol.BackupsDir(), "v1.tar.gz.aeb"))
	key, _ := ParseKey(secretKeyB64)
	if plain, err := open(stored, key); err != nil || !bytes.Equal(plain, data) || res.RawSizeBytes != int64(len(data)) {
		t.Fatalf("round trip: %v", err)
	}
}

func TestRunVolumeRefusals(t *testing.T) {
	svc, f, _ := newService(t)
	f.Volumes["aethera_pgdata"] = &agentv1.VolumeInfo{Name: "aethera_pgdata"}
	f.Volumes["bound"] = &agentv1.VolumeInfo{Name: "bound"}
	f.VolumeOpts = map[string]map[string]string{"bound": {"type": "none", "o": "bind", "device": "/etc"}}
	try := func(vol string) error {
		_, err := svc.Run(context.Background(), &agentv1.BackupRun{Source: volumeSource(vol), Target: localLoc("a.aeb"), Encryption: enc()}, IO{})
		return err
	}
	if _, ok := policy.AsViolation(try("aethera_pgdata")); !ok {
		t.Fatal("installation volume archived")
	}
	if _, ok := policy.AsViolation(try("bound")); !ok {
		t.Fatal("bind-backed volume archived")
	}
	if !errors.Is(try("nosuch"), docker.ErrNotFound) {
		t.Fatal("missing volume not NOT_FOUND")
	}
	if v, ok := policy.AsViolation(try("/etc")); !ok || v.Code != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
		t.Fatalf("a path is not a volume: %v", v) // not a policy violation but an invalid argument
	}
	if len(f.Helpers) != 0 {
		t.Fatal("a helper ran for a refused volume")
	}
}

func TestRunVolumeFailureCode(t *testing.T) {
	svc, f, _ := newService(t)
	f.Volumes["v"] = &agentv1.VolumeInfo{Name: "v"}
	f.HelperFunc = func(_ context.Context, spec docker.HelperSpec) (int, error) {
		io.WriteString(spec.Stderr, "tar: no space\n")
		return 1, nil
	}
	_, err := svc.Run(context.Background(), &agentv1.BackupRun{Source: volumeSource("v"), Target: localLoc("a.aeb"), Encryption: enc()}, IO{})
	var be *Error
	if !errors.As(err, &be) || be.ExitCode != 1 || !strings.Contains(be.Msg, "tar: no space") {
		t.Fatalf("err = %v", err)
	}
}

func TestRunCancelledAbortsUpload(t *testing.T) {
	svc, f, pol := newService(t)
	ctx, cancel := context.WithCancel(context.Background())
	f.ExecFunc = func(ctx context.Context, spec docker.ExecSpec) (int, error) {
		spec.Stdout.Write(dumpBytes(100))
		cancel()
		<-ctx.Done()
		return 0, ctx.Err()
	}
	_, err := svc.Run(ctx, &agentv1.BackupRun{Source: execSource("sleep"), Target: localLoc("c.aeb"), Encryption: enc()}, IO{})
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("err = %v", err)
	}
	entries, _ := os.ReadDir(pol.BackupsDir())
	if len(entries) != 0 {
		t.Fatalf("leftovers: %v", entries)
	}
}

func TestProgressIsReported(t *testing.T) {
	svc, f, _ := newService(t)
	var mu sync.Mutex
	var msgs []string
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		spec.Stdout.Write(dumpBytes(5000))
		time.Sleep(60 * time.Millisecond)
		return 0, nil
	}
	_, err := svc.Run(context.Background(), &agentv1.BackupRun{Source: execSource("x"), Target: localLoc("p.aeb"), Encryption: enc()},
		IO{Progress: func(step, msg string, pct int32) { mu.Lock(); msgs = append(msgs, step+":"+msg); mu.Unlock() }})
	if err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	defer mu.Unlock()
	if len(msgs) == 0 || !strings.HasPrefix(msgs[0], "backup:") {
		t.Fatalf("progress %v", msgs)
	}
}

// ---------------------------------------------------------------------------
// Restore

// seedLocal makes an encrypted object in the backup directory and returns its
// checksum and plaintext.
func seedLocal(t *testing.T, pol *policy.Policy, rel string, n int) (sum string, plain []byte) {
	t.Helper()
	plain = dumpBytes(n)
	key, _ := ParseKey(secretKeyB64)
	obj := seal(t, key, plain)
	path := filepath.Join(pol.BackupsDir(), filepath.FromSlash(rel))
	os.MkdirAll(filepath.Dir(path), 0o700)
	if err := os.WriteFile(path, obj, 0o600); err != nil {
		t.Fatal(err)
	}
	return sha(obj), plain
}

func TestRestoreExecSink(t *testing.T) {
	svc, f, pol := newService(t)
	sum, plain := seedLocal(t, pol, "r/o.aeb", 150_000)
	var got []byte
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		var err error
		got, err = io.ReadAll(spec.Stdin)
		return 0, err
	}
	res, err := svc.Restore(context.Background(), &agentv1.BackupRestore{
		BackupId: "b", Source: localLoc("r/o.aeb"), Encryption: enc(), Sink: execSink("pg_restore", "-d", "x"), ExpectedSha256: strings.ToUpper(sum),
	}, IO{})
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(got, plain) || res.RestoredBytes != int64(len(plain)) {
		t.Fatalf("restored %d bytes (%v)", len(got), res)
	}
}

func TestRestoreRefusesBeforeTouchingTheSink(t *testing.T) {
	svc, f, pol := newService(t)
	sum, _ := seedLocal(t, pol, "r/o.aeb", 3*ChunkSize+10)
	obj := filepath.Join(pol.BackupsDir(), "r", "o.aeb")
	good, _ := os.ReadFile(obj)
	restore := func(expected string, e *agentv1.BackupEncryption) error {
		_, err := svc.Restore(context.Background(), &agentv1.BackupRestore{Source: localLoc("r/o.aeb"), Encryption: e, Sink: execSink("x"), ExpectedSha256: expected}, IO{})
		return err
	}
	code := func(err error) agentv1.ErrorCode {
		var be *Error
		if errors.As(err, &be) {
			return be.Code
		}
		return agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED
	}
	// Wrong checksum.
	if c := code(restore(strings.Repeat("0", 64), enc())); c != agentv1.ErrorCode_ERROR_CODE_CHECKSUM_MISMATCH {
		t.Fatalf("checksum: %v", c)
	}
	// Wrong key id.
	other := enc()
	other.KeyId = "k2"
	if c := code(restore(sum, other)); c != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
		t.Fatalf("key id: %v", c)
	}
	// Wrong key.
	wrong := enc()
	wrong.Key = &agentv1.SecretValue{Value: base64.StdEncoding.EncodeToString(bytes.Repeat([]byte{7}, 32))}
	if c := code(restore(sum, wrong)); c != agentv1.ErrorCode_ERROR_CODE_CHECKSUM_MISMATCH {
		t.Fatalf("wrong key: %v", c)
	}
	// Corrupted in the last chunk (no checksum given): detected by the verification pass.
	bad := append([]byte{}, good...)
	bad[len(bad)-3] ^= 0xff
	os.WriteFile(obj, bad, 0o600)
	if c := code(restore("", enc())); c != agentv1.ErrorCode_ERROR_CODE_CHECKSUM_MISMATCH {
		t.Fatalf("corrupt: %v", c)
	}
	// Truncated at a chunk boundary.
	os.WriteFile(obj, good[:5+2+SaltSize+ChunkSize+16], 0o600)
	if c := code(restore("", enc())); c != agentv1.ErrorCode_ERROR_CODE_CHECKSUM_MISMATCH {
		t.Fatalf("truncated: %v", c)
	}
	// Missing.
	os.Remove(obj)
	if c := code(restore("", enc())); c != agentv1.ErrorCode_ERROR_CODE_NOT_FOUND {
		t.Fatalf("missing: %v", c)
	}
	if len(f.Execs) != 0 {
		t.Fatalf("the sink ran %d times for objects that could not be verified", len(f.Execs))
	}
}

func TestRestoreExecFailureCarriesStderr(t *testing.T) {
	svc, f, pol := newService(t)
	seedLocal(t, pol, "o.aeb", 100)
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		io.Copy(io.Discard, spec.Stdin)
		io.WriteString(spec.Stderr, "pg_restore: error: could not connect\n")
		return 1, nil
	}
	_, err := svc.Restore(context.Background(), &agentv1.BackupRestore{Source: localLoc("o.aeb"), Encryption: enc(), Sink: execSink("pg_restore")}, IO{})
	var be *Error
	if !errors.As(err, &be) || be.ExitCode != 1 || !strings.Contains(be.Msg, "could not connect") {
		t.Fatalf("err = %v", err)
	}
}

func TestRestoreVolumeSink(t *testing.T) {
	svc, f, pol := newService(t)
	f.Volumes["appdata"] = &agentv1.VolumeInfo{Name: "appdata"}
	_, plain := seedLocal(t, pol, "o.aeb", 90_000)
	var got []byte
	f.HelperFunc = func(_ context.Context, spec docker.HelperSpec) (int, error) {
		var err error
		got, err = io.ReadAll(spec.Stdin)
		return 0, err
	}
	sink := func(clear bool) *agentv1.BackupSink {
		return &agentv1.BackupSink{Kind: &agentv1.BackupSink_Volume{Volume: &agentv1.VolumeRestore{Volume: "appdata", ClearFirst: clear}}}
	}
	if _, err := svc.Restore(context.Background(), &agentv1.BackupRestore{Source: localLoc("o.aeb"), Encryption: enc(), Sink: sink(true)}, IO{}); err != nil {
		t.Fatal(err)
	}
	h := f.Helpers[0]
	script := h.Cmd[len(h.Cmd)-1]
	if h.ReadOnly || h.Volume != "appdata" || !bytes.Equal(got, plain) ||
		!strings.Contains(script, "-mindepth 1") || !strings.Contains(script, "tar xzf - -C /data") {
		t.Fatalf("helper %+v", h)
	}
	if _, err := svc.Restore(context.Background(), &agentv1.BackupRestore{Source: localLoc("o.aeb"), Encryption: enc(), Sink: sink(false)}, IO{}); err != nil {
		t.Fatal(err)
	}
	if script := f.Helpers[1].Cmd[len(f.Helpers[1].Cmd)-1]; strings.Contains(script, "rm") {
		t.Fatalf("cleared without clear_first: %s", script)
	}
	// Installation volumes cannot be a sink.
	f.Volumes["aethera_data"] = &agentv1.VolumeInfo{Name: "aethera_data"}
	bad := &agentv1.BackupSink{Kind: &agentv1.BackupSink_Volume{Volume: &agentv1.VolumeRestore{Volume: "aethera_data"}}}
	if _, err := svc.Restore(context.Background(), &agentv1.BackupRestore{Source: localLoc("o.aeb"), Encryption: enc(), Sink: bad}, IO{}); err == nil {
		t.Fatal("restored into an installation volume")
	}
}

// memStore is an in-memory remote object.
type memStore struct {
	mu      sync.Mutex
	data    []byte
	aborted bool
	deleted bool
	openErr error
}

func (m *memStore) Describe() string { return "mem://obj" }
func (m *memStore) Put(ctx context.Context, fill func(io.Writer) error) error {
	var b bytes.Buffer
	if err := fill(&b); err != nil {
		m.mu.Lock()
		m.aborted = true
		m.mu.Unlock()
		return err
	}
	m.mu.Lock()
	m.data = b.Bytes()
	m.mu.Unlock()
	return nil
}
func (m *memStore) Open(ctx context.Context) (io.ReadCloser, error) {
	if m.openErr != nil {
		return nil, m.openErr
	}
	return io.NopCloser(bytes.NewReader(m.data)), nil
}
func (m *memStore) Delete(ctx context.Context) error { m.deleted = true; return nil }

func TestRemoteRoundTripUsesAndRemovesTempFile(t *testing.T) {
	svc, f, _ := newService(t)
	ms := &memStore{}
	svc.OpenStore = func(context.Context, *agentv1.BackupLocation) (Store, error) { return ms, nil }
	svc.TempDir = filepath.Join(t.TempDir(), "restore-tmp")
	data := dumpBytes(300_000)
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		if spec.Stdout != nil && spec.Stdin == nil {
			spec.Stdout.Write(data)
		}
		return 0, nil
	}
	res, err := svc.Run(context.Background(), &agentv1.BackupRun{Source: execSource("dump"), Target: localLoc("ignored"), Encryption: enc()}, IO{})
	if err != nil {
		t.Fatal(err)
	}
	if res.StoredSha256 != sha(ms.data) {
		t.Fatal("stored checksum differs")
	}
	var got []byte
	var tmpSeen bool
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		entries, _ := os.ReadDir(svc.TempDir)
		tmpSeen = len(entries) == 1
		got, _ = io.ReadAll(spec.Stdin)
		return 0, nil
	}
	if _, err := svc.Restore(context.Background(), &agentv1.BackupRestore{Source: localLoc("ignored"), Encryption: enc(), Sink: execSink("restore"), ExpectedSha256: res.StoredSha256}, IO{}); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(got, data) || !tmpSeen {
		t.Fatalf("restore through a temp file failed (tmp seen %v)", tmpSeen)
	}
	if entries, _ := os.ReadDir(svc.TempDir); len(entries) != 0 {
		t.Fatalf("temp file left behind: %v", entries)
	}
	// A checksum mismatch also cleans up.
	if _, err := svc.Restore(context.Background(), &agentv1.BackupRestore{Source: localLoc("ignored"), Encryption: enc(), Sink: execSink("restore"), ExpectedSha256: strings.Repeat("a", 64)}, IO{}); err == nil {
		t.Fatal("mismatch accepted")
	}
	if entries, _ := os.ReadDir(svc.TempDir); len(entries) != 0 {
		t.Fatalf("temp file left behind after a failure: %v", entries)
	}
}

func TestFailedRunAbortsRemoteUpload(t *testing.T) {
	svc, f, _ := newService(t)
	ms := &memStore{}
	svc.OpenStore = func(context.Context, *agentv1.BackupLocation) (Store, error) { return ms, nil }
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		spec.Stdout.Write(dumpBytes(10))
		return 2, nil
	}
	if _, err := svc.Run(context.Background(), &agentv1.BackupRun{Source: execSource("x"), Target: localLoc("i"), Encryption: enc()}, IO{}); err == nil {
		t.Fatal("expected failure")
	}
	if !ms.aborted || ms.data != nil {
		t.Fatal("upload not aborted")
	}
}

func TestRestoreDownloadErrorsAreMapped(t *testing.T) {
	svc, _, _ := newService(t)
	ms := &memStore{openErr: Errorf(agentv1.ErrorCode_ERROR_CODE_NOT_FOUND, "S3: The specified key does not exist.")}
	svc.OpenStore = func(context.Context, *agentv1.BackupLocation) (Store, error) { return ms, nil }
	_, err := svc.Restore(context.Background(), &agentv1.BackupRestore{Source: localLoc("i"), Encryption: enc(), Sink: execSink("x")}, IO{})
	var be *Error
	if !errors.As(err, &be) || be.Code != agentv1.ErrorCode_ERROR_CODE_NOT_FOUND {
		t.Fatalf("err = %v", err)
	}
}

// ---------------------------------------------------------------------------
// Delete

func TestDeleteLocalMissingIsFine(t *testing.T) {
	svc, _, pol := newService(t)
	seedLocal(t, pol, "d/x.aeb", 10)
	req := &agentv1.BackupDelete{Locations: []*agentv1.BackupLocation{localLoc("d/x.aeb"), localLoc("d/never-existed.aeb")}}
	if err := svc.Delete(context.Background(), req, IO{}); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(pol.BackupsDir(), "d", "x.aeb")); !os.IsNotExist(err) {
		t.Fatal("not deleted")
	}
	// Deleting again is fine too, and a bad path is an error that does not stop the others.
	bad := &agentv1.BackupDelete{Locations: []*agentv1.BackupLocation{localLoc("../escape"), localLoc("d/x.aeb")}}
	if err := svc.Delete(context.Background(), bad, IO{}); err == nil {
		t.Fatal("escaping path deleted")
	}
}

func TestLocalPathRejections(t *testing.T) {
	svc, _, _ := newService(t)
	for _, p := range []string{"../x", "/abs", "a/../../b", ""} {
		_, err := svc.Run(context.Background(), &agentv1.BackupRun{Source: execSource("x"), Target: localLoc(p), Encryption: enc()}, IO{})
		if err == nil {
			t.Fatalf("%q accepted", p)
		}
	}
}

func TestTailMasksAndTruncates(t *testing.T) {
	tl := NewTail(10)
	io.WriteString(tl, strings.Repeat("a", 10000)+"password=hunter2-secret END")
	b, trunc := tl.Result(secrets.NewMasker("hunter2-secret"), 12)
	if !trunc || len(b) != 12 || strings.Contains(string(b), "hunter2") || !strings.HasSuffix(string(b), "=*** END") {
		t.Fatalf("tail %q trunc=%v", b, trunc)
	}
	small := NewTail(100)
	io.WriteString(small, "short")
	if b, trunc := small.Result(nil, 100); string(b) != "short" || trunc {
		t.Fatalf("%q %v", b, trunc)
	}
}

func TestEndpointStoreValidation(t *testing.T) {
	_, _, pol := newService(t)
	loc := &agentv1.S3Location{Endpoint: "http://example.invalid", Bucket: "b", Key: "k", AccessKeyId: "a", SecretAccessKey: &agentv1.SecretValue{Value: "s3cr3t-value"}}
	_, err := NewS3Store(context.Background(), pol, loc)
	if err == nil || strings.Contains(err.Error(), "s3cr3t-value") {
		t.Fatalf("err = %v", err)
	}
	loc.Endpoint = "http://127.0.0.1:9101"
	loc.ForcePathStyle = true
	st, err := NewS3Store(context.Background(), pol, loc)
	if err != nil || st.Describe() != "s3://b/k" {
		t.Fatalf("%v %v", st, err)
	}
}
