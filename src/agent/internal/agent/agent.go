// Package agent wires the subsystems together: transport client, dispatcher,
// Docker operations, log streams, metrics, discovery, events, certificate
// renewal and self-update.
package agent

import (
	"path/filepath"
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"log/slog"
	"net/netip"

	"sync"
	"sync/atomic"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/keepalive"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
	"github.com/mihaim21/aethera-forge/agent/internal/compose"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/enroll"
	"github.com/mihaim21/aethera-forge/agent/internal/events"
	"github.com/mihaim21/aethera-forge/agent/internal/logstream"
	"github.com/mihaim21/aethera-forge/agent/internal/ops"
	"github.com/mihaim21/aethera-forge/agent/internal/pki"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
	"github.com/mihaim21/aethera-forge/agent/internal/proxy"
	"github.com/mihaim21/aethera-forge/agent/internal/selfupdate"
	"github.com/mihaim21/aethera-forge/agent/internal/state"
	"github.com/mihaim21/aethera-forge/agent/internal/transport"
)

// Collector produces metrics and discovery reports (metrics.Collector in
// production, a fake in tests).
type Collector interface {
	Metrics(ctx context.Context) (*agentv1.MetricsReport, error)
	Discovery(ctx context.Context) (*agentv1.DiscoveryReport, error)
}

// Options configure an Agent.
type Options struct {
	Config    *config.Config
	Dir       state.Dir
	Docker    docker.API
	Collector Collector
	Version   string
	Logger    *slog.Logger
	// Exe enables self-update when set (path of the running binary).
	Exe       string
	// DeployTools enables the build, compose and proxy subsystems (they need the docker and git CLIs on the host).
	DeployTools bool
	Restarter   selfupdate.Restarter
	Clock     transport.Clock
	Rand      func() float64
	Now       func() time.Time
	// Test hooks.
	DialOptions    []grpc.DialOption
	Keepalive      *keepalive.ClientParameters
	WelcomeTimeout time.Duration
	HealthyAfter   time.Duration
	TLSRetry       time.Duration
	StatusInterval time.Duration
	RenewCheck     time.Duration
	LogConfig      logstream.Config
	// LogForwarder, when set, receives the AGENT log stream once Run starts.
	LogForwarder *LogForwarder
}

// Agent is the assembled agent.
type Agent struct {
	o   Options
	log *slog.Logger

	identity atomic.Pointer[state.Identity]
	client   *transport.Client
	disp     *dispatch.Dispatcher
	logs     *logstream.Manager
	pol      *policy.Policy
	ring     *events.Ring
	conv     *events.Converter
	updater  *selfupdate.Updater
	store    *dispatch.IdemStore
	deps     *ops.Deps

	processID    string
	dockerStatus atomic.Int32
	kick         chan struct{}
	renewMu      sync.Mutex
	diskHigh     sync.Map

	baseCtx context.Context
}

// New assembles an agent from an enrolled state directory.
func New(o Options) (*Agent, error) {
	if o.Logger == nil {
		o.Logger = slog.Default()
	}
	if o.Now == nil {
		o.Now = time.Now
	}
	if o.StatusInterval <= 0 {
		o.StatusInterval = 5 * time.Second
	}
	if o.RenewCheck <= 0 {
		o.RenewCheck = time.Hour
	}
	id, err := o.Dir.LoadIdentity()
	if err != nil {
		return nil, err
	}
	a := &Agent{o: o, log: o.Logger, kick: make(chan struct{}, 1), processID: events.NewID()}
	a.identity.Store(id)
	a.dockerStatus.Store(int32(agentv1.DockerStatus_DOCKER_STATUS_UNSPECIFIED))
	return a, nil
}

// Identity returns the loaded identity.
func (a *Agent) Identity() *state.Identity { return a.identity.Load() }

// Client exposes the transport client (tests).
func (a *Agent) Client() *transport.Client { return a.client }

func (a *Agent) tlsConfig() (*tls.Config, error) {
	id := a.identity.Load()
	var cfg *tls.Config
	var err error
	for i := 0; i < 4; i++ {
		cfg, err = pki.MTLSConfig(id.Endpoint, a.o.Dir.CertPath(), a.o.Dir.KeyPath(), a.o.Dir.CAPath(), a.o.Config.TrustSystemRoots)
		if err == nil {
			return cfg, nil
		}
		time.Sleep(150 * time.Millisecond) // a renewal may be replacing key and cert
	}
	return nil, err
}

