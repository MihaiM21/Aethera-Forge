package ops

import (
	"context"
	"errors"
	"io"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
	"github.com/mihaim21/aethera-forge/agent/internal/compose"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
)

// Capabilities of the Phase 3 subsystems.
const (
	CapBuilds  = "builds"
	CapCompose = "compose"
	CapProxy   = "proxy"
)

// logWriters opens a lossless log stream for a build/deploy operation. Output is masked with the secrets carried by the
// command. The returned close function ends the stream with a reason.
func (d *Deps) logWriters(env *dispatch.Env, id string, src agentv1.LogSource) (out, errw io.Writer, closeFn func(error)) {
	if d.Logs == nil {
		return io.Discard, io.Discard, func(error) {}
	}
	st := d.Logs.Open(id, src, true, env.Masker)
	return st.Writer(agentv1.LogStream_LOG_STREAM_STDOUT), st.Writer(agentv1.LogStream_LOG_STREAM_STDERR), func(err error) {
		if err != nil {
			st.Close(env.Masker.String(err.Error()))
			return
		}
		st.Close("")
	}
}

// opError maps build/compose failures to protocol codes; cancellation and deadlines keep their own codes.
func opError(err error) error {
	var f *build.Failure
	switch {
	case errors.As(err, &f):
		return &dispatch.Error{Code: f.Code, Msg: f.Error()}
	case errors.Is(err, context.Canceled), errors.Is(err, context.DeadlineExceeded):
		return err
	}
	return dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_INTERNAL, "%v", err)
}

func (d *Deps) build(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetBuild()
	if req.GetBuildId() == "" {
		return nil, invalidArg("build_id is required")
	}
	out, errw, closeLog := d.logWriters(env, req.GetBuildId(), agentv1.LogSource_LOG_SOURCE_BUILD)
	res, err := d.Builder.Run(ctx, req, out, errw, env.Progress)
	closeLog(err)
	if err != nil {
		return nil, opError(err)
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Build{Build: res}}, nil
}

func (d *Deps) buildDetect(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetBuildDetect()
	if req.GetGit() == nil {
		return nil, invalidArg("a git source is required")
	}
	res, err := d.Builder.Detect(ctx, req, io.Discard)
	if err != nil {
		return nil, opError(err)
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_BuildDetect{BuildDetect: res}}, nil
}

// composeLog streams compose output under deploy:<command id>, the control plane's default stream for non-build commands.
func (d *Deps) composeLog(env *dispatch.Env) (io.Writer, io.Writer, func(error)) {
	return d.logWriters(env, "deploy:"+env.Cmd.GetCommandId(), agentv1.LogSource_LOG_SOURCE_DEPLOY)
}

func composeResult(r *agentv1.ComposeResult) *agentv1.CommandResult {
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Compose{Compose: r}}
}

func (d *Deps) composeUp(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetComposeUp()
	out, errw, closeLog := d.composeLog(env)
	env.Progress("compose", "starting project "+req.GetProject().GetProjectName(), -1)
	var wait time.Duration
	if req.GetWaitTimeout() != nil {
		wait = req.GetWaitTimeout().AsDuration()
	}
	res, err := d.Compose.Up(ctx, req.GetProject(), compose.UpOptions{
		Services: req.GetServices(), PullPolicy: req.GetPullPolicy(), Build: req.GetBuild(), ForceRecreate: req.GetForceRecreate(),
		RemoveOrphans: req.GetRemoveOrphans(), Wait: req.GetWait(), WaitTimeout: wait,
	}, out, errw)
	closeLog(err)
	if err != nil {
		return nil, composeError(err)
	}
	return composeResult(res), nil
}

func (d *Deps) composeDown(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetComposeDown()
	out, errw, closeLog := d.composeLog(env)
	var timeout time.Duration
	if req.GetTimeout() != nil {
		timeout = req.GetTimeout().AsDuration()
	}
	err := d.Compose.Down(ctx, req.GetProject(), req.GetRemoveVolumes(), req.GetRemoveOrphans(), timeout, out, errw)
	closeLog(err)
	if err != nil {
		return nil, composeError(err)
	}
	return composeResult(&agentv1.ComposeResult{ProjectName: req.GetProject().GetProjectName()}), nil
}

func (d *Deps) composePs(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetComposePs()
	res, err := d.Compose.Ps(ctx, req.GetProject(), req.GetAll(), io.Discard)
	if err != nil {
		return nil, composeError(err)
	}
	return composeResult(res), nil
}

func (d *Deps) composePull(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetComposePull()
	out, errw, closeLog := d.composeLog(env)
	err := d.Compose.Pull(ctx, req.GetProject(), req.GetServices(), out, errw)
	closeLog(err)
	if err != nil {
		return nil, composeError(err)
	}
	return empty(), nil
}

// composeError separates invalid input (including policy refusals of the compose file) from runtime failures.
func composeError(err error) error {
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	var ve *compose.ValidationError
	if errors.As(err, &ve) {
		return dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "%v", err)
	}
	return dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_BUILD_FAILED, "%v", err)
}

func (d *Deps) proxyEnsure(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	res, err := d.Proxy.Ensure(ctx, env.Cmd.GetProxyEnsure())
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ProxyEnsure{ProxyEnsure: res}}, nil
}

// registerPhase3 wires the build, compose and proxy arms for the subsystems that were configured.
func (d *Deps) registerPhase3(r func(string, string, dispatch.Handler)) {
	if d.Builder != nil {
		r("build", CapBuilds, d.build)
		r("build_detect", CapBuilds, d.buildDetect)
	}
	if d.Compose != nil {
		r("compose_up", CapCompose, d.composeUp)
		r("compose_down", CapCompose, d.composeDown)
		r("compose_ps", CapCompose, d.composePs)
		r("compose_pull", CapCompose, d.composePull)
	}
	if d.Proxy != nil {
		r("proxy_ensure", CapProxy, d.proxyEnsure)
	}
}

// phase3Capabilities lists the capabilities of the configured Phase 3 subsystems.
func (d *Deps) phase3Capabilities() []string {
	var c []string
	if d.Builder != nil {
		c = append(c, CapBuilds)
	}
	if d.Compose != nil {
		c = append(c, CapCompose)
	}
	if d.Proxy != nil {
		c = append(c, CapProxy)
	}
	return c
}
