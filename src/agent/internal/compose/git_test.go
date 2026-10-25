package compose

import (
	"bytes"
	"context"
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

const repoCompose = "services:\n  web:\n    build: ./app\n    ports: [\"8080:80\"]\n    expose: [\"3000\"]\n  db:\n    image: postgres:17\n"

func gitProject(token string) *agentv1.ComposeProject {
	g := &agentv1.GitSource{Url: "https://github.com/acme/shop.git", Ref: "main"}
	if token != "" {
		g.Credentials = &agentv1.GitCredentials{Material: &agentv1.GitCredentials_HttpsToken{HttpsToken: &agentv1.HttpsTokenCredential{Token: &agentv1.SecretValue{Value: token}}}}
	}
	return &agentv1.ComposeProject{
		ProjectName: "shop", Git: g, OverrideFile: "services:\n  web:\n    labels:\n      a: b\n",
		Env:       []*agentv1.EnvVar{plain("TAG", "v1")},
		BuildArgs: []*agentv1.EnvVar{{Name: "NPM_TOKEN", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "npm-s3cret"}}}},
	}
}

func gitRecorder() *recorder {
	return &recorder{
		composeFile: map[string]string{"docker-compose.yml": repoCompose},
		config:      `{"services":{"web":{"build":{"dockerfile":"Dockerfile"},"ports":[{"target":80,"published":"8080"}],"expose":["3000"]},"db":{"image":"postgres:17"}}}`,
	}
}

func joined(c build.Cmd) string { return strings.Join(c.Args, " ") }

func TestUpFromGitChecksOutBuildsAndStarts(t *testing.T) {
	dir := t.TempDir()
	r := gitRecorder()
	svc := &Service{Runner: r, Dir: dir, Binds: denyBinds{}}
	p := gitProject("ghp_tokentoken")
	var out bytes.Buffer
	res, err := svc.Up(context.Background(), p, UpOptions{Build: true, Wait: true}, &out, &out)
	if err != nil {
		t.Fatal(err)
	}
	if res.CommitSha != "0123456789abcdef0123456789abcdef01234567" {
		t.Errorf("commit = %q", res.CommitSha)
	}
	checkout := filepath.Join(dir, "shop", "src")

	// git: token in the environment, never in an argument vector; the remote is forgotten afterwards.
	var sawHeader, sawRemoteRemove bool
	var order []string
	for _, c := range r.calls {
		if strings.Contains(joined(c), "ghp_tokentoken") || strings.Contains(joined(c), "npm-s3cret") {
			t.Errorf("secret in argv: %s %v", c.Name, c.Args)
		}
		if c.Name == "git" {
			for _, e := range c.Env {
				if strings.HasPrefix(e, "GIT_CONFIG_VALUE_0=") {
					sawHeader = true
				}
			}
			if c.Args[0] == "remote" && c.Args[1] == "remove" {
				sawRemoteRemove = true
			}
			continue
		}
		switch {
		case has(c.Args, "config"):
			order = append(order, "config")
		case has(c.Args, "build"):
			order = append(order, "build")
		case has(c.Args, "up"):
			order = append(order, "up")
		}
	}
	if !sawHeader || !sawRemoteRemove {
		t.Errorf("token via env: %v, remote removed: %v", sawHeader, sawRemoteRemove)
	}
	if strings.Join(order, ",") != "config,build,up" {
		t.Fatalf("order = %v", order)
	}

	for _, c := range r.calls {
		if c.Name != "docker" || !(has(c.Args, "build") || has(c.Args, "up")) {
			continue
		}
		a := joined(c)
		for _, want := range []string{
			"--project-directory " + checkout,
			"-f " + filepath.Join(checkout, "docker-compose.yml"),
			"-f " + filepath.Join(dir, "shop", "compose.aethera.yaml"),
		} {
			if !strings.Contains(a, want) {
				t.Errorf("missing %q in %s", want, a)
			}
		}
		if has(c.Args, "build") {
			if !strings.Contains(a, "--build-arg NPM_TOKEN") || !has(c.Env, "NPM_TOKEN=npm-s3cret") || !has(c.Env, "DOCKER_BUILDKIT=1") {
				t.Errorf("build args must come from the environment: %s %v", a, c.Env)
			}
		}
		if has(c.Args, "up") {
			if has(c.Args, "--build") || has(c.Env, "NPM_TOKEN=npm-s3cret") {
				t.Errorf("up must not rebuild or carry build args: %s", a)
			}
		}
	}
	assertNoSecretOnDisk(t, dir, "ghp_tokentoken")
	assertNoSecretOnDisk(t, dir, "npm-s3cret")
	if _, err := os.Stat(checkout); err != nil {
		t.Errorf("checkout missing while the project is up: %v", err)
	}

	// compose config ran with every profile and the env-resolution flag.
	cfg := r.calls[firstCall(r, "config")]
	if !strings.Contains(joined(cfg), "--profile * config --format json --no-env-resolution") {
		t.Errorf("config argv = %s", joined(cfg))
	}

	// Down removes the checkout and does not need the repository.
	r.calls = nil
	if err := svc.Down(context.Background(), p, false, true, 5*time.Second, &out, &out); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(checkout); !os.IsNotExist(err) {
		t.Errorf("checkout survived compose down: %v", err)
	}
	for _, c := range r.calls {
		if c.Name == "git" || has(c.Args, "-f") {
			t.Errorf("down must work by project name only: %s %v", c.Name, c.Args)
		}
	}
}

