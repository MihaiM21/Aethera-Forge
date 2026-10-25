//go:build integration

package compose

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
	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

func runCmd(t *testing.T, dir string, name string, args ...string) string {
	t.Helper()
	c := exec.Command(name, args...)
	c.Dir = dir
	c.Env = append(os.Environ(), "GIT_AUTHOR_NAME=t", "GIT_AUTHOR_EMAIL=t@example.com", "GIT_COMMITTER_NAME=t", "GIT_COMMITTER_EMAIL=t@example.com")
	out, err := c.CombinedOutput()
	if err != nil {
		t.Fatalf("%s %v: %v\n%s", name, args, err, out)
	}
	return strings.TrimSpace(string(out))
}

// bareRepo creates a local bare repository holding files and returns its path and HEAD commit.
func bareRepo(t *testing.T, files map[string]string) (string, string) {
	t.Helper()
	root := t.TempDir()
	bare, work := filepath.Join(root, "repo.git"), filepath.Join(root, "work")
	runCmd(t, root, "git", "init", "-q", "--bare", "-b", "main", bare)
	runCmd(t, root, "git", "init", "-q", "-b", "main", work)
	for name, content := range files {
		p := filepath.Join(work, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	runCmd(t, work, "git", "add", "-A")
	runCmd(t, work, "git", "commit", "-q", "-m", "init")
	runCmd(t, work, "git", "push", "-q", bare, "main")
	return bare, runCmd(t, work, "git", "rev-parse", "HEAD")
}

const itCompose = `services:
  web:
    image: aethera-it-gitapp:test
    build:
      context: ./app
    labels:
      it.value: "${IT_VALUE}"
    expose: ["3000"]
  side:
    image: busybox
    command: ["sh", "-c", "sleep 300"]
`

func itService(t *testing.T) *Service {
	return &Service{Runner: build.OSRunner{}, Dir: t.TempDir(), allowLocalGit: true}
}

// The whole path against a local repository: inspect, build with a build argument, up, down.
func TestRealComposeFromGit(t *testing.T) {
	repo, head := bareRepo(t, map[string]string{
		"docker-compose.yml": itCompose,
		"app/Dockerfile":     "FROM busybox\nARG GREETING\nRUN echo \"$GREETING\" > /greeting\nCMD [\"sh\", \"-c\", \"sleep 300\"]\n",
	})
	svc := itService(t)
	value := `a$b #c 'q' "d" \e`
	project := &agentv1.ComposeProject{
		ProjectName: "aethera-it-git",
		Git:         &agentv1.GitSource{Url: repo, Ref: "main"},
		Env:         []*agentv1.EnvVar{{Name: "IT_VALUE", Value: &agentv1.EnvVar_Plain{Plain: value}}},
		BuildArgs:   []*agentv1.EnvVar{{Name: "GREETING", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "hello from git"}}}},
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()
	var out, errw bytes.Buffer
	t.Cleanup(func() {
		svc.Down(context.Background(), project, true, true, time.Second, &out, &errw)
		exec.Command("docker", "rmi", "-f", "aethera-it-gitapp:test").Run()
	})

	ins, err := svc.Inspect(ctx, project, &errw)
	if err != nil {
		t.Fatalf("inspect: %v\n%s", err, errw.String())
	}
	if ins.CommitSha != head || len(ins.Violations) != 0 || len(ins.ComposeSha256) != 64 {
		t.Fatalf("inspect = %v", ins)
	}
	if len(ins.Services) != 2 || ins.Services[0].Name != "side" || ins.Services[1].Name != "web" || !ins.Services[1].HasBuild ||
		len(ins.Services[1].Ports) != 1 || ins.Services[1].Ports[0] != 3000 {
		t.Fatalf("services = %v", ins.Services)
	}

	res, err := svc.Up(ctx, project, UpOptions{Build: true, Wait: true, WaitTimeout: time.Minute, PullPolicy: agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_MISSING}, &out, &errw)
	if err != nil {
		t.Fatalf("up: %v\n%s\n%s", err, out.String(), errw.String())
	}
	if res.CommitSha != head || len(res.Services) != 2 {
		t.Fatalf("result = %v", res)
	}
	var webID string
	for _, s := range res.Services {
		if s.State != agentv1.ContainerState_CONTAINER_STATE_RUNNING {
			t.Errorf("%s is %v", s.Service, s.State)
		}
		if s.Service == "web" {
			webID = s.ContainerId
		}
	}
	if got := runCmd(t, "", "docker", "exec", webID, "cat", "/greeting"); got != "hello from git" {
		t.Errorf("build argument not applied: %q", got)
	}
	if got := runCmd(t, "", "docker", "inspect", "-f", `{{index .Config.Labels "it.value"}}`, webID); got != value {
		t.Errorf("interpolated value = %q, want %q", got, value)
	}
	// BuildKit echoes `RUN` lines with their ARGs expanded; the agent's log writers mask the command's secret values (as
	// they do for the build engine), so the raw output is not asserted on here.
	assertNoSecretOnDisk(t, svc.Dir, "hello from git")

	if err := svc.Down(ctx, project, true, true, time.Second, &out, &errw); err != nil {
		t.Fatalf("down: %v\n%s", err, errw.String())
	}
	if _, err := os.Stat(filepath.Join(svc.Dir, "aethera-it-git", "src")); !os.IsNotExist(err) {
		t.Errorf("checkout survived down: %v", err)
	}
	after, err := svc.Ps(ctx, project, true, &errw)
	if err != nil || len(after.Services) != 0 {
		t.Fatalf("after down: %v %v", after, err)
	}
}

// docker compose config needs no daemon: it proves the quoting of the interpolation file against the real parser.
func TestRealEnvFileQuoting(t *testing.T) {
	values := []string{"plain", "a$b", "${X}", "$$", "x #not a comment", `say "hi"`, `back\slash`, "it's", `it's $5 "q" \`, "two\nlines", "it's\nnew", "", " padded "}
	var doc strings.Builder
	doc.WriteString("services:\n  a:\n    image: x\n    environment:\n")
	var vars []*agentv1.EnvVar
	for i, v := range values {
		n := "V" + string(rune('A'+i))
		vars = append(vars, &agentv1.EnvVar{Name: n, Value: &agentv1.EnvVar_Plain{Plain: v}})
		doc.WriteString("      " + n + ": \"${" + n + "}\"\n")
	}
	dir := t.TempDir()
	file, err := dotenvFile(vars)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "env"), []byte(file), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "compose.yaml"), []byte(doc.String()), 0o600); err != nil {
		t.Fatal(err)
	}
	cfgOut := runCmd(t, dir, "docker", "compose", "-p", "t", "--env-file", "env", "-f", "compose.yaml", "config", "--format", "json")
	cfg, err := parseResolved([]byte(cfgOut))
	if err != nil {
		t.Fatal(err)
	}
	got, _ := cfg.Services["a"]["environment"].(map[string]any)
	for i, v := range values {
		n := "V" + string(rune('A'+i))
		if got[n] != strings.ReplaceAll(v, "$", "$$") { // config prints a compose file, in which a literal $ is $$
			t.Errorf("%s: %q read back as %q", n, v, got[n])
		}
	}
}

// A repository that asks for host power is reported by inspect and refused by up, before anything runs.
func TestRealComposeFromGitIsPolicyChecked(t *testing.T) {
	repo, _ := bareRepo(t, map[string]string{"compose.yaml": "services:\n  a:\n    image: busybox\n    privileged: true\n    network_mode: host\n  b:\n    image: busybox\n    volumes:\n      - /var/run/docker.sock:/s\nvolumes: {}\n"})
	svc := itService(t)
	project := &agentv1.ComposeProject{ProjectName: "aethera-it-bad", Git: &agentv1.GitSource{Url: repo, Ref: "main"}}
	var errw bytes.Buffer
	ins, err := svc.Inspect(context.Background(), project, &errw)
	if err != nil || len(ins.Violations) < 3 {
		t.Fatalf("inspect = %v, %v", ins, err)
	}
	_, err = svc.Up(context.Background(), project, UpOptions{}, &errw, &errw)
	var ve *ValidationError
	if err == nil || !errorsAs(err, &ve) {
		t.Fatalf("up = %v", err)
	}
}

func errorsAs(err error, target **ValidationError) bool {
	for err != nil {
		if v, ok := err.(*ValidationError); ok {
			*target = v
			return true
		}
		u, ok := err.(interface{ Unwrap() error })
		if !ok {
			return false
		}
		err = u.Unwrap()
	}
	return false
}
