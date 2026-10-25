package ops

import (
	"context"
	"io"
	"strings"
	"sync"
	"testing"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/backup"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
	"github.com/mihaim21/aethera-forge/agent/internal/policy"
	"github.com/mihaim21/aethera-forge/agent/internal/secrets"
)

func newExecDeps(t *testing.T) (*Deps, *dockertest.Fake, *policy.Policy) {
	t.Helper()
	d, f := newDeps()
	cfg := config.Default()
	cfg.StateDir = t.TempDir()
	cfg.Finalize()
	pol := policy.New(cfg)
	pol.SetSelfContainer("feedfeedfeed")
	d.Policy = pol
	d.Exec = &backup.Executor{Docker: f, Policy: pol}
	d.Backup = &backup.Service{Docker: f, Policy: pol}
	f.AddContainer(&agentv1.ContainerInfo{
		Id: "c-web", Name: "web", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING,
		Labels: map[string]string{policy.LabelManaged: "true", policy.LabelWorkload: "w1"},
	})
	return d, f, pol
}

func execCmd(x *agentv1.ContainerExec) *agentv1.Command {
	return &agentv1.Command{CommandId: "cmd-1", Request: &agentv1.Command_ContainerExec{ContainerExec: x}}
}

func runExec(t *testing.T, d *Deps, x *agentv1.ContainerExec) (*agentv1.ContainerExecResult, error) {
	t.Helper()
	cmd := execCmd(x)
	env := &dispatch.Env{Cmd: cmd, Progress: func(string, string, int32) {}, Masker: secrets.ForMessage(cmd)}
	res, err := d.containerExec(context.Background(), env)
	if err != nil {
		return nil, err
	}
	return res.GetContainerExec(), nil
}

func TestContainerExecReturnsExitCodeAndTail(t *testing.T) {
	d, f, _ := newExecDeps(t)
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		io.WriteString(spec.Stdout, "token is hunter2-secret ok\n")
		io.WriteString(spec.Stderr, "boom\n")
		return 7, nil
	}
	res, err := runExec(t, d, &agentv1.ContainerExec{
		Container: "web", Argv: []string{"sh", "-c", "run"}, User: "app", WorkingDir: "/srv",
		Env: []*agentv1.EnvVar{
			{Name: "TOKEN", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "hunter2-secret"}}},
			{Name: "MODE", Value: &agentv1.EnvVar_Plain{Plain: "prod"}},
		},
	})
	if err != nil {
		t.Fatalf("a non-zero exit code must still succeed: %v", err)
	}
	if res.ExitCode != 7 || res.OutputTruncated {
		t.Fatalf("result %v", res)
	}
	out := string(res.OutputTail)
	if strings.Contains(out, "hunter2") || !strings.Contains(out, "token is *** ok") || !strings.Contains(out, "boom") {
		t.Fatalf("tail %q", out)
	}
	sp := f.Execs[0]
	if sp.Container != "c-web" || sp.User != "app" || sp.WorkingDir != "/srv" || strings.Join(sp.Argv, "|") != "sh|-c|run" {
		t.Fatalf("spec %+v", sp)
	}
	if strings.Join(sp.Env, ",") != "TOKEN=hunter2-secret,MODE=prod" {
		t.Fatalf("env %v", sp.Env)
	}
}

func TestContainerExecTailLimits(t *testing.T) {
	d, f, _ := newExecDeps(t)
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		io.WriteString(spec.Stdout, strings.Repeat("x", 100_000)+"END")
		return 0, nil
	}
	res, err := runExec(t, d, &agentv1.ContainerExec{Container: "web", Argv: []string{"a"}})
	if err != nil || len(res.OutputTail) != 16<<10 || !res.OutputTruncated || !strings.HasSuffix(string(res.OutputTail), "END") {
		t.Fatalf("default tail: %v %d %v", err, len(res.GetOutputTail()), res.GetOutputTruncated())
	}
	res, _ = runExec(t, d, &agentv1.ContainerExec{Container: "web", Argv: []string{"a"}, OutputTailBytes: 1 << 30})
	if len(res.OutputTail) != 64<<10 {
		t.Fatalf("cap: %d", len(res.OutputTail))
	}
	res, _ = runExec(t, d, &agentv1.ContainerExec{Container: "web", Argv: []string{"a"}, OutputTailBytes: 5})
	if string(res.OutputTail) != "xxxEND"[1:] {
		t.Fatalf("custom: %q", res.OutputTail)
	}
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		io.WriteString(spec.Stdout, "hi")
		return 0, nil
	}
	res, _ = runExec(t, d, &agentv1.ContainerExec{Container: "web", Argv: []string{"a"}})
	if string(res.OutputTail) != "hi" || res.OutputTruncated {
		t.Fatalf("short output: %q %v", res.OutputTail, res.OutputTruncated)
	}
}