func firstCall(r *recorder, word string) int {
	for i, c := range r.calls {
		if c.Name == "docker" && has(c.Args, word) {
			return i
		}
	}
	return -1
}

func TestPsFromGitDoesNotCloneAndUpIsFreshEveryTime(t *testing.T) {
	dir := t.TempDir()
	r := gitRecorder()
	svc := &Service{Runner: r, Dir: dir, Binds: denyBinds{}}
	if _, err := svc.Ps(context.Background(), gitProject(""), true, &bytes.Buffer{}); err != nil {
		t.Fatal(err)
	}
	for _, c := range r.calls {
		if c.Name == "git" {
			t.Fatal("ps cloned the repository")
		}
	}
	marker := filepath.Join(dir, "shop", "src", "stale")
	for i := 0; i < 2; i++ {
		if i == 1 {
			if _, err := os.Stat(marker); err != nil {
				t.Fatal(err)
			}
		}
		if _, err := svc.Up(context.Background(), gitProject(""), UpOptions{}, buf(), buf()); err != nil {
			t.Fatal(err)
		}
		if i == 0 {
			if err := os.WriteFile(marker, []byte("x"), 0o644); err != nil {
				t.Fatal(err)
			}
		}
	}
}

func buf() *bytes.Buffer { return &bytes.Buffer{} }

func TestUpRefusesWhatTheResolvedConfigurationForbids(t *testing.T) {
	r := gitRecorder()
	// The repository's file looks innocent; an include/extends/override makes the resolved service privileged.
	r.config = `{"services":{"web":{"image":"x","privileged":true},"db":{"image":"x","network_mode":"host"}},"volumes":{"d":{"external":true,"name":"d"}}}`
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	_, err := svc.Up(context.Background(), gitProject(""), UpOptions{Build: true}, buf(), buf())
	var ve *ValidationError
	if !errors.As(err, &ve) || ve.Invalid {
		t.Fatalf("err = %v", err)
	}
	for _, want := range []string{`service "web": privileged`, `service "db": host networking`, `volume "d": external`} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("missing %q in %v", want, err)
		}
	}
	if firstCall(r, "up") >= 0 || firstCall(r, "build") >= 0 {
		t.Error("something ran although the configuration was refused")
	}
}

func TestInlineProjectsAreVettedOnTheResolvedFileToo(t *testing.T) {
	r := &recorder{config: `{"services":{"a":{"image":"x","cap_add":["SYS_ADMIN"]}}}`}
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	_, err := svc.Up(context.Background(), &agentv1.ComposeProject{ProjectName: "p", ComposeFile: "include:\n  - ./more.yaml\n"}, UpOptions{}, buf(), buf())
	if err == nil || !strings.Contains(err.Error(), "SYS_ADMIN") {
		t.Fatalf("err = %v", err)
	}
}

func TestOldComposeWithoutNoEnvResolutionIsStillVetted(t *testing.T) {
	r := gitRecorder()
	r.configFlagErr = true
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	if _, err := svc.Up(context.Background(), gitProject(""), UpOptions{}, buf(), buf()); err != nil {
		t.Fatal(err)
	}
}

