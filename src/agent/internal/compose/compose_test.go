package compose

import (
	"bytes"
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

type denyBinds struct{}

func (denyBinds) CheckBindSource(src string) error { return errors.New("denied " + src) }

type recorder struct{ calls []build.Cmd }

func (r *recorder) Run(_ context.Context, c build.Cmd) error {
	r.calls = append(r.calls, c)
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
	up := strings.Join(r.calls[0].Args, " ")
	for _, want := range []string{"compose -p shop", "up -d", "--pull always", "--remove-orphans", "--wait --wait-timeout 90", "compose.aethera.yaml", "--env-file"} {
		if !strings.Contains(up, want) {
			t.Errorf("missing %q in %s", want, up)
		}
	}
	if strings.Contains(up, "s3cret") {
		t.Error("secret on command line")
	}
	env, err := os.ReadFile(filepath.Join(dir, "shop", ".env"))
	if err != nil || string(env) != "PW=s3cret\n" {
		t.Fatalf("env file = %q, %v", env, err)
	}
	if got, _ := os.ReadFile(filepath.Join(dir, "shop", "compose.yaml")); string(got) != p.ComposeFile {
		t.Error("user compose file must be stored verbatim")
	}
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
