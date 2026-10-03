// Package dispatch is the typed command dispatcher (ADR 0002 "Command
// allowlist", "Command idempotency, timeouts, cancellation"). It owns ack
// timing, idempotency, deadlines, cancellation, the concurrency limit and
// capability gating. What each command does lives in registered handlers; a
// command with no handler is REJECTED_UNSUPPORTED. There is no generic
// exec/shell path.
package dispatch

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"sort"
	"sync"
	"sync/atomic"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
	"github.com/mihaim21/aethera-forge/agent/internal/secrets"
)

// DefaultTimeout bounds commands that arrive without a deadline.
const DefaultTimeout = 30 * time.Minute

// forceFinishSlack is how long after cancel grace the dispatcher stops waiting
// for an unresponsive handler and reports CANCELLED anyway.
const forceFinishSlack = 5 * time.Second

var errCancelled = errors.New("command cancelled")

// Sender delivers control-priority messages to the control plane. It returns
// false when there is no live session (the message is dropped; results are
// recovered through idempotent redelivery).
type Sender interface {
	Control(msg *agentv1.AgentMessage) bool
}

// Checker is the policy gate.
type Checker interface {
	CheckCommand(ctx context.Context, cmd *agentv1.Command) error
}

// Env is what a handler receives besides its context.
type Env struct {
	Cmd *agentv1.Command
	// Progress reports step-level progress (percent < 0 = unknown).
	Progress func(step, message string, percent int32)
	// Masker knows the secret values carried by this command.
	Masker *secrets.Masker
	grace  *atomic.Int64
}

// CancelGrace is the grace period of a cancel request (0 until cancelled).
func (e *Env) CancelGrace() time.Duration { return time.Duration(e.grace.Load()) }

// Handler executes one command arm. The returned message carries only the
// typed result oneof (everything else is filled in by the dispatcher).
type Handler func(ctx context.Context, env *Env) (*agentv1.CommandResult, error)

// Error is a handler failure with a stable protocol code.
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

// codeOf maps an arbitrary error to code and exit code.
func codeOf(err error) (agentv1.ErrorCode, int32) {
	var e *Error
	if errors.As(err, &e) {
		return e.Code, e.ExitCode
	}
	if v, ok := policy.AsViolation(err); ok {
		return v.Code, 0
	}
	return docker.ErrorCode(err), 0
}

type registration struct {
	handler    Handler
	capability string
}

type exec struct {
	key      string
	typ      string
	waiters  map[string]struct{}
	cancel   context.CancelCauseFunc
	grace    atomic.Int64
	started  time.Time
	deadline time.Time
	masker   *secrets.Masker
	finished bool
}

// Config assembles a Dispatcher.
type Config struct {
	Sender       Sender
	Policy       Checker
	Store        *IdemStore
	Capabilities []string
	// Now is the local clock; Skew returns (control plane time - local time).
	Now           func() time.Time
	Skew          func() time.Duration
	MaxConcurrent int
	// BaseContext is the parent of every execution (agent lifetime, not session
	// lifetime: a dropped stream must not cancel work).
	BaseContext context.Context
	Logger      *slog.Logger
}

// Dispatcher runs commands.
type Dispatcher struct {
	cfg      Config
	caps     map[string]bool
	handlers map[string]registration

	mu      sync.Mutex
	maxConc int
	byKey   map[string]*exec
	byCmd   map[string]*exec
	seq     atomic.Uint64
	wg      sync.WaitGroup
}

// New creates a dispatcher.
func New(cfg Config) *Dispatcher {
	if cfg.Now == nil {
		cfg.Now = time.Now
	}
	if cfg.Skew == nil {
		cfg.Skew = func() time.Duration { return 0 }
	}
	if cfg.BaseContext == nil {
		cfg.BaseContext = context.Background()
	}
	if cfg.Logger == nil {
		cfg.Logger = slog.Default()
	}
	if cfg.Store == nil {
		cfg.Store, _ = OpenIdemStore("", cfg.Now)
	}
	if cfg.MaxConcurrent <= 0 {
		cfg.MaxConcurrent = 8
	}
	d := &Dispatcher{
		cfg: cfg, caps: map[string]bool{}, handlers: map[string]registration{},
		maxConc: cfg.MaxConcurrent, byKey: map[string]*exec{}, byCmd: map[string]*exec{},
	}
	for _, c := range cfg.Capabilities {
		d.caps[c] = true
	}
	return d
}

// Register binds the handler for a Command.request arm (proto field name such
// as "container_create"). A non-empty capability must be advertised in Hello.
func (d *Dispatcher) Register(arm, capability string, h Handler) {
	d.handlers[arm] = registration{handler: h, capability: capability}
}

