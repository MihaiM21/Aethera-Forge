// Package selfupdate implements AgentSelfUpdate (ADR 0002): HTTPS-only
// download, mandatory SHA-256, optional Ed25519 signature over the SHA-256
// digest, a smoke test of the new binary, an atomic swap that keeps the previous
// binary, and automatic rollback when the new binary does not reach Welcome
// within two minutes. Process exit is behind the Restarter interface.
package selfupdate

import (
	"context"
	"crypto/ed25519"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// Tunables.
const (
	// Window is how long the new binary has to confirm (reach Welcome).
	Window  = 2 * time.Minute
	slack   = time.Minute
	maxBoot = 3
	// DefaultMaxSize caps the download.
	DefaultMaxSize = 256 << 20
)

// Sentinel errors (mapped to protocol error codes by the caller).
var (
	ErrInsecureURL = errors.New("self-update URL must use https")
	ErrChecksum    = errors.New("downloaded binary does not match the expected SHA-256")
	ErrSignature   = errors.New("binary signature is missing or invalid")
	ErrBadBinary   = errors.New("downloaded file is not a usable aethera-agent binary")
	ErrBusy        = errors.New("another self-update is in progress")
)

// Restarter ends the process so the service manager starts the new binary.
type Restarter interface{ Restart() }

// Request is the validated content of an AgentSelfUpdate command.
type Request struct {
	URL       string
	SHA256    string // hex
	Version   string
	Signature []byte
}

// Result describes an applied update.
type Result struct {
	PreviousVersion string
	NewVersion      string
}

// Updater performs self-updates.
type Updater struct {
	Exe            string // the running binary; must be writable (under the state dir)
	StateDir       string
	CurrentVersion string
	PublicKey      ed25519.PublicKey // nil = signatures not required
	Client         *http.Client      // nil = https-only default client
	Restarter      Restarter
	// Probe runs the candidate binary's --version; nil = real exec.
	Probe        func(ctx context.Context, path string) (string, error)
	Now          func() time.Time
	MaxSize      int64
	RestartDelay time.Duration // delay between result and exit (default 2s)

	mu       sync.Mutex
	applying bool
	watchdog *time.Timer
}

type marker struct {
	Previous        string    `json:"previous"`
	PreviousVersion string    `json:"previous_version"`
	NewVersion      string    `json:"new_version"`
	AppliedAt       time.Time `json:"applied_at"`
	Boots           int       `json:"boots"`
}

func (u *Updater) now() time.Time {
	if u.Now != nil {
		return u.Now()
	}
	return time.Now()
}

func (u *Updater) markerPath() string { return filepath.Join(u.StateDir, "state", "selfupdate.json") }

func (u *Updater) client() *http.Client {
	if u.Client != nil {
		return u.Client
	}
	return &http.Client{
		Timeout: 15 * time.Minute,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if req.URL.Scheme != "https" {
				return ErrInsecureURL
			}
			if len(via) >= 5 {
				return errors.New("too many redirects")
			}
			return nil
		},
	}
}

// Validate checks a request without side effects.
func Validate(r Request) error {
	uu, err := url.Parse(r.URL)
	if err != nil || uu.Scheme != "https" || uu.Host == "" || uu.User != nil {
		return ErrInsecureURL
	}
	if b, err := hex.DecodeString(r.SHA256); err != nil || len(b) != sha256.Size {
		return fmt.Errorf("sha256 must be 64 hex characters")
	}
	return nil
}

// Apply downloads, verifies and swaps in the new binary, then schedules the
// restart. It returns before the process exits so the result can be sent.
func (u *Updater) Apply(ctx context.Context, r Request) (*Result, error) {
	if err := Validate(r); err != nil {
		return nil, err
	}
	if u.PublicKey != nil && len(r.Signature) == 0 {
		return nil, fmt.Errorf("%w: a release key is configured but the command carries no signature", ErrSignature)
	}
	u.mu.Lock()
	if u.applying {
		u.mu.Unlock()
		return nil, ErrBusy
	}
	u.applying = true
	u.mu.Unlock()
	release := func() { u.mu.Lock(); u.applying = false; u.mu.Unlock() }

	tmp := u.Exe + ".new"
	digest, err := u.download(ctx, r.URL, tmp)
	if err != nil {
		_ = os.Remove(tmp)
		release()
		return nil, err
	}
	want, _ := hex.DecodeString(r.SHA256)
	if subtle.ConstantTimeCompare(digest, want) != 1 {
		_ = os.Remove(tmp)
		release()
		return nil, ErrChecksum
	}
	if u.PublicKey != nil && !ed25519.Verify(u.PublicKey, digest, r.Signature) {
		_ = os.Remove(tmp)
		release()
		return nil, ErrSignature
	}
	if err := os.Chmod(tmp, 0o755); err != nil {
		_ = os.Remove(tmp)
		release()
		return nil, err
	}
	out, err := u.probe(ctx, tmp)
	if err != nil || !strings.HasPrefix(strings.TrimSpace(out), "aethera-agent") {
		_ = os.Remove(tmp)
		release()
		return nil, ErrBadBinary
	}
	if err := u.swap(tmp, r.Version); err != nil {
		_ = os.Remove(tmp)
		release()
		return nil, err
	}
	delay := u.RestartDelay
	if delay <= 0 {
		delay = 2 * time.Second
	}
	if u.Restarter != nil {
		time.AfterFunc(delay, u.Restarter.Restart)
	}
	return &Result{PreviousVersion: u.CurrentVersion, NewVersion: r.Version}, nil
}