func TestProxyNetworkOfTheOverrideIsAllowed(t *testing.T) {
	r := gitRecorder()
	r.config = `{"services":{"web":{"image":"x","networks":{"default":null,"aethera-proxy":null}}},"networks":{"default":{"name":"shop_default"},"aethera-proxy":{"name":"aethera-proxy","external":true}}}`
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	p := gitProject("")
	p.OverrideFile = "services:\n  web:\n    networks: [default, aethera-proxy]\nnetworks:\n  aethera-proxy:\n    external: true\n    name: aethera-proxy\n"
	if _, err := svc.Up(context.Background(), p, UpOptions{}, buf(), buf()); err != nil {
		t.Fatal(err)
	}
}

func TestGitSourceValidationAndExclusivity(t *testing.T) {
	svc := &Service{Runner: gitRecorder(), Dir: t.TempDir(), Binds: denyBinds{}}
	both := gitProject("")
	both.ComposeFile = repoCompose
	neither := &agentv1.ComposeProject{ProjectName: "p"}
	pathOnly := &agentv1.ComposeProject{ProjectName: "p", ComposeFile: repoCompose, ComposePath: "x.yml"}
	for name, p := range map[string]*agentv1.ComposeProject{"both": both, "neither": neither, "path without git": pathOnly} {
		_, err := svc.Up(context.Background(), p, UpOptions{}, buf(), buf())
		var ve *ValidationError
		if !errors.As(err, &ve) || !ve.Invalid {
			t.Errorf("%s: err = %v", name, err)
		}
	}
	for _, url := range []string{"file:///etc", "/srv/repo", "ext::sh -c id", "--upload-pack=x", ""} {
		p := gitProject("")
		p.Git.Url = url
		_, err := svc.Up(context.Background(), p, UpOptions{}, buf(), buf())
		var ve *ValidationError
		if !errors.As(err, &ve) || !ve.Invalid {
			t.Errorf("url %q: err = %v", url, err)
		}
	}
	p := gitProject("")
	p.Git.Ref = "--evil"
	if _, err := svc.Up(context.Background(), p, UpOptions{}, buf(), buf()); err == nil {
		t.Error("option-like ref accepted")
	}
	for _, path := range []string{"/etc/passwd", "../x.yml", "a/../../x.yml", `..\x.yml`, `\abs.yml`, "C:/x.yml", ".git/config", "~/x"} {
		p := gitProject("")
		p.ComposePath = path
		_, err := svc.Up(context.Background(), p, UpOptions{}, buf(), buf())
		var ve *ValidationError
		if !errors.As(err, &ve) {
			t.Errorf("compose_path %q: err = %v", path, err)
		}
	}
}

func TestCloneFailureIsAGitFailure(t *testing.T) {
	r := &failingGit{recorder: gitRecorder()}
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	_, err := svc.Up(context.Background(), gitProject(""), UpOptions{}, buf(), buf())
	var f *build.Failure
	if !errors.As(err, &f) || f.Code != agentv1.ErrorCode_ERROR_CODE_GIT_FAILED {
		t.Fatalf("err = %v", err)
	}
	if _, statErr := os.Stat(filepath.Join(svc.Dir, "shop", "src")); !os.IsNotExist(statErr) {
		t.Error("half a checkout left behind")
	}
}

type failingGit struct{ *recorder }

func (f *failingGit) Run(ctx context.Context, c build.Cmd) error {
	if c.Name == "git" && c.Args[0] == "fetch" {
		return errors.New("authentication failed")
	}
	return f.recorder.Run(ctx, c)
}

func TestInspectReportsServicesPortsHashAndViolations(t *testing.T) {
	r := gitRecorder()
	r.config = `{"services":{"web":{"build":{"dockerfile":"Dockerfile"},"ports":[{"target":80,"published":"8080"},{"target":80,"published":"8081"}],"expose":["3000","4000-4002/tcp"]},"db":{"image":"postgres:17","privileged":true}}}`
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	res, err := svc.Inspect(context.Background(), gitProject(""), buf())
	if err != nil {
		t.Fatal(err)
	}
	if res.CommitSha == "" || res.ComposeSha256 == "" || len(res.ComposeSha256) != 64 {
		t.Errorf("commit/hash = %q %q", res.CommitSha, res.ComposeSha256)
	}
	if len(res.Services) != 2 || res.Services[0].Name != "db" || res.Services[0].Image != "postgres:17" || res.Services[0].HasBuild {
		t.Fatalf("services = %v", res.Services)
	}
	web := res.Services[1]
	if !web.HasBuild || web.Image != "" || len(web.Ports) != 5 || web.Ports[0] != 80 || web.Ports[1] != 3000 || web.Ports[4] != 4002 {
		t.Errorf("web = %v", web)
	}
	if len(res.Violations) != 1 || !strings.Contains(res.Violations[0], "privileged") {
		t.Errorf("violations = %v", res.Violations)
	}
	// Read-only: nothing but git and `config` ran.
	for _, c := range r.calls {
		if c.Name == "docker" && !has(c.Args, "config") {
			t.Errorf("inspect ran %v", c.Args)
		}
	}
}

