//go:build integration

package backup

import (
	"bytes"
	"context"
	"errors"
	"os"
	"os/exec"
	"strings"
	"testing"
	"time"

	"github.com/minio/minio-go/v7"
	"github.com/minio/minio-go/v7/pkg/credentials"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
)

// These tests need a Docker daemon (and pull busybox, postgres:17, minio/minio).
// They start containers named g1-*, MinIO on 127.0.0.1:9101, and remove them.
//
//	go test -tags integration -run TestIntegration -v ./internal/backup

const (
	minioAddr   = "127.0.0.1:9101"
	minioUser   = "g1accesskey"
	minioSecret = "g1-secret-key-value"
)

func dockerCLI(t *testing.T, args ...string) string {
	t.Helper()
	out, err := exec.Command("docker", args...).CombinedOutput()
	if err != nil {
		t.Fatalf("docker %s: %v\n%s", strings.Join(args, " "), err, out)
	}
	return strings.TrimSpace(string(out))
}

func dockerQuiet(args ...string) { _ = exec.Command("docker", args...).Run() }

func integrationService(t *testing.T) (*Service, *docker.SDK, *policy.Policy) {
	t.Helper()
	dk, err := docker.NewSDK("")
	if err != nil {
		t.Skipf("no docker client: %v", err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if st, _ := dk.Status(ctx); st != agentv1.DockerStatus_DOCKER_STATUS_RUNNING {
		t.Skip("docker daemon not running")
	}
	cfg := config.Default()
	cfg.StateDir = t.TempDir()
	cfg.Finalize()
	pol := policy.New(cfg)
	return &Service{Docker: dk, Policy: pol}, dk, pol
}

func startLabelled(t *testing.T, name string, runArgs ...string) {
	t.Helper()
	dockerQuiet("rm", "-f", name)
	args := append([]string{"run", "-d", "--name", name, "--label", "aethera.managed=true", "--label", "aethera.workload=g1"}, runArgs...)
	dockerCLI(t, args...)
	t.Cleanup(func() { dockerQuiet("rm", "-f", "-v", name) })
}

func TestIntegrationExecIntoLabelledContainer(t *testing.T) {
	svc, _, _ := integrationService(t)
	startLabelled(t, "g1-exec", "busybox:1.37", "sleep", "300")
	dockerCLI(t, "run", "-d", "--name", "g1-foreign", "busybox:1.37", "sleep", "300")
	t.Cleanup(func() { dockerQuiet("rm", "-f", "g1-foreign") })

	ex := svc.exec()
	ctx := context.Background()
	var out, errb bytes.Buffer
	code, err := ex.Run(ctx, ExecRequest{
		Container: "g1-exec", Argv: []string{"sh", "-c", `echo "out:$GREETING:$(pwd):$(id -un)"; echo oops >&2; exit 5`},
		Env:        []*agentv1.EnvVar{{Name: "GREETING", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "hello"}}}},
		WorkingDir: "/tmp", Stdout: &out, Stderr: &errb,
	})
	if err != nil || code != 5 {
		t.Fatalf("code %d err %v", code, err)
	}
	if strings.TrimSpace(out.String()) != "out:hello:/tmp:root" || strings.TrimSpace(errb.String()) != "oops" {
		t.Fatalf("stdout %q stderr %q", out.String(), errb.String())
	}

	// stdin reaches the process and is closed at EOF.
	out.Reset()
	code, err = ex.Run(ctx, ExecRequest{Container: "g1-exec", Argv: []string{"wc", "-c"}, Stdin: strings.NewReader("12345"), Stdout: &out})
	if err != nil || code != 0 || strings.TrimSpace(out.String()) != "5" {
		t.Fatalf("stdin: %d %v %q", code, err, out.String())
	}

	// Missing, stopped and unmanaged containers.
	if _, err := ex.Run(ctx, ExecRequest{Container: "g1-nonexistent", Argv: []string{"true"}}); !errors.Is(err, docker.ErrNotFound) {
		t.Fatalf("missing: %v", err)
	}
	if _, err := ex.Run(ctx, ExecRequest{Container: "g1-foreign", Argv: []string{"true"}}); err == nil || !strings.Contains(err.Error(), "not a workload managed by Aethera") {
		t.Fatalf("unmanaged: %v", err)
	}
	dockerCLI(t, "stop", "-t", "0", "g1-exec")
	if _, err := ex.Run(ctx, ExecRequest{Container: "g1-exec", Argv: []string{"true"}}); !errors.Is(err, docker.ErrConflict) {
		t.Fatalf("stopped: %v", err)
	}
}