// Capabilities lists the advertised capability strings.
func (d *Dispatcher) Capabilities() []string {
	out := make([]string, 0, len(d.caps))
	for c := range d.caps {
		out = append(out, c)
	}
	sort.Strings(out)
	return out
}

// SetMaxConcurrent updates the limit (from Welcome / local config).
func (d *Dispatcher) SetMaxConcurrent(n int) {
	if n <= 0 {
		return
	}
	d.mu.Lock()
	d.maxConc = n
	d.mu.Unlock()
}

// Running is the number of executions in progress.
func (d *Dispatcher) Running() int {
	d.mu.Lock()
	defer d.mu.Unlock()
	return len(d.byKey)
}

// RunningIDs lists the command ids of in-flight work (Hello.running_command_ids).
func (d *Dispatcher) RunningIDs() []string {
	d.mu.Lock()
	defer d.mu.Unlock()
	ids := make([]string, 0, len(d.byCmd))
	for id := range d.byCmd {
		ids = append(ids, id)
	}
	return ids
}

// Wait blocks until all executions ended (tests, shutdown).
func (d *Dispatcher) Wait() { d.wg.Wait() }

func armOf(cmd *agentv1.Command) string {
	m := cmd.ProtoReflect()
	od := m.Descriptor().Oneofs().ByName("request")
	if od == nil {
		return ""
	}
	if fd := m.WhichOneof(od); fd != nil {
		return string(fd.Name())
	}
	return ""
}

func (d *Dispatcher) send(msg *agentv1.AgentMessage) { d.cfg.Sender.Control(msg) }

func (d *Dispatcher) ack(id string, st agentv1.AckStatus, code agentv1.ErrorCode, msg string) {
	d.send(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_CommandAck{CommandAck: &agentv1.CommandAck{
		CommandId: id, Status: st, ErrorCode: code, Message: msg, ReceivedAt: timestamppb.New(d.cfg.Now()),
	}}})
}

func (d *Dispatcher) sendResult(id string, res *agentv1.CommandResult, replayed bool) {
	r := proto.Clone(res).(*agentv1.CommandResult)
	r.CommandId, r.Replayed = id, replayed
	d.send(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_CommandResult{CommandResult: r}})
}

func (d *Dispatcher) controlNow() time.Time { return d.cfg.Now().Add(d.cfg.Skew()) }

// remaining converts the absolute (control plane clock) deadline into a local
// timeout using the skew measured at Welcome.
func (d *Dispatcher) remaining(deadline *timestamppb.Timestamp) (time.Duration, bool) {
	if deadline == nil || deadline.AsTime().IsZero() || deadline.GetSeconds() == 0 {
		return DefaultTimeout, false
	}
	return deadline.AsTime().Sub(d.controlNow()), true
}

