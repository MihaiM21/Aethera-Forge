package logstream

import (
	"bytes"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/secrets"
)

type fakeSink struct {
	mu    sync.Mutex
	msgs  []*agentv1.LogChunk
	epoch atomic.Uint64
	down  atomic.Bool
}

func (f *fakeSink) SendData(m *agentv1.AgentMessage) bool {
	if f.down.Load() {
		return false
	}
	f.mu.Lock()
	f.msgs = append(f.msgs, m.GetLogChunk())
	f.mu.Unlock()
	return true
}

func (f *fakeSink) Epoch() uint64 { return f.epoch.Load() }

func (f *fakeSink) chunks() []*agentv1.LogChunk {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]*agentv1.LogChunk(nil), f.msgs...)
}

func (f *fakeSink) bytes() int {
	n := 0
	for _, c := range f.chunks() {
		n += len(c.GetData())
	}
	return n
}

func (f *fakeSink) data() string {
	var b strings.Builder
	for _, c := range f.chunks() {
		b.Write(c.GetData())
	}
	return b.String()
}

func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

func fast(chunk int, window uint64) Config {
	return Config{ChunkMax: chunk, Window: window, Flush: 20 * time.Millisecond, BufferCap: 4096}
}

func TestChunksAreSequencedAndSizedOnLineBoundaries(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(64, 1<<20))
	s := m.Open("b1", agentv1.LogSource_LOG_SOURCE_BUILD, true, nil)
	line := strings.Repeat("a", 29) + "\n" // 30 bytes
	for i := 0; i < 10; i++ {
		if err := s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte(line)); err != nil {
			t.Fatal(err)
		}
	}
	waitFor(t, "all data", func() bool { return sink.bytes() == 300 })
	var seq uint64
	for _, c := range sink.chunks() {
		seq++
		if c.GetSequence() != seq {
			t.Fatalf("sequence %d, want %d", c.GetSequence(), seq)
		}
		if len(c.GetData()) > 64 {
			t.Fatalf("chunk of %d bytes exceeds the 64 byte limit", len(c.GetData()))
		}
		if !bytes.HasSuffix(c.GetData(), []byte("\n")) {
			t.Fatalf("chunk does not end on a line boundary: %q", c.GetData())
		}
		if c.GetSource() != agentv1.LogSource_LOG_SOURCE_BUILD || c.GetStreamId() != "b1" || c.GetTimestamp() == nil {
			t.Fatalf("metadata: %v", c)
		}
	}
}

func TestSmallWritesFlushAfterTheTimer(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(32*1024, 1<<20))
	s := m.Open("c", agentv1.LogSource_LOG_SOURCE_CONTAINER, false, nil)
	_ = s.Write(agentv1.LogStream_LOG_STREAM_STDERR, []byte("hello\n"))
	waitFor(t, "timer flush", func() bool { return sink.bytes() == 6 })
	if sink.chunks()[0].GetStream() != agentv1.LogStream_LOG_STREAM_STDERR {
		t.Fatal("stream kind lost")
	}
}

func TestSecretIsMaskedAcrossWriteBoundaries(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(1024, 1<<20))
	s := m.Open("b", agentv1.LogSource_LOG_SOURCE_BUILD, true, secrets.NewMasker("supersecret123"))
	_ = s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte("token=supers"))
	time.Sleep(60 * time.Millisecond) // the partial line outlives the flush timer
	_ = s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte("ecret123 done\nnext line\n"))
	waitFor(t, "output", func() bool { return strings.Contains(sink.data(), "next line") })
	got := sink.data()
	if strings.Contains(got, "supersecret123") || strings.Contains(got, "supers\x00") {
		t.Fatalf("secret leaked: %q", got)
	}
	if !strings.Contains(got, "token=*** done\n") {
		t.Fatalf("secret not masked in place: %q", got)
	}
	// No chunk, on its own, may contain a partial prefix beyond what is safe either.
	for _, c := range sink.chunks() {
		if strings.Contains(string(c.GetData()), "ecret123") {
			t.Fatalf("chunk leaks the secret tail: %q", c.GetData())
		}
	}
}

