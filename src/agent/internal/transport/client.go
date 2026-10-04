// Package transport is the agent's persistent gRPC link to the control plane
// (ADR 0002): one AgentService.Connect stream over mTLS (TLS 1.3, trusting only
// the Aethera CA), Hello/Welcome, application heartbeats, HTTP/2 keepalive,
// full-jitter reconnect backoff, supersede/disconnect handling, clock skew
// measurement and the priority send queue.
package transport

import (
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"sync"
	"sync/atomic"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/keepalive"
	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/pki"
)

// ErrRevoked means the control plane revoked this agent; reconnecting is
// pointless until it is enrolled again.
var ErrRevoked = errors.New("agent revoked by the control plane: re-enroll with a new join token")

const (
	maxMsgSize         = 8 << 20
	defaultWelcomeWait = 30 * time.Second
	defaultHeartbeat   = 15 * time.Second
	// Transport keepalive (ADR 0002): ping after 20 s idle, 10 s ack timeout.
	keepaliveTime    = 20 * time.Second
	keepaliveTimeout = 10 * time.Second
)

// Session is one live Connect stream.
type Session struct {
	Welcome *agentv1.Welcome
	// Skew is (control plane clock - local clock) measured at Welcome.
	Skew time.Duration
	// Epoch increments with every new session.
	Epoch uint64

	out  *Sender
	ctx  context.Context
	seq  atomic.Uint64
	boot time.Time
}

// Control queues a control-priority message on this session.
func (s *Session) Control(m *agentv1.AgentMessage) bool { return s.out.Control(m) }

// Data queues a data-priority message on this session.
func (s *Session) Data(m *agentv1.AgentMessage) bool { return s.out.Data(m) }

// Context ends when the session ends.
func (s *Session) Context() context.Context { return s.ctx }

// Handler is the agent logic the client calls into. Callbacks must not block
// for long: OnControl runs on the receive loop (so acks keep their order).
type Handler interface {
	// Hello returns the Hello for the next connection.
	Hello(reconnectAttempt uint32) *agentv1.Hello
	DockerStatus() agentv1.DockerStatus
	RunningCommands() uint32
	// OnWelcome runs once after Welcome; start per-session goroutines bound to
	// s.Context() and return.
	OnWelcome(s *Session)
	OnControl(s *Session, msg *agentv1.ControlMessage)
	OnSessionEnd(s *Session)
	// OnUpgradeRequired is called for Disconnect(UPGRADE_REQUIRED).
	OnUpgradeRequired(d *agentv1.Disconnect)
}

// Options configure a Client.
type Options struct {
	Endpoint string
	// TLS builds the client config for each attempt (so renewed certificates
	// are picked up).
	TLS     func() (*tls.Config, error)
	Handler Handler
	Clock   Clock
	// Rand feeds the backoff jitter (nil = math/rand/v2).
	Rand           func() float64
	Logger         *slog.Logger
	WelcomeTimeout time.Duration
	// DialOptions are appended to the gRPC dial options (tests).
	DialOptions []grpc.DialOption
	// Keepalive overrides the HTTP/2 keepalive parameters (tests).
	Keepalive *keepalive.ClientParameters
	// OnTLSFailure is called when attempts fail because of certificate errors.
	HealthyAfter time.Duration
	TLSRetry     time.Duration
}

// Client maintains the connection.
type Client struct {
	o       Options
	backoff Backoff
	log     *slog.Logger

	mu     sync.Mutex
	cur    *Session
	epoch  atomic.Uint64
	skew   atomic.Int64
	reload chan struct{}
}

// New creates a client.
func New(o Options) *Client {
	if o.Clock == nil {
		o.Clock = RealClock
	}
	if o.Logger == nil {
		o.Logger = slog.Default()
	}
	if o.WelcomeTimeout <= 0 {
		o.WelcomeTimeout = defaultWelcomeWait
	}
	if o.HealthyAfter <= 0 {
		o.HealthyAfter = HealthyAfter
	}
	if o.TLSRetry <= 0 {
		o.TLSRetry = TLSRetry
	}
	return &Client{o: o, backoff: Backoff{Rand: o.Rand}, log: o.Logger, reload: make(chan struct{}, 1)}
}

// Control sends on the current session; false when disconnected.
func (c *Client) Control(m *agentv1.AgentMessage) bool {
	c.mu.Lock()
	s := c.cur
	c.mu.Unlock()
	return s != nil && s.Control(m)
}