func (u *Updater) download(ctx context.Context, rawURL, dst string) ([]byte, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return nil, err
	}
	resp, err := u.client().Do(req)
	if err != nil {
		return nil, fmt.Errorf("download: %w", err)
	}
	defer resp.Body.Close()
	if resp.Request != nil && resp.Request.URL.Scheme != "https" {
		return nil, ErrInsecureURL
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("download: unexpected HTTP status %d", resp.StatusCode)
	}
	max := u.MaxSize
	if max <= 0 {
		max = DefaultMaxSize
	}
	f, err := os.OpenFile(dst, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o700)
	if err != nil {
		return nil, err
	}
	h := sha256.New()
	n, err := io.Copy(io.MultiWriter(f, h), io.LimitReader(resp.Body, max+1))
	if cerr := f.Close(); err == nil {
		err = cerr
	}
	if err != nil {
		return nil, fmt.Errorf("download: %w", err)
	}
	if n > max {
		return nil, fmt.Errorf("download exceeds the %d byte limit", max)
	}
	return h.Sum(nil), nil
}

func (u *Updater) probe(ctx context.Context, path string) (string, error) {
	if u.Probe != nil {
		return u.Probe(ctx, path)
	}
	pctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	// Fixed argv: the only argument is the literal "--version".
	out, err := exec.CommandContext(pctx, path, "--version").Output()
	return string(out), err
}

// swap keeps the current binary as Exe.prev and atomically renames tmp over Exe.
func (u *Updater) swap(tmp, newVersion string) error {
	prev := u.Exe + ".prev"
	_ = os.Remove(prev)
	if err := os.Link(u.Exe, prev); err != nil {
		if cerr := copyFile(u.Exe, prev); cerr != nil {
			return fmt.Errorf("keep previous binary: %w", cerr)
		}
	}
	m := marker{Previous: prev, PreviousVersion: u.CurrentVersion, NewVersion: newVersion, AppliedAt: u.now()}
	if err := u.saveMarker(&m); err != nil {
		return err
	}
	if err := os.Rename(tmp, u.Exe); err != nil {
		_ = os.Remove(u.markerPath())
		return fmt.Errorf("swap binary: %w", err)
	}
	return nil
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(dst, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o755)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

func (u *Updater) saveMarker(m *marker) error {
	b, err := json.Marshal(m)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(u.markerPath()), 0o700); err != nil {
		return err
	}
	tmp := u.markerPath() + ".tmp"
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, u.markerPath())
}

func (u *Updater) loadMarker() (*marker, error) {
	b, err := os.ReadFile(u.markerPath())
	if err != nil {
		return nil, err
	}
	var m marker
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, err
	}
	return &m, nil
}

// Action is what OnStart decided.
type Action int

const (
	// NoUpdate: no update pending.
	NoUpdate Action = iota
	// Probation: a new binary is on trial; call Confirm after Welcome.
	Probation
	// RolledBack: the previous binary was restored and a restart requested.
	RolledBack
)

// OnStart must run at process start. It starts the rollback watchdog while a
// freshly installed binary is on probation, and rolls back immediately when
// that binary already failed to confirm.
func (u *Updater) OnStart() Action {
	m, err := u.loadMarker()
	if err != nil {
		return NoUpdate
	}
	elapsed := u.now().Sub(m.AppliedAt)
	m.Boots++
	if elapsed > Window+slack || m.Boots > maxBoot {
		u.Rollback()
		return RolledBack
	}
	_ = u.saveMarker(m)
	u.mu.Lock()
	u.watchdog = time.AfterFunc(Window-elapsed, func() {
		if _, err := u.loadMarker(); err == nil {
			u.Rollback()
		}
	})
	u.mu.Unlock()
	return Probation
}

// Confirm marks the new binary healthy (the agent reached Welcome).
func (u *Updater) Confirm() {
	u.mu.Lock()
	if u.watchdog != nil {
		u.watchdog.Stop()
		u.watchdog = nil
	}
	u.mu.Unlock()
	_ = os.Remove(u.markerPath())
}

// Rollback restores the previous binary and requests a restart.
func (u *Updater) Rollback() {
	m, err := u.loadMarker()
	if err != nil {
		return
	}
	if err := os.Rename(m.Previous, u.Exe); err == nil {
		_ = os.Remove(u.markerPath())
		if u.Restarter != nil {
			u.Restarter.Restart()
		}
	}
}