func TestCreditExhaustionStopsSendingUntilAcked(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(50, 100))
	s := m.Open("b", agentv1.LogSource_LOG_SOURCE_BUILD, true, nil)
	go func() {
		for i := 0; i < 20; i++ {
			if s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte(strings.Repeat("x", 49)+"\n")) != nil {
				return
			}
		}
	}()
	waitFor(t, "first window", func() bool { return sink.bytes() == 100 })
	time.Sleep(150 * time.Millisecond)
	if got := sink.bytes(); got != 100 {
		t.Fatalf("sent %d bytes with a 100 byte window", got)
	}
	m.FlowControl(&agentv1.LogFlowControl{StreamId: "b", AckedSequence: 2, WindowBytes: 100})
	waitFor(t, "second window", func() bool { return sink.bytes() == 200 })
	// window 0 pauses
	m.FlowControl(&agentv1.LogFlowControl{StreamId: "b", AckedSequence: 4, WindowBytes: 0})
	time.Sleep(100 * time.Millisecond)
	paused := sink.bytes()
	time.Sleep(100 * time.Millisecond)
	if sink.bytes() != paused {
		t.Fatal("window 0 did not pause the stream")
	}
}

func TestLosslessStreamAppliesBackPressure(t *testing.T) {
	sink := &fakeSink{}
	cfg := Config{ChunkMax: 50, Window: 50, Flush: 10 * time.Millisecond, BufferCap: 100}
	m := NewManager(sink, cfg)
	s := m.Open("b", agentv1.LogSource_LOG_SOURCE_BUILD, true, nil)
	done := make(chan struct{})
	go func() {
		defer close(done)
		for i := 0; i < 12; i++ { // 600 bytes >> window 50 + buffer 100
			_ = s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte(strings.Repeat("y", 49)+"\n"))
		}
	}()
	select {
	case <-done:
		t.Fatal("producer finished although the control plane granted almost no credit: output was dropped instead of back-pressured")
	case <-time.After(300 * time.Millisecond):
	}
	// Grant credit step by step; everything must arrive, nothing lost.
	var acked uint64
	for i := 0; i < 40 && sink.bytes() < 600; i++ {
		time.Sleep(15 * time.Millisecond)
		cs := sink.chunks()
		if n := len(cs); n > 0 {
			acked = cs[n-1].GetSequence()
		}
		m.FlowControl(&agentv1.LogFlowControl{StreamId: "b", AckedSequence: acked, WindowBytes: 50})
	}
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("producer never finished")
	}
	waitFor(t, "all 600 bytes", func() bool { return sink.bytes() == 600 })
	for _, c := range sink.chunks() {
		if c.GetDroppedBytes() != 0 {
			t.Fatal("lossless stream dropped data")
		}
	}
}

func TestLossyStreamDropsOldestAndReportsMarker(t *testing.T) {
	sink := &fakeSink{}
	cfg := Config{ChunkMax: 40, Window: 40, Flush: 10 * time.Millisecond, BufferCap: 120}
	m := NewManager(sink, cfg)
	s := m.Open("c", agentv1.LogSource_LOG_SOURCE_CONTAINER, false, nil)
	for i := 0; i < 20; i++ { // 20 x 30 bytes, never acked
		if err := s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte(strings.Repeat("z", 29)+"\n")); err != nil {
			t.Fatal(err)
		}
	}
	waitFor(t, "first chunk", func() bool { return sink.bytes() > 0 })
	m.FlowControl(&agentv1.LogFlowControl{StreamId: "c", AckedSequence: 1, WindowBytes: 4096})
	waitFor(t, "chunk with dropped marker", func() bool {
		for _, c := range sink.chunks() {
			if c.GetDroppedBytes() > 0 {
				return true
			}
		}
		return false
	})
}

func TestCloseSendsEOFAndStreamFinishesWhenAcked(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(1024, 1<<20))
	s := m.Open("c", agentv1.LogSource_LOG_SOURCE_CONTAINER, false, nil)
	_ = s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte("partial without newline"))
	s.Close("container removed")
	waitFor(t, "eof", func() bool {
		cs := sink.chunks()
		return len(cs) > 0 && cs[len(cs)-1].GetEof()
	})
	cs := sink.chunks()
	if cs[len(cs)-1].GetEofReason() != "container removed" {
		t.Fatalf("eof reason %q", cs[len(cs)-1].GetEofReason())
	}
	if !strings.Contains(sink.data(), "partial without newline") {
		t.Fatal("unterminated final line lost on close")
	}
	if len(m.ActiveIDs()) != 1 {
		t.Fatal("stream must stay known until its EOF is acknowledged (resume)")
	}
	m.FlowControl(&agentv1.LogFlowControl{StreamId: "c", AckedSequence: cs[len(cs)-1].GetSequence(), WindowBytes: 1024})
	select {
	case <-s.Done():
	case <-time.After(2 * time.Second):
		t.Fatal("stream not finished after EOF ack")
	}
	if len(m.ActiveIDs()) != 0 {
		t.Fatal("finished stream still listed")
	}
	if s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte("x")) == nil {
		t.Fatal("write to a finished stream must fail")
	}
}

