package dispatch

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// Idempotency table limits (ADR 0002).
const (
	IdemTTL = 24 * time.Hour
	IdemMax = 5000
)

// State of an idempotency entry.
type State string

const (
	StateRunning State = "running"
	StateDone    State = "done"
)

// Entry is one idempotency table row.
type Entry struct {
	Key       string
	State     State
	CommandID string    // delivery that started the execution
	Deadline  time.Time // absolute deadline of the execution
	Finished  time.Time
	Result    []byte // marshalled CommandResult (command_id unset)
}

// journalRec is the on-disk line format. Rows are appended; the last record
// for a key wins; the file is compacted when it grows.
type journalRec struct {
	K string `json:"k"`
	S State  `json:"s"`
	C string `json:"c,omitempty"`
	D int64  `json:"d,omitempty"` // deadline, unix ms
	F int64  `json:"f,omitempty"` // finished, unix ms
	R []byte `json:"r,omitempty"`
	X bool   `json:"x,omitempty"` // tombstone
}

// IdemStore is the persistent idempotency table. A nil path keeps it in memory
// only (tests).
type IdemStore struct {
	mu      sync.Mutex
	path    string
	now     func() time.Time
	max     int
	ttl     time.Duration
	entries map[string]*Entry
	f       *os.File
	lines   int
}

// OpenIdemStore loads (or creates) the table at path. Entries that were still
// "running" when the previous agent process ended are completed as FAILED:
// their work was interrupted and, to avoid double execution of non-idempotent
// operations, the job engine must retry with a new key.
func OpenIdemStore(path string, now func() time.Time) (*IdemStore, error) {
	if now == nil {
		now = time.Now
	}
	s := &IdemStore{path: path, now: now, max: IdemMax, ttl: IdemTTL, entries: map[string]*Entry{}}
	if path == "" {
		return s, nil
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, err
	}
	if err := s.load(); err != nil {
		return nil, err
	}
	s.expireLocked()
	if err := s.compactLocked(); err != nil {
		return nil, err
	}
	return s, nil
}

func (s *IdemStore) load() error {
	data, err := os.ReadFile(s.path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil
		}
		return err
	}
	sc := bufio.NewScanner(bytes.NewReader(data))
	sc.Buffer(make([]byte, 0, 64*1024), 64<<20)
	for sc.Scan() {
		var r journalRec
		if json.Unmarshal(sc.Bytes(), &r) != nil {
			continue // torn last line after a crash
		}
		if r.X {
			delete(s.entries, r.K)
			continue
		}
		e := &Entry{Key: r.K, State: r.S, CommandID: r.C, Result: r.R}
		if r.D != 0 {
			e.Deadline = time.UnixMilli(r.D)
		}
		if r.F != 0 {
			e.Finished = time.UnixMilli(r.F)
		}
		s.entries[r.K] = e
	}
	now := s.now()
	for _, e := range s.entries {
		if e.State != StateRunning {
			continue
		}
		res := &agentv1.CommandResult{
			Status:       agentv1.CommandStatus_COMMAND_STATUS_FAILED,
			ErrorCode:    agentv1.ErrorCode_ERROR_CODE_INTERNAL,
			ErrorMessage: "agent restarted while the command was running; outcome unknown, retry with a new idempotency key",
			FinishedAt:   timestamppb.New(now),
		}
		b, _ := proto.Marshal(res)
		e.State, e.Result, e.Finished = StateDone, b, now
	}
	return nil
}

func (s *IdemStore) compactLocked() error {
	if s.path == "" {
		return nil
	}
	if s.f != nil {
		s.f.Close()
		s.f = nil
	}
	var buf bytes.Buffer
	keys := make([]string, 0, len(s.entries))
	for k := range s.entries {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		line, _ := json.Marshal(recOf(s.entries[k]))
		buf.Write(line)
		buf.WriteByte('\n')
	}
	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, buf.Bytes(), 0o600); err != nil {
		return err
	}
	if err := os.Rename(tmp, s.path); err != nil {
		return err
	}
	f, err := os.OpenFile(s.path, os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	s.f = f
	s.lines = len(keys)
	return nil
}

func recOf(e *Entry) journalRec {
	r := journalRec{K: e.Key, S: e.State, C: e.CommandID, R: e.Result}
	if !e.Deadline.IsZero() {
		r.D = e.Deadline.UnixMilli()
	}
	if !e.Finished.IsZero() {
		r.F = e.Finished.UnixMilli()
	}
	return r
}

func (s *IdemStore) appendLocked(r journalRec) {
	if s.f == nil {
		return
	}
	line, err := json.Marshal(r)
	if err != nil {
		return
	}
	line = append(line, '\n')
	if _, err := s.f.Write(line); err == nil {
		_ = s.f.Sync()
		s.lines++
	}
	if s.lines > 4*s.max {
		_ = s.compactLocked()
	}
}

// Get returns a copy of the entry for key.
func (s *IdemStore) Get(key string) (Entry, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	e, ok := s.entries[key]
	if !ok {
		return Entry{}, false
	}
	return *e, true
}

// PutRunning records that key started executing.
func (s *IdemStore) PutRunning(key, commandID string, deadline time.Time) {
	s.mu.Lock()
	defer s.mu.Unlock()
	e := &Entry{Key: key, State: StateRunning, CommandID: commandID, Deadline: deadline}
	s.entries[key] = e
	s.appendLocked(recOf(e))
	s.expireLocked()
}

// Finish stores the terminal result for key.
func (s *IdemStore) Finish(key string, result *agentv1.CommandResult) error {
	c := proto.Clone(result).(*agentv1.CommandResult)
	c.CommandId, c.Replayed = "", false
	b, err := proto.Marshal(c)
	if err != nil {
		return fmt.Errorf("marshal result: %w", err)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	e := s.entries[key]
	if e == nil {
		e = &Entry{Key: key}
		s.entries[key] = e
	}
	e.State, e.Result, e.Finished = StateDone, b, s.now()
	s.appendLocked(recOf(e))
	s.expireLocked()
	return nil
}

// Len is the number of rows.
func (s *IdemStore) Len() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.entries)
}

// expireLocked drops done rows older than max(TTL, deadline) and enforces the
// size cap by evicting the oldest finished rows. Running rows are kept.
func (s *IdemStore) expireLocked() {
	now := s.now()
	for k, e := range s.entries {
		if e.State != StateDone {
			continue
		}
		keepUntil := e.Finished.Add(s.ttl)
		if e.Deadline.After(keepUntil) {
			keepUntil = e.Deadline
		}
		if now.After(keepUntil) {
			s.deleteLocked(k)
		}
	}
	if over := len(s.entries) - s.max; over > 0 {
		done := make([]*Entry, 0, len(s.entries))
		for _, e := range s.entries {
			if e.State == StateDone {
				done = append(done, e)
			}
		}
		sort.Slice(done, func(i, j int) bool { return done[i].Finished.Before(done[j].Finished) })
		for i := 0; i < over && i < len(done); i++ {
			s.deleteLocked(done[i].Key)
		}
	}
}

func (s *IdemStore) deleteLocked(k string) {
	delete(s.entries, k)
	s.appendLocked(journalRec{K: k, X: true})
}

// Close releases the journal file.
func (s *IdemStore) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.f != nil {
		err := s.f.Close()
		s.f = nil
		return err
	}
	return nil
}
