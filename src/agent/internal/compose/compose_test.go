package compose

import (
	"bytes"
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

type denyBinds struct{}

func (denyBinds) CheckBindSource(src string) error { return errors.New("denied " + src) }

// recorder is a fake runner: it records every command, answers `ps` and `config`, and plays git (a checkout writes
// composeFile into the working directory).
type recorder struct {
	calls []build.Cmd
	// config is the output of `docker compose config`; the default is one nginx service.
	config string
	// configFlagErr makes `config --no-env-resolution` fail as an old compose would.
	configFlagErr bool
	// composeFile (name -> content) is written into the checkout by `git checkout`.
	composeFile map[string]string
	sha         string
	failBuild   bool
	// envSeen is the --env-file content of the latest command that had one.
	envSeen string
}

func (r *recorder) Run(_ context.Context, c build.Cmd) error {
	r.calls = append(r.calls, c)
	if f := argAfter(c.Args, "--env-file"); f != "" {
		b, _ := os.ReadFile(f)
		r.envSeen = string(b)
	}
	if c.Name == "git" && len(c.Args) > 0 {
		switch c.Args[0] {
		case "checkout":
			for name, content := range r.composeFile {
				if err := os.WriteFile(filepath.Join(c.Dir, name), []byte(content), 0o644); err != nil {
					return err
				}
			}
		case "rev-parse":
			sha := r.sha
			if sha == "" {
				sha = "0123456789abcdef0123456789abcdef01234567"
			}
			c.Stdout.Write([]byte(sha + "\n"))
		}
		return nil
	}
	if has(c.Args, "config") {
		if r.configFlagErr && has(c.Args, "--no-env-resolution") {
			c.Stderr.Write([]byte("unknown flag: --no-env-resolution"))
			return &exec.ExitError{}
		}
		cfg := r.config
		if cfg == "" {
			cfg = `{"services":{"web":{"image":"nginx"}}}`
		}
		c.Stdout.Write([]byte(cfg))
		return nil
	}
	if r.failBuild && has(c.Args, "build") {
		return errors.New("boom")
	}
	if len(c.Args) > 0 && c.Stdout != nil {
		for _, a := range c.Args {
			if a == "ps" {
				c.Stdout.Write([]byte(`{"ID":"c1","Name":"p-web-1","Image":"nginx","Service":"web","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}` + "\n" +
					`{"ID":"c2","Name":"p-db-1","Image":"pg","Service":"db","State":"exited","Health":"","ExitCode":1,"Publishers":[]}` + "\n"))
			}
		}
	}
	return nil
}

func has(args []string, s string) bool {
	for _, a := range args {
		if a == s {
			return true
		}
	}
	return false
}

func TestValidateRejectsHostPower(t *testing.T) {
	bad := map[string]string{
		"privileged":  "services:\n  a:\n    image: x\n    privileged: true\n",
		"host net":    "services:\n  a:\n    image: x\n    network_mode: host\n",
		"host pid":    "services:\n  a:\n    image: x\n    pid: host\n",
		"devices":     "services:\n  a:\n    image: x\n    devices: [\"/dev/sda:/dev/sda\"]\n",
		"cap":         "services:\n  a:\n    image: x\n    cap_add: [SYS_ADMIN]\n",
		"unconfined":  "services:\n  a:\n    image: x\n    security_opt: [\"seccomp=unconfined\"]\n",
		"socket":      "services:\n  a:\n    image: x\n    volumes: [\"/var/run/docker.sock:/var/run/docker.sock\"]\n",
		"long socket": "services:\n  a:\n    image: x\n    volumes:\n      - type: bind\n        source: /var/run/docker.sock\n        target: /s\n",
		"escape":      "services:\n  a:\n    image: x\n    volumes: [\"../../etc:/etc\"]\n",
		"abs build":   "services:\n  a:\n    build: /etc\n",
		"abs context": "services:\n  a:\n    build:\n      context: /home/user\n",
		"up build":    "services:\n  a:\n    build:\n      context: ../other\n",
		"env abs":     "services:\n  a:\n    image: x\n    env_file: /etc/environment\n",
		"env list":    "services:\n  a:\n    image: x\n    env_file:\n      - ./ok.env\n      - path: ../../secrets.env\n",
		"secret file": "services:\n  a:\n    image: x\nsecrets:\n  s:\n    file: /etc/shadow\n",
	}
	for name, doc := range bad {
		if err := Validate(doc, denyBinds{}); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if err := Validate("services:\n  a:\n    image: x\n    volumes: [\"data:/d\", \"./conf:/c\"]\n", denyBinds{}); err != nil {
		t.Errorf("named and project-relative volumes rejected: %v", err)
	}
	if err := Validate("services:\n  a:\n    build: ./app\n    env_file: [.env, ./conf/a.env]\n", denyBinds{}); err != nil {
		t.Errorf("project-local build and env files rejected: %v", err)
	}
	if err := Validate("services: [", nil); err == nil {
		t.Error("invalid yaml accepted")
	}
}

func TestParsePsHandlesNDJSONAndArray(t *testing.T) {
	arr := []byte(`[{"ID":"a","Service":"web","State":"running","Health":"unhealthy"}]`)
	got := ParsePs(arr)
	if len(got) != 1 || got[0].Health != agentv1.ContainerHealth_CONTAINER_HEALTH_UNHEALTHY {
		t.Fatalf("array: %v", got)
	}
}

func TestUpWritesFilesAndRunsCompose(t *testing.T) {
	dir := t.TempDir()
	r := &recorder{}
	svc := &Service{Runner: r, Dir: dir, Binds: denyBinds{}}
	p := &agentv1.ComposeProject{
		ProjectName: "shop", ComposeFile: "services:\n  web:\n    image: nginx\n", OverrideFile: "services:\n  web:\n    labels:\n      a: b\n",
		Env: []*agentv1.EnvVar{{Name: "PW", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "s3cret"}}}},
	}
	res, err := svc.Up(context.Background(), p, UpOptions{
		Services: []string{"web"}, PullPolicy: agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_ALWAYS, RemoveOrphans: true, Wait: true, WaitTimeout: 90 * time.Second,
	}, &bytes.Buffer{}, &bytes.Buffer{})
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Services) != 2 || res.Services[0].Service != "db" || res.Services[1].Ports[0].HostPort != 8080 {
		t.Fatalf("services = %v", res.Services)
	}
	up := strings.Join(r.calls[1].Args, " ") // the first call resolves the configuration
	for _, want := range []string{"compose -p shop --project-directory " + filepath.Join(dir, "shop"), "up -d", "--pull always", "--remove-orphans", "--wait --wait-timeout 90", "compose.aethera.yaml", "--env-file"} {
		if !strings.Contains(up, want) {
			t.Errorf("missing %q in %s", want, up)
		}
	}
	if strings.Contains(up, "s3cret") {
		t.Error("secret on command line")
	}
	// The interpolation file only exists while the command runs, and nothing with a value is left in the project directory.
	if r.envSeen != "PW='s3cret'\n" {
		t.Fatalf("env file during the run = %q", r.envSeen)
	}
	if f := argAfter(r.calls[1].Args, "--env-file"); f == "" {
		t.Fatal("no --env-file")
	} else if _, err := os.Stat(f); !os.IsNotExist(err) {
		t.Errorf("env file still there: %v", err)
	}
	assertNoSecretOnDisk(t, dir, "s3cret")
	if got, _ := os.ReadFile(filepath.Join(dir, "shop", "compose.yaml")); string(got) != p.ComposeFile {
		t.Error("user compose file must be stored verbatim")
	}
}

