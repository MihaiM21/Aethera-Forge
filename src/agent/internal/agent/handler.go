package agent

import (
	"context"
	"runtime"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/enroll"
	"github.com/mihaim21/aethera-forge/agent/internal/events"
	"github.com/mihaim21/aethera-forge/agent/internal/metrics"
	"github.com/mihaim21/aethera-forge/agent/internal/transport"
)

const (
	defaultMetricsInterval = 10 * time.Second
	diskPressurePercent    = 90.0
)

// handler adapts the Agent to transport.Handler.
type handler struct{ a *Agent }

var _ transport.Handler = (*handler)(nil)

func (h *handler) Hello(attempt uint32) *agentv1.Hello {
	a := h.a
	id := a.identity.Load()
	return &agentv1.Hello{
		ServerId:           id.ServerID,
		AgentVersion:       a.o.Version,
		ProtocolVersion:    enroll.ProtocolVersion,
		Capabilities:       a.disp.Capabilities(),
		Os:                 runtime.GOOS,
		Architecture:       runtime.GOARCH,
		BootId:             metrics.BootID(),
		ProcessId:          a.processID,
		RunningCommandIds:  a.disp.RunningIDs(),
		ActiveLogStreamIds: a.logs.ActiveIDs(),
		ReconnectAttempt:   attempt,
	}
}

func (h *handler) DockerStatus() agentv1.DockerStatus {
	return agentv1.DockerStatus(h.a.dockerStatus.Load())
}

func (h *handler) RunningCommands() uint32 { return uint32(h.a.disp.Running()) }

// OnWelcome applies server-driven config and starts the per-session loops.
func (h *handler) OnWelcome(s *transport.Session) {
	a := h.a
	w := s.Welcome
	limit := a.o.Config.MaxConcurrentCommands
	if m := int(w.GetMaxConcurrentCommands()); m > 0 && (limit <= 0 || m < limit) {
		limit = m
	}
	a.disp.SetMaxConcurrent(limit)
	a.logs.SetLimits(w.GetLogChunkMaxBytes(), w.GetLogInitialWindowBytes())
	if a.updater != nil {
		a.updater.Confirm() // reached Welcome: a freshly installed binary is healthy
	}
	a.flushEvents(s)

	ctx := s.Context()
	go a.metricsLoop(ctx, s)
	go a.discoveryLoop(ctx, s)
}

func (h *handler) OnControl(s *transport.Session, msg *agentv1.ControlMessage) {
	a := h.a
	switch p := msg.GetPayload().(type) {
	case *agentv1.ControlMessage_Command:
		a.disp.Handle(p.Command)
	case *agentv1.ControlMessage_CancelCommand:
		a.disp.Cancel(p.CancelCommand)
	case *agentv1.ControlMessage_LogFlowControl:
		a.logs.FlowControl(p.LogFlowControl)
	case *agentv1.ControlMessage_CertRotation:
		a.log.Info("control plane asked for certificate rotation", "reason", p.CertRotation.GetReason())
		go a.renew(a.baseCtx, "rotation hint")
	default:
		a.log.Debug("ignoring unknown control message")
	}
}

func (h *handler) OnSessionEnd(*transport.Session) { h.a.logs.SessionEnded() }

func (h *handler) OnUpgradeRequired(d *agentv1.Disconnect) {
	h.a.log.Warn("control plane requires a newer agent; waiting for a self-update command", "message", d.GetMessage())
}

func (a *Agent) metricsLoop(ctx context.Context, s *transport.Session) {
	interval := s.Welcome.GetMetricsInterval().AsDuration()
	if interval <= 0 {
		interval = defaultMetricsInterval
	}
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		rep, err := a.o.Collector.Metrics(ctx)
		if err == nil && rep != nil {
			a.checkDisks(rep)
			// Metrics are not buffered across gaps: a failed send is dropped.
			s.Data(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_Metrics{Metrics: rep}})
		}
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		}
	}
}

// checkDisks raises DISK_PRESSURE once when a filesystem crosses the threshold.
func (a *Agent) checkDisks(rep *agentv1.MetricsReport) {
	for _, d := range rep.GetHost().GetDisks() {
		if d.GetTotalBytes() <= 0 {
			continue
		}
		high := float64(d.GetUsedBytes())/float64(d.GetTotalBytes())*100 >= diskPressurePercent
		was, _ := a.diskHigh.Load(d.GetMountPoint())
		if high && was != true {
			a.diskHigh.Store(d.GetMountPoint(), true)
			a.publishEvent(&agentv1.EventNotice{
				EventId: events.NewID(), Type: agentv1.EventType_EVENT_TYPE_DISK_PRESSURE,
				OccurredAt: timestamppb.New(a.o.Now()), Message: "disk usage above 90% on " + d.GetMountPoint(),
			})
		} else if !high && was == true {
			a.diskHigh.Store(d.GetMountPoint(), false)
		}
	}
}

func (a *Agent) discoveryLoop(ctx context.Context, s *transport.Session) {
	var tick <-chan time.Time
	if iv := s.Welcome.GetDiscoveryInterval().AsDuration(); iv > 0 {
		t := time.NewTicker(iv)
		defer t.Stop()
		tick = t.C
	}
	send := func() {
		rep, err := a.o.Collector.Discovery(ctx)
		if err != nil || rep == nil {
			return
		}
		s.Data(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_Discovery{Discovery: rep}})
	}
	send()
	for {
		select {
		case <-ctx.Done():
			return
		case <-tick:
			send()
		case <-a.kick:
			send()
		}
	}
}
