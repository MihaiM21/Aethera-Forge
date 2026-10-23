// Package compose runs Docker Compose projects for the agent (WP3.1, ADR 0010). A project's compose file is either stored
// verbatim next to an Aethera-generated override file (labels, networks) or, with a Git source, checked out of the
// repository so that relative `build:` contexts resolve inside the checkout. Everything is data written to the agent's
// managed project directory and never interpolated into a shell. The configuration docker compose resolves from the files
// is vetted before anything starts: it must not request host-level privileges that the container policy refuses for
// single containers.
package compose

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

var projectNameRe = regexp.MustCompile(`^[a-z0-9][a-z0-9_-]*$`)
var serviceNameRe = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]*$`)

// ValidationError marks input the agent refuses (bad names, compose content that violates policy), as opposed to a
// runtime failure of docker compose. Invalid marks a malformed request (INVALID_ARGUMENT) rather than a policy refusal.
type ValidationError struct {
	Err     error
	Invalid bool
}

func (e *ValidationError) Error() string { return e.Err.Error() }
func (e *ValidationError) Unwrap() error { return e.Err }

func bad(format string, a ...any) error { return &ValidationError{Err: fmt.Errorf(format, a...)} }

func invalid(format string, a ...any) error {
	return &ValidationError{Err: fmt.Errorf(format, a...), Invalid: true}
}

// BindChecker vets host paths of bind mounts (implemented by policy.Policy).
type BindChecker interface {
	CheckBindSource(src string) error
}

// Service runs compose commands under Dir/<project>.
type Service struct {
	Runner build.Runner
	Dir    string
	Binds  BindChecker

	// allowLocalGit lets tests clone a local bare repository; production refuses file paths and file:// URLs.
	allowLocalGit bool

	mu    sync.Mutex
	locks map[string]chan struct{}
}

// lock serializes the commands of one project: a checkout is replaced wholesale, so nothing may run in it meanwhile.
func (s *Service) lock(ctx context.Context, name string) (func(), error) {
	s.mu.Lock()
	if s.locks == nil {
		s.locks = map[string]chan struct{}{}
	}
	ch := s.locks[name]
	if ch == nil {
		ch = make(chan struct{}, 1)
		s.locks[name] = ch
	}
	s.mu.Unlock()
	select {
	case ch <- struct{}{}:
		return func() { <-ch }, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

// project is a prepared project on disk.
type project struct {
	name string
	dir  string // <projects dir>/<name>
	root string // the checkout, or dir for an inline file: local paths must stay below it
	base []string
	// profiles are the --profile arguments; they follow base and are replaced by "all profiles" for policy checks.
	profiles []string
	env      []string
	cleanup  func()

	commit   string
	userFile []byte // the compose file the user wrote (inline, or read from the repository)
	override string
	// findings are the policy findings of the user's file text; parsed is false when it is not YAML.
	findings []string
	parsed   bool

	buildPairs, buildNames []string
}

type prepMode int

const (
	// modeRun is for commands that need the current source: a fresh checkout.
	modeRun prepMode = iota
	// modeLight is for ps and down, which work from the project's name and labels: no checkout for Git projects.
	modeLight
)

func checkName(p *agentv1.ComposeProject) error {
	if !projectNameRe.MatchString(p.GetProjectName()) {
		return bad("invalid compose project name %q", p.GetProjectName())
	}
	return nil
}

// prepare writes the project's files and returns what is needed to run docker compose against them. Findings about the
// user's file text are an error unless soft is set (inspect reports them instead). The caller must call cleanup.
func (s *Service) prepare(ctx context.Context, p *agentv1.ComposeProject, mode prepMode, soft bool, out, errw io.Writer) (res *project, err error) {
	if err := checkName(p); err != nil {
		return nil, err
	}
	git, inline := p.GetGit(), strings.TrimSpace(p.GetComposeFile()) != ""
	switch {
	case git != nil && inline:
		return nil, invalid("compose_file and git are mutually exclusive")
	case git == nil && !inline:
		return nil, invalid("exactly one of compose_file and git is required")
	case git == nil && p.GetComposePath() != "":
		return nil, invalid("compose_path needs a git source")
	}
	if git != nil && !s.allowLocalGit {
		if err := build.ValidateGitSource(git); err != nil {
			return nil, invalid("git source: %v", err)
		}
	}
	if p.GetOverrideFile() != "" {
		if err := Validate(p.GetOverrideFile(), s.Binds); err != nil {
			return nil, bad("override file: %v", err)
		}
	}
	dotenv, err := dotenvFile(p.GetEnv())
	if err != nil {
		return nil, err
	}
	pairs, names, err := buildArgs(p.GetBuildArgs(), p.GetEnv())
	if err != nil {
		return nil, err
	}
	for _, prof := range p.GetProfiles() {
		if !projectNameRe.MatchString(prof) {
			return nil, bad("invalid profile %q", prof)
		}
	}

	dir := filepath.Join(s.Dir, p.GetProjectName())
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return nil, err
	}
	// Secrets (interpolation variables, registry credentials) live in a per-command directory that is removed when the
	// command ends; the project directory keeps nothing that carries a credential.
	tmp, err := os.MkdirTemp(dir, ".run-")
	if err != nil {
		return nil, err
	}
	pr := &project{name: p.GetProjectName(), dir: dir, root: dir, override: p.GetOverrideFile(), buildPairs: pairs, buildNames: names,
		cleanup: func() { _ = os.RemoveAll(tmp) }}
	defer func() {
		if err != nil {
			pr.cleanup()
		}
	}()

	write := func(path, content string) error { return os.WriteFile(path, []byte(content), 0o600) }
	var files []string
	switch {
	case git == nil:
		main := filepath.Join(dir, "compose.yaml")
		if err := write(main, p.GetComposeFile()); err != nil {
			return nil, err
		}
		pr.userFile = []byte(p.GetComposeFile())
		files = append(files, main)
	case mode == modeRun:
		co, err := s.checkout(ctx, git, dir, out, errw)
		if err != nil {
			return nil, err
		}
		pr.root, pr.commit = co.Dir, co.Commit
		main, content, err := findComposeFile(co.Dir, p.GetComposePath())
		if err != nil {
			return nil, err
		}
		pr.userFile = content
		files = append(files, main)
	}
	if len(files) > 0 {
		pr.findings, pr.parsed = rawFindings(string(pr.userFile), s.Binds)
		if len(pr.findings) > 0 && !soft {
			return nil, bad("%s", strings.Join(pr.findings, "; "))
		}
		if p.GetOverrideFile() != "" {
			ov := filepath.Join(dir, "compose.aethera.yaml")
			if err := write(ov, p.GetOverrideFile()); err != nil {
				return nil, err
			}
			files = append(files, ov)
		}
	}

	pr.base = []string{"compose", "-p", p.GetProjectName(), "--project-directory", pr.root}
	for _, f := range files {
		pr.base = append(pr.base, "-f", f)
	}
	if len(p.GetEnv()) > 0 {
		// Replaces the repository's own .env as the source of interpolation values; the variables the user set win.
		envFile := filepath.Join(tmp, ".env")
		if err := write(envFile, dotenv); err != nil {
			return nil, err
		}
		pr.base = append(pr.base, "--env-file", envFile)
	}
	for _, prof := range p.GetProfiles() {
		pr.profiles = append(pr.profiles, "--profile", prof)
	}
	if len(p.GetRegistryAuths()) > 0 {
		cfg := map[string]map[string]string{}
		for _, a := range p.GetRegistryAuths() {
			e := map[string]string{}
			if a.GetPassword().GetValue() != "" {
				e["auth"] = base64.StdEncoding.EncodeToString([]byte(a.GetUsername() + ":" + a.GetPassword().GetValue()))
			}
			if t := a.GetIdentityToken().GetValue(); t != "" {
				e["identitytoken"] = t
			}
			cfg[a.GetServer()] = e
		}
		cdir := filepath.Join(tmp, "docker-config")
		if err := os.MkdirAll(cdir, 0o700); err != nil {
			return nil, err
		}
		b, _ := json.Marshal(map[string]any{"auths": cfg})
		if err := os.WriteFile(filepath.Join(cdir, "config.json"), b, 0o600); err != nil {
			return nil, err
		}
		pr.env = append(pr.env, "DOCKER_CONFIG="+cdir)
	}
	return pr, nil
}

// argv builds a docker compose argument vector: global options, profiles, then the subcommand.
func (pr *project) argv(sub ...string) []string {
	a := append(append([]string{}, pr.base...), pr.profiles...)
	return append(a, sub...)
}

// ---- policy on the resolved configuration ---------------------------------------------------------------------------

// configError is a refusal of `docker compose config` itself (unreadable file, missing variable): the project cannot run.
type configError struct{ msg string }

func (e *configError) Error() string { return "docker compose config: " + e.msg }

// config resolves the project (`docker compose config --format json`, every profile enabled) and vets it. The findings
// cover the user's file text and the resolved configuration. Nothing is started or built.
func (s *Service) config(ctx context.Context, pr *project, errw io.Writer) (*resolvedConfig, []string, error) {
	run := func(extra ...string) ([]byte, string, error) {
		var out bytes.Buffer
		tail := &tailBuffer{max: 4096}
		args := append(append([]string{}, pr.base...), "--profile", "*", "config", "--format", "json")
		c := build.Cmd{Name: "docker", Args: append(args, extra...), Dir: pr.dir, Env: pr.env, Stdout: &out, Stderr: io.MultiWriter(errw, tail)}
		err := s.Runner.Run(ctx, c)
		return out.Bytes(), tail.String(), err
	}
	// --no-env-resolution keeps env_file entries visible so their paths can be vetted; older compose lacks it.
	b, msg, err := run("--no-env-resolution")
	if err != nil && strings.Contains(msg, "unknown flag") {
		b, msg, err = run()
	}
	if err != nil {
		if ctx.Err() != nil {
			return nil, nil, ctx.Err()
		}
		var ee *exec.ExitError
		if errors.As(err, &ee) {
			return nil, nil, &configError{msg: strings.TrimSpace(msg)}
		}
		return nil, nil, fmt.Errorf("docker compose config: %w", err)
	}
	cfg, err := parseResolved(b)
	if err != nil {
		return nil, nil, err
	}
	findings := append([]string{}, pr.findings...)
	findings = append(findings, CheckResolved(cfg, ResolvedPolicy{Root: pr.root, Binds: s.Binds, ExternalNetworks: overrideNetworks(pr.override)})...)
	return cfg, dedupe(findings), nil
}

// vet is config for commands that start or pull: any finding, or a configuration docker compose cannot resolve, refuses
// the command.
func (s *Service) vet(ctx context.Context, pr *project, errw io.Writer) (*resolvedConfig, error) {
	cfg, findings, err := s.config(ctx, pr, errw)
	var ce *configError
	switch {
	case errors.As(err, &ce):
		if len(pr.findings) > 0 {
			return nil, bad("%s", strings.Join(dedupe(pr.findings), "; "))
		}
		return nil, invalid("%v", err)
	case err != nil:
		return nil, err
	}
	if len(findings) > 0 {
		return nil, bad("%s", strings.Join(findings, "; "))
	}
	return cfg, nil
}

func dedupe(in []string) []string {
	seen := map[string]bool{}
	out := in[:0:0]
	for _, f := range in {
		if !seen[f] {
			seen[f] = true
			out = append(out, f)
		}
	}
	return out
}

// buildServices lists the services that have a build section, limited to the requested ones (all when none are).
func buildServices(cfg *resolvedConfig, only []string) []string {
	want := map[string]bool{}
	for _, n := range only {
		want[n] = true
	}
	var out []string
	for _, n := range sortedKeys(cfg.Services) {
		if cfg.Services[n]["build"] != nil && (len(want) == 0 || want[n]) {
			out = append(out, n)
		}
	}
	return out
}

// tailBuffer keeps the last max bytes written to it.
type tailBuffer struct {
	max int
	buf []byte
}

func (t *tailBuffer) Write(p []byte) (int, error) {
	t.buf = append(t.buf, p...)
	if len(t.buf) > t.max {
		t.buf = t.buf[len(t.buf)-t.max:]
	}
	return len(p), nil
}

func (t *tailBuffer) String() string { return string(t.buf) }

// ---- commands -----------------------------------------------------------------------------------------------------

// UpOptions mirror the ComposeUp command.
type UpOptions struct {
	Services      []string
	PullPolicy    agentv1.ComposePullPolicy
	Build         bool
	ForceRecreate bool
	RemoveOrphans bool
	Wait          bool
	WaitTimeout   time.Duration
}

// Up starts the project and returns the resulting service states. With Build set, services that have a build section are
// built first (`docker compose build`, with the project's build arguments), then `up` starts everything.
func (s *Service) Up(ctx context.Context, p *agentv1.ComposeProject, o UpOptions, out, errw io.Writer) (*agentv1.ComposeResult, error) {
	if err := checkName(p); err != nil {
		return nil, err
	}
	if err := checkServiceNames(o.Services); err != nil {
		return nil, err
	}
	unlock, err := s.lock(ctx, p.GetProjectName())
	if err != nil {
		return nil, err
	}
	defer unlock()
	pr, err := s.prepare(ctx, p, modeRun, false, out, errw)
	if err != nil {
		return nil, err
	}
	defer pr.cleanup()
	cfg, err := s.vet(ctx, pr, errw)
	if err != nil {
		return nil, err
	}

	if o.Build && len(buildServices(cfg, o.Services)) > 0 {
		args := pr.argv("build")
		for _, n := range pr.buildNames {
			args = append(args, "--build-arg", n) // the value comes from the environment, like the build engine does it
		}
		env := append(append([]string{"DOCKER_BUILDKIT=1", "BUILDKIT_PROGRESS=plain"}, pr.env...), pr.buildPairs...)
		args = append(args, o.Services...)
		if err := s.Runner.Run(ctx, build.Cmd{Name: "docker", Args: args, Dir: pr.dir, Env: env, Stdout: out, Stderr: errw}); err != nil {
			return nil, fmt.Errorf("compose build: %w", err)
		}
	}

	args := pr.argv("up", "-d")
	switch o.PullPolicy {
	case agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_ALWAYS:
		args = append(args, "--pull", "always")
	case agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_NEVER:
		args = append(args, "--pull", "never")
	case agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_MISSING:
		args = append(args, "--pull", "missing")
	}
	if o.ForceRecreate {
		args = append(args, "--force-recreate")
	}
	if o.RemoveOrphans {
		args = append(args, "--remove-orphans")
	}
	if o.Wait {
		args = append(args, "--wait")
		if o.WaitTimeout > 0 {
			args = append(args, "--wait-timeout", strconv.Itoa(int(o.WaitTimeout.Seconds())))
		}
	}
	args = append(args, o.Services...)
	if err := s.run(ctx, pr, args, out, errw); err != nil {
		return nil, fmt.Errorf("compose up: %w", err)
	}
	res, err := s.ps(ctx, pr, p.GetProjectName(), true, errw)
	if err != nil {
		return nil, err
	}
	res.CommitSha = pr.commit
	return res, nil
}

// Down stops and removes the project and deletes its checkout. A Git project is taken down by name (compose finds its
// containers, networks and volumes by label), so a repository that is gone or unreachable does not block it.
func (s *Service) Down(ctx context.Context, p *agentv1.ComposeProject, removeVolumes, removeOrphans bool, timeout time.Duration, out, errw io.Writer) error {
	if err := checkName(p); err != nil {
		return err
	}
	unlock, err := s.lock(ctx, p.GetProjectName())
	if err != nil {
		return err
	}
	defer unlock()
	pr, err := s.prepare(ctx, p, modeLight, false, out, errw)
	if err != nil {
		return err
	}
	defer pr.cleanup()
	args := pr.argv("down")
	if removeVolumes {
		args = append(args, "--volumes")
	}
	if removeOrphans {
		args = append(args, "--remove-orphans")
	}
	if timeout > 0 {
		args = append(args, "--timeout", strconv.Itoa(int(timeout.Seconds())))
	}
	if err := s.run(ctx, pr, args, out, errw); err != nil {
		return err
	}
	return os.RemoveAll(filepath.Join(pr.dir, checkoutDirName))
}

// Pull pulls the images of the project (not those a build section produces).
func (s *Service) Pull(ctx context.Context, p *agentv1.ComposeProject, services []string, out, errw io.Writer) error {
	if err := checkName(p); err != nil {
		return err
	}
	if err := checkServiceNames(services); err != nil {
		return err
	}
	unlock, err := s.lock(ctx, p.GetProjectName())
	if err != nil {
		return err
	}
	defer unlock()
	pr, err := s.prepare(ctx, p, modeRun, false, out, errw)
	if err != nil {
		return err
	}
	defer pr.cleanup()
	cfg, err := s.vet(ctx, pr, errw)
	if err != nil {
		return err
	}
	args := pr.argv("pull")
	if len(buildServices(cfg, nil)) > 0 {
		args = append(args, "--ignore-buildable")
	}
	return s.run(ctx, pr, append(args, services...), out, errw)
}

// Ps lists the project's containers.
func (s *Service) Ps(ctx context.Context, p *agentv1.ComposeProject, all bool, errw io.Writer) (*agentv1.ComposeResult, error) {
	pr, err := s.prepare(ctx, p, modeLight, false, io.Discard, errw)
	if err != nil {
		return nil, err
	}
	defer pr.cleanup()
	return s.ps(ctx, pr, p.GetProjectName(), all, errw)
}

func (s *Service) ps(ctx context.Context, pr *project, name string, all bool, errw io.Writer) (*agentv1.ComposeResult, error) {
	args := pr.argv("ps", "--format", "json")
	if all {
		args = append(args, "-a")
	}
	var buf bytes.Buffer
	c := build.Cmd{Name: "docker", Args: args, Dir: pr.dir, Env: pr.env, Stdout: &buf, Stderr: errw}
	if err := s.Runner.Run(ctx, c); err != nil {
		return nil, fmt.Errorf("compose ps: %w", err)
	}
	return &agentv1.ComposeResult{ProjectName: name, Services: ParsePs(buf.Bytes())}, nil
}

func (s *Service) run(ctx context.Context, pr *project, args []string, out, errw io.Writer) error {
	return s.Runner.Run(ctx, build.Cmd{Name: "docker", Args: args, Dir: pr.dir, Env: pr.env, Stdout: out, Stderr: errw})
}

func checkServiceNames(names []string) error {
	for _, n := range names {
		if !serviceNameRe.MatchString(n) {
			return bad("invalid service name %q", n)
		}
	}
	return nil
}
