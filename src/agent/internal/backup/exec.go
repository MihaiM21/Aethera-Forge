package backup

import (
	"context"
	"fmt"
	"io"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
)

// ExecRequest is one program to run inside a workload container.
type ExecRequest struct {
	Container  string
	Argv       []string
	Env        []*agentv1.EnvVar
	User       string
	WorkingDir string
	Stdin      io.Reader
	Stdout     io.Writer
	Stderr     io.Writer
}

// Executor runs programs in the containers Aethera manages (ADR 0010): the
// container must exist and run (NOT_FOUND / CONFLICT) and pass the policy
// (managed workload, not the proxy, not the agent itself).
type Executor struct {
	Docker docker.API
	Policy *policy.Policy
}

// Run executes the request and returns the process's exit code. It returns an
// error only when the program could not run to completion.
func (e *Executor) Run(ctx context.Context, req ExecRequest) (int, error) {
	info, err := e.Docker.ContainerInspect(ctx, req.Container, false)
	if err != nil {
		return 0, err
	}
	if info.GetState() != agentv1.ContainerState_CONTAINER_STATE_RUNNING {
		return 0, fmt.Errorf("%w: container %q is not running", docker.ErrConflict, req.Container)
	}
	if err := e.Policy.CheckExecTarget(info); err != nil {
		return 0, err
	}
	// Address the container by id from here on: a name could be re-pointed.
	return e.Docker.ContainerExec(ctx, docker.ExecSpec{
		Container: info.GetId(), Argv: req.Argv, Env: envStrings(req.Env), User: req.User, WorkingDir: req.WorkingDir,
		Stdin: req.Stdin, Stdout: req.Stdout, Stderr: req.Stderr,
	})
}