func TestIntegrationExecDeadline(t *testing.T) {
	svc, _, _ := integrationService(t)
	startLabelled(t, "g1-deadline", "busybox:1.37", "sleep", "300")
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	start := time.Now()
	_, err := svc.exec().Run(ctx, ExecRequest{Container: "g1-deadline", Argv: []string{"sleep", "60"}})
	if !errors.Is(err, context.DeadlineExceeded) || time.Since(start) > 15*time.Second {
		t.Fatalf("err %v after %v", err, time.Since(start))
	}
	// The process is killed on Linux hosts where the agent sees the container's processes.
	t.Logf("exec returned after %v", time.Since(start))
}

func startMinio(t *testing.T) *minio.Client {
	t.Helper()
	dockerQuiet("rm", "-f", "g1-minio")
	// minio/minio is the default; AETHERA_TEST_MINIO_IMAGE selects another build
	// (the Docker Hub repository no longer serves new pulls; Bitnami images start the server by themselves).
	image := os.Getenv("AETHERA_TEST_MINIO_IMAGE")
	if image == "" {
		image = "minio/minio"
	}
	args := []string{"run", "-d", "--name", "g1-minio", "-p", "127.0.0.1:9101:9000",
		"-e", "MINIO_ROOT_USER=" + minioUser, "-e", "MINIO_ROOT_PASSWORD=" + minioSecret, image}
	if !strings.Contains(image, "bitnami") {
		args = append(args, "server", "/data")
	}
	dockerCLI(t, args...)
	t.Cleanup(func() { dockerQuiet("rm", "-f", "-v", "g1-minio") })
	cl, err := minio.New(minioAddr, &minio.Options{Creds: credentials.NewStaticV4(minioUser, minioSecret, "")})
	if err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(60 * time.Second)
	for {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		_, err := cl.ListBuckets(ctx)
		cancel()
		if err == nil {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("minio did not start: %v", err)
		}
		time.Sleep(time.Second)
	}
	if err := cl.MakeBucket(context.Background(), "g1-backups", minio.MakeBucketOptions{}); err != nil {
		t.Fatal(err)
	}
	return cl
}

func s3Target(key string) *agentv1.BackupLocation {
	return &agentv1.BackupLocation{Kind: &agentv1.BackupLocation_S3{S3: &agentv1.S3Location{
		Endpoint: "http://" + minioAddr, Bucket: "g1-backups", Key: key, AccessKeyId: minioUser,
		SecretAccessKey: &agentv1.SecretValue{Value: minioSecret}, ForcePathStyle: true,
	}}}
}

func psql(t *testing.T, ctr, db, sql string) string {
	t.Helper()
	return dockerCLI(t, "exec", ctr, "psql", "-U", "postgres", "-d", db, "-tAc", sql)
}

