package docker

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"strings"
	"syscall"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

func TestStripInspectEnvRemovesValuesKeepsNames(t *testing.T) {
	raw := []byte(`{"Id":"abc","Config":{"Image":"x","Env":["PATH=/usr/bin","DB_PASSWORD=hunter2","EMPTY=","NOEQ"],"Cmd":["run"]},"State":{"Running":true}}`)
	out, err := StripInspectEnv(raw)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(out), "hunter2") || strings.Contains(string(out), "/usr/bin") {
		t.Fatalf("env values leaked: %s", out)
	}
	var doc struct {
		Config struct {
			Env []string
			Cmd []string
		}
		State struct{ Running bool }
		Id    string
	}
	if err := json.Unmarshal(out, &doc); err != nil {
		t.Fatal(err)
	}
	want := []string{"PATH=", "DB_PASSWORD=", "EMPTY=", "NOEQ"}
	if fmt.Sprint(doc.Config.Env) != fmt.Sprint(want) {
		t.Fatalf("env = %v, want %v", doc.Config.Env, want)
	}
	if doc.Id != "abc" || !doc.State.Running || len(doc.Config.Cmd) != 1 {
		t.Fatalf("other fields damaged: %s", out)
	}
}

func TestStripInspectEnvWithoutConfigAndInvalidJSON(t *testing.T) {
	if _, err := StripInspectEnv([]byte(`{"Id":"x"}`)); err != nil {
		t.Fatal(err)
	}
	if _, err := StripInspectEnv([]byte(`not json`)); err == nil {
		t.Fatal("invalid JSON accepted")
	}
}

func TestClassifyDaemonError(t *testing.T) {
	cases := []struct {
		err       error
		installed bool
		want      agentv1.DockerStatus
	}{
		{nil, true, agentv1.DockerStatus_DOCKER_STATUS_RUNNING},
		{fmt.Errorf("dial unix /var/run/docker.sock: connect: permission denied"), true, agentv1.DockerStatus_DOCKER_STATUS_PERMISSION_DENIED},
		{fmt.Errorf("x: %w", os.ErrPermission), true, agentv1.DockerStatus_DOCKER_STATUS_PERMISSION_DENIED},
		{fmt.Errorf("dial unix /var/run/docker.sock: connect: no such file or directory"), true, agentv1.DockerStatus_DOCKER_STATUS_STOPPED},
		{fmt.Errorf("dial unix /var/run/docker.sock: connect: no such file or directory"), false, agentv1.DockerStatus_DOCKER_STATUS_NOT_INSTALLED},
		{fmt.Errorf("x: %w", syscall.ECONNREFUSED), true, agentv1.DockerStatus_DOCKER_STATUS_STOPPED},
		{context.DeadlineExceeded, true, agentv1.DockerStatus_DOCKER_STATUS_UNREACHABLE},
		{errors.New("something odd"), true, agentv1.DockerStatus_DOCKER_STATUS_UNREACHABLE},
	}
	for i, c := range cases {
		if got := ClassifyDaemonError(c.err, c.installed); got != c.want {
			t.Errorf("case %d (%v): got %v want %v", i, c.err, got, c.want)
		}
	}
}

func TestErrorCodeMapping(t *testing.T) {
	cases := map[error]agentv1.ErrorCode{
		fmt.Errorf("%w: x", ErrNotFound):     agentv1.ErrorCode_ERROR_CODE_NOT_FOUND,
		fmt.Errorf("%w: x", ErrConflict):     agentv1.ErrorCode_ERROR_CODE_CONFLICT,
		fmt.Errorf("%w: x", ErrUnavailable):  agentv1.ErrorCode_ERROR_CODE_DOCKER_UNAVAILABLE,
		fmt.Errorf("%w: x", ErrAuth):         agentv1.ErrorCode_ERROR_CODE_REGISTRY_AUTH_FAILED,
		fmt.Errorf("%w: x", ErrPortInUse):    agentv1.ErrorCode_ERROR_CODE_PORT_CONFLICT,
		fmt.Errorf("%w: x", ErrPullFailed):   agentv1.ErrorCode_ERROR_CODE_IMAGE_PULL_FAILED,
		fmt.Errorf("%w: x", ErrAlreadyExist): agentv1.ErrorCode_ERROR_CODE_ALREADY_EXISTS,
		context.DeadlineExceeded:             agentv1.ErrorCode_ERROR_CODE_TIMEOUT,
		context.Canceled:                     agentv1.ErrorCode_ERROR_CODE_CANCELLED,
		errors.New("boom"):                   agentv1.ErrorCode_ERROR_CODE_INTERNAL,
	}
	for err, want := range cases {
		if got := ErrorCode(err); got != want {
			t.Errorf("%v: got %v want %v", err, got, want)
		}
	}
}

func TestHealthFromStatusAndStates(t *testing.T) {
	if healthFromStatus("Up 3 hours (healthy)") != agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY ||
		healthFromStatus("Up 3 hours (unhealthy)") != agentv1.ContainerHealth_CONTAINER_HEALTH_UNHEALTHY ||
		healthFromStatus("Up 2 seconds (health: starting)") != agentv1.ContainerHealth_CONTAINER_HEALTH_STARTING ||
		healthFromStatus("Up 1 hour") != agentv1.ContainerHealth_CONTAINER_HEALTH_NONE {
		t.Fatal("health parsing wrong")
	}
	if parseState("exited") != agentv1.ContainerState_CONTAINER_STATE_EXITED || parseState("weird") != agentv1.ContainerState_CONTAINER_STATE_UNSPECIFIED {
		t.Fatal("state parsing wrong")
	}
}
