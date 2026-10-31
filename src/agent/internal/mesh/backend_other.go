//go:build !linux

package mesh

import (
	"errors"
	"runtime"
)

// otherBackend is the stub of hosts without the Linux kernel WireGuard module: the mesh is advertised nowhere.
type otherBackend struct{}

// NewBackend returns the stub backend.
func NewBackend() Backend { return otherBackend{} }

var errNotLinux = errors.New("the private mesh needs Linux (this agent runs on " + runtime.GOOS + ")")

func (otherBackend) Probe() error                    { return errNotLinux }
func (otherBackend) Apply(LinkConfig) error          { return errNotLinux }
func (otherBackend) Read(string) (*LinkState, error) { return nil, errNotLinux }
func (otherBackend) Remove(string) error             { return nil }
