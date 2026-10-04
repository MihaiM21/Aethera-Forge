// Package compose runs Docker Compose projects for the agent (WP3.1). The user's compose file is stored verbatim next to an
// Aethera-generated override file (labels, networks); both are data written to the agent's managed project directory and
// never interpolated into a shell. The compose file is vetted first: it must not request host-level privileges that the
// container policy refuses for single containers.
package compose

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"

	"gopkg.in/yaml.v3"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

var projectNameRe = regexp.MustCompile(`^[a-z0-9][a-z0-9_-]*$`)

// ValidationError marks input the agent refuses (bad names, compose content that violates policy), as opposed to a
// runtime failure of docker compose.
type ValidationError struct{ Err error }

func (e *ValidationError) Error() string { return e.Err.Error() }
func (e *ValidationError) Unwrap() error { return e.Err }

func bad(format string, a ...any) error { return &ValidationError{Err: fmt.Errorf(format, a...)} }

// BindChecker vets host paths of bind mounts (implemented by policy.Policy).
type BindChecker interface {
	CheckBindSource(src string) error
}

// Service runs compose commands under Dir/<project>.
type Service struct {
	Runner build.Runner
	Dir    string
	Binds  BindChecker
}

// Handle is a prepared project on disk.
type project struct {
	dir   string
	args  []string // -p name -f ... --env-file ...
	env   []string
	files []string
}

func (s *Service) prepare(p *agentv1.ComposeProject) (*project, error) {
	if !projectNameRe.MatchString(p.GetProjectName()) {
		return nil, bad("invalid compose project name %q", p.GetProjectName())
	}
	if strings.TrimSpace(p.GetComposeFile()) == "" {
		return nil, bad("compose_file is required")
	}
	if err := Validate(p.GetComposeFile(), s.Binds); err != nil {
		return nil, &ValidationError{Err: err}
	}
	if p.GetOverrideFile() != "" {
		if err := Validate(p.GetOverrideFile(), s.Binds); err != nil {
			return nil, bad("override file: %v", err)
		}
	}
	dir := filepath.Join(s.Dir, p.GetProjectName())
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return nil, err
	}
	pr := &project{dir: dir, args: []string{"compose", "-p", p.GetProjectName()}}

	write := func(name, content string, perm os.FileMode) (string, error) {
		path := filepath.Join(dir, name)
		return path, os.WriteFile(path, []byte(content), perm)
	}
	main, err := write("compose.yaml", p.GetComposeFile(), 0o600)
	if err != nil {
		return nil, err
	}
	pr.args = append(pr.args, "-f", main)
	if p.GetOverrideFile() != "" {
		ov, err := write("compose.aethera.yaml", p.GetOverrideFile(), 0o600)
		if err != nil {
			return nil, err
		}
		pr.args = append(pr.args, "-f", ov)
	}

	var env bytes.Buffer
	for _, v := range p.GetEnv() {
		if !regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*$`).MatchString(v.GetName()) {
			return nil, bad("invalid environment variable name %q", v.GetName())
		}
		val := v.GetPlain()
		if sec := v.GetSecret(); sec != nil {
			val = sec.GetValue()
		}
		if strings.ContainsAny(val, "\r\n") {
			return nil, bad("environment variable %s has a multi-line value; compose .env files do not support it", v.GetName())
		}
		env.WriteString(v.GetName() + "=" + val + "\n")
	}
	envFile, err := write(".env", env.String(), 0o600)
	if err != nil {
		return nil, err
	}
	pr.args = append(pr.args, "--env-file", envFile)

	for _, prof := range p.GetProfiles() {
		if !projectNameRe.MatchString(prof) {
			return nil, bad("invalid profile %q", prof)
		}
		pr.args = append(pr.args, "--profile", prof)
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
		cdir := filepath.Join(dir, "docker-config")
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

// Up starts the project and returns the resulting service states.
func (s *Service) Up(ctx context.Context, p *agentv1.ComposeProject, o UpOptions, out, errw io.Writer) (*agentv1.ComposeResult, error) {
	pr, err := s.prepare(p)
	if err != nil {
		return nil, err
	}
	args := append([]string{}, pr.args...)
	args = append(args, "up", "-d")
	switch o.PullPolicy {
	case agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_ALWAYS:
		args = append(args, "--pull", "always")
	case agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_NEVER:
		args = append(args, "--pull", "never")
	case agentv1.ComposePullPolicy_COMPOSE_PULL_POLICY_MISSING:
		args = append(args, "--pull", "missing")
	}
	if o.Build {
		args = append(args, "--build")
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
	if err := checkServiceNames(o.Services); err != nil {
		return nil, err
	}
	args = append(args, o.Services...)
	if err := s.run(ctx, pr, args, out, errw); err != nil {
		return nil, fmt.Errorf("compose up: %w", err)
	}
	return s.Ps(ctx, p, true, errw)
}

// Down stops and removes the project.
func (s *Service) Down(ctx context.Context, p *agentv1.ComposeProject, removeVolumes, removeOrphans bool, timeout time.Duration, out, errw io.Writer) error {
	pr, err := s.prepare(p)
	if err != nil {
		return err
	}
	args := append(append([]string{}, pr.args...), "down")
	if removeVolumes {
		args = append(args, "--volumes")
	}
	if removeOrphans {
		args = append(args, "--remove-orphans")
	}
	if timeout > 0 {
		args = append(args, "--timeout", strconv.Itoa(int(timeout.Seconds())))
	}
	return s.run(ctx, pr, args, out, errw)
}

// Pull pulls the images of the project.
func (s *Service) Pull(ctx context.Context, p *agentv1.ComposeProject, services []string, out, errw io.Writer) error {
	pr, err := s.prepare(p)
	if err != nil {
		return err
	}
	if err := checkServiceNames(services); err != nil {
		return err
	}
	args := append(append([]string{}, pr.args...), "pull")
	return s.run(ctx, pr, append(args, services...), out, errw)
}

// Ps lists the project's containers.
func (s *Service) Ps(ctx context.Context, p *agentv1.ComposeProject, all bool, errw io.Writer) (*agentv1.ComposeResult, error) {
	pr, err := s.prepare(p)
	if err != nil {
		return nil, err
	}
	args := append(append([]string{}, pr.args...), "ps", "--format", "json")
	if all {
		args = append(args, "-a")
	}
	var buf bytes.Buffer
	c := build.Cmd{Name: "docker", Args: args, Dir: pr.dir, Env: pr.env, Stdout: &buf, Stderr: errw}
	if err := s.Runner.Run(ctx, c); err != nil {
		return nil, fmt.Errorf("compose ps: %w", err)
	}
	return &agentv1.ComposeResult{ProjectName: p.GetProjectName(), Services: ParsePs(buf.Bytes())}, nil
}

func (s *Service) run(ctx context.Context, pr *project, args []string, out, errw io.Writer) error {
	return s.Runner.Run(ctx, build.Cmd{Name: "docker", Args: args, Dir: pr.dir, Env: pr.env, Stdout: out, Stderr: errw})
}

func checkServiceNames(names []string) error {
	for _, n := range names {
		if !regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]*$`).MatchString(n) {
			return bad("invalid service name %q", n)
		}
	}
	return nil
}

