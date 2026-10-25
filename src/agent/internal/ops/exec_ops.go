package ops

import (
	"context"
	"errors"
	"io"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/backup"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
)

// Capabilities of the R1 subsystems (ADR 0010). Like the Phase 3 ones they are
// advertised only when the subsystem is wired.
const (
	CapExec   = "container.exec"
	CapBackup = "backup.v1"
)

const (
	defaultExecTail = 16 << 10
	maxExecTail     = 64 << 10
)

// execLog streams a command's output as DEPLOY chunks of the command, like compose.
func (d *Deps) execLog(env *dispatch.Env) (io.Writer, io.Writer, func(error)) {
	return d.logWriters(env, "deploy:"+env.Cmd.GetCommandId(), agentv1.LogSource_LOG_SOURCE_DEPLOY)
}

// execError keeps protocol codes of backup errors; everything else (policy
// violations, Docker sentinels, deadline, cancellation) is classified by the dispatcher.
func execError(err error) error {
	var be *backup.Error
	if errors.As(err, &be) {
		return &dispatch.Error{Code: be.Code, Msg: be.Msg, ExitCode: be.ExitCode}
	}
	return err
}

// containerExec runs a program inside a workload container. The command succeeds
// whenever the program ran to completion, whatever its exit code.
func (d *Deps) containerExec(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetContainerExec()
	n := int(req.GetOutputTailBytes())
	if n <= 0 {
		n = defaultExecTail
	}
	n = min(n, maxExecTail)

	out, errw, closeLog := d.execLog(env)
	tail := backup.NewTail(n)
	start := d.now()
	code, err := d.Exec.Run(ctx, backup.ExecRequest{
		Container: req.GetContainer(), Argv: req.GetArgv(), Env: req.GetEnv(), User: req.GetUser(), WorkingDir: req.GetWorkingDir(),
		Stdout: io.MultiWriter(out, tail), Stderr: io.MultiWriter(errw, tail),
	})
	closeLog(err)
	if err != nil {
		return nil, execError(err)
	}
	b, truncated := tail.Result(env.Masker, n)
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ContainerExec{ContainerExec: &agentv1.ContainerExecResult{
		ExitCode: int32(code), OutputTail: b, OutputTruncated: truncated, Duration: durationpb.New(d.now().Sub(start).Round(time.Millisecond)),
	}}}, nil
}

// registerExec wires container.exec when an Executor is configured.
func (d *Deps) registerExec(r func(string, string, dispatch.Handler)) {
	if d.Exec != nil {
		r("container_exec", CapExec, d.containerExec)
	}
}