func TestResumeResendsUnackedChunksAfterReconnect(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(100, 1<<20))
	s := m.Open("b", agentv1.LogSource_LOG_SOURCE_BUILD, true, nil)
	for i := 0; i < 4; i++ {
		_ = s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte(strings.Repeat("r", 99)+"\n"))
	}
	waitFor(t, "4 chunks", func() bool { return len(sink.chunks()) == 4 })

	// Connection drops; new session. The control plane stored through seq 2.
	sink.epoch.Add(1)
	before := len(sink.chunks())
	m.FlowControl(&agentv1.LogFlowControl{StreamId: "b", AckedSequence: 2, WindowBytes: 1 << 20})
	waitFor(t, "resend", func() bool { return len(sink.chunks()) == before+2 })
	re := sink.chunks()[before:]
	if re[0].GetSequence() != 3 || re[1].GetSequence() != 4 {
		t.Fatalf("resent sequences %d,%d want 3,4", re[0].GetSequence(), re[1].GetSequence())
	}
	// A second flow control in the same session does not resend again.
	m.FlowControl(&agentv1.LogFlowControl{StreamId: "b", AckedSequence: 3, WindowBytes: 1 << 20})
	time.Sleep(60 * time.Millisecond)
	if len(sink.chunks()) != before+2 {
		t.Fatal("chunks resent twice in one session")
	}
}

func TestChunksCreatedWhileDisconnectedAreKept(t *testing.T) {
	sink := &fakeSink{}
	sink.down.Store(true)
	m := NewManager(sink, fast(100, 1<<20))
	s := m.Open("b", agentv1.LogSource_LOG_SOURCE_BUILD, true, nil)
	_ = s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte("while offline\n"))
	time.Sleep(100 * time.Millisecond)
	if len(sink.chunks()) != 0 {
		t.Fatal("sink was down")
	}
	sink.down.Store(false)
	sink.epoch.Add(1)
	m.FlowControl(&agentv1.LogFlowControl{StreamId: "b", AckedSequence: 0, WindowBytes: 1 << 20})
	waitFor(t, "delivery after reconnect", func() bool { return strings.Contains(sink.data(), "while offline") })
}

func TestSessionEndedDropsOnlyContainerStreams(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(100, 1<<20))
	build := m.Open("b", agentv1.LogSource_LOG_SOURCE_BUILD, true, nil)
	ctr := m.Open("c", agentv1.LogSource_LOG_SOURCE_CONTAINER, false, nil)
	ag := m.Open("agent", agentv1.LogSource_LOG_SOURCE_AGENT, false, nil)
	m.SessionEnded()
	select {
	case <-ctr.Done():
	case <-time.After(time.Second):
		t.Fatal("container stream survived the session")
	}
	if m.Get("b") != build || m.Get("agent") != ag {
		t.Fatal("build/agent streams must survive reconnects")
	}
}

func TestAbortOverridesGracefulCloseStuckWithoutCredit(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(50, 50))
	s := m.Open("c", agentv1.LogSource_LOG_SOURCE_CONTAINER, false, nil)
	_ = s.Write(agentv1.LogStream_LOG_STREAM_STDOUT, []byte(strings.Repeat("q", 49)+"\n"+strings.Repeat("q", 49)+"\n"))
	waitFor(t, "window used", func() bool { return sink.bytes() == 50 })
	s.Close("reader finished") // 50 more bytes pending, no credit: EOF cannot follow
	time.Sleep(60 * time.Millisecond)
	for _, c := range sink.chunks() {
		if c.GetEof() {
			t.Fatal("EOF sent before pending data drained")
		}
	}
	s.Abort("stopped by control plane")
	waitFor(t, "eof after abort", func() bool {
		cs := sink.chunks()
		return len(cs) > 0 && cs[len(cs)-1].GetEof() && cs[len(cs)-1].GetEofReason() == "stopped by control plane"
	})
}

func TestStopSendsEOF(t *testing.T) {
	sink := &fakeSink{}
	m := NewManager(sink, fast(100, 1<<20))
	m.Open("c", agentv1.LogSource_LOG_SOURCE_CONTAINER, false, nil)
	if !m.Stop("c", "stopped") || m.Stop("nope", "x") {
		t.Fatal("Stop result wrong")
	}
	waitFor(t, "eof", func() bool {
		cs := sink.chunks()
		return len(cs) == 1 && cs[0].GetEof() && cs[0].GetEofReason() == "stopped"
	})
}
