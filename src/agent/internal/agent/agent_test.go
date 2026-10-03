package agent

import (
	"strings"
	"testing"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
	"github.com/mihaim21/aethera-forge/agent/internal/fakecp"
)

func TestHelloWelcomeHeartbeatDiscoveryMetrics(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()

	h := c.Hello
	if h.GetServerId() != "srv-1" || h.GetAgentVersion() != "test-1.0" || h.GetProtocolVersion() != 1 {
		t.Fatalf("unexpected hello: %v", h)
	}
	caps := strings.Join(h.GetCapabilities(), ",")
	for _, want := range []string{"docker.containers", "logs.follow", "health.probe"} {
		if !strings.Contains(caps, want) {
			t.Errorf("capability %s missing from %s", want, caps)
		}
	}
	if strings.Contains(caps, "build") || strings.Contains(caps, "compose") || strings.Contains(caps, "proxy") {
		t.Errorf("agent advertises Phase 3 capabilities it does not have: %s", caps)
	}
	if h.GetProcessId() == "" || h.GetOs() == "" {
		t.Errorf("hello lacks process/os info: %v", h)
	}

	hb1 := c.Await(func(m *agentv1.AgentMessage) bool { return m.GetHeartbeat() != nil }, wait)
	hb2 := c.Await(func(m *agentv1.AgentMessage) bool { return m.GetHeartbeat() != nil }, wait)
	if hb1 == nil || hb2 == nil {
		t.Fatal("expected heartbeats at the Welcome interval")
	}
	if hb1.GetHeartbeat().GetSeq() != 1 || hb2.GetHeartbeat().GetSeq() != 2 {
		t.Errorf("heartbeat seq = %d,%d want 1,2", hb1.GetHeartbeat().GetSeq(), hb2.GetHeartbeat().GetSeq())
	}
	if hb1.GetHeartbeat().GetConfigVersion() != "cfg-1" {
		t.Errorf("heartbeat config version = %q", hb1.GetHeartbeat().GetConfigVersion())
	}
	if c.Await(func(m *agentv1.AgentMessage) bool { return m.GetDiscovery() != nil }, wait) == nil {
		t.Error("no discovery report after Welcome")
	}
	if c.Await(func(m *agentv1.AgentMessage) bool { return m.GetMetrics() != nil }, wait) == nil {
		t.Error("no metrics report after Welcome")
	}
	// Docker status reaches the heartbeat once the monitor has polled.
	eventually(t, "docker status in heartbeat", func() bool {
		hb := c.Await(func(m *agentv1.AgentMessage) bool {
			return m.GetHeartbeat().GetDockerStatus() == agentv1.DockerStatus_DOCKER_STATUS_RUNNING
		}, 200*time.Millisecond)
		return hb != nil
	})
}

func TestPingAnsweredWithPong(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	send(t, c, &agentv1.ControlMessage{Payload: &agentv1.ControlMessage_Ping{Ping: &agentv1.Ping{Nonce: "n-1", SentAt: timestamppb.Now()}}})
	m := c.Await(func(m *agentv1.AgentMessage) bool { return m.GetPong().GetNonce() == "n-1" }, wait)
	if m == nil {
		t.Fatal("no pong")
	}
}

