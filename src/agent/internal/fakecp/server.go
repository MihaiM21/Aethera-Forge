package fakecp

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"fmt"
	"io"
	"net"
	"sync"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/keepalive"
	"google.golang.org/grpc/peer"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/pki"
)

// Server is a fake control plane.
type Server struct {
	agentv1.UnimplementedAgentServiceServer
	agentv1.UnimplementedEnrollmentServiceServer

	CA       *CA
	Addr     string
	ServerID string
	// Token is the single-use join token Enroll accepts.
	Token string

	// Welcome parameters (zero values get test-friendly defaults).
	HeartbeatInterval time.Duration
	MetricsInterval   time.Duration
	DiscoveryInterval time.Duration
	MaxConcurrent     uint32
	ChunkMax          uint32
	Window            uint64
	// ServerTime stamps Welcome.server_time (nil = real time); use it to fake skew.
	ServerTime func() time.Time
	// Validity of issued client certificates.
	Validity time.Duration
	// OnHello may refuse a connection by returning a Disconnect.
	OnHello func(h *agentv1.Hello, attempt int) *agentv1.Disconnect
	// RenewCalls counts RenewCertificate calls.
	RenewCalls int

	mu        sync.Mutex
	tokenUsed bool
	attempts  int
	conns     []*Conn
	connCh    chan *Conn
	grpcSrv   *grpc.Server
	lis       net.Listener
}

// Start launches the server on 127.0.0.1:0.
func Start(token, serverID string) (*Server, error) {
	ca, err := NewCA(nil)
	if err != nil {
		return nil, err
	}
	s := &Server{CA: ca, Token: token, ServerID: serverID, connCh: make(chan *Conn, 64), Validity: 30 * 24 * time.Hour}
	cert, err := ca.ServerCert("127.0.0.1", "localhost")
	if err != nil {
		return nil, err
	}
	pool := x509.NewCertPool()
	pool.AddCert(ca.Cert)
	tlsCfg := &tls.Config{
		MinVersion:   tls.VersionTLS13,
		Certificates: []tls.Certificate{cert},
		ClientAuth:   tls.VerifyClientCertIfGiven,
		ClientCAs:    pool,
	}
	lis, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	s.lis = lis
	s.Addr = lis.Addr().String()
	s.grpcSrv = grpc.NewServer(
		grpc.Creds(credentials.NewTLS(tlsCfg)),
		grpc.KeepaliveEnforcementPolicy(keepalive.EnforcementPolicy{MinTime: time.Second, PermitWithoutStream: true}),
	)
	agentv1.RegisterAgentServiceServer(s.grpcSrv, s)
	agentv1.RegisterEnrollmentServiceServer(s.grpcSrv, s)
	go func() { _ = s.grpcSrv.Serve(lis) }()
	return s, nil
}

// Stop shuts the server down immediately.
func (s *Server) Stop() { s.grpcSrv.Stop() }

// Conns returns the channel on which new Connect sessions are announced.
func (s *Server) Conns() <-chan *Conn { return s.connCh }

// ConnCount is the number of Connect streams accepted so far.
func (s *Server) ConnCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.conns)
}

// Attempts is the number of Connect attempts (including refused ones).
func (s *Server) Attempts() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.attempts
}

func (s *Server) now() time.Time {
	if s.ServerTime != nil {
		return s.ServerTime()
	}
	return time.Now()
}

func orDefault(d, def time.Duration) time.Duration {
	if d <= 0 {
		return def
	}
	return d
}

// Enroll implements EnrollmentService (no client certificate required).
func (s *Server) Enroll(_ context.Context, req *agentv1.EnrollRequest) (*agentv1.EnrollResponse, error) {
	s.mu.Lock()
	ok := !s.tokenUsed && req.GetJoinToken().GetValue() == s.Token && s.Token != ""
	if ok {
		s.tokenUsed = true
	}
	s.mu.Unlock()
	if !ok {
		return nil, status.Error(codes.PermissionDenied, "join token is invalid, expired or already used")
	}
	certPEM, notAfter, err := s.CA.SignCSR(req.GetCsrPem(), s.ServerID, s.Validity)
	if err != nil {
		return nil, status.Error(codes.InvalidArgument, err.Error())
	}
	return &agentv1.EnrollResponse{
		ServerId:             s.ServerID,
		ClientCertificatePem: certPEM,
		CaChainPem:           string(s.CA.PEM),
		CaFingerprintSha256:  s.CA.Fingerprint,
		ControlPlaneEndpoint: s.Addr,
		CertificateNotAfter:  timestamppb.New(notAfter),
		RenewBefore:          durationpb.New(10 * 24 * time.Hour),
	}, nil
}

// identity extracts the server id from the verified client certificate.
func identity(ctx context.Context) (string, error) {
	p, ok := peer.FromContext(ctx)
	if !ok {
		return "", status.Error(codes.Unauthenticated, "no peer")
	}
	ti, ok := p.AuthInfo.(credentials.TLSInfo)
	if !ok || len(ti.State.VerifiedChains) == 0 || len(ti.State.PeerCertificates) == 0 {
		return "", status.Error(codes.Unauthenticated, "client certificate required")
	}
	id, ok := pki.ServerIDFromCert(ti.State.PeerCertificates[0])
	if !ok {
		return "", status.Error(codes.Unauthenticated, "certificate has no server identity")
	}
	return id, nil
}

