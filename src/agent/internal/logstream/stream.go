// Package logstream implements the agent side of ADR 0002 "Log streaming and
// flow control": line buffering, secret masking before sending, chunking by
// size or time, strictly increasing sequence numbers, per-stream credit based
// flow control, lossless (build/deploy) versus lossy (container, agent)
// policies, and resend-after-reconnect from the control plane's acked sequence.
package logstream

import (
	"bytes"
	"errors"
	"io"
	"sync"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/secrets"
)

// Defaults (ADR 0002).
const (
	DefaultChunkMax  = 32 * 1024
	DefaultWindow    = 256 * 1024
	DefaultFlush     = 250 * time.Millisecond
	DefaultBufferCap = 1 << 20
	maxPartialLine   = 64 * 1024
	eofRetention     = 10 * time.Minute
)

// ErrClosed is returned by writes to a finished stream.
var ErrClosed = errors.New("log stream closed")

// Sink is the transport side of the manager.
type Sink interface {
	// SendData queues a data-priority message. It returns false when there is
	// no live session; the stream keeps the chunk for resend.
	SendData(msg *agentv1.AgentMessage) bool
	// Epoch changes whenever a new session starts.
	Epoch() uint64
}

// Config tunes a Manager. Zero values mean defaults.
type Config struct {
	ChunkMax  int
	Window    uint64
	Flush     time.Duration
	BufferCap int
}

func (c Config) withDefaults() Config {
	if c.ChunkMax <= 0 {
		c.ChunkMax = DefaultChunkMax
	}
	if c.Window == 0 {
		c.Window = DefaultWindow
	}
	if c.Flush <= 0 {
		c.Flush = DefaultFlush
	}
	if c.BufferCap <= 0 {
		c.BufferCap = DefaultBufferCap
	}
	return c
}

// Manager owns all active log streams.
type Manager struct {
	sink Sink

	mu      sync.Mutex
	cfg     Config
	streams map[string]*Stream
}

// NewManager creates a manager that sends through sink.
func NewManager(sink Sink, cfg Config) *Manager {
	return &Manager{sink: sink, cfg: cfg.withDefaults(), streams: map[string]*Stream{}}
}

// SetLimits applies the Welcome parameters to streams opened from now on.
func (m *Manager) SetLimits(chunkMax uint32, window uint64) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if chunkMax > 0 {
		m.cfg.ChunkMax = int(chunkMax)
	}
	if window > 0 {
		m.cfg.Window = window
	}
}

// Open starts a stream. lossless streams (build, deploy) apply back-pressure
// to the producer; lossy ones drop the oldest data and report dropped_bytes.
// An existing stream with the same id is aborted and replaced.
func (m *Manager) Open(id string, source agentv1.LogSource, lossless bool, masker *secrets.Masker) *Stream {
	m.mu.Lock()
	old := m.streams[id]
	cfg := m.cfg
	s := &Stream{
		m: m, id: id, source: source, lossless: lossless, masker: masker, cfg: cfg,
		window: cfg.Window, wake: make(chan struct{}, 1), spaceCh: make(chan struct{}),
		stop: make(chan struct{}), done: make(chan struct{}), partial: map[agentv1.LogStream]*partialLine{},
		sentEpoch: m.sink.Epoch(),
	}
	m.streams[id] = s
	m.mu.Unlock()
	if old != nil {
		old.finish()
	}
	go s.pump()
	return s
}

// Get returns an active stream.
func (m *Manager) Get(id string) *Stream {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.streams[id]
}

// ActiveIDs lists streams still being served or awaiting acknowledgement
// (Hello.active_log_stream_ids).
func (m *Manager) ActiveIDs() []string {
	m.mu.Lock()
	defer m.mu.Unlock()
	ids := make([]string, 0, len(m.streams))
	for id := range m.streams {
		ids = append(ids, id)
	}
	return ids
}

// Stop aborts a stream, sending a final EOF chunk with reason.
func (m *Manager) Stop(id, reason string) bool {
	s := m.Get(id)
	if s == nil {
		return false
	}
	s.Abort(reason)
	return true
}

// FlowControl applies a LogFlowControl message.
func (m *Manager) FlowControl(fc *agentv1.LogFlowControl) {
	if s := m.Get(fc.GetStreamId()); s != nil {
		s.flowControl(fc.GetAckedSequence(), fc.GetWindowBytes())
	}
}