func TestIntegrationPostgresToMinioAndBack(t *testing.T) {
	svc, _, _ := integrationService(t)
	mc := startMinio(t)
	startLabelled(t, "g1-pg", "-e", "POSTGRES_PASSWORD=pgpass", "postgres:17")
	for i := 0; ; i++ {
		if exec.Command("docker", "exec", "g1-pg", "pg_isready", "-U", "postgres").Run() == nil &&
			exec.Command("docker", "exec", "g1-pg", "psql", "-U", "postgres", "-tAc", "select 1").Run() == nil {
			break
		}
		if i > 60 {
			t.Fatal("postgres did not start")
		}
		time.Sleep(time.Second)
	}
	dockerCLI(t, "exec", "g1-pg", "createdb", "-U", "postgres", "appdb")
	psql(t, "g1-pg", "appdb", "create table items(id int primary key, name text); insert into items select g, 'item-' || g from generate_series(1, 5000) g;")

	enc := &agentv1.BackupEncryption{Key: &agentv1.SecretValue{Value: secretKeyB64}, KeyId: "mk1-test"}
	key := "aethera/org/g1/db/20261023T030000Z-run1.pgdump.aeb"
	run := &agentv1.BackupRun{
		BackupId: "run1", Target: s3Target(key), Encryption: enc,
		Source: &agentv1.BackupSource{Kind: &agentv1.BackupSource_Exec{Exec: &agentv1.ExecDump{
			Container: "g1-pg", Argv: []string{"pg_dump", "-Fc", "-U", "postgres", "-d", "appdb"},
			Env: []*agentv1.EnvVar{{Name: "PGPASSWORD", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "pgpass"}}}},
		}}},
	}
	var logs bytes.Buffer
	res, err := svc.Run(context.Background(), run, IO{Out: &logs, Err: &logs})
	if err != nil {
		t.Fatalf("backup: %v\n%s", err, logs.String())
	}
	st, err := mc.StatObject(context.Background(), "g1-backups", key, minio.StatObjectOptions{})
	if err != nil || st.Size != res.StoredSizeBytes || res.RawSizeBytes == 0 || len(res.StoredSha256) != 64 {
		t.Fatalf("stat %v %v result %v", st, err, res)
	}
	if strings.Contains(logs.String(), secretKeyB64) || strings.Contains(logs.String(), minioSecret) {
		t.Fatal("secret in the command log")
	}

	// Restore into a fresh database.
	dockerCLI(t, "exec", "g1-pg", "createdb", "-U", "postgres", "fresh")
	restore := &agentv1.BackupRestore{
		BackupId: "run1", Source: s3Target(key), Encryption: enc, ExpectedSha256: res.StoredSha256,
		Sink: &agentv1.BackupSink{Kind: &agentv1.BackupSink_Exec{Exec: &agentv1.ExecRestore{
			Container: "g1-pg", Argv: []string{"pg_restore", "--clean", "--if-exists", "--no-owner", "-U", "postgres", "-d", "fresh"},
		}}},
	}
	rr, err := svc.Restore(context.Background(), restore, IO{Out: &logs, Err: &logs})
	if err != nil {
		t.Fatalf("restore: %v\n%s", err, logs.String())
	}
	if rr.RestoredBytes != res.RawSizeBytes {
		t.Fatalf("restored %d, dumped %d", rr.RestoredBytes, res.RawSizeBytes)
	}
	if got := psql(t, "g1-pg", "fresh", "select count(*) || ':' || max(name) from items where id = 4242 or id = 1"); got != "2:item-4242" {
		t.Fatalf("row check = %q", got)
	}

	// A wrong checksum is refused and nothing is restored.
	dockerCLI(t, "exec", "g1-pg", "createdb", "-U", "postgres", "untouched")
	restore.ExpectedSha256 = strings.Repeat("0", 64)
	restore.Sink.GetExec().Argv = []string{"pg_restore", "--no-owner", "-U", "postgres", "-d", "untouched"}
	if _, err := svc.Restore(context.Background(), restore, IO{}); err == nil || !strings.Contains(err.Error(), "checksum") {
		t.Fatalf("checksum mismatch: %v", err)
	}
	if got := psql(t, "g1-pg", "untouched", "select count(*) from pg_tables where schemaname = 'public'"); got != "0" {
		t.Fatalf("a refused restore touched the database: %s tables", got)
	}

	// A failing dump aborts the multipart upload: no object, no incomplete upload.
	bigKey := "aethera/org/g1/failing.aeb"
	fail := &agentv1.BackupRun{
		BackupId: "run2", Target: s3Target(bigKey), Encryption: enc,
		Source: &agentv1.BackupSource{Kind: &agentv1.BackupSource_Exec{Exec: &agentv1.ExecDump{
			Container: "g1-pg", Argv: []string{"sh", "-c", "head -c 40000000 /dev/urandom; echo dump failed >&2; exit 1"},
		}}},
	}
	_, err = svc.Run(context.Background(), fail, IO{})
	var be *Error
	if !errors.As(err, &be) || be.ExitCode != 1 || !strings.Contains(be.Msg, "dump failed") {
		t.Fatalf("failing dump: %v", err)
	}
	if _, err := mc.StatObject(context.Background(), "g1-backups", bigKey, minio.StatObjectOptions{}); err == nil {
		t.Fatal("a failed backup left an object")
	}
	for u := range mc.ListIncompleteUploads(context.Background(), "g1-backups", "aethera/", true) {
		t.Fatalf("incomplete upload left behind: %v", u.Key)
	}

	// Delete: the object goes, a second delete is fine.
	del := &agentv1.BackupDelete{Locations: []*agentv1.BackupLocation{s3Target(key)}}
	for i := 0; i < 2; i++ {
		if err := svc.Delete(context.Background(), del, IO{}); err != nil {
			t.Fatalf("delete %d: %v", i, err)
		}
	}
	if _, err := mc.StatObject(context.Background(), "g1-backups", key, minio.StatObjectOptions{}); err == nil {
		t.Fatal("object still there")
	}
	// A restore of the deleted object is NOT_FOUND.
	restore.ExpectedSha256 = ""
	if _, err := svc.Restore(context.Background(), restore, IO{}); err == nil || !strings.Contains(err.Error(), "S3") {
		t.Fatalf("restore of a deleted object: %v", err)
	}
}

