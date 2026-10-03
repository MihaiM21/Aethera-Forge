package transport

import (
	"testing"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// These tests exercise the generated protocol code so that a stale or broken
// generation fails the build.

func TestAgentMessageRoundTrip(t *testing.T) {
	in := &agentv1.AgentMessage{
		Payload: &agentv1.AgentMessage_Hello{
			Hello: &agentv1.Hello{
				ServerId:        "srv-1",
				AgentVersion:    "0.0.0-test",
				ProtocolVersion: 1,
				Capabilities:    []string{"logs.follow", "compose.v2"},
			},
		},
	}

	b, err := proto.Marshal(in)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	out := &agentv1.AgentMessage{}
	if err := proto.Unmarshal(b, out); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if !proto.Equal(in, out) {
		t.Fatalf("round trip mismatch: %v != %v", in, out)
	}
	hello := out.GetHello()
	if hello == nil || hello.GetServerId() != "srv-1" || hello.GetProtocolVersion() != 1 {
		t.Fatalf("unexpected hello: %v", hello)
	}
}

func TestControlMessageCommandRoundTrip(t *testing.T) {
	deadline := time.Date(2026, 10, 3, 12, 0, 0, 0, time.UTC)
	in := &agentv1.ControlMessage{
		Payload: &agentv1.ControlMessage_Command{
			Command: &agentv1.Command{
				CommandId:      "cmd-1",
				IdempotencyKey: "deploy-42-step-3",
				Deadline:       timestamppb.New(deadline),
				Request: &agentv1.Command_ContainerCreate{
					ContainerCreate: &agentv1.ContainerCreate{
						Spec:  &agentv1.ContainerSpec{Image: "nginx:1.27", Name: "web"},
						Start: true,
					},
				},
			},
		},
	}

	b, err := proto.Marshal(in)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	out := &agentv1.ControlMessage{}
	if err := proto.Unmarshal(b, out); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if !proto.Equal(in, out) {
		t.Fatalf("round trip mismatch: %v != %v", in, out)
	}
	cmd := out.GetCommand()
	if cmd == nil || cmd.GetContainerCreate() == nil {
		t.Fatalf("expected a ContainerCreate command, got %v", cmd)
	}
	if got := cmd.GetContainerCreate().GetSpec().GetImage(); got != "nginx:1.27" {
		t.Fatalf("image = %q", got)
	}
	if !cmd.GetDeadline().AsTime().Equal(deadline) {
		t.Fatalf("deadline = %v", cmd.GetDeadline().AsTime())
	}
}
