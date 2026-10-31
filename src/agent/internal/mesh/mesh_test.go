package mesh

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// fakeBackend records what the manager asks of the operating system.
type fakeBackend struct {
	probeErr error
	applyErr error
	applied  []LinkConfig
	state    *LinkState
	removed  []string
}

func (f *fakeBackend) Probe() error { return f.probeErr }
func (f *fakeBackend) Apply(c LinkConfig) error {
	if f.applyErr != nil {
		return f.applyErr
	}
	f.applied = append(f.applied, c)
	st := &LinkState{Up: true, Addresses: []netip.Prefix{c.Address}, ListenPort: c.ListenPort}
	for _, p := range c.Peers {
		st.Peers = append(st.Peers, PeerState{PublicKey: p.PublicKey, Endpoint: p.Endpoint})
	}
	f.state = st
	return nil
}
func (f *fakeBackend) Read(string) (*LinkState, error) {
	if f.state == nil {
		return nil, ErrNoLink
	}
	return f.state, nil
}
func (f *fakeBackend) Remove(name string) error {
	f.removed = append(f.removed, name)
	f.state = nil
	return nil
}

func newKey(t *testing.T) wgtypes.Key {
	t.Helper()
	k, err := wgtypes.GeneratePrivateKey()
	if err != nil {
		t.Fatal(err)
	}
	return k.PublicKey()
}

func newManager(t *testing.T) (*Manager, *fakeBackend) {
	t.Helper()
	fb := &fakeBackend{}
	return &Manager{
		KeyPath: filepath.Join(t.TempDir(), "mesh.key"), Backend: fb,
		ResolveEndpoint: func(s string) (*net.UDPAddr, error) { return net.ResolveUDPAddr("udp", "203.0.113.5:51820") },
	}, fb
}

func applyReq(t *testing.T, peers ...*agentv1.MeshPeer) *agentv1.MeshApply {
	t.Helper()
	return &agentv1.MeshApply{
		Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD, InterfaceName: "aethera0",
		Address: "10.210.0.3/16", Cidr: "10.210.0.0/16", ListenPort: 51820, Peers: peers,
	}
}

func peer(t *testing.T, ip string) *agentv1.MeshPeer {
	t.Helper()
	return &agentv1.MeshPeer{
		PublicKey: newKey(t).String(), Endpoint: "198.51.100.7:51820", AllowedIps: []string{ip}, PersistentKeepaliveSeconds: 25, Name: "srv",
	}
}

func code(err error) agentv1.ErrorCode {
	var me *Error
	if errors.As(err, &me) {
		return me.Code
	}
	return agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED
}

func TestParseApplyAcceptsAValidMesh(t *testing.T) {
	p, err := ParseApply(applyReq(t, peer(t, "10.210.0.4/32"), peer(t, "10.210.0.5/32")), WireGuardInterface)
	if err != nil {
		t.Fatal(err)
	}
	if p.MTU != DefaultMTU || p.ListenPort != 51820 || len(p.Peers) != 2 || p.Peers[0].Keepalive != 25*time.Second {
		t.Fatalf("plan %+v", p)
	}
}