// SessionEnded drops lossy streams: nobody is listening any more and the
// control plane re-issues LogStreamStart after reconnecting. Lossless streams
// keep buffering (bounded by their window) for resume.
func (m *Manager) SessionEnded() {
	m.mu.Lock()
	var lossy []*Stream
	for _, s := range m.streams {
		if !s.lossless && s.source == agentv1.LogSource_LOG_SOURCE_CONTAINER {
			lossy = append(lossy, s)
		}
	}
	m.mu.Unlock()
	for _, s := range lossy {
		s.finish()
	}
}

// CloseAll aborts every stream without sending anything (shutdown).
func (m *Manager) CloseAll() {
	m.mu.Lock()
	all := make([]*Stream, 0, len(m.streams))
	for _, s := range m.streams {
		all = append(all, s)
	}
	m.mu.Unlock()
	for _, s := range all {
		s.finish()
	}
}

func (m *Manager) remove(s *Stream) {
	m.mu.Lock()
	if m.streams[s.id] == s {
		delete(m.streams, s.id)
	}
	m.mu.Unlock()
}

type segment struct {
	stream agentv1.LogStream
	data   []byte
	ts     time.Time
}

type partialLine struct {
	buf   []byte
	since time.Time
}

type sentChunk struct {
	seq   uint64
	bytes uint64
	msg   *agentv1.AgentMessage
}

// Stream is one multiplexed log stream (stream_id).
type Stream struct {
	m        *Manager
	id       string
	source   agentv1.LogSource
	lossless bool
	masker   *secrets.Masker
	cfg      Config

	mu           sync.Mutex
	pending      []segment
	pendingBytes int
	partial      map[agentv1.LogStream]*partialLine
	seq          uint64
	unacked      []sentChunk
	inflight     uint64
	window       uint64
	dropped      uint64
	sentEpoch    uint64
	closing      bool // producer finished; flush then EOF
	eofReason    string
	eofSent      bool
	finished     bool
	oldest       time.Time // arrival time of the oldest pending byte

	wake    chan struct{}
	spaceCh chan struct{}
	stop    chan struct{}
	done    chan struct{}
}

// ID returns the stream id.
func (s *Stream) ID() string { return s.id }

// Done is closed when the stream is fully finished.
func (s *Stream) Done() <-chan struct{} { return s.done }

// Writer returns an io.Writer for one OS stream.
func (s *Stream) Writer(stream agentv1.LogStream) io.Writer {
	return writerFunc(func(p []byte) (int, error) { return len(p), s.Write(stream, p) })
}

type writerFunc func([]byte) (int, error)

func (f writerFunc) Write(p []byte) (int, error) { return f(p) }

func (s *Stream) signal() {
	select {
	case s.wake <- struct{}{}:
	default:
	}
}

func (s *Stream) broadcastSpaceLocked() {
	close(s.spaceCh)
	s.spaceCh = make(chan struct{})
}

// Write feeds raw output. Complete lines are masked and queued; partial lines
// wait for their newline (masking therefore works across read boundaries).
// For lossless streams it blocks while the buffer is full.
func (s *Stream) Write(stream agentv1.LogStream, p []byte) error {
	if len(p) == 0 {
		return nil
	}
	now := time.Now()
	s.mu.Lock()
	for {
		if s.finished || s.closing {
			s.mu.Unlock()
			return ErrClosed
		}
		if !s.lossless || s.pendingBytes+len(p) <= s.cfg.BufferCap || s.pendingBytes == 0 {
			break
		}
		ch := s.spaceCh
		s.mu.Unlock()
		s.signal()
		select {
		case <-ch:
		case <-s.stop:
			return ErrClosed
		}
		s.mu.Lock()
	}
	pl := s.partial[stream]
	if pl == nil {
		pl = &partialLine{}
		s.partial[stream] = pl
	}
	if len(pl.buf) == 0 {
		pl.since = now
	}
	pl.buf = append(pl.buf, p...)
	if i := bytes.LastIndexByte(pl.buf, '\n'); i >= 0 {
		lines := append([]byte(nil), pl.buf[:i+1]...)
		pl.buf = append([]byte(nil), pl.buf[i+1:]...)
		if len(pl.buf) > 0 {
			pl.since = now
		}
		s.enqueueLocked(stream, s.masker.Bytes(lines), now)
	}
	if len(pl.buf) > maxPartialLine {
		s.flushPartialLocked(stream, true, now)
	}
	s.mu.Unlock()
	s.signal()
	return nil
}