// psEntry is one line of `docker compose ps --format json`.
type psEntry struct {
	ID         string `json:"ID"`
	Name       string `json:"Name"`
	Image      string `json:"Image"`
	Service    string `json:"Service"`
	State      string `json:"State"`
	Health     string `json:"Health"`
	ExitCode   int32  `json:"ExitCode"`
	Publishers []struct {
		TargetPort    uint32 `json:"TargetPort"`
		PublishedPort uint32 `json:"PublishedPort"`
		Protocol      string `json:"Protocol"`
		URL           string `json:"URL"`
	} `json:"Publishers"`
}

// ParsePs reads NDJSON or a JSON array as printed by different compose versions.
func ParsePs(b []byte) []*agentv1.ComposeServiceStatus {
	var entries []psEntry
	trim := bytes.TrimSpace(b)
	if bytes.HasPrefix(trim, []byte("[")) {
		_ = json.Unmarshal(trim, &entries)
	} else {
		dec := json.NewDecoder(bytes.NewReader(trim))
		for dec.More() {
			var e psEntry
			if dec.Decode(&e) != nil {
				break
			}
			entries = append(entries, e)
		}
	}
	out := make([]*agentv1.ComposeServiceStatus, 0, len(entries))
	for _, e := range entries {
		st := &agentv1.ComposeServiceStatus{
			Service: e.Service, ContainerId: e.ID, ContainerName: e.Name, Image: e.Image, ExitCode: e.ExitCode,
			State: mapState(e.State), Health: mapHealth(e.Health),
		}
		for _, p := range e.Publishers {
			if p.PublishedPort == 0 {
				continue
			}
			proto := agentv1.PortProtocol_PORT_PROTOCOL_TCP
			if strings.EqualFold(p.Protocol, "udp") {
				proto = agentv1.PortProtocol_PORT_PROTOCOL_UDP
			}
			st.Ports = append(st.Ports, &agentv1.PortMapping{ContainerPort: p.TargetPort, HostPort: p.PublishedPort, Protocol: proto})
		}
		out = append(out, st)
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Service < out[j].Service })
	return out
}

