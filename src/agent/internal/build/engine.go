package build

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	"google.golang.org/protobuf/proto"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

// Job is what an Engine gets for one build. Tags are applied to the image in the local daemon.
type Job struct {
	Req *agentv1.BuildRequest
	// Dir is the build context directory (the repository root joined with context_path). Empty for the image engine.
	Dir  string
	Tags []string
	// Out and Err receive build output already masked for secrets.
	Out, Err io.Writer
	Runner   Runner
	Docker   docker.API
	// TempDir is private scratch space removed after the build.
	TempDir  string
	Progress func(step, message string, percent int32)
}

// Engine builds an image from a Job (spec section 4, IBuildEngine on the control-plane side).
type Engine interface {
	Name() string
	Build(ctx context.Context, j *Job) error
}

// ErrUnsupportedEngine is returned for an engine name nobody registered.
type ErrUnsupportedEngine struct{ Name string }

func (e *ErrUnsupportedEngine) Error() string {
	return fmt.Sprintf("unsupported build engine %q", e.Name)
}

// Engines is the registry used by Service.
type Engines map[string]Engine

// DefaultEngines registers dockerfile, nixpacks, static and image.
func DefaultEngines() Engines {
	e := Engines{}
	for _, x := range []Engine{DockerfileEngine{}, NixpacksEngine{}, StaticEngine{}, ImageEngine{}} {
		e[x.Name()] = x
	}
	return e
}

// Select picks the engine named by engine_name (which wins) or by the enum.
func (e Engines) Select(req *agentv1.BuildRequest) (Engine, error) {
	name := req.GetEngineName()
	if name == "" {
		switch req.GetEngine() {
		case agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE:
			name = "dockerfile"
		case agentv1.BuildEngine_BUILD_ENGINE_NIXPACKS:
			name = "nixpacks"
		case agentv1.BuildEngine_BUILD_ENGINE_STATIC:
			name = "static"
		case agentv1.BuildEngine_BUILD_ENGINE_IMAGE:
			name = "image"
		}
	}
	if eng, ok := e[name]; ok {
		return eng, nil
	}
	return nil, &ErrUnsupportedEngine{Name: name}
}