// flushPartialLocked emits an unterminated line. Unless all is set it holds
// back the last maxSecret-1 bytes so a secret split across reads is still
// caught when the rest arrives.
func (s *Stream) flushPartialLocked(stream agentv1.LogStream, all bool, now time.Time) {
	pl := s.partial[stream]
	if pl == nil || len(pl.buf) == 0 {
		return
	}
	masked := s.masker.Bytes(pl.buf)
	hold := 0
	if !all && !s.masker.Empty() {
		hold = s.masker.MaxLen() - 1
	}
	if len(masked) <= hold {
		return
	}
	emit := append([]byte(nil), masked[:len(masked)-hold]...)
	pl.buf = append([]byte(nil), masked[len(masked)-hold:]...)
	pl.since = now
	s.enqueueLocked(stream, emit, now)
}

func (s *Stream) enqueueLocked(stream agentv1.LogStream, data []byte, now time.Time) {
	if len(data) == 0 {
		return
	}
	if !s.lossless {
		// Drop the oldest data to stay within the cap (lossy-with-marker).
		for s.pendingBytes+len(data) > s.cfg.BufferCap && len(s.pending) > 0 {
			d := s.pending[0]
			s.pending = s.pending[1:]
			s.pendingBytes -= len(d.data)
			s.dropped += uint64(len(d.data))
		}
		if len(data) > s.cfg.BufferCap {
			s.dropped += uint64(len(data) - s.cfg.BufferCap)
			data = data[len(data)-s.cfg.BufferCap:]
		}
	}
	if s.pendingBytes == 0 {
		s.oldest = now
	}
	if n := len(s.pending); n > 0 && s.pending[n-1].stream == stream {
		s.pending[n-1].data = append(s.pending[n-1].data, data...)
	} else {
		s.pending = append(s.pending, segment{stream: stream, data: append([]byte(nil), data...), ts: now})
	}
	s.pendingBytes += len(data)
}

// Close marks the producer finished: remaining data is flushed, then an EOF
// chunk (with reason, if abnormal) is sent.
func (s *Stream) Close(reason string) {
	s.mu.Lock()
	if s.closing || s.finished {
		s.mu.Unlock()
		return
	}
	now := time.Now()
	for st := range s.partial {
		s.flushPartialLocked(st, true, now)
	}
	s.closing = true
	s.eofReason = reason
	s.broadcastSpaceLocked()
	s.mu.Unlock()
	s.signal()
}

// Abort discards buffered data and sends EOF immediately.
func (s *Stream) Abort(reason string) {
	s.mu.Lock()
	// Abort also overrides a graceful Close that is still waiting for credit.
	if s.eofSent || s.finished {
		s.mu.Unlock()
		return
	}
	s.pending, s.pendingBytes = nil, 0
	s.partial = map[agentv1.LogStream]*partialLine{}
	s.closing = true
	s.eofReason = reason
	s.broadcastSpaceLocked()
	s.mu.Unlock()
	s.signal()
}

func (s *Stream) flowControl(acked, window uint64) {
	s.mu.Lock()
	if s.finished {
		s.mu.Unlock()
		return
	}
	keep := s.unacked[:0]
	for _, c := range s.unacked {
		if c.seq <= acked {
			s.inflight -= c.bytes
			continue
		}
		keep = append(keep, c)
	}
	s.unacked = keep
	s.window = window
	// A new session: replay everything the control plane has not stored.
	var resend []*agentv1.AgentMessage
	if epoch := s.m.sink.Epoch(); epoch != s.sentEpoch {
		for _, c := range s.unacked {
			resend = append(resend, c.msg)
		}
		s.sentEpoch = epoch
	}
	allDone := s.eofSent && len(s.unacked) == 0
	s.broadcastSpaceLocked()
	s.mu.Unlock()
	for _, msg := range resend {
		s.m.sink.SendData(msg)
	}
	if allDone {
		s.finish()
		return
	}
	s.signal()
}

func (s *Stream) finish() {
	s.mu.Lock()
	if s.finished {
		s.mu.Unlock()
		return
	}
	s.finished = true
	s.closing = true
	s.pending, s.pendingBytes = nil, 0
	close(s.stop)
	s.broadcastSpaceLocked()
	s.mu.Unlock()
	s.m.remove(s)
	close(s.done)
}