func TestParseApplyRefusals(t *testing.T) {
	good := func() *agentv1.MeshApply { return applyReq(t, peer(t, "10.210.0.4/32")) }
	cases := []struct {
		name string
		mut  func(r *agentv1.MeshApply)
		code agentv1.ErrorCode
		want string
	}{
		{"other interface", func(r *agentv1.MeshApply) { r.InterfaceName = "wg0" }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "not allowed"},
		{"public range", func(r *agentv1.MeshApply) { r.Cidr, r.Address = "8.8.0.0/16", "8.8.0.3/16" }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "private"},
		{"default route", func(r *agentv1.MeshApply) { r.Cidr, r.Address = "0.0.0.0/0", "10.0.0.3/0" }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "private"},
		{"too wide", func(r *agentv1.MeshApply) { r.Cidr, r.Address = "10.0.0.0/8", "10.0.0.3/8" }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "prefix"},
		{"too narrow", func(r *agentv1.MeshApply) { r.Cidr, r.Address = "10.210.0.0/28", "10.210.0.3/28" }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "prefix"},
		{"host bits", func(r *agentv1.MeshApply) { r.Cidr = "10.210.1.0/16" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "host bits"},
		{"ipv6", func(r *agentv1.MeshApply) { r.Cidr, r.Address = "fd00::/16", "fd00::3/16" }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "IPv4"},
		{"address outside", func(r *agentv1.MeshApply) { r.Address = "10.211.0.3/16" }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "inside"},
		{"address wrong prefix", func(r *agentv1.MeshApply) { r.Address = "10.210.0.3/24" }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "inside"},
		{"network address", func(r *agentv1.MeshApply) { r.Address = "10.210.0.0/16" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "network or broadcast"},
		{"broadcast address", func(r *agentv1.MeshApply) { r.Address = "10.210.255.255/16" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "network or broadcast"},
		{"port zero", func(r *agentv1.MeshApply) { r.ListenPort = 0 }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "listen port"},
		{"port too big", func(r *agentv1.MeshApply) { r.ListenPort = 70000 }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "listen port"},
		{"mtu", func(r *agentv1.MeshApply) { r.Mtu = 100 }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "mtu"},
		{"wide allowed ip", func(r *agentv1.MeshApply) { r.Peers[0].AllowedIps = []string{"10.210.0.0/24"} }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "/32"},
		{"default allowed ip", func(r *agentv1.MeshApply) { r.Peers[0].AllowedIps = []string{"0.0.0.0/0"} }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "/32"},
		{"allowed ip outside", func(r *agentv1.MeshApply) { r.Peers[0].AllowedIps = []string{"192.168.1.1/32"} }, agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "outside"},
		{"allowed ip is self", func(r *agentv1.MeshApply) { r.Peers[0].AllowedIps = []string{"10.210.0.3/32"} }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "usable"},
		{"no allowed ip", func(r *agentv1.MeshApply) { r.Peers[0].AllowedIps = nil }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "allowed IP"},
		{"bad key", func(r *agentv1.MeshApply) { r.Peers[0].PublicKey = "not-a-key" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "public key"},
		{"short key", func(r *agentv1.MeshApply) { r.Peers[0].PublicKey = "AAAA" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "public key"},
		{"endpoint no port", func(r *agentv1.MeshApply) { r.Peers[0].Endpoint = "198.51.100.7" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "host:port"},
		{"endpoint port", func(r *agentv1.MeshApply) { r.Peers[0].Endpoint = "198.51.100.7:0" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "port"},
		{"endpoint host", func(r *agentv1.MeshApply) { r.Peers[0].Endpoint = "bad host!:51820" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "not valid"},
		{"endpoint unspecified", func(r *agentv1.MeshApply) { r.Peers[0].Endpoint = "0.0.0.0:51820" }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "dialled"},
		{"keepalive", func(r *agentv1.MeshApply) { r.Peers[0].PersistentKeepaliveSeconds = 100000 }, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "keepalive"},
		{"duplicate key", func(r *agentv1.MeshApply) {
			r.Peers = append(r.Peers, &agentv1.MeshPeer{PublicKey: r.Peers[0].PublicKey, AllowedIps: []string{"10.210.0.9/32"}})
		}, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "twice"},
		{"duplicate allowed ip", func(r *agentv1.MeshApply) {
			r.Peers = append(r.Peers, &agentv1.MeshPeer{PublicKey: newKey(t).String(), AllowedIps: []string{"10.210.0.4/32"}})
		}, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "another peer"},
		{"too many peers", func(r *agentv1.MeshApply) {
			for i := 0; i <= MaxPeers; i++ {
				r.Peers = append(r.Peers, &agentv1.MeshPeer{})
			}
		}, agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, "at most"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := good()
			c.mut(r)
			_, err := ParseApply(r, WireGuardInterface)
			if err == nil {
				t.Fatal("expected a refusal")
			}
			if code(err) != c.code || !strings.Contains(err.Error(), c.want) {
				t.Fatalf("code=%v err=%q, want %v containing %q", code(err), err, c.code, c.want)
			}
		})
	}
}

func TestParseApplyIgnoresNothingInNames(t *testing.T) {
	r := applyReq(t, peer(t, "10.210.0.4/32"))
	r.InterfaceName = "wg0\nINJECT"
	_, err := ParseApply(r, WireGuardInterface)
	if err == nil || strings.ContainsAny(err.Error(), "\n") {
		t.Fatalf("message must be sanitized: %q", err)
	}
}

func TestApplyCreatesTheKeyOnceAndReturnsOnlyThePublicKey(t *testing.T) {
	m, fb := newManager(t)
	st, err := m.Apply(context.Background(), applyReq(t, peer(t, "10.210.0.4/32")))
	if err != nil {
		t.Fatal(err)
	}
	if !st.Up || st.PublicKey == "" || len(st.Addresses) != 1 || st.Addresses[0] != "10.210.0.3/16" || len(st.Peers) != 1 {
		t.Fatalf("state %+v", st)
	}
	data, err := os.ReadFile(m.KeyPath)
	if err != nil {
		t.Fatal(err)
	}
	priv := strings.TrimSpace(string(data))
	if priv == st.PublicKey {
		t.Fatal("the stored file holds the public key")
	}
	if runtime.GOOS != "windows" {
		if fi, _ := os.Stat(m.KeyPath); fi.Mode().Perm() != 0o600 {
			t.Fatalf("mode %v", fi.Mode().Perm())
		}
	}
	if strings.Contains(st.String(), priv) {
		t.Fatal("the private key leaked into the result")
	}
	if fb.applied[0].PrivateKey.String() != priv {
		t.Fatal("the backend did not get the stored key")
	}

	// A second apply keeps the identity.
	st2, err := m.Apply(context.Background(), applyReq(t))
	if err != nil || st2.PublicKey != st.PublicKey {
		t.Fatalf("identity changed: %v %v", err, st2)
	}
	if len(st2.Peers) != 0 {
		t.Fatalf("peers must be replaced, got %d", len(st2.Peers))
	}
}

func TestApplyRefusesAPeerWithItsOwnKey(t *testing.T) {
	m, _ := newManager(t)
	st, err := m.Apply(context.Background(), applyReq(t))
	if err != nil {
		t.Fatal(err)
	}
	p := peer(t, "10.210.0.4/32")
	p.PublicKey = st.PublicKey
	if _, err := m.Apply(context.Background(), applyReq(t, p)); err == nil || code(err) != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
		t.Fatalf("err %v", err)
	}
}

func TestApplyOrdersPeersAndResolvesEndpoints(t *testing.T) {
	m, fb := newManager(t)
	var asked []string
	m.ResolveEndpoint = func(s string) (*net.UDPAddr, error) {
		asked = append(asked, s)
		return net.ResolveUDPAddr("udp", "203.0.113.9:4242")
	}
	noEndpoint := peer(t, "10.210.0.5/32")
	noEndpoint.Endpoint = ""
	if _, err := m.Apply(context.Background(), applyReq(t, peer(t, "10.210.0.4/32"), noEndpoint)); err != nil {
		t.Fatal(err)
	}
	cfg := fb.applied[0]
	if len(asked) != 1 || len(cfg.Peers) != 2 {
		t.Fatalf("asked %v peers %d", asked, len(cfg.Peers))
	}
	if cfg.Peers[0].PublicKey.String() > cfg.Peers[1].PublicKey.String() {
		t.Fatal("peers must be in a stable order")
	}
	withEp := 0
	for _, p := range cfg.Peers {
		if p.Endpoint != nil {
			withEp++
			if p.Endpoint.Port != 4242 {
				t.Fatalf("endpoint %v", p.Endpoint)
			}
		}
	}
	if withEp != 1 {
		t.Fatalf("%d peers with an endpoint", withEp)
	}
}

func TestApplyReportsAnUnresolvableEndpoint(t *testing.T) {
	m, _ := newManager(t)
	m.ResolveEndpoint = func(string) (*net.UDPAddr, error) { return nil, errors.New("no such host") }
	_, err := m.Apply(context.Background(), applyReq(t, peer(t, "10.210.0.4/32")))
	if err == nil || !strings.Contains(err.Error(), "resolve") || code(err) != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
		t.Fatalf("err %v", err)
	}
}

func TestApplyWithoutKernelSupportIsUnsupported(t *testing.T) {
	m, fb := newManager(t)
	fb.probeErr = errors.New("operation not permitted")
	if caps := m.Capabilities(); contains(caps, CapWireGuard) {
		t.Fatalf("capabilities %v", caps)
	}
	_, err := m.Apply(context.Background(), applyReq(t))
	if code(err) != agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED || !strings.Contains(err.Error(), "operation not permitted") {
		t.Fatalf("err %v", err)
	}
	if _, serr := os.Stat(m.KeyPath); serr == nil {
		t.Fatal("no key must be generated on a host that cannot run the mesh")
	}
}

func TestBackendFailureIsReportedWithoutSecrets(t *testing.T) {
	m, fb := newManager(t)
	fb.applyErr = errors.New("set the address of aethera0: file exists")
	_, err := m.Apply(context.Background(), applyReq(t))
	if code(err) != agentv1.ErrorCode_ERROR_CODE_INTERNAL || !strings.Contains(err.Error(), "file exists") {
		t.Fatalf("err %v", err)
	}
}

func TestCapabilitiesAdvertiseWhatTheHostCanDo(t *testing.T) {
	m, _ := newManager(t)
	caps := m.Capabilities()
	if !contains(caps, CapWireGuard) {
		t.Fatalf("capabilities %v", caps)
	}
	if contains(caps, CapTailscale) != (runtime.GOOS == "linux") {
		t.Fatalf("tailscale capability %v on %s", caps, runtime.GOOS)
	}
}

func TestStatusIsReadOnly(t *testing.T) {
	m, fb := newManager(t)
	req := &agentv1.MeshStatus{Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD, InterfaceName: "aethera0"}
	st, err := m.Status(context.Background(), req)
	if err != nil {
		t.Fatal(err)
	}
	if st.Up || st.Error == "" || st.PublicKey != "" {
		t.Fatalf("before the first apply: %+v", st)
	}
	if _, serr := os.Stat(m.KeyPath); serr == nil {
		t.Fatal("status must not generate a key")
	}
	if _, err := m.Apply(context.Background(), applyReq(t, peer(t, "10.210.0.4/32"))); err != nil {
		t.Fatal(err)
	}
	fb.state.Peers[0].LastHandshake = time.Now().Add(-30 * time.Second)
	fb.state.Peers[0].RxBytes, fb.state.Peers[0].TxBytes = 10, 20
	st, err = m.Status(context.Background(), req)
	if err != nil || !st.Up || st.PublicKey == "" || len(st.Peers) != 1 {
		t.Fatalf("%v %+v", err, st)
	}
	if st.Peers[0].LastHandshake == nil || st.Peers[0].RxBytes != 10 || st.Peers[0].TxBytes != 20 {
		t.Fatalf("peer %+v", st.Peers[0])
	}
	if len(fb.applied) != 1 {
		t.Fatal("status changed the interface")
	}
	req.InterfaceName = "eth0"
	if _, err := m.Status(context.Background(), req); code(err) != agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION {
		t.Fatalf("err %v", err)
	}
}

func TestRemoveKeepsTheKey(t *testing.T) {
	m, fb := newManager(t)
	st, err := m.Apply(context.Background(), applyReq(t))
	if err != nil {
		t.Fatal(err)
	}
	if err := m.Remove(context.Background(), &agentv1.MeshRemove{InterfaceName: "aethera0"}); err != nil {
		t.Fatal(err)
	}
	if len(fb.removed) != 1 || fb.removed[0] != "aethera0" {
		t.Fatalf("removed %v", fb.removed)
	}
	again, err := m.Apply(context.Background(), applyReq(t))
	if err != nil || again.PublicKey != st.PublicKey {
		t.Fatalf("the identity must survive a removal: %v", err)
	}
	if err := m.Remove(context.Background(), &agentv1.MeshRemove{InterfaceName: "docker0"}); code(err) != agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION {
		t.Fatalf("err %v", err)
	}
}

func TestTailscaleOnlyReadsTheAddress(t *testing.T) {
	m, fb := newManager(t)
	m.TailscaleAddr = func() (netip.Addr, bool, error) { return netip.MustParseAddr("100.101.102.103"), true, nil }
	st, err := m.Apply(context.Background(), &agentv1.MeshApply{Provider: agentv1.MeshProvider_MESH_PROVIDER_TAILSCALE})
	if err != nil {
		t.Fatal(err)
	}
	if !st.Up || len(st.Addresses) != 1 || st.Addresses[0] != "100.101.102.103/32" || st.PublicKey != "" || st.InterfaceName != "tailscale0" {
		t.Fatalf("state %+v", st)
	}
	if len(fb.applied) != 0 {
		t.Fatal("tailscale mode must not create an interface")
	}
	if _, serr := os.Stat(m.KeyPath); serr == nil {
		t.Fatal("tailscale mode must not create a key")
	}

	m.TailscaleAddr = func() (netip.Addr, bool, error) {
		return netip.Addr{}, false, errors.New("the interface tailscale0 was not found")
	}
	if _, err := m.Apply(context.Background(), &agentv1.MeshApply{Provider: agentv1.MeshProvider_MESH_PROVIDER_TAILSCALE}); code(err) != agentv1.ErrorCode_ERROR_CODE_NOT_FOUND {
		t.Fatalf("err %v", err)
	}
	// Status reports instead of failing.
	st, err = m.Status(context.Background(), &agentv1.MeshStatus{Provider: agentv1.MeshProvider_MESH_PROVIDER_TAILSCALE})
	if err != nil || st.Up || st.Error == "" {
		t.Fatalf("%v %+v", err, st)
	}
}

func TestKeyFileIsNeverOverwritten(t *testing.T) {
	path := filepath.Join(t.TempDir(), "mesh.key")
	if err := os.WriteFile(path, []byte("garbage"), 0o600); err != nil {
		t.Fatal(err)
	}
	_, err := LoadOrCreateKey(path)
	if err == nil || strings.Contains(err.Error(), "garbage") {
		t.Fatalf("err %v", err)
	}
	if data, _ := os.ReadFile(path); string(data) != "garbage" {
		t.Fatal("a malformed key file was overwritten")
	}
}

func TestPublicKeyOfMissingFile(t *testing.T) {
	_, ok, err := PublicKey(filepath.Join(t.TempDir(), "none"))
	if err != nil || ok {
		t.Fatalf("ok=%v err=%v", ok, err)
	}
}

func contains(ss []string, s string) bool {
	for _, x := range ss {
		if x == s {
			return true
		}
	}
	return false
}
