package build

import (
	"bytes"
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
)

// recorder is a Runner that records invocations; hooks may fake side effects.
type recorder struct {
	calls []Cmd
	hook  func(c Cmd) error
}

func (r *recorder) Run(_ context.Context, c Cmd) error {
	r.calls = append(r.calls, c)
	if r.hook != nil {
		return r.hook(c)
	}
	return nil
}

func (r *recorder) find(name string, first string) *Cmd {
	for i := range r.calls {
		if r.calls[i].Name == name && len(r.calls[i].Args) > 0 && r.calls[i].Args[0] == first {
			return &r.calls[i]
		}
	}
	return nil
}

func write(t *testing.T, dir, name, content string) {
	t.Helper()
	p := filepath.Join(dir, name)
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestDetectDockerfileFirst(t *testing.T) {
	d := t.TempDir()
	write(t, d, "Dockerfile", "FROM scratch\nEXPOSE 8080/tcp 9090\n")
	write(t, d, "package.json", `{"scripts":{"build":"x"}}`)
	got := Detect(d)
	if len(got) < 2 || got[0].Engine != agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE {
		t.Fatalf("candidates = %v", got)
	}
	if p := got[0].SuggestedPorts; len(p) != 2 || p[0] != 8080 || p[1] != 9090 {
		t.Fatalf("ports = %v", p)
	}
}

func TestDetectViteIsStaticAndNextIsNot(t *testing.T) {
	d := t.TempDir()
	write(t, d, "package.json", `{"scripts":{"build":"vite build"},"devDependencies":{"vite":"5"}}`)
	write(t, d, "pnpm-lock.yaml", "")
	got := Detect(d)
	if got[0].Engine != agentv1.BuildEngine_BUILD_ENGINE_STATIC || got[0].OutputDir != "dist" ||
		got[0].BuildCommand != "pnpm run build" {
		t.Fatalf("vite: %v", got[0])
	}

	n := t.TempDir()
	write(t, n, "package.json", `{"scripts":{"build":"next build","start":"next start"},"dependencies":{"next":"15"}}`)
	got = Detect(n)
	if got[0].Engine != agentv1.BuildEngine_BUILD_ENGINE_NIXPACKS || got[0].StartCommand != "npm run start" {
		t.Fatalf("next: %v", got[0])
	}
}

func TestDetectOtherLanguagesAndEmpty(t *testing.T) {
	d := t.TempDir()
	if len(Detect(d)) != 0 {
		t.Fatal("empty dir should have no candidates")
	}
	write(t, d, "go.mod", "module x")
	if got := Detect(d); len(got) != 1 || got[0].Language != "go" {
		t.Fatalf("go: %v", got)
	}
}

func TestValidateGitURL(t *testing.T) {
	for _, ok := range []string{"https://github.com/a/b.git", "git@github.com:a/b.git", "ssh://git@host/a/b"} {
		if err := ValidateGitURL(ok); err != nil {
			t.Errorf("%q rejected: %v", ok, err)
		}
	}
	for _, bad := range []string{"", "--upload-pack=x", "file:///etc", "ext::sh -c id", "/local/path", "-oProxyCommand=x"} {
		if err := ValidateGitURL(bad); err == nil {
			t.Errorf("%q accepted", bad)
		}
	}
}

func TestContextDirRejectsEscapes(t *testing.T) {
	root := t.TempDir()
	for _, bad := range []string{"../x", "a/../../x", "/etc"} {
		if _, err := ContextDir(root, bad); err == nil {
			t.Errorf("%q accepted", bad)
		}
	}
	if p, err := ContextDir(root, "apps/web"); err != nil || !strings.HasSuffix(filepath.ToSlash(p), "apps/web") {
		t.Fatalf("p=%q err=%v", p, err)
	}
}

func TestCloneUsesEnvForTokenNotArgs(t *testing.T) {
	r := &recorder{hook: func(c Cmd) error {
		if len(c.Args) > 0 && c.Args[0] == "rev-parse" {
			c.Stdout.Write([]byte("abc123\n"))
		}
		return nil
	}}
	src := &agentv1.GitSource{
		Url: "https://github.com/a/b.git", Ref: "main", Depth: 1,
		Credentials: &agentv1.GitCredentials{Material: &agentv1.GitCredentials_HttpsToken{HttpsToken: &agentv1.HttpsTokenCredential{
			Username: "bot", Token: &agentv1.SecretValue{Value: "s3cret-token"},
		}}},
	}
	co, err := Clone(context.Background(), r, src, t.TempDir(), &bytes.Buffer{})
	if err != nil || co.Commit != "abc123" {
		t.Fatalf("co=%v err=%v", co, err)
	}
	for _, c := range r.calls {
		if strings.Contains(strings.Join(c.Args, " "), "s3cret-token") {
			t.Fatalf("token on command line: %v", c.Args)
		}
	}
	fetch := r.find("git", "fetch")
	if fetch == nil || !strings.Contains(strings.Join(fetch.Args, " "), "--depth=1 origin main") {
		t.Fatalf("fetch = %v", fetch)
	}
	if !strings.Contains(strings.Join(fetch.Env, " "), "http.extraHeader") {
		t.Fatalf("env = %v", fetch.Env)
	}
}

func TestCloneRejectsBadInput(t *testing.T) {
	r := &recorder{}
	for _, src := range []*agentv1.GitSource{
		{Url: "ext::sh -c id"}, {Url: "https://h/x", Ref: "--upload-pack=x"}, {Url: "https://h/x", Commit: "not-a-sha!"},
	} {
		if _, err := Clone(context.Background(), r, src, t.TempDir(), &bytes.Buffer{}); err == nil {
			t.Errorf("%v accepted", src)
		}
	}
	if len(r.calls) != 0 {
		t.Fatal("git must not run for invalid input")
	}
}

func TestStaticDockerfile(t *testing.T) {
	df, err := StaticDockerfile(&agentv1.BuildRequest{
		InstallCommand: "npm ci", BuildCommand: "npm run build", OutputDir: "dist", SpaFallback: true,
		Env: []*agentv1.EnvVar{{Name: "API_URL", Value: &agentv1.EnvVar_Plain{Plain: "x"}}},
	})
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"FROM node:22-alpine AS build", "RUN npm ci", "RUN npm run build", "COPY --from=build /app/dist", "try_files", "ARG API_URL"} {
		if !strings.Contains(df, want) {
			t.Errorf("missing %q in:\n%s", want, df)
		}
	}
	if _, err := StaticDockerfile(&agentv1.BuildRequest{OutputDir: "../etc"}); err == nil {
		t.Error("output dir escape accepted")
	}
}

