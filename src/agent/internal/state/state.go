// Package state defines the on-disk layout under the state directory
// (default /var/lib/aethera) and atomic file helpers.
package state

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

// Dir is the agent state directory.
type Dir string

func (d Dir) KeyPath() string      { return filepath.Join(string(d), "agent.key") }
func (d Dir) CertPath() string     { return filepath.Join(string(d), "agent.crt") }
func (d Dir) CAPath() string       { return filepath.Join(string(d), "ca.crt") }
func (d Dir) IdentityPath() string { return filepath.Join(string(d), "enrollment.json") }
func (d Dir) StatePath() string    { return filepath.Join(string(d), "state") }
func (d Dir) ProjectsPath() string { return filepath.Join(string(d), "projects") }
func (d Dir) BinPath() string      { return filepath.Join(string(d), "bin") }

// Identity is what enrollment leaves behind besides the key and certificates.
type Identity struct {
	ServerID      string        `json:"server_id"`
	Endpoint      string        `json:"endpoint"`
	CAFingerprint string        `json:"ca_sha256"`
	NotAfter      time.Time     `json:"not_after"`
	RenewBefore   time.Duration `json:"renew_before"`
}

// ErrNotEnrolled means no identity is stored yet.
var ErrNotEnrolled = errors.New("agent is not enrolled: run 'aethera-agent enroll' first")

// LoadIdentity reads enrollment.json.
func (d Dir) LoadIdentity() (*Identity, error) {
	data, err := os.ReadFile(d.IdentityPath())
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, ErrNotEnrolled
		}
		return nil, err
	}
	var id Identity
	if err := json.Unmarshal(data, &id); err != nil {
		return nil, fmt.Errorf("parse %s: %w", d.IdentityPath(), err)
	}
	if id.ServerID == "" || id.Endpoint == "" {
		return nil, fmt.Errorf("%s is incomplete", d.IdentityPath())
	}
	return &id, nil
}

// SaveIdentity writes enrollment.json atomically.
func (d Dir) SaveIdentity(id *Identity) error {
	data, err := json.MarshalIndent(id, "", "  ")
	if err != nil {
		return err
	}
	return WriteFileAtomic(d.IdentityPath(), data, 0o600)
}

// Ensure creates the directory tree with restrictive permissions.
func (d Dir) Ensure() error {
	for _, p := range []string{string(d), d.StatePath(), d.ProjectsPath()} {
		if err := os.MkdirAll(p, 0o700); err != nil {
			return err
		}
	}
	return nil
}

// WriteFileAtomic writes data to a temp file in the same directory and renames
// it over path, so readers never see a partial file.
func WriteFileAtomic(path string, data []byte, perm os.FileMode) error {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, "."+filepath.Base(path)+".tmp*")
	if err != nil {
		return err
	}
	name := tmp.Name()
	cleanup := func() { _ = os.Remove(name) }
	if err := tmp.Chmod(perm); err != nil && !isChmodUnsupported(err) {
		tmp.Close()
		cleanup()
		return err
	}
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		cleanup()
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		cleanup()
		return err
	}
	if err := tmp.Close(); err != nil {
		cleanup()
		return err
	}
	if err := os.Rename(name, path); err != nil {
		cleanup()
		return err
	}
	return nil
}

func isChmodUnsupported(err error) bool { return errors.Is(err, errors.ErrUnsupported) }