// SendData sends on the current session; false when disconnected.
func (c *Client) SendData(m *agentv1.AgentMessage) bool {
	c.mu.Lock()
	s := c.cur
	c.mu.Unlock()
	return s != nil && s.Data(m)
}

// Epoch changes whenever a new session starts.
func (c *Client) Epoch() uint64 { return c.epoch.Load() }

// Connected reports whether a Welcomed session is live.
func (c *Client) Connected() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.cur != nil
}

// Skew is the last measured (control plane - local) clock offset.
func (c *Client) Skew() time.Duration { return time.Duration(c.skew.Load()) }

// Reload ends the current session and reconnects immediately (new
// certificate). It does not count as a failure.
func (c *Client) Reload() {
	select {
	case c.reload <- struct{}{}:
	default:
	}
}

type outcome struct {
	err        error
	welcomed   bool
	welcomedAt time.Time
	disconnect *agentv1.Disconnect
	reloaded   bool
	tlsFailure bool
}

// Run connects and reconnects until ctx ends or the agent is revoked.
func (c *Client) Run(ctx context.Context) error {
	for ctx.Err() == nil {
		out := c.connectOnce(ctx)
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if out.disconnect != nil && out.disconnect.GetReason() == agentv1.DisconnectReason_DISCONNECT_REASON_REVOKED {
			c.log.Error("control plane revoked this agent; not reconnecting until re-enrolled")
			return ErrRevoked
		}
		if out.reloaded {
			continue
		}
		if out.welcomed && c.o.Clock.Now().Sub(out.welcomedAt) >= c.o.HealthyAfter {
			c.backoff.Reset()
		}
		delay := c.backoff.Next()
		switch {
		case out.tlsFailure:
			delay = c.o.TLSRetry
			c.log.Error("TLS/certificate error: certificate expired or untrusted - re-enroll if this persists", "error", errText(out.err), "retry_in", delay.String())
		case out.disconnect != nil:
			if out.disconnect.GetReason() == agentv1.DisconnectReason_DISCONNECT_REASON_UPGRADE_REQUIRED {
				c.o.Handler.OnUpgradeRequired(out.disconnect)
			}
			if ra := out.disconnect.GetReconnectAfter().AsDuration(); ra > 0 {
				delay = ra
			}
			c.log.Warn("disconnected by control plane", "reason", out.disconnect.GetReason().String(), "retry_in", delay.String())
		default:
			c.log.Warn("connection lost", "error", errText(out.err), "retry_in", delay.String())
		}
		select {
		case <-c.reload:
			continue
		default:
		}
		if err := c.sleep(ctx, delay); err != nil {
			return err
		}
	}
	return ctx.Err()
}

// sleep waits delay but wakes early on Reload.
func (c *Client) sleep(ctx context.Context, delay time.Duration) error {
	sctx, cancel := context.WithCancel(ctx)
	defer cancel()
	go func() {
		select {
		case <-c.reload:
			cancel()
		case <-sctx.Done():
		}
	}()
	err := c.o.Clock.Sleep(sctx, delay)
	if err != nil && ctx.Err() == nil {
		return nil // woken by Reload
	}
	return err
}

func errText(err error) string {
	if err == nil {
		return "stream closed"
	}
	return err.Error()
}

func (c *Client) dialOptions(tlsCfg *tls.Config) []grpc.DialOption {
	ka := keepalive.ClientParameters{Time: keepaliveTime, Timeout: keepaliveTimeout, PermitWithoutStream: true}
	if c.o.Keepalive != nil {
		ka = *c.o.Keepalive
	}
	opts := []grpc.DialOption{
		grpc.WithTransportCredentials(credentials.NewTLS(tlsCfg)),
		grpc.WithKeepaliveParams(ka),
		grpc.WithDefaultCallOptions(grpc.MaxCallRecvMsgSize(maxMsgSize), grpc.MaxCallSendMsgSize(maxMsgSize)),
	}
	return append(opts, c.o.DialOptions...)
}

