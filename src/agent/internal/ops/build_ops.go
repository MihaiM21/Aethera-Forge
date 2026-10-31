package ops

import (
	"context"
	"errors"
	"io"
	"os/exec"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
	"github.com/mihaim21/aethera-forge/agent/internal/compose"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
)

// Capabilities of the Phase 3 subsystems. The names are the ones of the protocol (agent.proto, Hello.capabilities): the control
// plane only sends a command whose capability the agent listed, so a different spelling makes every such command fail with
// "the agent does not support ...".
const (
	// CapBuilds covers the build and build_detect arms (Dockerfile, static and image engines run on Docker alone).
	CapBuilds = "build.dockerfile"
	// CapNixpacks is advertised only when the nixpacks CLI is installed on the host.
	CapNixpacks = "build.nixpacks"
	// CapBuildQuota (ADR 0012) is advertised when builds can run in the BuildKit builder container `aethera-buildkit` with the server's
	// build limits. The control plane only sends BuildRequest.builder_limits to an agent that lists it.
	CapBuildQuota = "build.quota"
	CapCompose  = "compose.v2"
	// CapComposeGit (compose projects checked out of a Git repository, ComposeInspect) is advertised when compose is wired
	// and the git CLI is installed.
	CapComposeGit = "compose.git"
	CapProxy      = "proxy.traefik"
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
func (d *Deps) composeLog(env *dispatch.Env, src agentv1.LogSource) (io.Writer, io.Writer, func(error)) {
	return d.logWriters(env, "deploy:"+env.Cmd.GetCommandId(), src)
}

func (d *Deps) lookPath() func(string) (string, error) {
	if d.LookPath != nil {
		return d.LookPath
	}
	return exec.LookPath
}

func (d *Deps) gitAvailable() bool {
	_, err := d.lookPath()("git")
	return err == nil
}

// needGit refuses a project with a Git source on an agent without the git CLI (the capability gate cannot: compose_up
// itself is a compose.v2 command).
func (d *Deps) needGit(p *agentv1.ComposeProject) error {
	if p.GetGit() != nil && !d.gitAvailable() {
		return dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED, "this agent has no git installed, so it cannot deploy compose projects from a repository")
	}
	return nil
}

func composeResult(r *agentv1.ComposeResult) *agentv1.CommandResult {
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Compose{Compose: r}}
}

func (d *Deps) composeUp(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetComposeUp()
	if err := d.needGit(req.GetProject()); err != nil {
		return nil, err
	}
	// A project from a repository that is built here streams its whole output (checkout, build, up) as build log.
	src := agentv1.LogSource_LOG_SOURCE_DEPLOY
	if req.GetProject().GetGit() != nil && req.GetBuild() {
		src = agentv1.LogSource_LOG_SOURCE_BUILD
	}
	out, errw, closeLog := d.composeLog(env, src)
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
	out, errw, closeLog := d.composeLog(env, agentv1.LogSource_LOG_SOURCE_DEPLOY)
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
	if err := d.needGit(req.GetProject()); err != nil {
		return nil, err
	}
	out, errw, closeLog := d.composeLog(env, agentv1.LogSource_LOG_SOURCE_DEPLOY)
	err := d.Compose.Pull(ctx, req.GetProject(), req.GetServices(), out, errw)
	closeLog(err)
	if err != nil {
		return nil, composeError(err)
	}
	return empty(), nil
}

// composeInspect resolves a project without starting anything; policy findings come back in the result, not as an error.
func (d *Deps) composeInspect(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetComposeInspect()
	if err := d.needGit(req.GetProject()); err != nil {
		return nil, err
	}
	res, err := d.Compose.Inspect(ctx, req.GetProject(), io.Discard)
	if err != nil {
		return nil, composeError(err)
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ComposeInspect{ComposeInspect: res}}, nil
}

// composeError separates invalid input (including policy refusals of the compose file) from runtime failures.
func composeError(err error) error {
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	var ve *compose.ValidationError
	if errors.As(err, &ve) {
		if ve.Invalid {
			return dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "%v", err)
		}
		return dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "%v", err)
	}
	var f *build.Failure
	if errors.As(err, &f) {
		return &dispatch.Error{Code: f.Code, Msg: f.Error()}
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
		if d.gitAvailable() {
			r("compose_inspect", CapComposeGit, d.composeInspect)
		}
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
		lookPath := d.LookPath
		if lookPath == nil {
			lookPath = exec.LookPath
		}
		if _, err := lookPath("nixpacks"); err == nil {
			c = append(c, CapNixpacks)
		}
		if d.Builder.Builders != nil {
			c = append(c, CapBuildQuota)
		}
	}
	if d.Compose != nil {
		c = append(c, CapCompose)
		if d.gitAvailable() {
			c = append(c, CapComposeGit)
		}
	}
	if d.Proxy != nil {
		c = append(c, CapProxy)
	}
	return c
}
