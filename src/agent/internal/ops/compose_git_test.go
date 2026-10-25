package ops

import (
	"errors"
	"strings"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
	"github.com/mihaim21/aethera-forge/agent/internal/compose"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
)

func TestComposeGitCapabilityNeedsGit(t *testing.T) {
	d, _ := newDeps()
	d.Compose = &compose.Service{}
	d.LookPath = func(string) (string, error) { return "", errors.New("not installed") }
	if got := strings.Join(d.Capabilities(), " "); strings.Contains(got, CapComposeGit) || !strings.Contains(got, CapCompose) {
		t.Errorf("without git: %s", got)
	}
	d.LookPath = func(f string) (string, error) { return "/usr/bin/" + f, nil }
	if got := strings.Join(d.Capabilities(), " "); !strings.Contains(got, "compose.git") {
		t.Errorf("with git: %s", got)
	}
	// Without a compose subsystem there is no compose.git either.
	d.Compose = nil
	if got := strings.Join(d.Capabilities(), " "); strings.Contains(got, "compose.git") {
		t.Errorf("without compose: %s", got)
	}
}

func TestComposeGitProjectOnAnAgentWithoutGitIsUnsupported(t *testing.T) {
	d, _ := newDeps()
	d.LookPath = func(string) (string, error) { return "", errors.New("not installed") }
	err := d.needGit(&agentv1.ComposeProject{Git: &agentv1.GitSource{Url: "https://example.com/a.git"}})
	var de *dispatch.Error
	if !errors.As(err, &de) || de.Code != agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED {
		t.Fatalf("err = %v", err)
	}
	if err := d.needGit(&agentv1.ComposeProject{ComposeFile: "services: {}"}); err != nil {
		t.Errorf("inline project refused: %v", err)
	}
}

func TestComposeErrorCodes(t *testing.T) {
	code := func(err error) agentv1.ErrorCode {
		var de *dispatch.Error
		if !errors.As(composeError(err), &de) {
			t.Fatalf("not a dispatch error: %v", err)
		}
		return de.Code
	}
	if c := code(&compose.ValidationError{Err: errors.New("x"), Invalid: true}); c != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
		t.Errorf("invalid = %v", c)
	}
	if c := code(&compose.ValidationError{Err: errors.New("x")}); c != agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION {
		t.Errorf("policy = %v", c)
	}
	if c := code(&build.Failure{Code: agentv1.ErrorCode_ERROR_CODE_GIT_FAILED, Err: errors.New("x")}); c != agentv1.ErrorCode_ERROR_CODE_GIT_FAILED {
		t.Errorf("git = %v", c)
	}
	if c := code(errors.New("compose up: exit 1")); c != agentv1.ErrorCode_ERROR_CODE_BUILD_FAILED {
		t.Errorf("runtime = %v", c)
	}
}
