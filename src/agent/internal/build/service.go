package build

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/durationpb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

// Failure carries the protocol error code of a failed build stage.
type Failure struct {
	Code agentv1.ErrorCode
	Err  error
}

func (f *Failure) Error() string { return f.Err.Error() }
func (f *Failure) Unwrap() error { return f.Err }

func fail(code agentv1.ErrorCode, format string, a ...any) *Failure {
	return &Failure{Code: code, Err: fmt.Errorf(format, a...)}
}

// Service runs builds and repository detection. Work happens under WorkDir, one throw-away directory per call.
type Service struct {
	Runner  Runner
	Docker  docker.API
	Engines Engines
	WorkDir string
	Now     func() time.Time
}

func (s *Service) now() time.Time {
	if s.Now != nil {
		return s.Now()
	}
	return time.Now()
}

func (s *Service) scratch(prefix string) (string, func(), error) {
	if err := os.MkdirAll(s.WorkDir, 0o700); err != nil {
		return "", nil, err
	}
	dir, err := os.MkdirTemp(s.WorkDir, prefix+"-*")
	if err != nil {
		return "", nil, err
	}
	return dir, func() { os.RemoveAll(dir) }, nil
}

// Detect clones the repository and proposes build methods.
func (s *Service) Detect(ctx context.Context, req *agentv1.BuildDetect, log io.Writer) (*agentv1.BuildDetectResult, error) {
	tmp, cleanup, err := s.scratch("detect")
	if err != nil {
		return nil, err
	}
	defer cleanup()
	co, err := Clone(ctx, s.Runner, withDepth(req.GetGit()), filepath.Join(tmp, "src"), log)
	if err != nil {
		return nil, fail(agentv1.ErrorCode_ERROR_CODE_GIT_FAILED, "%v", err)
	}
	dir, err := ContextDir(co.Dir, req.GetContextPath())
	if err != nil {
		return nil, fail(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "%v", err)
	}
	return &agentv1.BuildDetectResult{Candidates: Detect(dir), CommitSha: co.Commit}, nil
}

func withDepth(g *agentv1.GitSource) *agentv1.GitSource {
	if g.GetDepth() == 0 && g.GetCommit() == "" {
		cp := proto.Clone(g).(*agentv1.GitSource)
		cp.Depth = 1
		return cp
	}
	return g
}

// Run executes one build and returns its result. out and errw must already mask secrets.
func (s *Service) Run(ctx context.Context, req *agentv1.BuildRequest, out, errw io.Writer, progress func(step, msg string, pct int32)) (*agentv1.BuildResult, error) {
	started := s.now()
	if len(req.GetImageTags()) == 0 {
		return nil, fail(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "at least one image tag is required")
	}
	for _, t := range req.GetImageTags() {
		if t == "" || strings.HasPrefix(t, "-") {
			return nil, fail(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "invalid image tag %q", t)
		}
	}
	if len(req.GetTargetPlatforms()) > 1 && !req.GetPush() {
		return nil, fail(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "multi-platform builds require push")
	}
	eng, err := s.Engines.Select(req)
	if err != nil {
		return nil, fail(agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED, "%v", err)
	}

	tmp, cleanup, err := s.scratch("build")
	if err != nil {
		return nil, err
	}
	defer cleanup()

	job := &Job{Req: req, Tags: req.GetImageTags(), Out: out, Err: errw, Runner: s.Runner, Docker: s.Docker, TempDir: tmp, Progress: progress}
	res := &agentv1.BuildResult{BuildId: req.GetBuildId(), EngineUsed: eng.Name(), Tags: req.GetImageTags()}

	if eng.Name() != "image" {
		if req.GetGit() == nil {
			return nil, fail(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "a git source is required for the %s engine", eng.Name())
		}
		progress("source", "cloning repository", 5)
		co, err := Clone(ctx, s.Runner, withDepth(req.GetGit()), filepath.Join(tmp, "src"), errw)
		if err != nil {
			return nil, classify(ctx, agentv1.ErrorCode_ERROR_CODE_GIT_FAILED, err)
		}
		res.CommitSha = co.Commit
		fmt.Fprintf(out, "checked out %s\n", co.Commit)
		if job.Dir, err = ContextDir(co.Dir, req.GetContextPath()); err != nil {
			return nil, fail(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "%v", err)
		}
	}

	progress("build", "building with "+eng.Name(), 20)
	if err := eng.Build(ctx, job); err != nil {
		return nil, classify(ctx, agentv1.ErrorCode_ERROR_CODE_BUILD_FAILED, err)
	}

	info, err := s.Docker.ImageInspect(ctx, req.GetImageTags()[0])
	switch {
	case err == nil:
		res.ImageId, res.SizeBytes, res.Platform = info.GetId(), info.GetSizeBytes(), info.GetOs()+"/"+info.GetArchitecture()
		res.RepoDigests = info.GetRepoDigests()
	case errors.Is(err, docker.ErrNotFound) && req.GetPush():
		// A multi-platform build lives only in the registry.
	default:
		return nil, classify(ctx, agentv1.ErrorCode_ERROR_CODE_BUILD_FAILED, err)
	}

	if req.GetPush() {
		progress("push", "pushing image", 85)
		if err := s.push(ctx, job, tmp); err != nil {
			return nil, classify(ctx, agentv1.ErrorCode_ERROR_CODE_REGISTRY_AUTH_FAILED, err)
		}
		res.Pushed = true
		if after, err := s.Docker.ImageInspect(ctx, req.GetImageTags()[0]); err == nil {
			res.RepoDigests = after.GetRepoDigests()
		}
	}
	for _, rd := range res.RepoDigests {
		if _, d, ok := strings.Cut(rd, "@"); ok {
			res.Digest = d
			break
		}
	}
	res.Duration = durationpb.New(s.now().Sub(started))
	progress("done", "build finished", 100)
	return res, nil
}

func (s *Service) push(ctx context.Context, j *Job, tmp string) error {
	var env []string
	if a := j.Req.GetPushOptions().GetAuth(); a != nil {
		dir, err := registryConfigDir(tmp, a)
		if err != nil {
			return err
		}
		env = append(env, "DOCKER_CONFIG="+dir)
	}
	for _, t := range j.Tags {
		if err := s.Runner.Run(ctx, Cmd{Name: "docker", Args: []string{"push", "--", t}, Env: env, Stdout: j.Out, Stderr: j.Err}); err != nil {
			return fmt.Errorf("push %s: %w", t, err)
		}
	}
	return nil
}

// classify keeps cancellation and timeouts distinguishable from build failures.
func classify(ctx context.Context, code agentv1.ErrorCode, err error) error {
	if ctx.Err() != nil {
		return ctx.Err()
	}
	var f *Failure
	if errors.As(err, &f) {
		return f
	}
	return &Failure{Code: code, Err: err}
}