var envNameRe = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*$`)

func envValue(v *agentv1.EnvVar) string {
	if s := v.GetSecret(); s != nil {
		return s.GetValue()
	}
	return v.GetPlain()
}

// envPairs validates names and returns KEY=VALUE entries for the process environment. Values are passed this way, never as
// arguments, so they do not show up in the process list.
func envPairs(vars []*agentv1.EnvVar) ([]string, []string, error) {
	var pairs, names []string
	for _, v := range vars {
		if !envNameRe.MatchString(v.GetName()) {
			return nil, nil, fmt.Errorf("invalid environment variable name %q", v.GetName())
		}
		pairs = append(pairs, v.GetName()+"="+envValue(v))
		names = append(names, v.GetName())
	}
	return pairs, names, nil
}

// registryConfigDir writes a throw-away docker config holding credentials and returns its path.
func registryConfigDir(parent string, auths ...*agentv1.RegistryAuth) (string, error) {
	type entry struct {
		Auth          string `json:"auth,omitempty"`
		IdentityToken string `json:"identitytoken,omitempty"`
	}
	cfg := struct {
		Auths map[string]entry `json:"auths"`
	}{Auths: map[string]entry{}}
	for _, a := range auths {
		if a == nil || a.GetServer() == "" {
			continue
		}
		e := entry{IdentityToken: a.GetIdentityToken().GetValue()}
		if a.GetPassword().GetValue() != "" {
			e.Auth = base64.StdEncoding.EncodeToString([]byte(a.GetUsername() + ":" + a.GetPassword().GetValue()))
		}
		cfg.Auths[a.GetServer()] = e
	}
	dir := filepath.Join(parent, "docker-config")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", err
	}
	b, _ := json.Marshal(cfg)
	return dir, os.WriteFile(filepath.Join(dir, "config.json"), b, 0o600)
}

// ---- dockerfile -------------------------------------------------------------------------------------------------

// DockerfileEngine builds with `docker build` (BuildKit).
type DockerfileEngine struct{}

// Name implements Engine.
func (DockerfileEngine) Name() string { return "dockerfile" }

// Build implements Engine.
func (DockerfileEngine) Build(ctx context.Context, j *Job) error {
	req := j.Req
	file := req.GetDockerfilePath()
	if file == "" {
		file = "Dockerfile"
	}
	dockerfile, err := ContextDir(j.Dir, file)
	if err != nil {
		return fmt.Errorf("dockerfile path: %w", err)
	}
	return dockerBuild(ctx, j, dockerfile, j.Dir)
}

// dockerBuild runs `docker build` for a Dockerfile and context using the request's args, secrets, cache and platforms.
func dockerBuild(ctx context.Context, j *Job, dockerfile, contextDir string) error {
	req := j.Req
	args := []string{"build", "-f", dockerfile}
	for _, t := range j.Tags {
		args = append(args, "-t", t)
	}
	if t := req.GetTargetStage(); t != "" {
		if strings.HasPrefix(t, "-") {
			return fmt.Errorf("invalid target stage")
		}
		args = append(args, "--target", t)
	}
	env := []string{"DOCKER_BUILDKIT=1", "BUILDKIT_PROGRESS=plain"}

	argPairs, argNames, err := envPairs(req.GetBuildArgs())
	if err != nil {
		return err
	}
	env = append(env, argPairs...)
	for _, n := range argNames {
		args = append(args, "--build-arg", n) // value comes from the environment
	}
	for _, s := range req.GetSecrets() {
		if !envNameRe.MatchString(s.GetId()) {
			return fmt.Errorf("invalid build secret id %q", s.GetId())
		}
		p := filepath.Join(j.TempDir, "secret-"+s.GetId())
		if err := os.WriteFile(p, []byte(s.GetValue().GetValue()), 0o600); err != nil {
			return err
		}
		args = append(args, "--secret", "id="+s.GetId()+",src="+p)
	}

	if c := req.GetCache(); c != nil {
		switch {
		case c.GetNoCache():
			args = append(args, "--no-cache")
		case c.GetEnabled():
			for _, f := range c.GetCacheFrom() {
				args = append(args, "--cache-from", f)
			}
			if c.GetCacheTo() != "" {
				args = append(args, "--cache-to", c.GetCacheTo())
			}
			if c.GetMode() == agentv1.CacheMode_CACHE_MODE_INLINE {
				args = append(args, "--build-arg", "BUILDKIT_INLINE_CACHE=1")
				env = append(env, "BUILDKIT_INLINE_CACHE=1")
			}
		}
	}
	if len(req.GetTargetPlatforms()) > 0 {
		args = append(args, "--platform", strings.Join(req.GetTargetPlatforms(), ","))
	}
	for k, v := range req.GetImageLabels() {
		args = append(args, "--label", k+"="+v)
	}
	args = append(args, "--", contextDir)

	j.Progress("build", "docker build", -1)
	return j.Runner.Run(ctx, Cmd{Name: "docker", Args: args, Dir: contextDir, Env: env, Stdout: j.Out, Stderr: j.Err})
}

// ---- nixpacks ---------------------------------------------------------------------------------------------------

// NixpacksEngine builds with the nixpacks CLI. It stays behind Engine so Railpack can replace it later.
type NixpacksEngine struct{}

// Name implements Engine.
func (NixpacksEngine) Name() string { return "nixpacks" }

// Build implements Engine.
func (NixpacksEngine) Build(ctx context.Context, j *Job) error {
	req := j.Req
	args := []string{"build", j.Dir}
	for _, t := range j.Tags {
		args = append(args, "--tag", t)
	}
	pairs, names, err := envPairs(req.GetEnv())
	if err != nil {
		return err
	}
	for _, n := range names {
		args = append(args, "--env", n) // nixpacks reads the value from its environment
	}
	if v := req.GetInstallCommand(); v != "" {
		args = append(args, "--install-cmd", v)
	}
	if v := req.GetBuildCommand(); v != "" {
		args = append(args, "--build-cmd", v)
	}
	if v := req.GetStartCommand(); v != "" {
		args = append(args, "--start-cmd", v)
	}
	if len(req.GetTargetPlatforms()) == 1 {
		args = append(args, "--platform", req.GetTargetPlatforms()[0])
	}
	if c := req.GetCache(); c != nil && c.GetNoCache() {
		args = append(args, "--no-cache")
	}
	j.Progress("build", "nixpacks build", -1)
	return j.Runner.Run(ctx, Cmd{Name: "nixpacks", Args: args, Dir: j.Dir, Env: pairs, Stdout: j.Out, Stderr: j.Err})
}

// ---- static -----------------------------------------------------------------------------------------------------

// StaticEngine builds assets in a Node container, then serves them from a small nginx image.
type StaticEngine struct{}

// Name implements Engine.
func (StaticEngine) Name() string { return "static" }

// StaticDockerfile renders the multi-stage Dockerfile. With no build command the context is served as is.
func StaticDockerfile(req *agentv1.BuildRequest) (string, error) {
	out := req.GetOutputDir()
	if out == "" {
		out = "dist"
	}
	if strings.ContainsAny(out, "\r\n\x00") || strings.HasPrefix(out, "/") || strings.Contains(out, "..") {
		return "", fmt.Errorf("invalid output directory")
	}
	var b strings.Builder
	if req.GetBuildCommand() == "" && req.GetInstallCommand() == "" {
		b.WriteString("FROM nginx:alpine\n")
		b.WriteString("COPY " + shellQuoteCopy(out) + " /usr/share/nginx/html\n")
	} else {
		b.WriteString("FROM node:22-alpine AS build\nWORKDIR /app\nCOPY . .\n")
		for _, v := range req.GetEnv() {
			if !envNameRe.MatchString(v.GetName()) {
				return "", fmt.Errorf("invalid environment variable name %q", v.GetName())
			}
			b.WriteString("ARG " + v.GetName() + "\nENV " + v.GetName() + "=$" + v.GetName() + "\n")
		}
		if v := req.GetInstallCommand(); v != "" {
			b.WriteString("RUN " + oneLine(v) + "\n")
		}
		if v := req.GetBuildCommand(); v != "" {
			b.WriteString("RUN " + oneLine(v) + "\n")
		}
		b.WriteString("FROM nginx:alpine\n")
		b.WriteString("COPY --from=build " + shellQuoteCopy("/app/"+strings.TrimPrefix(out, "./")) + " /usr/share/nginx/html\n")
	}
	if req.GetSpaFallback() {
		b.WriteString("COPY <<'NGINX' /etc/nginx/conf.d/default.conf\n")
		b.WriteString("server {\n  listen 80;\n  root /usr/share/nginx/html;\n  location / { try_files $uri $uri/ /index.html; }\n}\nNGINX\n")
	}
	b.WriteString("EXPOSE 80\n")
	return b.String(), nil
}

func oneLine(s string) string { return strings.NewReplacer("\r", " ", "\n", " ").Replace(s) }

// shellQuoteCopy keeps a COPY path as one JSON-array-free token; paths with spaces use the JSON form.
func shellQuoteCopy(p string) string {
	if strings.ContainsAny(p, " \t\"") {
		b, _ := json.Marshal(p)
		return string(b)
	}
	return p
}

// Build implements Engine.
func (StaticEngine) Build(ctx context.Context, j *Job) error {
	df, err := StaticDockerfile(j.Req)
	if err != nil {
		return err
	}
	p := filepath.Join(j.TempDir, "Dockerfile.static")
	if err := os.WriteFile(p, []byte(df), 0o600); err != nil {
		return err
	}
	// Env values are passed as build args (static sites bake public config at build time).
	req := j.Req
	if len(req.GetEnv()) > 0 {
		cp := proto.Clone(req).(*agentv1.BuildRequest)
		cp.BuildArgs = append(cp.BuildArgs, cp.GetEnv()...)
		j2 := *j
		j2.Req = cp
		return dockerBuild(ctx, &j2, p, j.Dir)
	}
	return dockerBuild(ctx, j, p, j.Dir)
}

// ---- image ------------------------------------------------------------------------------------------------------

// ImageEngine pulls a prebuilt image and tags it; nothing is built.
type ImageEngine struct{}

// Name implements Engine.
func (ImageEngine) Name() string { return "image" }

// Build implements Engine.
func (ImageEngine) Build(ctx context.Context, j *Job) error {
	ref := j.Req.GetImageReference()
	if ref == "" || strings.HasPrefix(ref, "-") {
		return fmt.Errorf("a valid image_reference is required")
	}
	j.Progress("pull", "pulling "+ref, 0)
	if err := j.Docker.ImagePull(ctx, ref, j.Req.GetImagePullAuth(), "", func(l string) { fmt.Fprintln(j.Out, l) }); err != nil {
		return err
	}
	for _, t := range j.Tags {
		if t == ref {
			continue
		}
		if err := j.Runner.Run(ctx, Cmd{Name: "docker", Args: []string{"tag", "--", ref, t}, Stdout: j.Out, Stderr: j.Err}); err != nil {
			return fmt.Errorf("tag %s: %w", t, err)
		}
	}
	return nil
}