// Run connects and serves until ctx ends.
func (a *Agent) Run(ctx context.Context) error {
	cfg := a.o.Config
	if err := a.o.Dir.Ensure(); err != nil {
		return err
	}
	store, err := dispatch.OpenIdemStore(a.o.Dir.StatePath()+"/idempotency.jsonl", a.o.Now)
	if err != nil {
		return fmt.Errorf("open idempotency table: %w", err)
	}
	defer store.Close()
	a.store = store

	baseCtx, cancelBase := context.WithCancel(context.Background())
	defer cancelBase()
	a.baseCtx = baseCtx

	a.pol = policy.New(cfg)
	a.pol.SetSubnets(a.dockerSubnets)
	a.ring = events.NewRing(0, 0, a.o.Now)
	a.conv = events.NewConverter()

	// The transport client is created first: the log manager and dispatcher
	// send through it.
	h := &handler{a: a}
	a.client = transport.New(transport.Options{
		Endpoint: a.identity.Load().Endpoint, TLS: a.tlsConfig, Handler: h, Clock: a.o.Clock, Rand: a.o.Rand,
		Logger: a.log, WelcomeTimeout: a.o.WelcomeTimeout, DialOptions: a.o.DialOptions, Keepalive: a.o.Keepalive,
		HealthyAfter: a.o.HealthyAfter, TLSRetry: a.o.TLSRetry,
	})
	a.logs = logstream.NewManager(a.client, a.o.LogConfig)
	if a.o.LogForwarder != nil {
		a.o.LogForwarder.Attach(a.logs.Open(AgentLogStreamID, agentv1.LogSource_LOG_SOURCE_AGENT, false, nil))
	}

	if a.o.Exe != "" {
		pub, perr := cfg.ReleaseKey()
		if perr != nil {
			return perr
		}
		a.updater = &selfupdate.Updater{
			Exe: a.o.Exe, StateDir: cfg.StateDir, CurrentVersion: a.o.Version, PublicKey: pub, Restarter: a.o.Restarter, Now: a.o.Now,
		}
		if act := a.updater.OnStart(); act == selfupdate.RolledBack {
			a.log.Error("new agent version never confirmed; rolled back to the previous binary")
			return nil
		}
	}

	a.deps = &ops.Deps{
		Docker: a.o.Docker, Policy: a.pol, Logs: a.logs, BaseContext: baseCtx, Updater: a.updater, Now: a.o.Now,
		Discover: a.o.Collector.Discovery,
	}
	if a.o.DeployTools {
		work := string(a.o.Dir)
		runner := build.OSRunner{}
		a.deps.Builder = &build.Service{Runner: runner, Docker: a.o.Docker, Engines: build.DefaultEngines(), WorkDir: filepath.Join(work, "build"), Now: a.o.Now}
		a.deps.Compose = &compose.Service{Runner: runner, Dir: a.o.Dir.ProjectsPath(), Binds: a.pol}
		a.deps.Proxy = &proxy.Manager{Docker: a.o.Docker, Dir: filepath.Join(work, "proxy")}
	}
	caps := a.deps.Capabilities()
	a.disp = dispatch.New(dispatch.Config{
		Sender: a.client, Policy: a.pol, Store: store, Capabilities: caps, Now: a.o.Now,
		Skew: a.client.Skew, MaxConcurrent: cfg.MaxConcurrentCommands, BaseContext: baseCtx, Logger: a.log,
	})
	ops.Register(a.disp, a.deps)

	var bg sync.WaitGroup
	bg.Add(3)
	go func() { defer bg.Done(); a.monitorDocker(baseCtx) }()
	go func() { defer bg.Done(); a.watchEvents(baseCtx) }()
	go func() { defer bg.Done(); a.renewLoop(baseCtx) }()

	runErr := a.runClient(ctx)

	// Graceful shutdown: stop background work, give commands a moment.
	cancelBase()
	done := make(chan struct{})
	go func() { a.disp.Wait(); close(done) }()
	select {
	case <-done:
	case <-time.After(10 * time.Second):
	}
	a.logs.CloseAll()
	bg.Wait()
	if errors.Is(runErr, context.Canceled) {
		return nil
	}
	return runErr
}

// runClient runs the link; after a revocation it idles until the host is
// re-enrolled (a different certificate appears on disk).
func (a *Agent) runClient(ctx context.Context) error {
	for {
		err := a.client.Run(ctx)
		if !errors.Is(err, transport.ErrRevoked) {
			return err
		}
		a.log.Error("agent is revoked; waiting for re-enrollment (run 'aethera-agent enroll' with a new token)")
		if !a.waitForNewIdentity(ctx) {
			return ctx.Err()
		}
		id, lerr := a.o.Dir.LoadIdentity()
		if lerr != nil {
			continue
		}
		a.identity.Store(id)
		a.log.Info("new identity found; reconnecting")
	}
}

func (a *Agent) waitForNewIdentity(ctx context.Context) bool {
	old, _ := pki.ReadLeaf(a.o.Dir.CertPath())
	interval := 30 * time.Second
	if a.o.RenewCheck < interval {
		interval = a.o.RenewCheck
	}
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return false
		case <-t.C:
			cur, err := pki.ReadLeaf(a.o.Dir.CertPath())
			if err == nil && (old == nil || cur.Serial != old.Serial) {
				return true
			}
		}
	}
}

