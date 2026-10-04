package agent

import (
	"context"
	"io"
	"log/slog"
	"net"
	"os"
	"strconv"
	"testing"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
	"github.com/mihaim21/aethera-forge/agent/internal/enroll"
	"github.com/mihaim21/aethera-forge/agent/internal/fakecp"
	"github.com/mihaim21/aethera-forge/agent/internal/state"
)

const wait = 5 * time.Second

type fakeCollector struct{}

func (fakeCollector) Metrics(context.Context) (*agentv1.MetricsReport, error) {
	return &agentv1.MetricsReport{CollectedAt: timestamppb.Now(), Host: &agentv1.HostMetrics{CpuPercent: 1}}, nil
}

func (fakeCollector) Discovery(context.Context) (*agentv1.DiscoveryReport, error) {
	return &agentv1.DiscoveryReport{CollectedAt: timestamppb.Now(), Host: &agentv1.HostFacts{Hostname: "test-host"}}, nil
}

// stepClock blocks every Sleep until the test releases it, and records delays.
type stepClock struct {
	sleeps  chan time.Duration
	release chan struct{}
	auto    bool
}

func newStepClock() *stepClock {
	return &stepClock{sleeps: make(chan time.Duration, 64), release: make(chan struct{}, 64)}
}

func (c *stepClock) Now() time.Time { return time.Now() }

func (c *stepClock) Sleep(ctx context.Context, d time.Duration) error {
	c.sleeps <- d
	if c.auto {
		return ctx.Err()
	}
	select {
	case <-c.release:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

type testEnv struct {
	t      *testing.T
	cp     *fakecp.Server
	dk     *dockertest.Fake
	dir    state.Dir
	agent  *Agent
	cancel context.CancelFunc
	done   chan error
	clock  *stepClock
}

type setup struct {
	cp       func(*fakecp.Server)
	opts     func(*Options)
	clock    *stepClock
	randZero bool
}

func newEnv(t *testing.T, s setup) *testEnv {
	t.Helper()
	cp, err := fakecp.Start("join-token-1", "srv-1")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(cp.Stop)
	if s.cp != nil {
		s.cp(cp)
	}
	dir := state.Dir(t.TempDir())
	ctx := context.Background()
	if _, err := enroll.Enroll(ctx, enroll.Params{
		Endpoint: cp.Addr, Token: "join-token-1", CASHA256: cp.CA.Fingerprint, Dir: dir, Version: "test",
	}); err != nil {
		t.Fatalf("enroll: %v", err)
	}
	cfg := config.Default()
	cfg.StateDir = string(dir)
	cfg.Policy.AllowedBindPrefixes = nil
	cfg.Finalize()

	dk := dockertest.New()
	clock := s.clock
	if clock == nil {
		clock = newStepClock()
		clock.auto = true
	}
	o := Options{
		Config: cfg, Dir: dir, Docker: dk, Collector: fakeCollector{}, Version: "test-1.0",
		Logger: slog.New(slog.NewTextHandler(io.Discard, nil)), Clock: clock,
		Rand:           func() float64 { return 0 }, // full jitter drawn as 0: reconnect at once
		StatusInterval: 50 * time.Millisecond, RenewCheck: time.Hour,
	}
	if s.randZero {
		o.Rand = func() float64 { return 0.999 }
	}
	if s.opts != nil {
		s.opts(&o)
	}
	a, err := New(o)
	if err != nil {
		t.Fatal(err)
	}
	rctx, cancel := context.WithCancel(ctx)
	e := &testEnv{t: t, cp: cp, dk: dk, dir: dir, agent: a, cancel: cancel, done: make(chan error, 1), clock: clock}
	go func() { e.done <- a.Run(rctx) }()
	t.Cleanup(func() {
		cancel()
		select {
		case <-e.done:
		case <-time.After(15 * time.Second):
			t.Error("agent did not stop")
		}
	})
	return e
}

func (e *testEnv) nextConn() *fakecp.Conn {
	e.t.Helper()
	select {
	case c := <-e.cp.Conns():
		return c
	case <-time.After(wait):
		e.t.Fatal("agent did not connect")
		return nil
	}
}

func isAck(id string) func(*agentv1.AgentMessage) bool {
	return func(m *agentv1.AgentMessage) bool { return m.GetCommandAck().GetCommandId() == id }
}

func isResult(id string) func(*agentv1.AgentMessage) bool {
	return func(m *agentv1.AgentMessage) bool { return m.GetCommandResult().GetCommandId() == id }
}

func (e *testEnv) ack(c *fakecp.Conn, id string) *agentv1.CommandAck {
	e.t.Helper()
	m := c.Await(isAck(id), wait)
	if m == nil {
		e.t.Fatalf("no ack for %s", id)
	}
	return m.GetCommandAck()
}

func (e *testEnv) result(c *fakecp.Conn, id string) *agentv1.CommandResult {
	e.t.Helper()
	m := c.Await(isResult(id), wait)
	if m == nil {
		e.t.Fatalf("no result for %s", id)
	}
	return m.GetCommandResult()
}

func send(t *testing.T, c *fakecp.Conn, m *agentv1.ControlMessage) {
	t.Helper()
	if err := c.Send(m); err != nil {
		t.Fatalf("send: %v", err)
	}
}

func command(c *agentv1.Command) *agentv1.ControlMessage {
	return &agentv1.ControlMessage{Payload: &agentv1.ControlMessage_Command{Command: c}}
}

func listCmd(id, key string, deadline time.Time) *agentv1.ControlMessage {
	c := &agentv1.Command{
		CommandId: id, IdempotencyKey: key,
		Request: &agentv1.Command_ContainerList{ContainerList: &agentv1.ContainerList{All: true}},
	}
	if !deadline.IsZero() {
		c.Deadline = timestamppb.New(deadline)
	}
	return command(c)
}

func cancelMsg(id string, grace time.Duration) *agentv1.ControlMessage {
	return &agentv1.ControlMessage{Payload: &agentv1.ControlMessage_CancelCommand{CancelCommand: &agentv1.CancelCommand{
		CommandId: id, Reason: "test", Grace: durationpb.New(grace),
	}}}
}

func disconnect(reason agentv1.DisconnectReason, after time.Duration) *agentv1.ControlMessage {
	return &agentv1.ControlMessage{Payload: &agentv1.ControlMessage_Disconnect{Disconnect: &agentv1.Disconnect{
		Reason: reason, ReconnectAfter: durationpb.New(after),
	}}}
}

// eventually polls cond.
func eventually(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(wait)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

func readFile(t *testing.T, path string) []byte {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func splitHostPort(t *testing.T, addr string) (string, uint32) {
	t.Helper()
	h, p, err := net.SplitHostPort(addr)
	if err != nil {
		t.Fatal(err)
	}
	n, _ := strconv.Atoi(p)
	return h, uint32(n)
}
