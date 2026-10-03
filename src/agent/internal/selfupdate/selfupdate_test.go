package selfupdate

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

type restarter struct{ n atomic.Int32 }

func (r *restarter) Restart() { r.n.Add(1) }

type fixture struct {
	t        *testing.T
	dir      string
	exe      string
	srv      *httptest.Server
	body     []byte
	status   int
	u        *Updater
	restart  *restarter
	probeOut string
	clock    time.Time
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	f := &fixture{t: t, dir: t.TempDir(), body: []byte("#!/bin/sh\necho aethera-agent 2.0.0\n"), status: 200,
		probeOut: "aethera-agent 2.0.0", clock: time.Unix(1_700_000_000, 0)}
	f.exe = filepath.Join(f.dir, "bin", "aethera-agent")
	if err := os.MkdirAll(filepath.Dir(f.exe), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(f.exe, []byte("OLD-BINARY"), 0o755); err != nil {
		t.Fatal(err)
	}
	f.srv = httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(f.status)
		_, _ = w.Write(f.body)
	}))
	t.Cleanup(f.srv.Close)
	f.restart = &restarter{}
	f.u = &Updater{
		Exe: f.exe, StateDir: f.dir, CurrentVersion: "1.0.0", Client: f.srv.Client(), Restarter: f.restart,
		Probe: func(context.Context, string) (string, error) { return f.probeOut, nil },
		Now:   func() time.Time { return f.clock }, RestartDelay: 10 * time.Millisecond,
	}
	return f
}

func (f *fixture) req() Request {
	sum := sha256.Sum256(f.body)
	return Request{URL: f.srv.URL + "/agent", SHA256: hex.EncodeToString(sum[:]), Version: "2.0.0"}
}

func (f *fixture) exeContent() string {
	b, err := os.ReadFile(f.exe)
	if err != nil {
		f.t.Fatal(err)
	}
	return string(b)
}

func TestApplySwapsBinaryKeepsPreviousAndRestarts(t *testing.T) {
	f := newFixture(t)
	res, err := f.u.Apply(context.Background(), f.req())
	if err != nil {
		t.Fatal(err)
	}
	if res.PreviousVersion != "1.0.0" || res.NewVersion != "2.0.0" {
		t.Fatalf("result = %+v", res)
	}
	if f.exeContent() != string(f.body) {
		t.Fatal("binary was not replaced")
	}
	prev, _ := os.ReadFile(f.exe + ".prev")
	if string(prev) != "OLD-BINARY" {
		t.Fatalf("previous binary not preserved: %q", prev)
	}
	st, _ := os.Stat(f.exe)
	if st.Mode()&0o100 == 0 {
		t.Fatal("new binary is not executable")
	}
	if _, err := os.Stat(f.exe + ".new"); !os.IsNotExist(err) {
		t.Fatal("temporary download left behind")
	}
	deadline := time.Now().Add(2 * time.Second)
	for f.restart.n.Load() == 0 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if f.restart.n.Load() != 1 {
		t.Fatalf("restart requested %d times", f.restart.n.Load())
	}
}

func TestApplyRefusesPlainHTTPAndBadChecksumInputs(t *testing.T) {
	f := newFixture(t)
	r := f.req()
	r.URL = strings.Replace(r.URL, "https://", "http://", 1)
	if _, err := f.u.Apply(context.Background(), r); !errors.Is(err, ErrInsecureURL) {
		t.Fatalf("err = %v", err)
	}
	r = f.req()
	r.SHA256 = "abc"
	if _, err := f.u.Apply(context.Background(), r); err == nil {
		t.Fatal("short checksum accepted")
	}
	if f.exeContent() != "OLD-BINARY" {
		t.Fatal("binary touched by a rejected update")
	}
}

func TestApplyRejectsChecksumMismatchWithoutTouchingBinary(t *testing.T) {
	f := newFixture(t)
	r := f.req()
	r.SHA256 = strings.Repeat("0", 64)
	if _, err := f.u.Apply(context.Background(), r); !errors.Is(err, ErrChecksum) {
		t.Fatalf("err = %v", err)
	}
	if f.exeContent() != "OLD-BINARY" {
		t.Fatal("binary replaced despite checksum mismatch")
	}
	if _, err := os.Stat(f.exe + ".new"); !os.IsNotExist(err) {
		t.Fatal("rejected download not cleaned up")
	}
	if f.restart.n.Load() != 0 {
		t.Fatal("restart requested after a failed update")
	}
}

func TestApplyRejectsHTTPErrorAndOversizeAndBadBinary(t *testing.T) {
	f := newFixture(t)
	f.status = 404
	if _, err := f.u.Apply(context.Background(), f.req()); err == nil {
		t.Fatal("404 accepted")
	}
	f.status = 200
	f.u.MaxSize = 4
	if _, err := f.u.Apply(context.Background(), f.req()); err == nil {
		t.Fatal("oversize download accepted")
	}
	f.u.MaxSize = 0
	f.probeOut = "something else 1.0"
	if _, err := f.u.Apply(context.Background(), f.req()); !errors.Is(err, ErrBadBinary) {
		t.Fatalf("err = %v", err)
	}
	if f.exeContent() != "OLD-BINARY" {
		t.Fatal("binary replaced by an update that failed")
	}
}