func TestContainerExecErrorCodes(t *testing.T) {
	d, f, _ := newExecDeps(t)
	f.AddContainer(&agentv1.ContainerInfo{Id: "c-off", Name: "off", State: agentv1.ContainerState_CONTAINER_STATE_EXITED,
		Labels: map[string]string{policy.LabelManaged: "true", policy.LabelWorkload: "w"}})
	f.AddContainer(&agentv1.ContainerInfo{Id: "c-other", Name: "other", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING})
	f.AddContainer(&agentv1.ContainerInfo{Id: "c-proxy", Name: "aethera-traefik", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING,
		Labels: map[string]string{policy.LabelManaged: "true", policy.LabelWorkload: "proxy"}})
	f.AddContainer(&agentv1.ContainerInfo{Id: "feedfeedfeed0000", Name: "agent", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING,
		Labels: map[string]string{policy.LabelManaged: "true", policy.LabelWorkload: "w"}})
	cases := map[string]agentv1.ErrorCode{
		"missing":         agentv1.ErrorCode_ERROR_CODE_NOT_FOUND,
		"off":             agentv1.ErrorCode_ERROR_CODE_CONFLICT,
		"other":           agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION,
		"aethera-traefik": agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION,
		"agent":           agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION,
	}
	for ctr, want := range cases {
		_, err := runExec(t, d, &agentv1.ContainerExec{Container: ctr, Argv: []string{"true"}})
		if err == nil {
			t.Fatalf("%s: no error", ctr)
		}
		var got agentv1.ErrorCode
		if de, ok := err.(*dispatch.Error); ok {
			got = de.Code
		} else if v, ok := policy.AsViolation(err); ok {
			got = v.Code
		} else {
			got = docker.ErrorCode(err)
		}
		if got != want {
			t.Fatalf("%s: code %v, want %v (%v)", ctr, got, want, err)
		}
	}
	if len(f.Execs) != 0 {
		t.Fatal("exec ran in a refused container")
	}
}

func TestExecAndBackupCapabilitiesAreConditional(t *testing.T) {
	d, _ := newDeps()
	for _, c := range d.Capabilities() {
		if c == CapExec || c == CapBackup {
			t.Fatalf("%s advertised without the subsystem", c)
		}
	}
	d2, _, _ := newExecDeps(t)
	caps := strings.Join(d2.Capabilities(), ",")
	if !strings.Contains(caps, "container.exec") || !strings.Contains(caps, "backup.v1") {
		t.Fatalf("caps %s", caps)
	}
	d2.Backup = nil
	caps = strings.Join(d2.Capabilities(), ",")
	if !strings.Contains(caps, "container.exec") || strings.Contains(caps, "backup.v1") {
		t.Fatalf("caps %s", caps)
	}
}

// capture is a dispatch.Sender that keeps the messages.
type capture struct {
	mu   sync.Mutex
	msgs []*agentv1.AgentMessage
}

func (c *capture) Control(m *agentv1.AgentMessage) bool {
	c.mu.Lock()
	c.msgs = append(c.msgs, m)
	c.mu.Unlock()
	return true
}

func (c *capture) results() []*agentv1.CommandResult {
	c.mu.Lock()
	defer c.mu.Unlock()
	var out []*agentv1.CommandResult
	for _, m := range c.msgs {
		if r := m.GetCommandResult(); r != nil {
			out = append(out, r)
		}
	}
	return out
}

func (c *capture) acks() []*agentv1.CommandAck {
	c.mu.Lock()
	defer c.mu.Unlock()
	var out []*agentv1.CommandAck
	for _, m := range c.msgs {
		if a := m.GetCommandAck(); a != nil {
			out = append(out, a)
		}
	}
	return out
}

func dispatcherFor(t *testing.T, d *Deps, pol *policy.Policy) (*dispatch.Dispatcher, *capture) {
	t.Helper()
	cap := &capture{}
	disp := dispatch.New(dispatch.Config{Sender: cap, Policy: pol, Capabilities: d.Capabilities(), BaseContext: context.Background()})
	Register(disp, d)
	return disp, cap
}

