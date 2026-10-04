// Package proxy manages the per-server reverse proxy (WP3.3). The control plane's IProxyProvider renders the provider
// configuration; the agent only makes the proxy container present, running and configured. Traefik discovers routes from
// the labels of other containers, so no per-route command ever reaches the agent.
package proxy

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

const (
	// ContainerName is the proxy container on every server.
	ContainerName = "aethera-traefik"
	// DefaultNetwork is joined by the proxy and by every routed application.
	DefaultNetwork = "aethera-proxy"
	// DefaultImage and DefaultVersion are used when the command leaves them empty.
	DefaultImage   = "traefik"
	DefaultVersion = "v3.6"

	configLabel  = "aethera.proxy.config-sha256"
	managedLabel = "aethera.managed"
	acmeVolume   = "aethera-traefik-acme"
)

var (
	imageRe   = regexp.MustCompile(`^[a-z0-9]+([._/-][a-z0-9]+)*(:[0-9]+)?(/[a-z0-9._/-]+)?$`)
	versionRe = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`)
)

// Manager makes the proxy present.
type Manager struct {
	Docker docker.API
	// Dir is where the rendered configuration is stored (0600).
	Dir string
}

// Ensure creates or updates the proxy container so that it runs the given configuration.
func (m *Manager) Ensure(ctx context.Context, req *agentv1.ProxyEnsure) (*agentv1.ProxyEnsureResult, error) {
	if p := req.GetProvider(); p != "" && p != "traefik" {
		return nil, fmt.Errorf("unsupported proxy provider %q", p)
	}
	if len(req.GetConfig()) == 0 {
		return nil, errors.New("proxy configuration is required")
	}
	image := orDefault(req.GetImage(), DefaultImage)
	version := orDefault(req.GetVersion(), DefaultVersion)
	if !imageRe.MatchString(image) || !versionRe.MatchString(version) {
		return nil, errors.New("invalid proxy image or version")
	}
	network := orDefault(req.GetNetwork(), DefaultNetwork)
	sum := sha256.Sum256(req.GetConfig())
	sumHex := hex.EncodeToString(sum[:])
	ref := image + ":" + version
	res := &agentv1.ProxyEnsureResult{Provider: "traefik", Version: version, ConfigSha256: sumHex}

	if err := m.ensureNetwork(ctx, network); err != nil {
		return nil, err
	}

	cfgPath := filepath.Join(m.Dir, "traefik.yml")
	if err := os.MkdirAll(m.Dir, 0o700); err != nil {
		return nil, err
	}
	if cur, err := os.ReadFile(cfgPath); err != nil || string(cur) != string(req.GetConfig()) {
		if err := os.WriteFile(cfgPath, req.GetConfig(), 0o600); err != nil {
			return nil, err
		}
		res.Changed = true
	}

	existing, err := m.Docker.ContainerInspect(ctx, ContainerName, false)
	switch {
	case err == nil:
		same := existing.GetLabels()[configLabel] == sumHex && existing.GetImage() == ref
		running := existing.GetState() == agentv1.ContainerState_CONTAINER_STATE_RUNNING
		if same && running {
			res.ContainerId, res.Running = existing.GetId(), true
			return res, nil
		}
		if same && !running {
			if err := m.Docker.ContainerStart(ctx, ContainerName); err != nil {
				return nil, err
			}
			res.ContainerId, res.Running, res.Changed = existing.GetId(), true, true
			return res, nil
		}
		if !req.GetRestartOnChange() && running && existing.GetImage() == ref {
			// The file changed but the caller does not want a restart; Traefik watches the file itself.
			res.ContainerId, res.Running = existing.GetId(), true
			return res, nil
		}
		if err := m.Docker.ContainerRemove(ctx, ContainerName, true, false); err != nil {
			return nil, err
		}
	case errors.Is(err, docker.ErrNotFound):
	default:
		return nil, err
	}

	if err := m.Docker.ImagePull(ctx, ref, nil, "", nil); err != nil {
		return nil, err
	}
	id, _, err := m.Docker.ContainerCreate(ctx, Spec(ref, cfgPath, network, sumHex, req.GetEnv()))
	if err != nil {
		return nil, err
	}
	if err := m.Docker.ContainerStart(ctx, id); err != nil {
		return nil, err
	}
	res.ContainerId, res.Running, res.Changed = id, true, true
	return res, nil
}

// Spec is the container definition of the proxy. It mounts the docker socket read-only because Traefik's docker provider
// needs it; this is the one place the agent hands the socket to a container, and it is not reachable through any command.
func Spec(image, cfgPath, network, cfgSum string, env []*agentv1.EnvVar) *agentv1.ContainerSpec {
	return &agentv1.ContainerSpec{
		Image: image, Name: ContainerName,
		Labels: map[string]string{managedLabel: "true", "aethera.role": "proxy", configLabel: cfgSum, "traefik.enable": "false"},
		Env:    env,
		Ports: []*agentv1.PortMapping{
			{ContainerPort: 80, HostPort: 80, Protocol: agentv1.PortProtocol_PORT_PROTOCOL_TCP},
			{ContainerPort: 443, HostPort: 443, Protocol: agentv1.PortProtocol_PORT_PROTOCOL_TCP},
		},
		Mounts: []*agentv1.VolumeMount{
			{Type: agentv1.MountType_MOUNT_TYPE_BIND, Source: "/var/run/docker.sock", Target: "/var/run/docker.sock", ReadOnly: true},
			{Type: agentv1.MountType_MOUNT_TYPE_BIND, Source: filepath.ToSlash(cfgPath), Target: "/etc/traefik/traefik.yml", ReadOnly: true},
			{Type: agentv1.MountType_MOUNT_TYPE_VOLUME, Source: acmeVolume, Target: "/acme"},
		},
		Networks:      []*agentv1.NetworkAttachment{{Network: network}},
		RestartPolicy: &agentv1.RestartPolicy{Name: agentv1.RestartPolicyName_RESTART_POLICY_NAME_UNLESS_STOPPED},
		CapDrop:       []string{"ALL"},
	}
}

func (m *Manager) ensureNetwork(ctx context.Context, name string) error {
	if _, err := m.Docker.NetworkInspect(ctx, name); err == nil {
		return nil
	} else if !errors.Is(err, docker.ErrNotFound) {
		return err
	}
	_, err := m.Docker.NetworkCreate(ctx, &agentv1.NetworkCreate{
		Name: name, Attachable: true, IfNotExists: true, Labels: map[string]string{managedLabel: "true"},
	})
	return err
}

func orDefault(v, d string) string {
	if strings.TrimSpace(v) == "" {
		return d
	}
	return v
}
