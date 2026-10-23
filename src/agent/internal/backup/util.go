package backup

import (
	"context"
	"errors"
	"fmt"
	"hash"
	"io"
	"sync"
	"sync/atomic"
	"syscall"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/secrets"
)

// Error is a failure with a stable protocol code. Messages never contain
// secret values (they are redacted before an Error leaves this package).
type Error struct {
	Code     agentv1.ErrorCode
	Msg      string
	ExitCode int32
}

func (e *Error) Error() string { return e.Msg }

// Errorf builds an *Error.
func Errorf(code agentv1.ErrorCode, format string, a ...any) *Error {
	return &Error{Code: code, Msg: fmt.Sprintf(format, a...)}
}

// isDiskFull reports "no space left on device" on every platform.
func isDiskFull(err error) bool {
	return errors.Is(err, syscall.ENOSPC) || isWindowsDiskFull(err)
}

// ctxReader fails reads once ctx is done.
type ctxReader struct {
	ctx context.Context
	r   io.Reader
}

func (c ctxReader) Read(p []byte) (int, error) {
	if err := c.ctx.Err(); err != nil {
		return 0, err
	}
	return c.r.Read(p)
}

// countWriter counts the bytes that pass through.
type countWriter struct {
	w io.Writer
	n *atomic.Int64
}

func (c *countWriter) Write(p []byte) (int, error) {
	n, err := c.w.Write(p)
	c.n.Add(int64(n))
	return n, err
}

// countReader counts the bytes read.
type countReader struct {
	r io.Reader
	n *atomic.Int64
}

func (c *countReader) Read(p []byte) (int, error) {
	n, err := c.r.Read(p)
	c.n.Add(int64(n))
	return n, err
}

// hashWriter hashes and counts what is written (the stored object).
type hashWriter struct {
	w io.Writer
	h hash.Hash
	n *atomic.Int64
}

func (h *hashWriter) Write(p []byte) (int, error) {
	n, err := h.w.Write(p)
	h.h.Write(p[:n])
	h.n.Add(int64(n))
	return n, err
}

// Tail keeps the end of what is written to it (the last bytes of a program's
// output) so a failure can say why.
type Tail struct {
	mu        sync.Mutex
	max       int
	buf       []byte
	truncated bool
}

// NewTail keeps up to max bytes (plus what masking needs, see Result).
func NewTail(max int) *Tail { return &Tail{max: max} }

// Write implements io.Writer.
func (t *Tail) Write(p []byte) (int, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.buf = append(t.buf, p...)
	if over := len(t.buf) - t.window(); over > 0 {
		t.buf = append(t.buf[:0], t.buf[over:]...)
		t.truncated = true
	}
	return len(p), nil
}

// window is the retained size: callers read at most max bytes, and secrets
// are masked before the cut, so keep a margin.
func (t *Tail) window() int { return t.max + 4096 }

// Result returns the last n bytes with secrets masked and whether older output
// was dropped.
func (t *Tail) Result(m *secrets.Masker, n int) ([]byte, bool) {
	t.mu.Lock()
	defer t.mu.Unlock()
	b := m.Bytes(append([]byte(nil), t.buf...))
	trunc := t.truncated
	if len(b) > n {
		b = b[len(b)-n:]
		trunc = true
	}
	return b, trunc
}

// String is Result as text.
func (t *Tail) String(m *secrets.Masker, n int) string {
	b, _ := t.Result(m, n)
	return string(b)
}

// envStrings renders variables as NAME=value for the Docker API.
func envStrings(vars []*agentv1.EnvVar) []string {
	out := make([]string, 0, len(vars))
	for _, e := range vars {
		v := e.GetPlain()
		if s := e.GetSecret(); s != nil {
			v = s.GetValue()
		}
		out = append(out, e.GetName()+"="+v)
	}
	return out
}
