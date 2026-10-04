//go:build integration

package compose

import (
	"bytes"
	"context"
	"os/exec"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

// Runs a real compose project: up (with the Aethera override adding a label), ps, down.
func TestRealComposeUpPsDown(t *testing.T) {
	svc := &Service{Runner: build.OSRunner{}, Dir: t.TempDir()}
	project := &agentv1.ComposeProject{
		ProjectName:  "aethera-it-compose",
		ComposeFile:  "services:\n  web:\n    image: busybox\n    command: [\"sh\", \"-c\", \"echo $GREETING; sleep 300\"]\n    environment:\n      GREETING: ${GREETING}\n",
		OverrideFile: "services:\n  web:\n    labels:\n      aethera.managed: \"true\"\n",
		Env:          []*agentv1.EnvVar{{Name: "GREETING", Value: &agentv1.EnvVar_Plain{Plain: "hi"}}},
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	var out, errw bytes.Buffer
	t.Cleanup(func() { svc.Down(context.Background(), project, true, true, 1*time.Second, &out, &errw) })

	res, err := svc.Up(ctx, project, UpOptions{Wait: true, WaitTimeout: time.Minute, PullPolicy: agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_MISSING}, &out, &errw)
	if err != nil {
		t.Fatalf("up: %v\n%s\n%s", err, out.String(), errw.String())
	}
	if len(res.Services) != 1 || res.Services[0].Service != "web" || res.Services[0].State != agentv1.ContainerState_CONTAINER_STATE_RUNNING {
		t.Fatalf("services = %v", res.Services)
	}
	label, err := exec.Command("docker", "inspect", "-f", "{{index .Config.Labels \"aethera.managed\"}}", res.Services[0].ContainerId).Output()
	if err != nil || string(bytes.TrimSpace(label)) != "true" {
		t.Fatalf("override label missing: %q %v", label, err)
	}
	if err := svc.Down(ctx, project, true, true, time.Second, &out, &errw); err != nil {
		t.Fatalf("down: %v\n%s", err, errw.String())
	}
	after, err := svc.Ps(ctx, project, true, &errw)
	if err != nil || len(after.Services) != 0 {
		t.Fatalf("after down: %v %v", after, err)
	}
}