// Handle processes one Command. It acks synchronously (so the ack precedes any
// progress or result on the control queue) and runs the work in a goroutine.
func (d *Dispatcher) Handle(cmd *agentv1.Command) {
	id := cmd.GetCommandId()
	if id == "" {
		d.ack("", agentv1.AckStatus_ACK_STATUS_REJECTED_POLICY, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "command_id is required")
		return
	}
	arm := armOf(cmd)
	reg, ok := d.handlers[arm]
	switch {
	case arm == "" || !ok:
		name := arm
		if name == "" {
			name = "(unknown)"
		}
		d.ack(id, agentv1.AckStatus_ACK_STATUS_REJECTED_UNSUPPORTED, agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED,
			fmt.Sprintf("command %s is not supported by this agent version", name))
		return
	case reg.capability != "" && !d.caps[reg.capability]:
		d.ack(id, agentv1.AckStatus_ACK_STATUS_REJECTED_UNSUPPORTED, agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED,
			fmt.Sprintf("command %s needs capability %s which this agent does not provide", arm, reg.capability))
		return
	}

	key := cmd.GetIdempotencyKey()
	if d.tryDuplicate(cmd, key) {
		return
	}

	timeout, hasDeadline := d.remaining(cmd.GetDeadline())
	if hasDeadline && timeout <= 0 {
		d.ack(id, agentv1.AckStatus_ACK_STATUS_REJECTED_EXPIRED, agentv1.ErrorCode_ERROR_CODE_TIMEOUT, "deadline already passed")
		return
	}
	d.mu.Lock()
	busy := len(d.byKey) >= d.maxConc
	d.mu.Unlock()
	if busy {
		d.ack(id, agentv1.AckStatus_ACK_STATUS_REJECTED_BUSY, agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED, "agent is at its concurrent command limit")
		return
	}
	if d.cfg.Policy != nil {
		pctx, cancel := context.WithTimeout(d.cfg.BaseContext, 3*time.Second)
		err := d.cfg.Policy.CheckCommand(pctx, cmd)
		cancel()
		if err != nil {
			code, _ := codeOf(err)
			d.ack(id, agentv1.AckStatus_ACK_STATUS_REJECTED_POLICY, code, sanitize(secrets.ForMessage(cmd), err.Error()))
			return
		}
	}

	// Register under the lock, re-checking duplicates and the limit.
	d.mu.Lock()
	if key != "" {
		if ex, ok := d.byKey[key]; ok {
			ex.waiters[id] = struct{}{}
			d.byCmd[id] = ex
			d.mu.Unlock()
			d.ack(id, agentv1.AckStatus_ACK_STATUS_DUPLICATE_RUNNING, agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED, "")
			return
		}
	}
	if len(d.byKey) >= d.maxConc {
		d.mu.Unlock()
		d.ack(id, agentv1.AckStatus_ACK_STATUS_REJECTED_BUSY, agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED, "agent is at its concurrent command limit")
		return
	}
	mapKey := key
	if mapKey == "" {
		mapKey = fmt.Sprintf("\x00anon-%d", d.seq.Add(1))
	}
	now := d.cfg.Now()
	ex := &exec{
		key: mapKey, typ: arm, waiters: map[string]struct{}{id: {}}, started: now,
		deadline: now.Add(timeout), masker: secrets.ForMessage(cmd),
	}
	ctx, cancel := context.WithCancelCause(d.cfg.BaseContext)
	ex.cancel = cancel
	d.byKey[mapKey] = ex
	d.byCmd[id] = ex
	if key != "" {
		d.cfg.Store.PutRunning(key, id, ex.deadline)
	}
	d.wg.Add(1)
	d.mu.Unlock()

	d.ack(id, agentv1.AckStatus_ACK_STATUS_ACCEPTED, agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED, "")
	go d.run(ctx, cancel, ex, reg.handler, cmd, timeout)
}

// tryDuplicate answers a redelivery of a known idempotency key.
func (d *Dispatcher) tryDuplicate(cmd *agentv1.Command, key string) bool {
	if key == "" {
		return false
	}
	id := cmd.GetCommandId()
	d.mu.Lock()
	if ex, ok := d.byKey[key]; ok {
		ex.waiters[id] = struct{}{}
		d.byCmd[id] = ex
		d.mu.Unlock()
		d.ack(id, agentv1.AckStatus_ACK_STATUS_DUPLICATE_RUNNING, agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED, "")
		return true
	}
	d.mu.Unlock()
	e, ok := d.cfg.Store.Get(key)
	if !ok || e.State != StateDone {
		return false
	}
	var res agentv1.CommandResult
	if err := proto.Unmarshal(e.Result, &res); err != nil {
		d.cfg.Logger.Error("cached result unreadable; re-executing", "key_present", true)
		return false
	}
	d.ack(id, agentv1.AckStatus_ACK_STATUS_DUPLICATE_COMPLETED, agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED, "")
	d.sendResult(id, &res, true)
	return true
}

func sanitize(m *secrets.Masker, s string) string {
	s = m.String(s)
	if len(s) > 2048 {
		s = s[:2048] + "...(truncated)"
	}
	return s
}

