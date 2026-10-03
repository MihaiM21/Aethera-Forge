package dispatch

import (
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

type clock struct{ t time.Time }

func (c *clock) now() time.Time          { return c.t }
func (c *clock) advance(d time.Duration) { c.t = c.t.Add(d) }

func okResult() *agentv1.CommandResult {
	return &agentv1.CommandResult{
		Status: agentv1.CommandStatus_COMMAND_STATUS_SUCCEEDED,
		Result: &agentv1.CommandResult_Empty{Empty: &agentv1.EmptyResult{}},
	}
}

func TestIdemStorePersistsAcrossRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state", "idem.jsonl")
	ck := &clock{t: time.Unix(1_700_000_000, 0)}
	s, err := OpenIdemStore(path, ck.now)
	if err != nil {
		t.Fatal(err)
	}
	s.PutRunning("k1", "cmd-1", ck.t.Add(time.Minute))
	res := okResult()
	res.ErrorMessage = "kept"
	if err := s.Finish("k1", res); err != nil {
		t.Fatal(err)
	}
	s.PutRunning("k2", "cmd-2", ck.t.Add(time.Minute)) // still running when the "crash" happens
	s.Close()

	s2, err := OpenIdemStore(path, ck.now)
	if err != nil {
		t.Fatal(err)
	}
	defer s2.Close()
	e1, ok := s2.Get("k1")
	if !ok || e1.State != StateDone {
		t.Fatalf("k1 = %+v ok=%v", e1, ok)
	}
	var got agentv1.CommandResult
	if err := proto.Unmarshal(e1.Result, &got); err != nil || got.GetErrorMessage() != "kept" {
		t.Fatalf("cached result lost: %v %v", err, &got)
	}
	e2, ok := s2.Get("k2")
	if !ok || e2.State != StateDone {
		t.Fatalf("interrupted command must be completed as failed, got %+v", e2)
	}
	var failed agentv1.CommandResult
	_ = proto.Unmarshal(e2.Result, &failed)
	if failed.GetStatus() != agentv1.CommandStatus_COMMAND_STATUS_FAILED || failed.GetErrorCode() != agentv1.ErrorCode_ERROR_CODE_INTERNAL {
		t.Fatalf("interrupted result = %v", &failed)
	}
}

func TestIdemStoreExpiryAfter24hAndDeadline(t *testing.T) {
	ck := &clock{t: time.Unix(1_700_000_000, 0)}
	s, _ := OpenIdemStore("", ck.now)
	s.PutRunning("short", "c", ck.t.Add(time.Minute))
	_ = s.Finish("short", okResult())
	s.PutRunning("long", "c", ck.t.Add(48*time.Hour)) // deadline beyond the TTL
	_ = s.Finish("long", okResult())

	ck.advance(23 * time.Hour)
	s.PutRunning("trigger", "c", ck.t) // any write runs expiry
	if _, ok := s.Get("short"); !ok {
		t.Fatal("entry expired before 24h")
	}
	ck.advance(2 * time.Hour)
	s.PutRunning("trigger2", "c", ck.t)
	if _, ok := s.Get("short"); ok {
		t.Fatal("entry survived past 24h")
	}
	if _, ok := s.Get("long"); !ok {
		t.Fatal("entry must live at least until its deadline")
	}
}

func TestIdemStoreCapEvictsOldestFinished(t *testing.T) {
	ck := &clock{t: time.Unix(1_700_000_000, 0)}
	s, _ := OpenIdemStore("", ck.now)
	s.max = 50
	for i := 0; i < 80; i++ {
		k := "k" + strconv.Itoa(i)
		s.PutRunning(k, "c", ck.t.Add(time.Minute))
		_ = s.Finish(k, okResult())
		ck.advance(time.Second)
	}
	if n := s.Len(); n > 50 {
		t.Fatalf("table holds %d entries, cap is 50", n)
	}
	if _, ok := s.Get("k0"); ok {
		t.Fatal("oldest entry was not evicted")
	}
	if _, ok := s.Get("k79"); !ok {
		t.Fatal("newest entry evicted")
	}
}

func TestIdemStoreToleratesTornJournalLine(t *testing.T) {
	path := filepath.Join(t.TempDir(), "idem.jsonl")
	ck := &clock{t: time.Unix(1_700_000_000, 0)}
	s, _ := OpenIdemStore(path, ck.now)
	s.PutRunning("k", "c", ck.t.Add(time.Minute))
	_ = s.Finish("k", okResult())
	s.Close()
	f, _ := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0o600)
	_, _ = f.WriteString(`{"k":"torn","s":"do`) // crash in the middle of a write
	f.Close()
	s2, err := OpenIdemStore(path, ck.now)
	if err != nil {
		t.Fatal(err)
	}
	defer s2.Close()
	if _, ok := s2.Get("k"); !ok {
		t.Fatal("intact record lost")
	}
	if _, ok := s2.Get("torn"); ok {
		t.Fatal("torn record resurrected")
	}
}

func TestIdemStoreFileIsPrivate(t *testing.T) {
	path := filepath.Join(t.TempDir(), "idem.jsonl")
	s, _ := OpenIdemStore(path, nil)
	defer s.Close()
	s.PutRunning("k", "c", time.Now().Add(time.Minute))
	st, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if st.Mode().Perm()&0o077 != 0 {
		t.Fatalf("journal mode %v is accessible to group/others", st.Mode().Perm())
	}
}