func TestExecThroughDispatcherDeadlineAndPolicy(t *testing.T) {
	d, f, pol := newExecDeps(t)
	disp, cap := dispatcherFor(t, d, pol)

	// A deadline kills the program and reports TIMED_OUT.
	f.ExecFunc = func(ctx context.Context, spec docker.ExecSpec) (int, error) { <-ctx.Done(); return 0, ctx.Err() }
	cmd := execCmd(&agentv1.ContainerExec{Container: "web", Argv: []string{"sleep", "100"}})
	cmd.Deadline = timestamppb.New(time.Now().Add(300 * time.Millisecond))
	disp.Handle(cmd)
	disp.Wait()
	rs := cap.results()
	if len(rs) != 1 || rs[0].GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_TIMED_OUT || rs[0].GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_TIMEOUT {
		t.Fatalf("results %v", rs)
	}

	// A refused container is a POLICY_VIOLATION, not a result.
	f.AddContainer(&agentv1.ContainerInfo{Id: "c-x", Name: "x", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING})
	cmd2 := execCmd(&agentv1.ContainerExec{Container: "x", Argv: []string{"id"}})
	cmd2.CommandId = "cmd-2"
	disp.Handle(cmd2)
	disp.Wait()
	rs = cap.results()
	if len(rs) != 2 || rs[1].GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_FAILED || rs[1].GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION {
		t.Fatalf("results %v", rs)
	}

	// A malformed argv is refused before the ack.
	cmd3 := execCmd(&agentv1.ContainerExec{Container: "web"})
	cmd3.CommandId = "cmd-3"
	disp.Handle(cmd3)
	acks := cap.acks()
	if last := acks[len(acks)-1]; last.GetCommandId() != "cmd-3" || last.GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_POLICY {
		t.Fatalf("ack %v", last)
	}
}

func TestExecNotRegisteredWithoutSubsystem(t *testing.T) {
	d, _ := newDeps()
	disp, cap := dispatcherFor(t, d, nil)
	disp.Handle(execCmd(&agentv1.ContainerExec{Container: "web", Argv: []string{"id"}}))
	acks := cap.acks()
	if len(acks) != 1 || acks[0].GetStatus() != agentv1.AckStatus_ACK_STATUS_REJECTED_UNSUPPORTED {
		t.Fatalf("acks %v", acks)
	}
}

func TestBackupRunThroughDispatcher(t *testing.T) {
	d, f, pol := newExecDeps(t)
	disp, cap := dispatcherFor(t, d, pol)
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		spec.Stdout.Write([]byte("PGDMP-fake-dump"))
		return 0, nil
	}
	key := "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE="
	run := &agentv1.BackupRun{
		BackupId:   "b-1",
		Source:     &agentv1.BackupSource{Kind: &agentv1.BackupSource_Exec{Exec: &agentv1.ExecDump{Container: "web", Argv: []string{"pg_dump"}}}},
		Target:     &agentv1.BackupLocation{Kind: &agentv1.BackupLocation_Local{Local: &agentv1.LocalLocation{Path: "w/b-1.aeb"}}},
		Encryption: &agentv1.BackupEncryption{Key: &agentv1.SecretValue{Value: key}, KeyId: "k1"},
	}
	disp.Handle(&agentv1.Command{CommandId: "bk-1", Request: &agentv1.Command_BackupRun{BackupRun: run}})
	disp.Wait()
	rs := cap.results()
	if len(rs) != 1 || rs[0].GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED || rs[0].GetBackupRun().GetRawSizeBytes() != int64(len("PGDMP-fake-dump")) {
		t.Fatalf("results %v", rs)
	}
	if len(rs[0].GetBackupRun().GetStoredSha256()) != 64 {
		t.Fatal("no checksum")
	}

	// Delete through the dispatcher answers an empty result.
	del := &agentv1.BackupDelete{Locations: []*agentv1.BackupLocation{run.Target}}
	disp.Handle(&agentv1.Command{CommandId: "bk-2", Request: &agentv1.Command_BackupDelete{BackupDelete: del}})
	disp.Wait()
	rs = cap.results()
	if len(rs) != 2 || rs[1].GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED {
		t.Fatalf("delete results %v", rs)
	}

	// A failing dump is FAILED with the exit code and no secret in the message.
	f.ExecFunc = func(_ context.Context, spec docker.ExecSpec) (int, error) {
		io.WriteString(spec.Stderr, "auth failed for "+key+"\n")
		return 2, nil
	}
	disp.Handle(&agentv1.Command{CommandId: "bk-3", Request: &agentv1.Command_BackupRun{BackupRun: run}})
	disp.Wait()
	rs = cap.results()
	last := rs[len(rs)-1]
	if last.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_FAILED || last.GetExitCode() != 2 || strings.Contains(last.GetErrorMessage(), key) ||
		last.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_INTERNAL {
		t.Fatalf("failure result %v", last)
	}
}
