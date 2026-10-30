// Package mesh is the agent's side of the private network between servers (ADR 0012). With WireGuard it owns
// one interface, aethera0: it keeps a key pair in the state directory (the private key never leaves it), and
// brings the interface to the exact configuration the control plane sends (addresses, peers). With Tailscale it
// only reads the address of tailscale0. It touches nothing else on the host: no other interface, no firewall
// rule, no route outside the mesh range.
package mesh

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/netip"
	"runtime"
	"sort"
	"sync"

	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// Capability strings advertised in Hello.
const (
	CapWireGuard = "mesh.wireguard"
	CapTailscale = "mesh.tailscale"
)

// Manager serves MeshApply, MeshStatus and MeshRemove.
type Manager struct {
	// KeyPath is the file the private key lives in (state.Dir.MeshKeyPath).
	KeyPath string
	// Backend is the operating system side; NewBackend() when nil.
	Backend Backend
	// InterfaceName is the WireGuard interface (WireGuardInterface when empty; tests use others).
	InterfaceName string
	// ResolveEndpoint turns "host:port" into an address (net.ResolveUDPAddr when nil).
	ResolveEndpoint func(string) (*net.UDPAddr, error)
	// TailscaleAddr returns the IPv4 address of tailscale0 and whether the interface is up (the real one when nil).
	TailscaleAddr func() (addr netip.Addr, up bool, err error)
	Logger        *slog.Logger

	mu        sync.Mutex // serialises changes of the interface
	probeOnce sync.Once
	probeErr  error
}

func (m *Manager) backend() Backend {
	if m.Backend == nil {
		m.Backend = NewBackend()
	}
	return m.Backend
}

func (m *Manager) ifname() string {
	if m.InterfaceName != "" {
		return m.InterfaceName
	}
	return WireGuardInterface
}

func (m *Manager) log() *slog.Logger {
	if m.Logger != nil {
		return m.Logger
	}
	return slog.Default()
}

// WireGuardUnavailable explains why this host cannot run WireGuard interfaces (nil = it can). Probed once.
func (m *Manager) WireGuardUnavailable() error {
	m.probeOnce.Do(func() { m.probeErr = m.backend().Probe() })
	return m.probeErr
}

// Capabilities lists what this host can do: mesh.wireguard only when the kernel lets the agent create a
// WireGuard link, mesh.tailscale on Linux (the agent only reads an interface).
func (m *Manager) Capabilities() []string {
	var caps []string
	if m.WireGuardUnavailable() == nil {
		caps = append(caps, CapWireGuard)
	}
	if runtime.GOOS == "linux" {
		caps = append(caps, CapTailscale)
	}
	return caps
}

// Apply brings the mesh to the configuration in req.
func (m *Manager) Apply(_ context.Context, req *agentv1.MeshApply) (*agentv1.MeshState, error) {
	switch req.GetProvider() {
	case agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD:
		return m.applyWireGuard(req)
	case agentv1.MeshProvider_MESH_PROVIDER_TAILSCALE:
		st := m.tailscaleState()
		if st.Error != "" {
			return nil, &Error{Code: agentv1.ErrorCode_ERROR_CODE_NOT_FOUND, Msg: st.Error}
		}
		return st, nil
	}
	return nil, invalid("provider is required")
}

// Status reads the interface without changing it. A problem is reported in MeshState.Error, not as a failure.
func (m *Manager) Status(_ context.Context, req *agentv1.MeshStatus) (*agentv1.MeshState, error) {
	switch req.GetProvider() {
	case agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD:
		if got := req.GetInterfaceName(); got != "" && got != m.ifname() {
			return nil, violation("interface %q is not allowed: the agent manages %q only", sanitize(got), m.ifname())
		}
		return m.wireGuardState(), nil
	case agentv1.MeshProvider_MESH_PROVIDER_TAILSCALE:
		return m.tailscaleState(), nil
	}
	return nil, invalid("provider is required")
}