func TestInspectOfAnUnreadableFileReportsInsteadOfFailing(t *testing.T) {
	r := gitRecorder()
	r.composeFile = map[string]string{"docker-compose.yml": "services: ["}
	svc := &Service{Runner: r, Dir: t.TempDir(), Binds: denyBinds{}}
	res, err := svc.Inspect(context.Background(), gitProject(""), buf())
	if err != nil || len(res.Violations) != 1 || !strings.Contains(res.Violations[0], "invalid compose file") {
		t.Fatalf("res = %v err = %v", res, err)
	}
	// Inline files can be inspected too.
	res, err = svc.Inspect(context.Background(), &agentv1.ComposeProject{ProjectName: "p", ComposeFile: "services:\n  a:\n    privileged: true\n"}, buf())
	if err != nil || len(res.Violations) == 0 {
		t.Fatalf("inline: res = %v err = %v", res, err)
	}
}

func TestComposePathPicksTheFileAndTheDefaultsKeepTheirOrder(t *testing.T) {
	root := t.TempDir()
	write := func(rel, content string) {
		p := filepath.Join(root, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	if _, _, err := findComposeFile(root, ""); err == nil {
		t.Error("an empty repository has no compose file")
	}
	for _, name := range []string{"compose.yml", "docker-compose.yaml", "compose.yaml", "docker-compose.yml"} {
		write(name, name)
		_, content, err := findComposeFile(root, "")
		if err != nil || string(content) != name {
			t.Fatalf("with %s present picked %q, %v", name, content, err)
		}
	}
	write("deploy/prod.yml", "prod")
	if p, content, err := findComposeFile(root, "deploy/./prod.yml"); err != nil || string(content) != "prod" || filepath.Base(p) != "prod.yml" {
		t.Fatalf("compose_path: %q %v", content, err)
	}
	if _, _, err := findComposeFile(root, "deploy/missing.yml"); err == nil {
		t.Error("missing compose_path accepted")
	}
}

func TestComposeFileSymlinkOutOfTheRepositoryIsRefused(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlinks need privileges on Windows")
	}
	root, outside := t.TempDir(), t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "c.yml"), []byte("services: {}"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(filepath.Join(outside, "c.yml"), filepath.Join(root, "docker-compose.yml")); err != nil {
		t.Fatal(err)
	}
	if _, _, err := findComposeFile(root, ""); err == nil {
		t.Error("symlinked compose file accepted")
	}
}

func TestProjectCommandsAreSerialized(t *testing.T) {
	svc := &Service{}
	unlock, err := svc.lock(context.Background(), "p")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	if _, err := svc.lock(ctx, "p"); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("second lock: %v", err)
	}
	if u, err := svc.lock(context.Background(), "other"); err != nil {
		t.Fatal(err)
	} else {
		u()
	}
	unlock()
	if u, err := svc.lock(context.Background(), "p"); err != nil {
		t.Fatal(err)
	} else {
		u()
	}
}

func TestRegistryCredentialsDoNotOutliveTheCommand(t *testing.T) {
	dir := t.TempDir()
	r := &recorder{}
	svc := &Service{Runner: r, Dir: dir, Binds: denyBinds{}}
	p := &agentv1.ComposeProject{ProjectName: "p", ComposeFile: "services:\n  web:\n    image: x\n", RegistryAuths: []*agentv1.RegistryAuth{
		{Server: "ghcr.io", Username: "me", Password: &agentv1.SecretValue{Value: "pw-s3cret"}},
	}}
	if _, err := svc.Up(context.Background(), p, UpOptions{}, buf(), buf()); err != nil {
		t.Fatal(err)
	}
	var cfgDir string
	for _, c := range r.calls {
		for _, e := range c.Env {
			if v, ok := strings.CutPrefix(e, "DOCKER_CONFIG="); ok {
				cfgDir = v
			}
		}
	}
	if cfgDir == "" {
		t.Fatal("DOCKER_CONFIG not set")
	}
	if _, err := os.Stat(cfgDir); !os.IsNotExist(err) {
		t.Errorf("docker config survived: %v", err)
	}
	assertNoSecretOnDisk(t, dir, "pw-s3cret")
}