func TestIntegrationBadS3Credentials(t *testing.T) {
	svc, _, _ := integrationService(t)
	startMinio(t)
	startLabelled(t, "g1-cred", "busybox:1.37", "sleep", "300")
	loc := s3Target("x.aeb")
	loc.GetS3().SecretAccessKey = &agentv1.SecretValue{Value: "definitely-wrong-secret"}
	_, err := svc.Run(context.Background(), &agentv1.BackupRun{
		Target: loc, Encryption: &agentv1.BackupEncryption{Key: &agentv1.SecretValue{Value: secretKeyB64}, KeyId: "k"},
		Source: &agentv1.BackupSource{Kind: &agentv1.BackupSource_Exec{Exec: &agentv1.ExecDump{Container: "g1-cred", Argv: []string{"echo", "x"}}}},
	}, IO{Masker: nil})
	var be *Error
	if !errors.As(err, &be) || be.Code != agentv1.ErrorCode_ERROR_CODE_PERMISSION_DENIED {
		t.Fatalf("err = %v", err)
	}
	if strings.Contains(err.Error(), "definitely-wrong-secret") {
		t.Fatal("secret in error")
	}
}

func TestIntegrationVolumeArchiveAndRestore(t *testing.T) {
	svc, _, _ := integrationService(t)
	const vol = "g1-vol"
	dockerQuiet("volume", "rm", "-f", vol)
	dockerCLI(t, "volume", "create", vol)
	t.Cleanup(func() { dockerQuiet("volume", "rm", "-f", vol) })
	dockerCLI(t, "run", "--rm", "-v", vol+":/data", "busybox:1.37", "sh", "-c",
		"mkdir -p /data/sub && echo hello > /data/a.txt && echo dot > /data/.hidden && echo deep > /data/sub/b.txt")

	enc := &agentv1.BackupEncryption{Key: &agentv1.SecretValue{Value: secretKeyB64}, KeyId: "k1"}
	run := &agentv1.BackupRun{
		BackupId: "vol1", Encryption: enc, Target: localLoc("g1/vol.tar.gz.aeb"),
		Source: &agentv1.BackupSource{Kind: &agentv1.BackupSource_Volume{Volume: &agentv1.VolumeArchive{Volume: vol}}},
	}
	res, err := svc.Run(context.Background(), run, IO{})
	if err != nil || res.RawSizeBytes == 0 {
		t.Fatalf("archive: %v %v", res, err)
	}
	// The archive is read-only access: changing the volume afterwards.
	dockerCLI(t, "run", "--rm", "-v", vol+":/data", "busybox:1.37", "sh", "-c", "echo changed > /data/a.txt && echo extra > /data/extra.txt && echo extra > /data/.extra")

	restore := &agentv1.BackupRestore{
		BackupId: "vol1", Encryption: enc, Source: localLoc("g1/vol.tar.gz.aeb"), ExpectedSha256: res.StoredSha256,
		Sink: &agentv1.BackupSink{Kind: &agentv1.BackupSink_Volume{Volume: &agentv1.VolumeRestore{Volume: vol, ClearFirst: true}}},
	}
	if _, err := svc.Restore(context.Background(), restore, IO{}); err != nil {
		t.Fatalf("restore: %v", err)
	}
	got := dockerCLI(t, "run", "--rm", "-v", vol+":/data", "busybox:1.37", "sh", "-c", "cd /data && find . -type f | sort && cat a.txt .hidden sub/b.txt")
	want := "./.hidden\n./a.txt\n./sub/b.txt\nhello\ndot\ndeep"
	if got != want {
		t.Fatalf("volume after restore:\n%s\nwant:\n%s", got, want)
	}

	// The installation's volumes and non-existent volumes are refused.
	for _, v := range []string{"aethera_pgdata", "g1-does-not-exist"} {
		r := &agentv1.BackupRun{
			Encryption: enc, Target: localLoc("g1/refused.aeb"),
			Source: &agentv1.BackupSource{Kind: &agentv1.BackupSource_Volume{Volume: &agentv1.VolumeArchive{Volume: v}}},
		}
		if _, err := svc.Run(context.Background(), r, IO{}); err == nil {
			t.Fatalf("%s archived", v)
		}
	}
}