func newService(r Runner, fake *dockertest.Fake) *Service {
	return &Service{Runner: r, Docker: fake, Engines: DefaultEngines(), WorkDir: filepath.Join(os.TempDir(), "aethera-build-test")}
}

func progressNoop(string, string, int32) {}

func TestRunDockerfileBuildKeepsSecretsOutOfArgs(t *testing.T) {
	fake := dockertest.New()
	r := &recorder{hook: func(c Cmd) error {
		switch {
		case c.Name == "git" && c.Args[0] == "rev-parse":
			c.Stdout.Write([]byte("deadbeef\n"))
		case c.Name == "docker" && c.Args[0] == "build":
			// the daemon now has the image
			fake.Images["sha256:img"] = &agentv1.ImageInfo{Id: "sha256:img", RepoTags: []string{"aethera/app:1"}, SizeBytes: 42, Os: "linux", Architecture: "amd64"}
		}
		return nil
	}}
	req := &agentv1.BuildRequest{
		BuildId: "b1", Engine: agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE, ImageTags: []string{"aethera/app:1"},
		Git:       &agentv1.GitSource{Url: "https://github.com/a/b.git", Ref: "main"},
		BuildArgs: []*agentv1.EnvVar{{Name: "TOKEN", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "hunter2"}}}},
		Secrets:   []*agentv1.BuildSecret{{Id: "npmrc", Value: &agentv1.SecretValue{Value: "//registry:_authToken=zzz"}}},
		Cache:     &agentv1.BuildCacheSettings{Enabled: true, CacheFrom: []string{"type=registry,ref=x"}},
	}
	res, err := newService(r, fake).Run(context.Background(), req, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop)
	if err != nil {
		t.Fatal(err)
	}
	if res.ImageId != "sha256:img" || res.CommitSha != "deadbeef" || res.Platform != "linux/amd64" || res.EngineUsed != "dockerfile" {
		t.Fatalf("result = %v", res)
	}
	b := r.find("docker", "build")
	joined := strings.Join(b.Args, " ")
	if strings.Contains(joined, "hunter2") || strings.Contains(joined, "authToken") {
		t.Fatalf("secret in args: %s", joined)
	}
	for _, want := range []string{"--build-arg TOKEN", "--secret id=npmrc,src=", "--cache-from type=registry,ref=x", "-t aethera/app:1"} {
		if !strings.Contains(joined, want) {
			t.Errorf("missing %q in %s", want, joined)
		}
	}
	if !strings.Contains(strings.Join(b.Env, " "), "TOKEN=hunter2") {
		t.Error("build arg value should travel in the environment")
	}
}