// dockerSubnets feeds the probe SSRF guard with Docker network subnets.
func (a *Agent) dockerSubnets(ctx context.Context) ([]netip.Prefix, error) {
	nets, err := a.o.Docker.NetworkList(ctx, nil)
	if err != nil {
		return nil, err
	}
	var out []netip.Prefix
	for _, n := range nets {
		for _, c := range n.GetIpam() {
			if p, perr := netip.ParsePrefix(c.GetSubnet()); perr == nil {
				out = append(out, p)
			}
		}
	}
	return out, nil
}

// publishEvent sends an event now or buffers it for the next session.
func (a *Agent) publishEvent(ev *agentv1.EventNotice) {
	if a.client != nil && a.client.Control(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_Event{Event: ev}}) {
		return
	}
	a.ring.Add(ev)
}

func (a *Agent) flushEvents(s *transport.Session) {
	for _, ev := range a.ring.Drain() {
		s.Control(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_Event{Event: ev}})
	}
}

func (a *Agent) systemEvent(t agentv1.EventType, msg string) {
	a.publishEvent(&agentv1.EventNotice{EventId: events.NewID(), Type: t, OccurredAt: timestamppb.New(a.o.Now()), Message: msg})
}

// monitorDocker tracks the daemon status for heartbeats and emits
// DOCKER_DAEMON_UP/DOWN on transitions.
func (a *Agent) monitorDocker(ctx context.Context) {
	t := time.NewTicker(a.o.StatusInterval)
	defer t.Stop()
	for {
		st, detail := a.o.Docker.Status(ctx)
		prev := agentv1.DockerStatus(a.dockerStatus.Swap(int32(st)))
		if prev != st {
			a.onDockerChange(prev, st, detail)
		}
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		}
	}
}

func (a *Agent) onDockerChange(prev, cur agentv1.DockerStatus, detail string) {
	up := cur == agentv1.DockerStatus_DOCKER_STATUS_RUNNING
	wasUp := prev == agentv1.DockerStatus_DOCKER_STATUS_RUNNING
	switch {
	case up && prev != agentv1.DockerStatus_DOCKER_STATUS_UNSPECIFIED:
		a.systemEvent(agentv1.EventType_EVENT_TYPE_DOCKER_DAEMON_UP, "docker daemon is reachable")
		a.kickDiscovery()
	case !up && (wasUp || prev == agentv1.DockerStatus_DOCKER_STATUS_UNSPECIFIED):
		a.systemEvent(agentv1.EventType_EVENT_TYPE_DOCKER_DAEMON_DOWN, "docker daemon unavailable: "+cur.String())
		a.kickDiscovery()
	}
	a.log.Info("docker status", "status", cur.String())
	_ = detail
}

func (a *Agent) kickDiscovery() {
	select {
	case a.kick <- struct{}{}:
	default:
	}
}

// watchEvents converts Docker events into notices, reconnecting to the daemon
// event stream whenever it drops.
func (a *Agent) watchEvents(ctx context.Context) {
	for ctx.Err() == nil {
		ectx, cancel := context.WithCancel(ctx)
		evs, errs := a.o.Docker.Events(ectx)
	loop:
		for {
			select {
			case <-ectx.Done():
				break loop
			case err := <-errs:
				if err != nil && ctx.Err() == nil {
					a.log.Debug("docker event stream ended", "error", err.Error())
				}
				break loop
			case ev, ok := <-evs:
				if !ok {
					break loop
				}
				if n, ok := a.conv.Convert(ev); ok {
					a.publishEvent(n)
				}
			}
		}
		cancel()
		select {
		case <-ctx.Done():
			return
		case <-time.After(5 * time.Second):
		}
	}
}

// renewLoop renews the client certificate inside its renewal window.
func (a *Agent) renewLoop(ctx context.Context) {
	t := time.NewTicker(a.o.RenewCheck)
	defer t.Stop()
	for {
		if id := a.identity.Load(); enroll.NeedsRenewal(id, a.o.Now()) {
			a.renew(ctx, "certificate is within its renewal window")
		}
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		}
	}
}

func (a *Agent) renew(ctx context.Context, why string) {
	a.renewMu.Lock()
	defer a.renewMu.Unlock()
	a.log.Info("renewing client certificate", "reason", why)
	id, err := enroll.Renew(ctx, a.o.Dir, a.o.Config.TrustSystemRoots, a.o.DialOptions...)
	if err != nil {
		a.log.Error("certificate renewal failed", "error", err.Error())
		return
	}
	a.identity.Store(id)
	a.client.Reload() // reconnect with the new certificate
}
