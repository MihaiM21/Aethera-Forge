package build

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"strconv"
	"sync"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

const (
	// BuilderName is the BuildKit builder container of the build quota (ADR 0012) and the buildx builder that talks to it.
	BuilderName = "aethera-buildkit"

	// BuildKitImage runs the builder. Pinned: a floating tag would change the builder under a running installation.
	BuildKitImage = "moby/buildkit:v0.23.2"

	limitsLabel = "aethera.buildkit.limits"
)

// Builders hands out a buildx builder whose BuildKit daemon runs with the given resource limits.
type Builders interface {
	// Ensure makes the builder exist with exactly these limits (created, or recreated when its limits changed) and returns its buildx name.
	Ensure(ctx context.Context, limits *agentv1.ResourceLimits) (string, error)
}

// QuotaRequested is true when a build request carries a build quota (ADR 0012): any non-zero CPU or memory limit.
func QuotaRequested(l *agentv1.ResourceLimits) bool {
	return l != nil && (l.GetCpuLimitCores() > 0 || l.GetMemoryLimitBytes() > 0)
}

// BuildKit manages the container `aethera-buildkit` and its buildx builder. Everything it runs is a fixed docker argument vector, never a
// shell; the only inputs are numbers (limits) from the control plane.
type BuildKit struct {
	Runner Runner
	Docker docker.API
	// Image overrides BuildKitImage (tests, air-gapped hosts with a mirror).
	Image string

	mu sync.Mutex
}

func (b *BuildKit) image() string {
	if b.Image != "" {
		return b.Image
	}
	return BuildKitImage
}

// fingerprint identifies the configuration a builder container was created with.
func (b *BuildKit) fingerprint(l *agentv1.ResourceLimits) string {
	return fmt.Sprintf("cpu=%s;mem=%d;image=%s", strconv.FormatFloat(l.GetCpuLimitCores(), 'f', -1, 64), l.GetMemoryLimitBytes(), b.image())
}

// Ensure implements Builders.
func (b *BuildKit) Ensure(ctx context.Context, limits *agentv1.ResourceLimits) (string, error) {
	if !QuotaRequested(limits) {
		return "", errors.New("no build quota to apply")
	}
	b.mu.Lock()
	defer b.mu.Unlock()

	want := b.fingerprint(limits)
	recreate := false
	info, err := b.Docker.ContainerInspect(ctx, BuilderName, false)
	switch {
	case err == nil:
		if info.GetLabels()[limitsLabel] == want && info.GetState() == agentv1.ContainerState_CONTAINER_STATE_RUNNING {
			if b.builderExists(ctx) {
				return BuilderName, nil
			}
		} else {
			// Other limits (or stopped): the container is replaced, so the quota is exactly what was asked for.
			if err := b.Docker.ContainerRemove(ctx, BuilderName, true, false); err != nil && !errors.Is(err, docker.ErrNotFound) {
				return "", fmt.Errorf("remove the old builder: %w", err)
			}
			recreate = true
		}
	case errors.Is(err, docker.ErrNotFound):
		recreate = true
	default:
		return "", fmt.Errorf("inspect the builder: %w", err)
	}

	if recreate {
		// The buildx builder points at the old container: drop it, ignoring "no such builder".
		_ = b.docker(ctx, nil, "buildx", "rm", "--force", BuilderName)
		args := []string{"run", "-d", "--name", BuilderName, "--privileged", "--restart", "unless-stopped",
			"--label", "aethera.managed=true", "--label", limitsLabel + "=" + want}
		if c := limits.GetCpuLimitCores(); c > 0 {
			args = append(args, "--cpus", strconv.FormatFloat(c, 'f', -1, 64))
		}
		if m := limits.GetMemoryLimitBytes(); m > 0 {
			// Memory and swap equal: the quota is a hard ceiling, not a suggestion the host's swap can stretch.
			args = append(args, "--memory", strconv.FormatInt(m, 10), "--memory-swap", strconv.FormatInt(m, 10))
		}
		args = append(args, "--", b.image())
		var out bytes.Buffer
		if err := b.docker(ctx, &out, args...); err != nil {
			return "", fmt.Errorf("start the BuildKit builder: %w: %s", err, out.String())
		}
	}

	if !b.builderExists(ctx) {
		var out bytes.Buffer
		if err := b.docker(ctx, &out, "buildx", "create", "--name", BuilderName, "--driver", "remote", "docker-container://"+BuilderName); err != nil {
			return "", fmt.Errorf("create the buildx builder: %w: %s", err, out.String())
		}
	}
	return BuilderName, nil
}

func (b *BuildKit) builderExists(ctx context.Context) bool {
	return b.docker(ctx, nil, "buildx", "inspect", BuilderName) == nil
}

func (b *BuildKit) docker(ctx context.Context, out *bytes.Buffer, args ...string) error {
	c := Cmd{Name: "docker", Args: args}
	if out != nil {
		c.Stdout, c.Stderr = out, out
	}
	return b.Runner.Run(ctx, c)
}