// Remove deletes the WireGuard interface and keeps the key pair. A no-op when it is gone.
func (m *Manager) Remove(_ context.Context, req *agentv1.MeshRemove) error {
	if got := req.GetInterfaceName(); got != "" && got != m.ifname() {
		return violation("interface %q is not allowed: the agent manages %q only", sanitize(got), m.ifname())
	}
	if m.WireGuardUnavailable() != nil {
		return nil // nothing can exist
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.backend().Remove(m.ifname()); err != nil {
		return &Error{Code: agentv1.ErrorCode_ERROR_CODE_INTERNAL, Msg: "remove the mesh interface: " + err.Error()}
	}
	m.log().Info("mesh interface removed", "interface", m.ifname())
	return nil
}

func (m *Manager) applyWireGuard(req *agentv1.MeshApply) (*agentv1.MeshState, error) {
	plan, err := ParseApply(req, m.ifname())
	if err != nil {
		return nil, err
	}
	if perr := m.WireGuardUnavailable(); perr != nil {
		return nil, &Error{Code: agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED, Msg: "WireGuard is not available on this host: " + perr.Error()}
	}

	m.mu.Lock()
	defer m.mu.Unlock()
	key, err := LoadOrCreateKey(m.KeyPath)
	if err != nil {
		return nil, &Error{Code: agentv1.ErrorCode_ERROR_CODE_INTERNAL, Msg: err.Error()}
	}
	self := key.PublicKey()

	cfg := LinkConfig{
		Name: plan.Name, MTU: plan.MTU, Address: plan.Address, CIDR: plan.CIDR,
		PrivateKey: key, ListenPort: plan.ListenPort,
	}
	for _, p := range plan.Peers {
		if p.PublicKey == self {
			return nil, invalid("peer %s has this server's own public key", p.label())
		}
		pc := PeerConfig{PublicKey: p.PublicKey, AllowedIPs: p.AllowedIPs, Keepalive: p.Keepalive}
		if p.Endpoint != "" {
			ep, err := m.resolve(p.Endpoint)
			if err != nil {
				return nil, invalid("peer %s: cannot resolve the endpoint: %v", p.label(), err)
			}
			pc.Endpoint = ep
		}
		cfg.Peers = append(cfg.Peers, pc)
	}
	// A stable order keeps repeated applies identical.
	sort.Slice(cfg.Peers, func(i, j int) bool { return cfg.Peers[i].PublicKey.String() < cfg.Peers[j].PublicKey.String() })

	if err := m.backend().Apply(cfg); err != nil {
		return nil, &Error{Code: agentv1.ErrorCode_ERROR_CODE_INTERNAL, Msg: "configure the mesh interface: " + err.Error()}
	}
	m.log().Info("mesh interface applied", "interface", plan.Name, "address", plan.Address.String(), "peers", len(cfg.Peers))
	return m.wireGuardState(), nil
}

func (p PeerPlan) label() string {
	if p.Name != "" {
		return p.Name
	}
	return p.PublicKey.String()[:8]
}

func (m *Manager) resolve(endpoint string) (*net.UDPAddr, error) {
	if m.ResolveEndpoint != nil {
		return m.ResolveEndpoint(endpoint)
	}
	return net.ResolveUDPAddr("udp", endpoint)
}

// wireGuardState reads the interface and the stored key into a MeshState.
func (m *Manager) wireGuardState() *agentv1.MeshState {
	st := &agentv1.MeshState{Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD, InterfaceName: m.ifname()}
	if pub, ok, err := PublicKey(m.KeyPath); err == nil && ok {
		st.PublicKey = pub.String()
	} else if err != nil {
		st.Error = err.Error()
		return st
	}
	if err := m.WireGuardUnavailable(); err != nil {
		st.Error = "WireGuard is not available on this host: " + err.Error()
		return st
	}
	ls, err := m.backend().Read(m.ifname())
	if err != nil {
		if errors.Is(err, ErrNoLink) {
			st.Error = "the mesh interface does not exist yet"
		} else {
			st.Error = "read the mesh interface: " + err.Error()
		}
		return st
	}
	st.Up = ls.Up
	st.ListenPort = uint32(ls.ListenPort)
	for _, a := range ls.Addresses {
		st.Addresses = append(st.Addresses, a.String())
	}
	for _, p := range ls.Peers {
		ps := &agentv1.MeshPeerState{PublicKey: p.PublicKey.String(), RxBytes: p.RxBytes, TxBytes: p.TxBytes}
		if p.Endpoint != nil {
			ps.Endpoint = p.Endpoint.String()
		}
		if !p.LastHandshake.IsZero() {
			ps.LastHandshake = timestamppb.New(p.LastHandshake)
		}
		st.Peers = append(st.Peers, ps)
	}
	if !st.Up {
		st.Error = "the mesh interface is down"
	}
	return st
}

// tailscaleState reports the IPv4 address of tailscale0. The agent never logs in to Tailscale or changes it.
func (m *Manager) tailscaleState() *agentv1.MeshState {
	st := &agentv1.MeshState{Provider: agentv1.MeshProvider_MESH_PROVIDER_TAILSCALE, InterfaceName: TailscaleInterface}
	lookup := m.TailscaleAddr
	if lookup == nil {
		lookup = tailscaleAddr
	}
	addr, up, err := lookup()
	switch {
	case err != nil:
		st.Error = err.Error()
	case !addr.IsValid():
		st.Error = "tailscale0 has no IPv4 address"
	default:
		st.Up = up
		st.Addresses = []string{netip.PrefixFrom(addr, 32).String()}
		if !up {
			st.Error = "tailscale0 is down"
		}
	}
	return st
}

// tailscaleAddr reads the first IPv4 address of tailscale0.
func tailscaleAddr() (netip.Addr, bool, error) {
	ifc, err := net.InterfaceByName(TailscaleInterface)
	if err != nil {
		return netip.Addr{}, false, fmt.Errorf("the interface %s was not found: is Tailscale installed and running on this server?", TailscaleInterface)
	}
	addrs, err := ifc.Addrs()
	if err != nil {
		return netip.Addr{}, false, fmt.Errorf("read the addresses of %s: %w", TailscaleInterface, err)
	}
	for _, a := range addrs {
		if ipn, ok := a.(*net.IPNet); ok {
			if ip, ok := netip.AddrFromSlice(ipn.IP); ok && ip.Unmap().Is4() {
				return ip.Unmap(), ifc.Flags&net.FlagUp != 0, nil
			}
		}
	}
	return netip.Addr{}, ifc.Flags&net.FlagUp != 0, nil
}
