package transport

import (
	"context"
	"sync"

	"google.golang.org/protobuf/proto"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// maxDataBytes bounds the data queue (log chunks, metrics, discovery).
const maxDataBytes = 16 << 20

// Sender is the two-level priority queue in front of the stream (ADR 0002):
// control messages (heartbeat, acks, progress, results, events, pongs) are
// always drained before data messages (log chunks, metrics, discovery). A
// message in flight is at most one log chunk, so a heartbeat waits for at most
// one chunk.
type Sender struct {
	mu        sync.Mutex
	control   []*agentv1.AgentMessage
	data      []*agentv1.AgentMessage
	dataBytes int
	closed    bool
	notify    chan struct{}
}

// NewSender creates an empty queue.
func NewSender() *Sender { return &Sender{notify: make(chan struct{}, 1)} }

func (s *Sender) wake() {
	select {
	case s.notify <- struct{}{}:
	default:
	}
}

// Control queues a control-priority message. False when closed.
func (s *Sender) Control(m *agentv1.AgentMessage) bool {
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		return false
	}
	s.control = append(s.control, m)
	s.mu.Unlock()
	s.wake()
	return true
}

// Data queues a data-priority message. False when closed or when the queue is
// full (the message is dropped; metrics are not buffered across gaps).
func (s *Sender) Data(m *agentv1.AgentMessage) bool {
	n := proto.Size(m)
	s.mu.Lock()
	if s.closed || s.dataBytes+n > maxDataBytes {
		s.mu.Unlock()
		return false
	}
	s.data = append(s.data, m)
	s.dataBytes += n
	s.mu.Unlock()
	s.wake()
	return true
}

// Pending returns the queue lengths (tests).
func (s *Sender) Pending() (control, data int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.control), len(s.data)
}

// Close rejects further messages and wakes the pump.
func (s *Sender) Close() {
	s.mu.Lock()
	s.closed = true
	s.mu.Unlock()
	s.wake()
}

// next pops the highest priority message, waiting if both queues are empty.
func (s *Sender) next(ctx context.Context) (*agentv1.AgentMessage, bool) {
	for {
		s.mu.Lock()
		if len(s.control) > 0 {
			m := s.control[0]
			s.control = s.control[1:]
			s.mu.Unlock()
			return m, true
		}
		if len(s.data) > 0 {
			m := s.data[0]
			s.data = s.data[1:]
			s.dataBytes -= proto.Size(m)
			s.mu.Unlock()
			return m, true
		}
		closed := s.closed
		s.mu.Unlock()
		if closed {
			return nil, false
		}
		select {
		case <-s.notify:
		case <-ctx.Done():
			return nil, false
		}
	}
}