// RenewCertificate implements AgentService.RenewCertificate.
func (s *Server) RenewCertificate(ctx context.Context, req *agentv1.RenewCertificateRequest) (*agentv1.RenewCertificateResponse, error) {
	id, err := identity(ctx)
	if err != nil {
		return nil, err
	}
	certPEM, notAfter, err := s.CA.SignCSR(req.GetCsrPem(), id, s.Validity)
	if err != nil {
		return nil, status.Error(codes.InvalidArgument, err.Error())
	}
	s.mu.Lock()
	s.RenewCalls++
	s.mu.Unlock()
	return &agentv1.RenewCertificateResponse{
		ClientCertificatePem: certPEM, CaChainPem: string(s.CA.PEM), CertificateNotAfter: timestamppb.New(notAfter),
	}, nil
}

// Connect implements AgentService.Connect.
func (s *Server) Connect(stream agentv1.AgentService_ConnectServer) error {
	id, err := identity(stream.Context())
	if err != nil {
		return err
	}
	first, err := stream.Recv()
	if err != nil {
		return err
	}
	hello := first.GetHello()
	if hello == nil || hello.GetServerId() != id {
		_ = stream.Send(&agentv1.ControlMessage{Payload: &agentv1.ControlMessage_Disconnect{Disconnect: &agentv1.Disconnect{
			Reason: agentv1.DisconnectReason_DISCONNECT_REASON_PROTOCOL_VIOLATION, Message: "first message must be Hello for this identity",
		}}})
		return nil
	}
	s.mu.Lock()
	s.attempts++
	attempt := s.attempts
	s.mu.Unlock()
	if s.OnHello != nil {
		if d := s.OnHello(hello, attempt); d != nil {
			return stream.Send(&agentv1.ControlMessage{Payload: &agentv1.ControlMessage_Disconnect{Disconnect: d}})
		}
	}
	c := &Conn{Hello: hello, stream: stream, done: make(chan struct{}), notify: make(chan struct{}, 1)}
	err = stream.Send(&agentv1.ControlMessage{Payload: &agentv1.ControlMessage_Welcome{Welcome: &agentv1.Welcome{
		ServerId:              id,
		SessionId:             fmt.Sprintf("sess-%d", attempt),
		HeartbeatInterval:     durationpb.New(orDefault(s.HeartbeatInterval, 100*time.Millisecond)),
		MetricsInterval:       durationpb.New(orDefault(s.MetricsInterval, time.Hour)),
		DiscoveryInterval:     durationpb.New(s.DiscoveryInterval),
		MaxConcurrentCommands: s.MaxConcurrent,
		LogChunkMaxBytes:      s.ChunkMax,
		LogInitialWindowBytes: s.Window,
		ConfigVersion:         "cfg-1",
		ServerTime:            timestamppb.New(s.now()),
	}}})
	if err != nil {
		return err
	}
	s.mu.Lock()
	s.conns = append(s.conns, c)
	s.mu.Unlock()
	s.connCh <- c
	go c.readLoop()
	select {
	case <-c.done:
	case <-stream.Context().Done():
	}
	return nil
}

// Conn is one scripted Connect session on the server side.
type Conn struct {
	Hello  *agentv1.Hello
	stream agentv1.AgentService_ConnectServer

	mu       sync.Mutex
	msgs     []*agentv1.AgentMessage
	consumed []bool
	notify   chan struct{}
	sendMu   sync.Mutex
	done     chan struct{}
	once     sync.Once
}

func (c *Conn) readLoop() {
	for {
		m, err := c.stream.Recv()
		if err != nil {
			if err != io.EOF {
				_ = err
			}
			c.Close()
			return
		}
		c.mu.Lock()
		c.msgs = append(c.msgs, m)
		c.consumed = append(c.consumed, false)
		c.mu.Unlock()
		select {
		case c.notify <- struct{}{}:
		default:
		}
	}
}

// Send delivers a control message to the agent.
func (c *Conn) Send(m *agentv1.ControlMessage) error {
	c.sendMu.Lock()
	defer c.sendMu.Unlock()
	return c.stream.Send(m)
}

// Close ends the stream from the server side.
func (c *Conn) Close() { c.once.Do(func() { close(c.done) }) }

// Done is closed when the stream ended.
func (c *Conn) Done() <-chan struct{} { return c.done }

// Await waits for the first not yet consumed message matching pred and marks
// it consumed. It returns nil on timeout.
func (c *Conn) Await(pred func(*agentv1.AgentMessage) bool, timeout time.Duration) *agentv1.AgentMessage {
	deadline := time.After(timeout)
	for {
		c.mu.Lock()
		for i, m := range c.msgs {
			if !c.consumed[i] && pred(m) {
				c.consumed[i] = true
				c.mu.Unlock()
				return m
			}
		}
		c.mu.Unlock()
		select {
		case <-c.notify:
		case <-deadline:
			return nil
		case <-c.done:
			// Drain once more: messages may have arrived with the close.
			c.mu.Lock()
			for i, m := range c.msgs {
				if !c.consumed[i] && pred(m) {
					c.consumed[i] = true
					c.mu.Unlock()
					return m
				}
			}
			c.mu.Unlock()
			return nil
		case <-time.After(20 * time.Millisecond):
		}
	}
}

// All returns every message received so far (consumed or not).
func (c *Conn) All() []*agentv1.AgentMessage {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]*agentv1.AgentMessage(nil), c.msgs...)
}

// Count counts received messages matching pred (consumed or not).
func (c *Conn) Count(pred func(*agentv1.AgentMessage) bool) int {
	n := 0
	for _, m := range c.All() {
		if pred(m) {
			n++
		}
	}
	return n
}