// pump cuts and sends chunks as credit and the flush timer allow.
func (s *Stream) pump() {
	timer := time.NewTimer(time.Hour)
	defer timer.Stop()
	for {
		chunk, wait := s.next()
		if chunk != nil {
			s.m.sink.SendData(chunk)
			continue
		}
		if !timer.Stop() {
			select {
			case <-timer.C:
			default:
			}
		}
		if wait <= 0 {
			wait = time.Hour
		}
		timer.Reset(wait)
		select {
		case <-s.wake:
		case <-timer.C:
		case <-s.stop:
			return
		}
	}
}

// next returns the next chunk to send, or how long to wait before looking again.
func (s *Stream) next() (*agentv1.AgentMessage, time.Duration) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.finished {
		return nil, 0
	}
	now := time.Now()

	// Time-based flush of unterminated lines.
	for st, pl := range s.partial {
		if len(pl.buf) > 0 && now.Sub(pl.since) >= s.cfg.Flush {
			s.flushPartialLocked(st, false, now)
		}
	}

	if len(s.pending) == 0 {
		if s.closing && !s.eofSent {
			return s.eofChunkLocked(now), 0
		}
		var wait time.Duration
		for _, pl := range s.partial {
			if len(pl.buf) > 0 {
				if w := s.cfg.Flush - now.Sub(pl.since); wait == 0 || w < wait {
					wait = max(w, time.Millisecond)
				}
			}
		}
		return nil, wait
	}

	// Enough data, or old enough, or finishing: cut a chunk if credit allows.
	age := now.Sub(s.oldest)
	if s.pendingBytes < s.cfg.ChunkMax && age < s.cfg.Flush && !s.closing {
		return nil, s.cfg.Flush - age
	}
	if s.window == 0 || s.inflight >= s.window {
		return nil, 0 // waiting for credit
	}
	limit := s.cfg.ChunkMax
	if credit := s.window - s.inflight; uint64(limit) > credit {
		limit = int(credit)
	}
	data, stream, ts := s.cutLocked(limit)
	s.seq++
	chunk := &agentv1.LogChunk{
		StreamId:     s.id,
		Source:       s.source,
		Sequence:     s.seq,
		Timestamp:    timestamppb.New(ts),
		Stream:       stream,
		Data:         data,
		DroppedBytes: s.dropped,
	}
	s.dropped = 0
	msg := &agentv1.AgentMessage{Payload: &agentv1.AgentMessage_LogChunk{LogChunk: chunk}}
	s.unacked = append(s.unacked, sentChunk{seq: s.seq, bytes: uint64(len(data)), msg: msg})
	s.inflight += uint64(len(data))
	s.broadcastSpaceLocked()
	if len(s.pending) > 0 {
		s.oldest = now
	}
	return msg, 0
}

func (s *Stream) eofChunkLocked(now time.Time) *agentv1.AgentMessage {
	s.seq++
	chunk := &agentv1.LogChunk{
		StreamId: s.id, Source: s.source, Sequence: s.seq, Timestamp: timestamppb.New(now),
		Eof: true, EofReason: s.eofReason, DroppedBytes: s.dropped,
	}
	s.dropped = 0
	msg := &agentv1.AgentMessage{Payload: &agentv1.AgentMessage_LogChunk{LogChunk: chunk}}
	s.unacked = append(s.unacked, sentChunk{seq: s.seq, msg: msg})
	s.eofSent = true
	time.AfterFunc(eofRetention, s.finish)
	return msg
}

// cutLocked removes up to limit bytes of one OS stream from the front,
// preferring to end on a line boundary.
func (s *Stream) cutLocked(limit int) ([]byte, agentv1.LogStream, time.Time) {
	first := s.pending[0]
	var buf []byte
	i := 0
	for ; i < len(s.pending) && s.pending[i].stream == first.stream && len(buf) < limit; i++ {
		buf = append(buf, s.pending[i].data...)
	}
	if len(buf) > limit {
		cut := limit
		if nl := bytes.LastIndexByte(buf[:limit], '\n'); nl >= 0 {
			cut = nl + 1
		}
		rest := append([]byte(nil), buf[cut:]...)
		buf = buf[:cut]
		// Put the remainder back as the head segment.
		head := segment{stream: first.stream, data: rest, ts: first.ts}
		s.pending = append([]segment{head}, s.pending[i:]...)
	} else {
		s.pending = s.pending[i:]
	}
	s.pendingBytes = 0
	for _, p := range s.pending {
		s.pendingBytes += len(p.data)
	}
	return buf, first.stream, first.ts
}
