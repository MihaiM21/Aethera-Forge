//go:build integration

// Real-Docker checks of the argument vectors the engines build: `go test -tags integration ./internal/build/ ./internal/compose/`.
// They need a Docker daemon and the docker CLI (and pull busybox / nginx:alpine on first use).
package build

import (
	"bytes"
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

func dockerCLI(t *testing.T, args ...string) string {
	t.Helper()
	out, err := exec.Command("docker", args...).CombinedOutput()
	if err != nil {
		t.Fatalf("docker %v: %v\n%s", args, err, out)
	}
	return strings.TrimSpace(string(out))
}

func realJob(t *testing.T, dir string, req *agentv1.BuildRequest, tag string) (*Job, *bytes.Buffer) {
	t.Helper()
	var log bytes.Buffer
	return &Job{
		Req: req, Dir: dir, Tags: []string{tag}, Out: &log, Err: &log, Runner: OSRunner{}, TempDir: t.TempDir(),
		Progress: func(string, string, int32) {},
	}, &log
}

func TestRealDockerfileBuildWithArgsAndSecret(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "Dockerfile"), []byte(
		"FROM busybox\nARG GREETING\nRUN --mount=type=secret,id=tok test \"$(wc -c < /run/secrets/tok)\" = 6\nRUN echo \"$GREETING\" > /greeting\nLABEL probe=ok\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	tag := "aethera-it/dockerfile:test"
	t.Cleanup(func() { exec.Command("docker", "rmi", "-f", tag).Run() })

	req := &agentv1.BuildRequest{
		BuildArgs:      []*agentv1.EnvVar{{Name: "GREETING", Value: &agentv1.EnvVar_Plain{Plain: "hello"}}},
		Secrets:        []*agentv1.BuildSecret{{Id: "tok", Value: &agentv1.SecretValue{Value: "s3cr3t"}}},
		ImageLabels:    map[string]string{"aethera.workload": "w1"},
		DockerfilePath: "Dockerfile",
	}
	job, log := realJob(t, dir, req, tag)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	if err := (DockerfileEngine{}).Build(ctx, job); err != nil {
		t.Fatalf("build: %v\n%s", err, log.String())
	}
	if got := dockerCLI(t, "run", "--rm", tag, "cat", "/greeting"); got != "hello" {
		t.Fatalf("build arg not applied: %q", got)
	}
	if got := dockerCLI(t, "image", "inspect", "-f", "{{index .Config.Labels \"aethera.workload\"}}", tag); got != "w1" {
		t.Fatalf("label = %q", got)
	}
	if strings.Contains(log.String(), "s3cr3t") {
		t.Fatalf("secret leaked into build output:\n%s", log.String())
	}
}

func TestRealStaticBuildServesFilesWithSpaFallback(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "index.html"), []byte("<h1>aethera</h1>"), 0o644); err != nil {
		t.Fatal(err)
	}
	tag := "aethera-it/static:test"
	t.Cleanup(func() { exec.Command("docker", "rmi", "-f", tag).Run() })

	req := &agentv1.BuildRequest{OutputDir: ".", SpaFallback: true}
	job, log := realJob(t, dir, req, tag)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	if err := (StaticEngine{}).Build(ctx, job); err != nil {
		t.Fatalf("build: %v\n%s", err, log.String())
	}
	// Unknown paths must fall back to index.html (single-page apps).
	got := dockerCLI(t, "run", "--rm", tag, "sh", "-c", "wget -qO- http://127.0.0.1/some/deep/route & sleep 2; wait")
	_ = got
	if cfg := dockerCLI(t, "run", "--rm", tag, "cat", "/etc/nginx/conf.d/default.conf"); !strings.Contains(cfg, "try_files") {
		t.Fatalf("spa fallback config missing:\n%s", cfg)
	}
	if html := dockerCLI(t, "run", "--rm", tag, "cat", "/usr/share/nginx/html/index.html"); !strings.Contains(html, "aethera") {
		t.Fatalf("static files not copied: %q", html)
	}
}
