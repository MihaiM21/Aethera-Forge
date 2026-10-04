// Package ops contains the handlers behind the command allowlist: Docker
// containers, images, volumes, networks, prune, log streaming, health probes,
// discovery refresh and self-update. Compose, build, build-detect and proxy
// commands (Phase 3) are registered only when their subsystem is configured; otherwise the
// dispatcher answers them REJECTED_UNSUPPORTED instead of faking success.
package ops

import (
	"context"
	"errors"
	"strings"
	"sync"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
	"github.com/mihaim21/aethera-forge/agent/internal/compose"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/logstream"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
	"github.com/mihaim21/aethera-forge/agent/internal/proxy"
	"github.com/mihaim21/aethera-forge/agent/internal/selfupdate"
)

// Capability strings advertised in Hello.
const (
	CapContainers = "docker.containers"
	CapImages     = "docker.images"
	CapVolumes    = "docker.volumes"
	CapNetworks   = "docker.networks"
	CapProbe      = "health.probe"
	CapLogs       = "logs.follow"
	CapDiscovery  = "discovery"
	CapPrune      = "system.prune"
	CapSelfUpdate = "selfupdate"
)

// Deps are the collaborators of the handlers.
type Deps struct {
	Docker docker.API
	Policy *policy.Policy
	Logs   *logstream.Manager
	// BaseContext is the agent lifetime; log streams outlive their command.
	BaseContext context.Context
	// Discover builds a fresh DiscoveryReport.
	Discover func(ctx context.Context) (*agentv1.DiscoveryReport, error)
	// Updater may be nil, in which case self-update is not offered.
	Updater *selfupdate.Updater
	Now     func() time.Time
	// Builder, Compose and Proxy are the Phase 3 subsystems; a nil one is not registered (and not advertised).
	Builder *build.Service
	Compose *compose.Service
	Proxy   *proxy.Manager
	// LookPath finds host tools (nixpacks); exec.LookPath when nil.
	LookPath func(file string) (string, error)

	streams sync.Map // stream_id -> context.CancelFunc
}

// Capabilities returns the capability set these handlers provide.
func (d *Deps) Capabilities() []string {
	caps := []string{CapContainers, CapImages, CapVolumes, CapNetworks, CapProbe, CapLogs, CapDiscovery, CapPrune}
	if d.Updater != nil {
		caps = append(caps, CapSelfUpdate)
	}
	return append(caps, d.phase3Capabilities()...)
}

func (d *Deps) now() time.Time {
	if d.Now != nil {
		return d.Now()
	}
	return time.Now()
}

// Register wires every implemented command arm into the dispatcher.
func Register(disp *dispatch.Dispatcher, d *Deps) {
	if d.BaseContext == nil {
		d.BaseContext = context.Background()
	}
	r := disp.Register
	r("container_create", CapContainers, d.containerCreate)
	r("container_start", CapContainers, d.containerStart)
	r("container_stop", CapContainers, d.containerStop)
	r("container_restart", CapContainers, d.containerRestart)
	r("container_remove", CapContainers, d.containerRemove)
	r("container_inspect", CapContainers, d.containerInspect)
	r("container_list", CapContainers, d.containerList)

	r("image_pull", CapImages, d.imagePull)
	r("image_list", CapImages, d.imageList)
	r("image_remove", CapImages, d.imageRemove)
	r("image_prune", CapImages, d.imagePrune)
	r("image_inspect", CapImages, d.imageInspect)

	r("volume_create", CapVolumes, d.volumeCreate)
	r("volume_list", CapVolumes, d.volumeList)
	r("volume_remove", CapVolumes, d.volumeRemove)
	r("volume_prune", CapVolumes, d.volumePrune)

	r("network_create", CapNetworks, d.networkCreate)
	r("network_list", CapNetworks, d.networkList)
	r("network_remove", CapNetworks, d.networkRemove)
	r("network_connect", CapNetworks, d.networkConnect)
	r("network_disconnect", CapNetworks, d.networkDisconnect)
	r("network_prune", CapNetworks, d.networkPrune)

	r("log_stream_start", CapLogs, d.logStreamStart)
	r("log_stream_stop", CapLogs, d.logStreamStop)
	r("health_probe", CapProbe, d.healthProbe)
	r("discovery_refresh", CapDiscovery, d.discoveryRefresh)
	r("system_prune", CapPrune, d.systemPrune)
	d.registerPhase3(r)
	if d.Updater != nil {
		r("agent_self_update", CapSelfUpdate, d.agentSelfUpdate)
	}
}

func empty() *agentv1.CommandResult {
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Empty{Empty: &agentv1.EmptyResult{}}}
}

func invalidArg(format string, a ...any) error {
	return dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, format, a...)
}

// matchLabels implements "key" / "key=value" filters (all must match).
func matchLabels(labels map[string]string, filters []string) bool {
	for _, f := range filters {
		k, v, hasV := strings.Cut(f, "=")
		got, ok := labels[k]
		if !ok || (hasV && got != v) {
			return false
		}
	}
	return true
}

func isNotFound(err error) bool { return errors.Is(err, docker.ErrNotFound) }

// dur converts an optional protobuf duration (nil = use the default).
func dur(d *durationpb.Duration) *time.Duration {
	if d == nil {
		return nil
	}
	v := d.AsDuration()
	return &v
}