func TestCommandAckThenResult(t *testing.T) {
	e := newEnv(t, setup{})
	e.dk.AddContainer(&agentv1.ContainerInfo{Id: "c1", Name: "web", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING})
	c := e.nextConn()

	send(t, c, listCmd("cmd-1", "key-1", time.Now().Add(time.Minute)))
	if a := e.ack(c, "cmd-1"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_ACCEPTED {
		t.Fatalf("ack = %v", a)
	}
	r := e.result(c, "cmd-1")
	if r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED || r.GetReplayed() {
		t.Fatalf("result = %v", r)
	}
	if got := r.GetContainerList().GetContainers(); len(got) != 1 || got[0].GetName() != "web" {
		t.Fatalf("containers = %v", got)
	}
	if r.GetStartedAt() == nil || r.GetFinishedAt() == nil {
		t.Error("result lacks timestamps")
	}
}

func TestUnsupportedCommandsAreRejectedNotFaked(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	cases := map[string]*agentv1.Command{
		"build":   {CommandId: "b", Request: &agentv1.Command_Build{Build: &agentv1.BuildRequest{}}},
		"compose": {CommandId: "c", Request: &agentv1.Command_ComposeUp{ComposeUp: &agentv1.ComposeUp{}}},
		"proxy":   {CommandId: "p", Request: &agentv1.Command_ProxyEnsure{ProxyEnsure: &agentv1.ProxyEnsure{Provider: "traefik"}}},
		"empty":   {CommandId: "e"},
	}
	for name, cmd := range cases {
		send(t, c, command(cmd))
		a := e.ack(c, cmd.CommandId)
		if a.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_UNSUPPORTED || a.GetMessage() == "" {
			t.Errorf("%s: ack = %v", name, a)
		}
	}
	// Self-update is not offered when the binary location is unknown.
	send(t, c, command(&agentv1.Command{CommandId: "u", Request: &agentv1.Command_AgentSelfUpdate{AgentSelfUpdate: &agentv1.AgentSelfUpdate{
		Url: "https://example.com/a", Sha256: strings.Repeat("0", 64),
	}}}))
	if a := e.ack(c, "u"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_UNSUPPORTED {
		t.Errorf("self-update ack = %v", a)
	}
}

func TestPolicyRejectsDockerSocketBindMount(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	spec := &agentv1.ContainerSpec{
		Image: "nginx", Name: "evil",
		Mounts: []*agentv1.VolumeMount{{Type: agentv1.MountType_MOUNT_TYPE_BIND, Source: "/var/run/docker.sock", Target: "/var/run/docker.sock"}},
	}
	send(t, c, command(&agentv1.Command{CommandId: "p1", Request: &agentv1.Command_ContainerCreate{ContainerCreate: &agentv1.ContainerCreate{Spec: spec, Start: true}}}))
	a := e.ack(c, "p1")
	if a.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_POLICY || a.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION {
		t.Fatalf("ack = %v", a)
	}
	if len(e.dk.Created) != 0 {
		t.Fatal("container was created despite the policy violation")
	}
}

func TestContainerCreateRunsAndPulls(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	spec := &agentv1.ContainerSpec{Image: "nginx:1.27", Name: "web"}
	send(t, c, command(&agentv1.Command{CommandId: "cc", Request: &agentv1.Command_ContainerCreate{ContainerCreate: &agentv1.ContainerCreate{
		Spec: spec, Start: true, PullIfMissing: true,
		PullAuth: &agentv1.RegistryAuth{Server: "ghcr.io", Username: "u", Password: &agentv1.SecretValue{Value: "hunter2-secret"}},
	}}}))
	r := e.result(c, "cc")
	if r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED {
		t.Fatalf("result = %v", r)
	}
	if r.GetContainerCreate().GetState() != agentv1.ContainerState_CONTAINER_STATE_RUNNING {
		t.Errorf("state = %v", r.GetContainerCreate().GetState())
	}
	calls := strings.Join(e.dk.CallLog(), "|")
	if !strings.Contains(calls, "ImagePull nginx:1.27") || !strings.Contains(calls, "ContainerStart") {
		t.Errorf("calls = %s", calls)
	}
	// A second create with the same name fails with ALREADY_EXISTS.
	send(t, c, command(&agentv1.Command{CommandId: "cc2", Request: &agentv1.Command_ContainerCreate{ContainerCreate: &agentv1.ContainerCreate{Spec: spec}}}))
	r2 := e.result(c, "cc2")
	if r2.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_ALREADY_EXISTS {
		t.Errorf("duplicate create result = %v", r2)
	}
}

func TestSecretsNeverAppearInResultErrors(t *testing.T) {
	e := newEnv(t, setup{})
	e.dk.PullErr = errString("denied: bad credentials for hunter2-secret")
	c := e.nextConn()
	send(t, c, command(&agentv1.Command{CommandId: "pp", Request: &agentv1.Command_ImagePull{ImagePull: &agentv1.ImagePull{
		Reference: "ghcr.io/acme/x:1",
		Auth:      &agentv1.RegistryAuth{Server: "ghcr.io", Username: "u", Password: &agentv1.SecretValue{Value: "hunter2-secret"}},
	}}}))
	r := e.result(c, "pp")
	if r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_FAILED {
		t.Fatalf("result = %v", r)
	}
	if strings.Contains(r.GetErrorMessage(), "hunter2-secret") || !strings.Contains(r.GetErrorMessage(), "***") {
		t.Fatalf("error message not masked: %q", r.GetErrorMessage())
	}
}

type errString string

func (e errString) Error() string { return string(e) }

func TestIdempotentRedeliveryReplaysCachedResult(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	send(t, c, listCmd("first", "job-1:deploy:0", time.Now().Add(time.Minute)))
	r1 := e.result(c, "first")

	send(t, c, listCmd("second", "job-1:deploy:0", time.Now().Add(time.Minute)))
	if a := e.ack(c, "second"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_DUPLICATE_COMPLETED {
		t.Fatalf("ack = %v", a)
	}
	r2 := e.result(c, "second")
	if !r2.GetReplayed() || r2.GetStatus() != r1.GetStatus() {
		t.Fatalf("replayed result = %v", r2)
	}
	n := 0
	for _, call := range e.dk.CallLog() {
		if strings.HasPrefix(call, "ContainerList") {
			n++
		}
	}
	if n != 1 {
		t.Fatalf("command executed %d times, want exactly once", n)
	}

	// A new retry number is a new key and really re-executes.
	send(t, c, listCmd("third", "job-1:deploy:1", time.Now().Add(time.Minute)))
	if r := e.result(c, "third"); r.GetReplayed() {
		t.Fatal("new key must execute, not replay")
	}
}

func TestDuplicateRunningBindsNewCommandIDToRunningExecution(t *testing.T) {
	e := newEnv(t, setup{})
	block := make(chan struct{})
	e.dk.BlockList = block
	c := e.nextConn()

	send(t, c, listCmd("orig", "k-run", time.Now().Add(time.Minute)))
	if a := e.ack(c, "orig"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_ACCEPTED {
		t.Fatalf("ack = %v", a)
	}
	send(t, c, listCmd("dup", "k-run", time.Now().Add(time.Minute)))
	if a := e.ack(c, "dup"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_DUPLICATE_RUNNING {
		t.Fatalf("ack = %v", a)
	}
	close(block)
	r1, r2 := e.result(c, "orig"), e.result(c, "dup")
	if r1.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED || r2.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED {
		t.Fatalf("results %v %v", r1, r2)
	}
}

func TestCancelCommand(t *testing.T) {
	e := newEnv(t, setup{})
	e.dk.BlockList = make(chan struct{})
	c := e.nextConn()
	send(t, c, listCmd("slow", "k-slow", time.Now().Add(time.Minute)))
	e.ack(c, "slow")
	send(t, c, cancelMsg("slow", time.Second))
	r := e.result(c, "slow")
	if r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_CANCELLED || r.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_CANCELLED {
		t.Fatalf("result = %v", r)
	}
	// The cancelled outcome is cached under the key (retry_no must change to re-run).
	send(t, c, listCmd("again", "k-slow", time.Now().Add(time.Minute)))
	if a := e.ack(c, "again"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_DUPLICATE_COMPLETED {
		t.Fatalf("ack = %v", a)
	}
}

func TestDeadlineExpiredOnArrivalAndTimeout(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	send(t, c, listCmd("old", "k-old", time.Now().Add(-time.Minute)))
	if a := e.ack(c, "old"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_EXPIRED {
		t.Fatalf("ack = %v", a)
	}

	e.dk.BlockList = make(chan struct{})
	send(t, c, listCmd("short", "k-short", time.Now().Add(300*time.Millisecond)))
	if a := e.ack(c, "short"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_ACCEPTED {
		t.Fatalf("ack = %v", a)
	}
	r := e.result(c, "short")
	if r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_TIMED_OUT || r.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_TIMEOUT {
		t.Fatalf("result = %v", r)
	}
}

func TestClockSkewIsAppliedToDeadlines(t *testing.T) {
	// The control plane clock runs one hour ahead of this host.
	skewed := func() time.Time { return time.Now().Add(time.Hour) }
	e := newEnv(t, setup{cp: func(s *fakecp.Server) { s.ServerTime = skewed }})
	c := e.nextConn()
	eventually(t, "skew measured", func() bool { return e.agent.Client().Skew() > 59*time.Minute })

	// Deadline 20 s in the future ON THE CONTROL PLANE CLOCK: valid.
	send(t, c, listCmd("ok", "k-ok", skewed().Add(20*time.Second)))
	if a := e.ack(c, "ok"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_ACCEPTED {
		t.Fatalf("skewed deadline wrongly refused: %v", a)
	}
	// A deadline in the past on the control plane clock is expired, even though
	// it is an hour in the future by the local clock.
	send(t, c, listCmd("late", "k-late", skewed().Add(-5*time.Second)))
	if a := e.ack(c, "late"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_EXPIRED {
		t.Fatalf("expired deadline accepted: %v", a)
	}
}

func TestConcurrencyLimitRejectsBusy(t *testing.T) {
	e := newEnv(t, setup{cp: func(s *fakecp.Server) { s.MaxConcurrent = 1 }})
	e.dk.BlockList = make(chan struct{})
	c := e.nextConn()
	send(t, c, listCmd("a", "ka", time.Now().Add(time.Minute)))
	e.ack(c, "a")
	send(t, c, listCmd("b", "kb", time.Now().Add(time.Minute)))
	if ack := e.ack(c, "b"); ack.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_BUSY {
		t.Fatalf("ack = %v", ack)
	}
	hb := c.Await(func(m *agentv1.AgentMessage) bool { return m.GetHeartbeat().GetRunningCommands() == 1 }, wait)
	if hb == nil {
		t.Error("heartbeat does not report running_commands")
	}
}

func TestReconnectReportsRunningCommandsAndAttempt(t *testing.T) {
	e := newEnv(t, setup{})
	e.dk.BlockList = make(chan struct{})
	c1 := e.nextConn()
	send(t, c1, listCmd("inflight", "k-if", time.Now().Add(time.Minute)))
	e.ack(c1, "inflight")

	c1.Close() // the control plane drops the stream
	c2 := e.nextConn()
	found := false
	for _, id := range c2.Hello.GetRunningCommandIds() {
		if id == "inflight" {
			found = true
		}
	}
	if !found {
		t.Fatalf("running_command_ids = %v, want inflight (commands survive disconnects)", c2.Hello.GetRunningCommandIds())
	}
	if c2.Hello.GetReconnectAttempt() < 1 {
		t.Errorf("reconnect_attempt = %d", c2.Hello.GetReconnectAttempt())
	}
	// Finish the command while connected to the second session: the result is delivered there.
	close(e.dk.BlockList)
	if r := e.result(c2, "inflight"); r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED {
		t.Fatalf("result = %v", r)
	}
}

func TestReconnectBackoffUsesFullJitterDelays(t *testing.T) {
	clock := newStepClock()
	e := newEnv(t, setup{clock: clock, randZero: true}) // rand = 0.999 => near the ceiling
	c1 := e.nextConn()
	c1.Close()

	var delays []time.Duration
	for i := 0; i < 3; i++ {
		select {
		case d := <-clock.sleeps:
			delays = append(delays, d)
		case <-time.After(wait):
			t.Fatalf("no reconnect sleep #%d", i)
		}
		if i == 0 {
			// The first delay is drawn from [0, 1s]; connection 2 succeeds after release.
		}
		clock.release <- struct{}{}
		c := e.nextConn()
		c.Close()
	}
	if delays[0] <= 0 || delays[0] > time.Second {
		t.Errorf("first delay %v not within (0, 1s]", delays[0])
	}
	// Each session lasted far less than 60 s, so the backoff must NOT have reset.
	if delays[1] <= time.Second || delays[1] > 2*time.Second {
		t.Errorf("second delay %v not within (1s, 2s]", delays[1])
	}
	if delays[2] <= 2*time.Second || delays[2] > 4*time.Second {
		t.Errorf("third delay %v not within (2s, 4s]", delays[2])
	}
}

func TestEventsBufferedWhileDisconnectedAreFlushedAfterWelcome(t *testing.T) {
	clock := newStepClock()
	e := newEnv(t, setup{clock: clock})
	c1 := e.nextConn()
	c1.Close()
	<-clock.sleeps // agent is now waiting to reconnect: no session

	e.dk.EventCh <- docker.Event{Action: "die", ContainerID: "c9", Name: "api", Time: time.Now(),
		Attributes: map[string]string{"name": "api", "exitCode": "137"}}
	time.Sleep(300 * time.Millisecond) // let the watcher buffer it
	clock.release <- struct{}{}
	c2 := e.nextConn()
	m := c2.Await(func(m *agentv1.AgentMessage) bool { return m.GetEvent().GetContainerId() == "c9" }, wait)
	if m == nil {
		t.Fatal("buffered event was not flushed after reconnect")
	}
	if m.GetEvent().GetType() != agentv1.EventType_EVENT_TYPE_CONTAINER_DIED || m.GetEvent().GetExitCode() != 137 {
		t.Errorf("event = %v", m.GetEvent())
	}
}

func TestRevokedStopsReconnecting(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	send(t, c, disconnect(agentv1.DisconnectReason_DISCONNECT_REASON_REVOKED, 0))
	time.Sleep(700 * time.Millisecond)
	if n := e.cp.ConnCount(); n != 1 {
		t.Fatalf("agent reconnected %d times after REVOKED", n)
	}
	select {
	case err := <-e.done:
		t.Fatalf("agent exited (%v); it must stay up waiting for re-enrollment", err)
	default:
	}
}

func TestSupersededReconnects(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	send(t, c, disconnect(agentv1.DisconnectReason_DISCONNECT_REASON_SUPERSEDED, 0))
	e.nextConn() // a second session is established
}

func TestServerReconnectAfterHintOverridesBackoff(t *testing.T) {
	clock := newStepClock()
	e := newEnv(t, setup{clock: clock})
	c := e.nextConn()
	send(t, c, disconnect(agentv1.DisconnectReason_DISCONNECT_REASON_SERVER_SHUTDOWN, 7*time.Second))
	select {
	case d := <-clock.sleeps:
		if d != 7*time.Second {
			t.Fatalf("delay = %v, want the server hint 7s", d)
		}
	case <-time.After(wait):
		t.Fatal("no sleep")
	}
}

func TestCertRotationHintRenewsAndReconnects(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	oldCert := readFile(t, e.dir.CertPath())
	send(t, c, &agentv1.ControlMessage{Payload: &agentv1.ControlMessage_CertRotation{CertRotation: &agentv1.CertRotationHint{Reason: "test"}}})
	e.nextConn() // reconnect with the renewed certificate
	if e.cp.RenewCalls != 1 {
		t.Fatalf("RenewCertificate called %d times", e.cp.RenewCalls)
	}
	if string(readFile(t, e.dir.CertPath())) == string(oldCert) {
		t.Fatal("certificate was not replaced")
	}
}

func TestLogStreamCreditFlowControl(t *testing.T) {
	e := newEnv(t, setup{cp: func(s *fakecp.Server) { s.ChunkMax = 512; s.Window = 1024 }})
	line := strings.Repeat("x", 99) + "\n" // 100 bytes
	for i := 0; i < 40; i++ {
		e.dk.InitialLogs = append(e.dk.InitialLogs, dockertest.LogLine{Stream: agentv1.LogStream_LOG_STREAM_STDOUT, Data: line})
	}
	e.dk.AddContainer(&agentv1.ContainerInfo{Id: "c1", Name: "app", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING})
	c := e.nextConn()
	send(t, c, command(&agentv1.Command{CommandId: "ls", Request: &agentv1.Command_LogStreamStart{LogStreamStart: &agentv1.LogStreamStart{
		StreamId: "s1", Container: "app", Follow: true, IncludeStdout: true,
	}}}))
	if r := e.result(c, "ls"); !r.GetLogStreamStart().GetStarted() {
		t.Fatalf("result = %v", r)
	}
	isChunk := func(m *agentv1.AgentMessage) bool { return m.GetLogChunk().GetStreamId() == "s1" }
	total := func() int {
		n := 0
		for _, m := range c.All() {
			if isChunk(m) {
				n += len(m.GetLogChunk().GetData())
			}
		}
		return n
	}
	eventually(t, "first window sent", func() bool { return total() >= 900 })
	time.Sleep(400 * time.Millisecond)
	if got := total(); got > 1024 {
		t.Fatalf("agent sent %d bytes with a 1024 byte window and no ack", got)
	}
	var lastSeq uint64
	for _, m := range c.All() {
		if isChunk(m) {
			ch := m.GetLogChunk()
			if ch.GetSequence() != lastSeq+1 {
				t.Fatalf("sequence %d after %d", ch.GetSequence(), lastSeq)
			}
			if len(ch.GetData()) > 512 {
				t.Fatalf("chunk of %d bytes exceeds log_chunk_max_bytes", len(ch.GetData()))
			}
			if ch.GetSource() != agentv1.LogSource_LOG_SOURCE_CONTAINER || ch.GetStream() != agentv1.LogStream_LOG_STREAM_STDOUT {
				t.Fatalf("chunk metadata = %v", ch)
			}
			lastSeq = ch.GetSequence()
		}
	}
	// Ack everything: more data may flow.
	before := total()
	send(t, c, &agentv1.ControlMessage{Payload: &agentv1.ControlMessage_LogFlowControl{LogFlowControl: &agentv1.LogFlowControl{
		StreamId: "s1", AckedSequence: lastSeq, WindowBytes: 1024,
	}}})
	eventually(t, "more data after credit", func() bool { return total() > before })

	// Stopping the stream ends it with an EOF chunk.
	send(t, c, command(&agentv1.Command{CommandId: "lstop", Request: &agentv1.Command_LogStreamStop{LogStreamStop: &agentv1.LogStreamStop{StreamId: "s1"}}}))
	if m := c.Await(func(m *agentv1.AgentMessage) bool { return isChunk(m) && m.GetLogChunk().GetEof() }, wait); m == nil {
		t.Fatal("no EOF chunk after LogStreamStop")
	}
}

func TestLogStreamForMissingContainerFinishesImmediately(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	send(t, c, command(&agentv1.Command{CommandId: "ls", Request: &agentv1.Command_LogStreamStart{LogStreamStart: &agentv1.LogStreamStart{
		StreamId: "gone", Container: "nope", Follow: true,
	}}}))
	r := e.result(c, "ls")
	if r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED || r.GetLogStreamStart().GetStarted() {
		t.Fatalf("result = %v", r)
	}
	if m := c.Await(func(m *agentv1.AgentMessage) bool {
		return m.GetLogChunk().GetStreamId() == "gone" && m.GetLogChunk().GetEof()
	}, wait); m == nil {
		t.Fatal("no EOF chunk")
	}
}

func TestHealthProbeLoopbackTCPAndSSRFGuard(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	// Probing the fake control plane's loopback port succeeds.
	host, port := splitHostPort(t, e.cp.Addr)
	send(t, c, command(&agentv1.Command{CommandId: "hp", Request: &agentv1.Command_HealthProbe{HealthProbe: &agentv1.HealthProbe{
		Target:  &agentv1.HealthProbe_Tcp{Tcp: &agentv1.TcpProbe{Host: host, Port: port}},
		Timeout: durationpb.New(time.Second), Retries: 2,
	}}}))
	if r := e.result(c, "hp"); r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED || !r.GetHealthProbe().GetHealthy() {
		t.Fatalf("result = %v", r)
	}
	// The cloud metadata address is refused at ack time.
	send(t, c, command(&agentv1.Command{CommandId: "ssrf", Request: &agentv1.Command_HealthProbe{HealthProbe: &agentv1.HealthProbe{
		Target: &agentv1.HealthProbe_Http{Http: &agentv1.HttpProbe{Url: "http://169.254.169.254/latest/meta-data"}},
	}}}))
	if a := e.ack(c, "ssrf"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_POLICY {
		t.Fatalf("ack = %v", a)
	}
	// A closed local port reports unhealthy with a partial result.
	send(t, c, command(&agentv1.Command{CommandId: "down", Request: &agentv1.Command_HealthProbe{HealthProbe: &agentv1.HealthProbe{
		Target:  &agentv1.HealthProbe_Tcp{Tcp: &agentv1.TcpProbe{Host: "127.0.0.1", Port: 1}},
		Timeout: durationpb.New(300 * time.Millisecond), Retries: 2, Interval: durationpb.New(10 * time.Millisecond),
	}}}))
	r := e.result(c, "down")
	if r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_FAILED || r.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_HEALTH_CHECK_FAILED ||
		r.GetHealthProbe() == nil || r.GetHealthProbe().GetHealthy() || r.GetHealthProbe().GetAttempts() != 2 {
		t.Fatalf("result = %v", r)
	}
}

func TestDockerDownIsReportedInHeartbeatAndErrors(t *testing.T) {
	e := newEnv(t, setup{})
	c := e.nextConn()
	e.dk.SetStatus(agentv1.DockerStatus_DOCKER_STATUS_STOPPED)
	if c.Await(func(m *agentv1.AgentMessage) bool {
		return m.GetHeartbeat().GetDockerStatus() == agentv1.DockerStatus_DOCKER_STATUS_STOPPED
	}, wait) == nil {
		t.Fatal("heartbeat never reported the stopped daemon")
	}
	if c.Await(func(m *agentv1.AgentMessage) bool {
		return m.GetEvent().GetType() == agentv1.EventType_EVENT_TYPE_DOCKER_DAEMON_DOWN
	}, wait) == nil {
		t.Fatal("no DOCKER_DAEMON_DOWN event")
	}
	send(t, c, listCmd("l", "kl", time.Now().Add(time.Minute)))
	r := e.result(c, "l")
	if r.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_DOCKER_UNAVAILABLE {
		t.Fatalf("result = %v", r)
	}
}