func TestSignatureRequiredWhenReleaseKeyConfigured(t *testing.T) {
	f := newFixture(t)
	pub, priv, _ := ed25519.GenerateKey(rand.Reader)
	f.u.PublicKey = pub

	if _, err := f.u.Apply(context.Background(), f.req()); !errors.Is(err, ErrSignature) {
		t.Fatalf("unsigned update accepted: %v", err)
	}
	r := f.req()
	r.Signature = ed25519.Sign(priv, []byte("not the digest"))
	if _, err := f.u.Apply(context.Background(), r); !errors.Is(err, ErrSignature) {
		t.Fatalf("bad signature accepted: %v", err)
	}
	if f.exeContent() != "OLD-BINARY" {
		t.Fatal("binary replaced by an unverified update")
	}

	digest, _ := hex.DecodeString(f.req().SHA256)
	r.Signature = ed25519.Sign(priv, digest) // signature over the raw SHA-256 digest
	if _, err := f.u.Apply(context.Background(), r); err != nil {
		t.Fatalf("valid signature rejected: %v", err)
	}
}

func TestPlainHTTPRedirectIsRefused(t *testing.T) {
	f := newFixture(t)
	plain := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write(f.body) }))
	defer plain.Close()
	redirect := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, plain.URL, http.StatusFound)
	}))
	defer redirect.Close()
	r := f.req()
	r.URL = redirect.URL
	// The default client's redirect policy refuses a downgrade to plain http.
	base := (&Updater{}).client()
	f.u.Client = &http.Client{Transport: redirect.Client().Transport, CheckRedirect: base.CheckRedirect}
	if _, err := f.u.Apply(context.Background(), r); err == nil {
		t.Fatal("redirect to plain http followed")
	}
}

func TestRollbackWhenNewBinaryNeverConfirms(t *testing.T) {
	f := newFixture(t)
	if _, err := f.u.Apply(context.Background(), f.req()); err != nil {
		t.Fatal(err)
	}
	// The service manager started the new binary; it dies before Welcome and is
	// restarted over and over.
	for boot := 1; boot <= maxBoot; boot++ {
		if act := f.u.OnStart(); act != Probation {
			t.Fatalf("boot %d: action = %v, want probation", boot, act)
		}
		f.u.watchdog.Stop()
	}
	before := f.restart.n.Load()
	if act := f.u.OnStart(); act != RolledBack {
		t.Fatalf("action = %v, want rollback", act)
	}
	if f.exeContent() != "OLD-BINARY" {
		t.Fatalf("previous binary not restored: %q", f.exeContent())
	}
	if f.restart.n.Load() != before+1 {
		t.Fatal("rollback did not request a restart")
	}
	if _, err := os.Stat(f.u.markerPath()); !os.IsNotExist(err) {
		t.Fatal("marker survives a rollback")
	}
	if act := f.u.OnStart(); act != NoUpdate {
		t.Fatalf("after rollback action = %v", act)
	}
}

func TestRollbackWhenWindowElapsedBeforeStart(t *testing.T) {
	f := newFixture(t)
	if _, err := f.u.Apply(context.Background(), f.req()); err != nil {
		t.Fatal(err)
	}
	f.clock = f.clock.Add(Window + 2*time.Minute) // the new binary hung without confirming
	if act := f.u.OnStart(); act != RolledBack {
		t.Fatalf("action = %v", act)
	}
	if f.exeContent() != "OLD-BINARY" {
		t.Fatal("not rolled back")
	}
}

func TestConfirmClearsProbation(t *testing.T) {
	f := newFixture(t)
	if _, err := f.u.Apply(context.Background(), f.req()); err != nil {
		t.Fatal(err)
	}
	if act := f.u.OnStart(); act != Probation {
		t.Fatalf("action = %v", act)
	}
	f.u.Confirm() // reached Welcome
	if _, err := os.Stat(f.u.markerPath()); !os.IsNotExist(err) {
		t.Fatal("marker kept after confirm")
	}
	if act := f.u.OnStart(); act != NoUpdate {
		t.Fatalf("action = %v", act)
	}
	if f.exeContent() != string(f.body) {
		t.Fatal("confirmed binary was rolled back")
	}
}

func TestWatchdogRollsBackAfterWindow(t *testing.T) {
	f := newFixture(t)
	if _, err := f.u.Apply(context.Background(), f.req()); err != nil {
		t.Fatal(err)
	}
	// Pretend nearly the whole window has elapsed so the watchdog fires quickly.
	f.clock = f.clock.Add(Window - 50*time.Millisecond)
	if act := f.u.OnStart(); act != Probation {
		t.Fatalf("action = %v", act)
	}
	deadline := time.Now().Add(2 * time.Second)
	for f.exeContent() != "OLD-BINARY" && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if f.exeContent() != "OLD-BINARY" {
		t.Fatal("watchdog did not roll back")
	}
}

func TestConcurrentUpdatesAreRefused(t *testing.T) {
	f := newFixture(t)
	f.u.applying = true
	if _, err := f.u.Apply(context.Background(), f.req()); !errors.Is(err, ErrBusy) {
		t.Fatalf("err = %v", err)
	}
}