func (d *Dispatcher) run(ctx context.Context, cancel context.CancelCauseFunc, ex *exec, h Handler, cmd *agentv1.Command, timeout time.Duration) {
	defer d.wg.Done()
	defer cancel(nil)
	log := d.cfg.Logger.With("command_type", ex.typ, "command_id", cmd.GetCommandId(), "job_id", cmd.GetJobId())
	started := d.cfg.Now()

	tctx, tcancel := context.WithTimeout(ctx, timeout)
	defer tcancel()

	env := &Env{Cmd: cmd, Masker: ex.masker, grace: &ex.grace}
	env.Progress = func(step, message string, percent int32) {
		d.mu.Lock()
		ids := make([]string, 0, len(ex.waiters))
		for w := range ex.waiters {
			ids = append(ids, w)
		}
		d.mu.Unlock()
		for _, w := range ids {
			d.send(&agentv1.AgentMessage{Payload: &agentv1.AgentMessage_CommandProgress{CommandProgress: &agentv1.CommandProgress{
				CommandId: w, Step: step, Message: sanitize(ex.masker, message), Percent: percent, At: timestamppb.New(d.cfg.Now()),
			}}})
		}
	}

	var res *agentv1.CommandResult
	var err error
	func() {
		defer func() {
			if r := recover(); r != nil {
				err = Errorf(agentv1.ErrorCode_ERROR_CODE_INTERNAL, "internal error in %s handler", ex.typ)
				log.Error("handler panic", "panic", fmt.Sprint(r))
			}
		}()
		res, err = h(tctx, env)
	}()

	out := &agentv1.CommandResult{StartedAt: timestamppb.New(started)}
	switch {
	case err == nil:
		if res != nil {
			out = proto.Clone(res).(*agentv1.CommandResult)
			out.StartedAt = timestamppb.New(started)
		}
		out.Status = agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED
		if out.Result == nil {
			out.Result = &agentv1.CommandResult_Empty{Empty: &agentv1.EmptyResult{}}
		}
	case errors.Is(context.Cause(ctx), errCancelled):
		out.Status = agentv1.CommandStatus_COMMAND_STATUS_CANCELLED
		out.ErrorCode = agentv1.ErrorCode_ERROR_CODE_CANCELLED
		out.ErrorMessage = "command cancelled"
	case errors.Is(tctx.Err(), context.DeadlineExceeded):
		out.Status = agentv1.CommandStatus_COMMAND_STATUS_TIMED_OUT
		out.ErrorCode = agentv1.ErrorCode_ERROR_CODE_TIMEOUT
		out.ErrorMessage = "command exceeded its deadline"
	default:
		out.Status = agentv1.CommandStatus_COMMAND_STATUS_FAILED
		out.ErrorCode, out.ExitCode = codeOf(err)
		out.ErrorMessage = sanitize(ex.masker, err.Error())
		if res != nil {
			out.Result = res.Result // partial data that is useful on failure
		}
	}
	d.complete(ex, out)
	log.Info("command finished", "status", out.GetStatus().String(), "error_code", out.GetErrorCode().String())
}

// complete records the result and delivers it to every waiter. Idempotent per
// execution (a force-finished handler that returns later is ignored).
func (d *Dispatcher) complete(ex *exec, out *agentv1.CommandResult) {
	d.mu.Lock()
	if ex.finished {
		d.mu.Unlock()
		return
	}
	ex.finished = true
	d.mu.Unlock()

	out.FinishedAt = timestamppb.New(d.cfg.Now())
	// Persist before releasing the key, so a redelivery always finds the result.
	if len(ex.key) > 0 && ex.key[0] != 0 {
		if err := d.cfg.Store.Finish(ex.key, out); err != nil {
			d.cfg.Logger.Error("persist result failed", "error", err.Error())
		}
	}
	d.mu.Lock()
	ids := make([]string, 0, len(ex.waiters))
	for w := range ex.waiters {
		ids = append(ids, w)
		delete(d.byCmd, w)
	}
	delete(d.byKey, ex.key)
	d.mu.Unlock()
	for _, w := range ids {
		d.sendResult(w, out, false)
	}
}

// Cancel handles CancelCommand.
func (d *Dispatcher) Cancel(c *agentv1.CancelCommand) {
	id := c.GetCommandId()
	d.mu.Lock()
	ex := d.byCmd[id]
	if ex == nil {
		d.mu.Unlock()
		d.cfg.Logger.Info("cancel for unknown command ignored", "command_id", id)
		return
	}
	if len(ex.waiters) > 1 {
		// Other deliveries still want the result: detach only this one.
		delete(ex.waiters, id)
		delete(d.byCmd, id)
		d.mu.Unlock()
		d.sendResult(id, &agentv1.CommandResult{
			Status: agentv1.CommandStatus_COMMAND_STATUS_CANCELLED, ErrorCode: agentv1.ErrorCode_ERROR_CODE_CANCELLED,
			ErrorMessage: "command cancelled", FinishedAt: timestamppb.New(d.cfg.Now()),
		}, false)
		return
	}
	grace := c.GetGrace().AsDuration()
	if c.GetGrace() == nil {
		grace = 15 * time.Second
	}
	ex.grace.Store(int64(grace))
	d.mu.Unlock()
	ex.cancel(errCancelled)
	time.AfterFunc(grace+forceFinishSlack, func() {
		d.complete(ex, &agentv1.CommandResult{
			Status: agentv1.CommandStatus_COMMAND_STATUS_CANCELLED, ErrorCode: agentv1.ErrorCode_ERROR_CODE_CANCELLED,
			ErrorMessage: "command cancelled", StartedAt: timestamppb.New(ex.started),
		})
	})
}
