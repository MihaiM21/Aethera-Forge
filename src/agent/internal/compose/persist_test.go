package compose

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

func TestUpKeepsDataBindsAcrossDeploymentsButTakesRepositoryFilesFresh(t *testing.T) {
	dir := t.TempDir()
	checkout := filepath.Join(dir, "shop", "src")
	r := gitRecorder()
	r.composeFile["nginx.conf"] = "events {}\n"
	r.config = `{"services":{"web":{"image":"nginx","volumes":[` +
		`{"type":"bind","source":` + quote(filepath.Join(checkout, "data")) + `,"target":"/var/lib/data"},` +
		`{"type":"bind","source":` + quote(filepath.Join(checkout, "nginx.conf")) + `,"target":"/etc/nginx/nginx.conf","read_only":true}]}}}`
	svc := &Service{Runner: r, Dir: dir, Binds: denyBinds{}}
	var out bytes.Buffer
	if _, err := svc.Up(context.Background(), gitProject(""), UpOptions{}, &out, &out); err != nil {
		t.Fatal(err)
	}

	persistFile := filepath.Join(dir, "shop", "compose.aethera-persist.yaml")
	b, err := os.ReadFile(persistFile)
	if err != nil {
		t.Fatalf("no persist override: %v", err)
	}
	ov := string(b)
	if !strings.Contains(ov, "/var/lib/data") || strings.Contains(ov, "nginx.conf") {
		t.Errorf("override = %s", ov)
	}
	if fi, err := os.Stat(filepath.Join(dir, "shop", persistDirName, "data")); err != nil || !fi.IsDir() {
		t.Errorf("persistent directory missing: %v", err)
	}
	up := lastCall(r, "up")
	if argAfterValue(up.Args, "-f", persistFile) == false {
		t.Errorf("up does not use the persist override: %v", up.Args)
	}
	if !strings.Contains(out.String(), "keeps ./data") {
		t.Errorf("log = %q", out.String())
	}
}

func TestPlaceholderDirectoriesAreData(t *testing.T) {
	d := t.TempDir()
	if err := os.WriteFile(filepath.Join(d, ".gitkeep"), nil, 0o644); err != nil {
		t.Fatal(err)
	}
	if providedByRepository(d) {
		t.Error("a directory holding only .gitkeep counts as source")
	}
	if err := os.WriteFile(filepath.Join(d, "seed.sql"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	if !providedByRepository(d) || providedByRepository(filepath.Join(d, "missing")) {
		t.Error("wrong answer for a real directory or a missing path")
	}
}

func TestRepositoryEnvFileComesBeforeAetheraVariables(t *testing.T) {
	dir := t.TempDir()
	r := gitRecorder()
	r.composeFile[".env"] = "FROM_REPO=1\n"
	svc := &Service{Runner: r, Dir: dir, Binds: denyBinds{}}
	if _, err := svc.Up(context.Background(), gitProject(""), UpOptions{}, buf(), buf()); err != nil {
		t.Fatal(err)
	}
	up := lastCall(r, "up")
	var files []string
	for i, a := range up.Args {
		if a == "--env-file" {
			files = append(files, up.Args[i+1])
		}
	}
	if len(files) != 2 || files[0] != filepath.Join(dir, "shop", "src", ".env") {
		t.Fatalf("env files = %v", files)
	}
	if !strings.Contains(r.envSeen, "TAG=") {
		t.Errorf("Aethera's variables are not the last env file: %q", r.envSeen)
	}
}

func TestInspectLeavesTheRunningCheckoutAlone(t *testing.T) {
	dir := t.TempDir()
	marker := filepath.Join(dir, "shop", "src", "data", "db.sqlite")
	if err := os.MkdirAll(filepath.Dir(marker), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(marker, []byte("rows"), 0o644); err != nil {
		t.Fatal(err)
	}
	svc := &Service{Runner: gitRecorder(), Dir: dir, Binds: denyBinds{}}
	if _, err := svc.Inspect(context.Background(), gitProject(""), buf()); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(marker); err != nil {
		t.Fatalf("inspect wiped the running checkout: %v", err)
	}
}

func TestHostPortsOfTheProxyAreRefused(t *testing.T) {
	r := gitRecorder()
	r.config = `{"services":{"web":{"image":"nginx","ports":[{"target":80,"published":"80"}]}}}`
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	_, err := svc.Up(context.Background(), gitProject(""), UpOptions{}, buf(), buf())
	if err == nil || !strings.Contains(err.Error(), "reverse proxy") {
		t.Fatalf("err = %v", err)
	}
}

func TestWaitFailureOfACompletedOneShotServiceIsNotAFailure(t *testing.T) {
	r := gitRecorder()
	r.failUp = true
	r.psOut = `{"ID":"c1","Name":"shop-web-1","Image":"nginx","Service":"web","State":"running","Health":"","ExitCode":0,"Publishers":[]}` + "\n" +
		`{"ID":"c2","Name":"shop-migrate-1","Image":"app","Service":"migrate","State":"exited","Health":"","ExitCode":0,"Publishers":[]}` + "\n"
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	if _, err := svc.Up(context.Background(), gitProject(""), UpOptions{Wait: true}, buf(), buf()); err != nil {
		t.Fatalf("completed one-shot service failed the deployment: %v", err)
	}

	r.psOut = strings.Replace(r.psOut, `"State":"exited","Health":"","ExitCode":0`, `"State":"exited","Health":"","ExitCode":3`, 1)
	if _, err := svc.Up(context.Background(), gitProject(""), UpOptions{Wait: true}, buf(), buf()); err == nil {
		t.Fatal("a service that exited 3 passed")
	}
}

func quote(s string) string { return `"` + strings.ReplaceAll(filepath.ToSlash(s), `"`, `\"`) + `"` }

func lastCall(r *recorder, sub string) (c build.Cmd) {
	for _, call := range r.calls {
		if has(call.Args, sub) && call.Name == "docker" {
			c = call
		}
	}
	return c
}

func argAfterValue(args []string, flag, want string) bool {
	for i, a := range args {
		if a == flag && i+1 < len(args) && args[i+1] == want {
			return true
		}
	}
	return false
}
