package agent

import (
	"context"
	"log/slog"
	"strings"
	"sync/atomic"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/logstream"
)

// LogForwarder is a slog.Handler that logs locally and forwards WARN and above
// to the control plane as an AGENT log stream (lossy, ADR 0002). Agent log
// lines never contain secrets: errors are sanitized at their source.
type LogForwarder struct {
	inner  slog.Handler
	stream atomic.Pointer[logstream.Stream]
}

// AgentLogStreamID is the stream id used for forwarded agent logs.
const AgentLogStreamID = "agent"

// NewLogForwarder wraps inner.
func NewLogForwarder(inner slog.Handler) *LogForwarder { return &LogForwarder{inner: inner} }

// Attach starts forwarding into st.
func (f *LogForwarder) Attach(st *logstream.Stream) { f.stream.Store(st) }

func (f *LogForwarder) Enabled(ctx context.Context, l slog.Level) bool {
	return f.inner.Enabled(ctx, l)
}

func (f *LogForwarder) Handle(ctx context.Context, r slog.Record) error {
	if r.Level >= slog.LevelWarn {
		if st := f.stream.Load(); st != nil {
			var b strings.Builder
			b.WriteString(r.Time.UTC().Format("2006-01-02T15:04:05Z"))
			b.WriteByte(' ')
			b.WriteString(r.Level.String())
			b.WriteByte(' ')
			b.WriteString(r.Message)
			r.Attrs(func(a slog.Attr) bool {
				b.WriteByte(' ')
				b.WriteString(a.Key)
				b.WriteByte('=')
				b.WriteString(a.Value.String())
				return true
			})
			b.WriteByte('\n')
			_ = st.Write(agentv1.LogStream_LOG_STREAM_STDERR, []byte(b.String()))
		}
	}
	return f.inner.Handle(ctx, r)
}

func (f *LogForwarder) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &forwarderChild{parent: f, inner: f.inner.WithAttrs(attrs)}
}

func (f *LogForwarder) WithGroup(name string) slog.Handler {
	return &forwarderChild{parent: f, inner: f.inner.WithGroup(name)}
}

// forwarderChild shares the parent's stream but uses a derived inner handler.
type forwarderChild struct {
	parent *LogForwarder
	inner  slog.Handler
}

func (c *forwarderChild) Enabled(ctx context.Context, l slog.Level) bool {
	return c.inner.Enabled(ctx, l)
}

func (c *forwarderChild) Handle(ctx context.Context, r slog.Record) error {
	tmp := &LogForwarder{inner: c.inner}
	tmp.stream.Store(c.parent.stream.Load())
	return tmp.Handle(ctx, r)
}

func (c *forwarderChild) WithAttrs(a []slog.Attr) slog.Handler {
	return &forwarderChild{parent: c.parent, inner: c.inner.WithAttrs(a)}
}

func (c *forwarderChild) WithGroup(n string) slog.Handler {
	return &forwarderChild{parent: c.parent, inner: c.inner.WithGroup(n)}
}
