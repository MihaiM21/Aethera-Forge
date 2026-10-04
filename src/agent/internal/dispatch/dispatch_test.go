package dispatch

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

type recSender struct {
	mu   sync.Mutex
	msgs []*agentv1.AgentMessage
}

func (r *recSender) Control(m *agentv1.AgentMessage) bool {
	r.mu.Lock()
	r.msgs = append(r.msgs, m)
	r.mu.Unlock()
	return true
}

func (r *recSender) find(pred func(*agentv1.AgentMessage) bool) *agentv1.AgentMessage {
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		r.mu.Lock()
		for _, m := range r.msgs {
			if pred(m) {
				r.mu.Unlock()
				return m
			}
		}
		r.mu.Unlock()
		time.Sleep(5 * time.Millisecond)
	}
	return nil
}

func (r *recSender) result(id string) *agentv1.CommandResult {
	m := r.find(func(m *agentv1.AgentMessage) bool { return m.GetCommandResult().GetCommandId() == id })
	if m == nil {
		return nil
	}
	return m.GetCommandResult()
}

func (r *recSender) ack(id string) *agentv1.CommandAck {
	m := r.find(func(m *agentv1.AgentMessage) bool { return m.GetCommandAck().GetCommandId() == id })
	if m == nil {
		return nil
	}
	return m.GetCommandAck()
}

func newDisp(s Sender, caps ...string) *Dispatcher {
	return New(Config{Sender: s, Capabilities: caps})
}

func listReq(id, key string) *agentv1.Command {
	return &agentv1.Command{
		CommandId: id, IdempotencyKey: key, Deadline: timestamppb.New(time.Now().Add(time.Minute)),
		Request: &agentv1.Command_ContainerList{ContainerList: &agentv1.ContainerList{}},
	}
}

func TestHandlerPanicBecomesInternalFailure(t *testing.T) {
	s := &recSender{}
	d := newDisp(s)
	d.Register("container_list", "", func(context.Context, *Env) (*agentv1.CommandResult, error) { panic("boom") })
	d.Handle(listReq("c1", "k1"))
	r := s.result("c1")
	if r == nil || r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_FAILED || r.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_INTERNAL {
		t.Fatalf("result = %v", r)
	}
	if r.GetErrorMessage() == "boom" {
		t.Fatal("panic value must not leak into the result")
	}
	if d.Running() != 0 {
		t.Fatal("panicked command still counted as running")
	}
}