func TestRunImageEnginePullsAndTags(t *testing.T) {
	fake := dockertest.New()
	r := &recorder{hook: func(c Cmd) error {
		if c.Args[0] == "tag" { // the real daemon would add the tag to the pulled image
			for _, im := range fake.Images {
				im.RepoTags = append(im.RepoTags, c.Args[len(c.Args)-1])
			}
		}
		return nil
	}}
	req := &agentv1.BuildRequest{
		Engine: agentv1.BuildEngine_BUILD_ENGINE_IMAGE, ImageReference: "nginx:latest", ImageTags: []string{"aethera/web:7"},
	}
	res, err := newService(r, fake).Run(context.Background(), req, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop)
	if err != nil {
		t.Fatal(err)
	}
	if tag := r.find("docker", "tag"); tag == nil {
		t.Fatal("expected docker tag")
	}
	if res.EngineUsed != "image" || res.ImageId == "" {
		t.Fatalf("result = %v", res)
	}
}

func TestRunValidationAndFailures(t *testing.T) {
	fake := dockertest.New()
	svc := newService(&recorder{}, fake)
	ctx := context.Background()
	code := func(err error) agentv1.ErrorCode {
		var f *Failure
		if !errors.As(err, &f) {
			t.Fatalf("not a Failure: %v", err)
		}
		return f.Code
	}

	_, err := svc.Run(ctx, &agentv1.BuildRequest{Engine: agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE}, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop)
	if code(err) != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
		t.Errorf("no tags: %v", err)
	}
	_, err = svc.Run(ctx, &agentv1.BuildRequest{EngineName: "railpack", ImageTags: []string{"a:b"}}, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop)
	if code(err) != agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED {
		t.Errorf("unknown engine: %v", err)
	}
	_, err = svc.Run(ctx, &agentv1.BuildRequest{
		Engine: agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE, ImageTags: []string{"a:b"}, TargetPlatforms: []string{"linux/amd64", "linux/arm64"},
	}, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop)
	if code(err) != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
		t.Errorf("multi-arch without push: %v", err)
	}

	failing := &recorder{hook: func(c Cmd) error {
		if c.Name == "docker" {
			return errors.New("exit 1")
		}
		if c.Args[0] == "rev-parse" {
			c.Stdout.Write([]byte("abc\n"))
		}
		return nil
	}}
	_, err = newService(failing, fake).Run(ctx, &agentv1.BuildRequest{
		Engine: agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE, ImageTags: []string{"a:b"}, Git: &agentv1.GitSource{Url: "https://h.io/x"},
	}, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop)
	if code(err) != agentv1.ErrorCode_ERROR_CODE_BUILD_FAILED {
		t.Errorf("failed build: %v", err)
	}
}

func TestDetectService(t *testing.T) {
	r := &recorder{hook: func(c Cmd) error {
		switch c.Args[0] {
		case "fetch":
			write(t, c.Dir, "go.mod", "module x")
		case "rev-parse":
			c.Stdout.Write([]byte("c0ffee\n"))
		}
		return nil
	}}
	res, err := newService(r, dockertest.New()).Detect(context.Background(),
		&agentv1.BuildDetect{Git: &agentv1.GitSource{Url: "https://h.io/x"}}, &bytes.Buffer{})
	if err != nil || res.CommitSha != "c0ffee" || len(res.Candidates) != 1 {
		t.Fatalf("res=%v err=%v", res, err)
	}
}
