package transport

import (
	"context"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

func TestBackoffCeilingFollowsADRTable(t *testing.T) {
	b := &Backoff{Rand: func() float64 { return 1 - 1e-12 }}
	want := []time.Duration{1, 2, 4, 8, 16, 32, 60, 60, 60, 60}
	for i, w := range want {
		got := b.Next()
		ceil := w * time.Second
		if got > ceil || got < ceil-time.Millisecond*10 {
			t.Fatalf("attempt %d: delay %v, want ~%v (ceiling)", i, got, ceil)
		}
	}
	// After 10 consecutive failures the cap rises to 5 minutes.
	if got := b.Next(); got <= 60*time.Second || got > 5*time.Minute {
		t.Fatalf("11th delay %v should come from the 5 minute cap", got)
	}
	for i := 0; i < 20; i++ {
		if got := b.Next(); got > 5*time.Minute {
			t.Fatalf("delay %v above the 5 minute cap", got)
		}
	}
}

func TestBackoffIsFullJitter(t *testing.T) {
	zero := &Backoff{Rand: func() float64 { return 0 }}
	for i := 0; i < 5; i++ {
		if d := zero.Next(); d != 0 {
			t.Fatalf("full jitter must be able to draw 0, got %v", d)
		}
	}
	half := &Backoff{Rand: func() float64 { return 0.5 }}
	if d := half.Next(); d != 500*time.Millisecond {
		t.Fatalf("0.5 of the 1s ceiling = %v", d)
	}
	if d := half.Next(); d != time.Second { // 0.5 of 2s
		t.Fatalf("0.5 of the 2s ceiling = %v", d)
	}
}

func TestBackoffReset(t *testing.T) {
	b := &Backoff{Rand: func() float64 { return 1 - 1e-12 }}
	for i := 0; i < 5; i++ {
		b.Next()
	}
	if b.Failures() != 5 {
		t.Fatalf("failures = %d", b.Failures())
	}
	b.Reset()
	if b.Failures() != 0 || b.Ceiling() != time.Second {
		t.Fatalf("after reset failures=%d ceiling=%v", b.Failures(), b.Ceiling())
	}
}

func hb(seq uint64) *agentv1.AgentMessage {
	return &agentv1.AgentMessage{Payload: &agentv1.AgentMessage_Heartbeat{Heartbeat: &agentv1.Heartbeat{Seq: seq}}}
}

func chunk(seq uint64, n int) *agentv1.AgentMessage {
	return &agentv1.AgentMessage{Payload: &agentv1.AgentMessage_LogChunk{LogChunk: &agentv1.LogChunk{Sequence: seq, Data: make([]byte, n)}}}
}

func TestSenderDrainsControlBeforeData(t *testing.T) {
	s := NewSender()
	for i := uint64(1); i <= 5; i++ {
		s.Data(chunk(i, 100))
	}
	s.Control(hb(1))
	s.Control(hb(2))
	ctx := context.Background()
	var order []string
	for i := 0; i < 7; i++ {
		m, ok := s.next(ctx)
		if !ok {
			t.Fatal("queue ended early")
		}
		if m.GetHeartbeat() != nil {
			order = append(order, "control")
		} else {
			order = append(order, "data")
		}
	}
	want := []string{"control", "control", "data", "data", "data", "data", "data"}
	for i := range want {
		if order[i] != want[i] {
			t.Fatalf("order = %v", order)
		}
	}
}

func TestSenderKeepsFIFOWithinAClass(t *testing.T) {
	s := NewSender()
	for i := uint64(1); i <= 3; i++ {
		s.Data(chunk(i, 1))
	}
	for i := uint64(1); i <= 3; i++ {
		m, _ := s.next(context.Background())
		if m.GetLogChunk().GetSequence() != i {
			t.Fatalf("chunk %d out of order: %d", i, m.GetLogChunk().GetSequence())
		}
	}
}

func TestSenderDataQueueIsBoundedAndControlIsNot(t *testing.T) {
	s := NewSender()
	big := chunk(1, 1<<20)
	accepted := 0
	for i := 0; i < 40; i++ {
		if s.Data(big) {
			accepted++
		}
	}
	if accepted >= 40 || accepted == 0 {
		t.Fatalf("accepted %d 1MiB messages; the bound is %d bytes", accepted, maxDataBytes)
	}
	if !s.Control(hb(1)) {
		t.Fatal("control messages must not be dropped because data is full")
	}
}

func TestSenderCloseUnblocksAndRejects(t *testing.T) {
	s := NewSender()
	done := make(chan bool)
	go func() {
		_, ok := s.next(context.Background())
		done <- ok
	}()
	time.Sleep(20 * time.Millisecond)
	s.Close()
	select {
	case ok := <-done:
		if ok {
			t.Fatal("next returned a message after close")
		}
	case <-time.After(time.Second):
		t.Fatal("next did not unblock")
	}
	if s.Control(hb(1)) || s.Data(chunk(1, 1)) {
		t.Fatal("closed sender accepted messages")
	}
}

func TestSenderContextCancelUnblocks(t *testing.T) {
	s := NewSender()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() { s.next(ctx); close(done) }()
	cancel()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("next ignored context cancellation")
	}
}

func TestRealClockSleepHonoursContext(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := RealClock.Sleep(ctx, time.Hour); err == nil {
		t.Fatal("sleep on a cancelled context returned nil")
	}
	if err := RealClock.Sleep(context.Background(), 5*time.Millisecond); err != nil {
		t.Fatal(err)
	}
}