func TestCapabilityGatingRejectsUnsupported(t *testing.T) {
	s := &recSender{}
	d := newDisp(s, "docker.containers")
	d.Register("container_list", "docker.containers", func(context.Context, *Env) (*agentv1.CommandResult, error) { return nil, nil })
	d.Register("image_list", "docker.images", func(context.Context, *Env) (*agentv1.CommandResult, error) { return nil, nil })
	d.Handle(&agentv1.Command{CommandId: "i", Request: &agentv1.Command_ImageList{ImageList: &agentv1.ImageList{}}})
	a := s.ack("i")
	if a == nil || a.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_UNSUPPORTED {
		t.Fatalf("ack = %v", a)
	}
	d.Handle(listReq("ok", "kok"))
	if a := s.ack("ok"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_ACCEPTED {
		t.Fatalf("ack = %v", a)
	}
}

func TestMissingCommandIDIsRejected(t *testing.T) {
	s := &recSender{}
	d := newDisp(s)
	d.Handle(&agentv1.Command{Request: &agentv1.Command_ContainerList{ContainerList: &agentv1.ContainerList{}}})
	a := s.ack("")
	if a == nil || a.GetStatus() == agentv1.AckStatus_ACK_STATUS_ACCEPTED {
		t.Fatalf("ack = %v", a)
	}
}

func TestCancelForcesResultFromUnresponsiveHandler(t *testing.T) {
	s := &recSender{}
	d := newDisp(s)
	release := make(chan struct{})
	d.Register("container_list", "", func(ctx context.Context, env *Env) (*agentv1.CommandResult, error) {
		<-release // ignores ctx on purpose
		return nil, nil
	})
	defer close(release)
	d.Handle(listReq("stuck", "kstuck"))
	// Shorten the slack via a tiny grace; the dispatcher still waits forceFinishSlack.
	d.Cancel(&agentv1.CancelCommand{CommandId: "stuck", Grace: durationpb.New(0)})
	start := time.Now()
	deadline := time.After(forceFinishSlack + 3*time.Second)
	for {
		if r := s.result("stuck"); r != nil {
			if r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_CANCELLED {
				t.Fatalf("status = %v", r.GetStatus())
			}
			break
		}
		select {
		case <-deadline:
			t.Fatal("no CANCELLED result from a hung handler")
		default:
		}
	}
	if time.Since(start) < forceFinishSlack-500*time.Millisecond {
		t.Fatalf("result forced too early (%v)", time.Since(start))
	}
	if d.Running() != 0 {
		t.Fatal("abandoned command still occupies a concurrency slot")
	}
}

func TestCancelOneWaiterKeepsExecutionForOthers(t *testing.T) {
	s := &recSender{}
	d := newDisp(s)
	release := make(chan struct{})
	d.Register("container_list", "", func(ctx context.Context, env *Env) (*agentv1.CommandResult, error) {
		select {
		case <-release:
			return nil, nil
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	})
	d.Handle(listReq("a", "same"))
	d.Handle(listReq("b", "same"))
	if a := s.ack("b"); a.GetStatus() != agentv1.AckStatus_ACK_STATUS_DUPLICATE_RUNNING {
		t.Fatalf("ack = %v", a)
	}
	d.Cancel(&agentv1.CancelCommand{CommandId: "b", Grace: durationpb.New(time.Second)})
	if r := s.result("b"); r == nil || r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_CANCELLED {
		t.Fatalf("cancelled waiter result = %v", r)
	}
	close(release)
	if r := s.result("a"); r == nil || r.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED {
		t.Fatalf("remaining waiter result = %v", r)
	}
}

func TestCancelUnknownCommandIsIgnored(t *testing.T) {
	d := newDisp(&recSender{})
	d.Cancel(&agentv1.CancelCommand{CommandId: "nope"}) // must not panic or block
}

func TestPolicyRejectionCarriesCodeAndNoSecret(t *testing.T) {
	s := &recSender{}
	d := New(Config{Sender: s, Policy: denyAll{}})
	d.Register("container_list", "", func(context.Context, *Env) (*agentv1.CommandResult, error) { return nil, nil })
	d.Handle(listReq("p", "kp"))
	a := s.ack("p")
	if a.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_POLICY || a.GetMessage() == "" {
		t.Fatalf("ack = %v", a)
	}
	if d.Running() != 0 {
		t.Fatal("rejected command registered as running")
	}
	// A rejected command leaves no idempotency entry: the same key may be retried.
	if _, ok := d.cfg.Store.Get("kp"); ok {
		t.Fatal("rejected command cached")
	}
}

type denyAll struct{}

func (denyAll) CheckCommand(context.Context, *agentv1.Command) error {
	return errors.New("denied by test")
}

func TestProgressGoesToEveryWaiter(t *testing.T) {
	s := &recSender{}
	d := newDisp(s)
	started := make(chan struct{})
	release := make(chan struct{})
	d.Register("container_list", "", func(ctx context.Context, env *Env) (*agentv1.CommandResult, error) {
		close(started)
		<-release
		env.Progress("step", "halfway", 50)
		return nil, nil
	})
	d.Handle(listReq("a", "kk"))
	<-started
	d.Handle(listReq("b", "kk"))
	close(release)
	for _, id := range []string{"a", "b"} {
		if m := s.find(func(m *agentv1.AgentMessage) bool { return m.GetCommandProgress().GetCommandId() == id }); m == nil {
			t.Errorf("no progress for %s", id)
		}
	}
}