func mapState(s string) agentv1.ContainerState {
	switch strings.ToLower(s) {
	case "created":
		return agentv1.ContainerState_CONTAINER_STATE_CREATED
	case "running":
		return agentv1.ContainerState_CONTAINER_STATE_RUNNING
	case "paused":
		return agentv1.ContainerState_CONTAINER_STATE_PAUSED
	case "restarting":
		return agentv1.ContainerState_CONTAINER_STATE_RESTARTING
	case "removing":
		return agentv1.ContainerState_CONTAINER_STATE_REMOVING
	case "exited":
		return agentv1.ContainerState_CONTAINER_STATE_EXITED
	case "dead":
		return agentv1.ContainerState_CONTAINER_STATE_DEAD
	}
	return agentv1.ContainerState_CONTAINER_STATE_UNSPECIFIED
}

func mapHealth(s string) agentv1.ContainerHealth {
	switch strings.ToLower(s) {
	case "healthy":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY
	case "unhealthy":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_UNHEALTHY
	case "starting":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_STARTING
	case "":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_NONE
	}
	return agentv1.ContainerHealth_CONTAINER_HEALTH_UNSPECIFIED
}

// Validate refuses compose content that asks for host-level power: privileged containers, host namespaces, devices,
// dangerous capabilities and bind mounts the container policy would refuse (the docker socket among them).
func Validate(content string, binds BindChecker) error {
	var doc struct {
		Services map[string]map[string]any `yaml:"services"`
	}
	if err := yaml.Unmarshal([]byte(content), &doc); err != nil {
		return fmt.Errorf("invalid compose file: %w", err)
	}
	for name, svc := range doc.Services {
		fail := func(format string, a ...any) error {
			return fmt.Errorf("service %q: "+format, append([]any{name}, a...)...)
		}
		if v, _ := svc["privileged"].(bool); v {
			return fail("privileged containers are not allowed")
		}
		for _, k := range []string{"network_mode", "pid", "ipc", "userns_mode", "uts", "cgroup"} {
			if s, _ := svc[k].(string); s == "host" || strings.HasPrefix(s, "container:") && k != "network_mode" {
				return fail("%s: %s is not allowed", k, s)
			}
		}
		if s, _ := svc["network_mode"].(string); s == "host" {
			return fail("host networking is not allowed")
		}
		if d, ok := svc["devices"].([]any); ok && len(d) > 0 {
			return fail("host devices are not allowed")
		}
		if caps, ok := svc["cap_add"].([]any); ok {
			for _, c := range caps {
				cs := strings.ToUpper(fmt.Sprint(c))
				if cs == "ALL" || cs == "SYS_ADMIN" || cs == "SYS_MODULE" || cs == "SYS_PTRACE" || cs == "NET_ADMIN" {
					return fail("capability %s is not allowed", cs)
				}
			}
		}
		if opts, ok := svc["security_opt"].([]any); ok {
			for _, o := range opts {
				if strings.Contains(fmt.Sprint(o), "unconfined") {
					return fail("unconfined security options are not allowed")
				}
			}
		}
		vols, _ := svc["volumes"].([]any)
		for _, v := range vols {
			src := bindSource(v)
			if src == "" {
				continue
			}
			if strings.HasPrefix(src, "./") || src == "." {
				continue // inside the managed project directory
			}
			if strings.Contains(src, "..") {
				return fail("bind mount %q must not contain '..'", src)
			}
			if strings.HasPrefix(src, "/") || strings.HasPrefix(src, "~") {
				if binds == nil {
					return fail("bind mounts are disabled")
				}
				if err := binds.CheckBindSource(src); err != nil {
					return fail("%v", err)
				}
			}
		}
	}
	return nil
}

// bindSource extracts the host path of a volume entry, or "" for named volumes.
func bindSource(v any) string {
	switch t := v.(type) {
	case string:
		parts := strings.Split(t, ":")
		if len(parts) < 2 {
			return ""
		}
		src := parts[0]
		if strings.HasPrefix(src, "/") || strings.HasPrefix(src, ".") || strings.HasPrefix(src, "~") {
			return src
		}
	case map[string]any:
		if t["type"] == "bind" {
			s, _ := t["source"].(string)
			return s
		}
	}
	return ""
}