func (c *Client) connectOnce(ctx context.Context) (out outcome) {
	tlsCfg, err := c.o.TLS()
	if err != nil {
		c.log.Error("cannot load credentials", "error", err.Error())
		return outcome{err: err, tlsFailure: true}
	}
	conn, err := grpc.NewClient(c.o.Endpoint, c.dialOptions(tlsCfg)...)
	if err != nil {
		return outcome{err: err}
	}
	defer conn.Close()

	sctx, cancel := context.WithCancel(ctx)
	defer cancel()
	stream, err := agentv1.NewAgentServiceClient(conn).Connect(sctx)
	if err != nil {
		return outcome{err: err, tlsFailure: pki.IsTLSError(err)}
	}

	hello := c.o.Handler.Hello(uint32(c.backoff.Failures()))
	helloSent := c.o.Clock.Now()
	if err := stream.Send(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_Hello{Hello: hello}}); err != nil {
		return outcome{err: err, tlsFailure: pki.IsTLSError(err)}
	}

	// Wait for Welcome (or a Disconnect) with a timeout.
	wd := time.AfterFunc(c.o.WelcomeTimeout, cancel)
	first, err := stream.Recv()
	wd.Stop()
	if err != nil {
		return outcome{err: err, tlsFailure: pki.IsTLSError(err)}
	}
	if d := first.GetDisconnect(); d != nil {
		return outcome{disconnect: d}
	}
	welcome := first.GetWelcome()
	if welcome == nil {
		return outcome{err: errors.New("protocol violation: first control message was not Welcome")}
	}
	now := c.o.Clock.Now()
	rtt := now.Sub(helloSent)
	var skew time.Duration
	if st := welcome.GetServerTime(); st != nil {
		// Server stamped its time roughly half an RTT before we received it.
		skew = st.AsTime().Sub(now.Add(-rtt / 2))
	}
	c.skew.Store(int64(skew))

	sess := &Session{Welcome: welcome, Skew: skew, Epoch: c.epoch.Add(1), out: NewSender(), boot: now}
	sess.ctx = sctx
	c.mu.Lock()
	c.cur = sess
	c.mu.Unlock()
	out = outcome{welcomed: true, welcomedAt: now}
	defer func() {
		c.mu.Lock()
		if c.cur == sess {
			c.cur = nil
		}
		c.mu.Unlock()
		sess.out.Close()
		c.o.Handler.OnSessionEnd(sess)
	}()

	var wg sync.WaitGroup
	defer wg.Wait()
	defer cancel()

	// Send pump: control before data.
	wg.Add(1)
	go func() {
		defer wg.Done()
		for {
			m, ok := sess.out.next(sctx)
			if !ok {
				return
			}
			if err := stream.Send(m); err != nil {
				cancel()
				return
			}
		}
	}()

	// Heartbeats.
	interval := welcome.GetHeartbeatInterval().AsDuration()
	if interval <= 0 {
		interval = defaultHeartbeat
	}
	wg.Add(1)
	go func() {
		defer wg.Done()
		c.heartbeats(sctx, sess, interval)
	}()

	// Reload watcher.
	var reloaded atomic.Bool
	wg.Add(1)
	go func() {
		defer wg.Done()
		select {
		case <-c.reload:
			reloaded.Store(true)
			cancel()
		case <-sctx.Done():
		}
	}()

	c.o.Handler.OnWelcome(sess)

	for {
		msg, err := stream.Recv()
		if err != nil {
			if reloaded.Load() {
				out.reloaded = true
				return out
			}
			if errors.Is(err, io.EOF) {
				out.err = nil
			} else {
				out.err = err
			}
			return out
		}
		switch p := msg.GetPayload().(type) {
		case *agentv1.ControlMessage_Ping:
			sess.Control(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_Pong{Pong: &agentv1.Pong{
				Nonce: p.Ping.GetNonce(), PingSentAt: p.Ping.GetSentAt(), ReceivedAt: timestamppb.New(c.o.Clock.Now()),
			}}})
		case *agentv1.ControlMessage_Disconnect:
			out.disconnect = p.Disconnect
			return out
		case *agentv1.ControlMessage_Welcome:
			c.log.Warn("ignoring unexpected second Welcome")
		default:
			c.o.Handler.OnControl(sess, msg)
		}
	}
}

func (c *Client) heartbeats(ctx context.Context, s *Session, interval time.Duration) {
	send := func() {
		s.Control(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_Heartbeat{Heartbeat: &agentv1.Heartbeat{
			Seq:             s.seq.Add(1),
			SentAt:          timestamppb.New(c.o.Clock.Now()),
			AgentUptime:     durationpb.New(time.Since(processStart)),
			DockerStatus:    c.o.Handler.DockerStatus(),
			RunningCommands: c.o.Handler.RunningCommands(),
			ConfigVersion:   s.Welcome.GetConfigVersion(),
		}}})
	}
	send()
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			send()
		}
	}
}

var processStart = time.Now()

// String describes the client for logs.
func (c *Client) String() string { return fmt.Sprintf("agent link to %s", c.o.Endpoint) }