func argAfter(args []string, flag string) string {
	for i, a := range args {
		if a == flag && i+1 < len(args) {
			return args[i+1]
		}
	}
	return ""
}

// assertNoSecretOnDisk fails when any file below dir contains the secret.
func assertNoSecretOnDisk(t *testing.T, dir, secret string) {
	t.Helper()
	_ = filepath.Walk(dir, func(p string, fi os.FileInfo, err error) error {
		if err != nil || fi.IsDir() {
			return nil
		}
		if b, _ := os.ReadFile(p); strings.Contains(string(b), secret) {
			t.Errorf("%s still holds the secret", p)
		}
		return nil
	})
}

func TestPrepareRejectsBadInput(t *testing.T) {
	svc := &Service{Runner: &recorder{}, Dir: t.TempDir()}
	for _, p := range []*agentv1.ComposeProject{
		{ProjectName: "../x", ComposeFile: "services: {}"},
		{ProjectName: "ok", ComposeFile: ""},
		{ProjectName: "ok", ComposeFile: "services:\n  a:\n    privileged: true\n"},
		{ProjectName: "ok", ComposeFile: "services: {}", Env: []*agentv1.EnvVar{{Name: "A B", Value: &agentv1.EnvVar_Plain{Plain: "x"}}}},
	} {
		_, err := svc.Ps(context.Background(), p, true, &bytes.Buffer{})
		var ve *ValidationError
		if !errors.As(err, &ve) {
			t.Errorf("%v: err = %v", p.ProjectName, err)
		}
	}
}
