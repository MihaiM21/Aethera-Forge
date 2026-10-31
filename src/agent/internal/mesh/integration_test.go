//go:build linux && integration

package mesh

import (
	"context"
	"errors"
	"net"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/vishvananda/netlink"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// These tests create real WireGuard interfaces, so they need CAP_NET_ADMIN and the kernel module:
//
//	docker run --rm --privileged -v "$PWD:/src" -w /src/src/agent golang:1.25 go test -tags integration ./internal/mesh/
//
// Two interfaces (aethtest0, aethtest1) live in one network namespace and talk over the loopback address, which
// is enough to prove keys, peers, endpoints, keepalive and the handshake work end to end. Production uses aethera0.

func realManager(t *testing.T, name string) *Manager {
	t.Helper()
	m := &Manager{
		KeyPath:       filepath.Join(t.TempDir(), "mesh.key"),
		Backend:       &linuxBackend{skipOverlapCheck: true},
		InterfaceName: name,
	}
	if err := m.WireGuardUnavailable(); err != nil {
		t.Skipf("WireGuard is not available here: %v", err)
	}
	t.Cleanup(func() { _ = m.Backend.Remove(name) })
	return m
}

func applyReal(t *testing.T, m *Manager, addr string, port uint32, peers ...*agentv1.MeshPeer) *agentv1.MeshState {
	t.Helper()
	st, err := m.Apply(context.Background(), &agentv1.MeshApply{
		Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD, Address: addr, Cidr: "10.210.0.0/16", ListenPort: port, Peers: peers,
	})
	if err != nil {
		t.Fatalf("apply on %s: %v", m.InterfaceName, err)
	}
	return st
}

func TestRealInterfacePairShakesHands(t *testing.T) {
	a, b := realManager(t, "aethtest0"), realManager(t, "aethtest1")
	sa := applyReal(t, a, "10.210.0.1/16", 51831)
	sb := applyReal(t, b, "10.210.0.2/16", 51832)
	if sa.PublicKey == "" || sb.PublicKey == "" || sa.PublicKey == sb.PublicKey {
		t.Fatalf("keys: %q %q", sa.PublicKey, sb.PublicKey)
	}
	if !sa.Up || sa.ListenPort != 51831 || len(sa.Addresses) != 1 || sa.Addresses[0] != "10.210.0.1/16" {
		t.Fatalf("state %+v", sa)
	}

	applyReal(t, a, "10.210.0.1/16", 51831, &agentv1.MeshPeer{
		PublicKey: sb.PublicKey, Endpoint: "127.0.0.1:51832", AllowedIps: []string{"10.210.0.2/32"}, PersistentKeepaliveSeconds: 1, Name: "b",
	})
	applyReal(t, b, "10.210.0.2/16", 51832, &agentv1.MeshPeer{
		PublicKey: sa.PublicKey, Endpoint: "127.0.0.1:51831", AllowedIps: []string{"10.210.0.1/32"}, PersistentKeepaliveSeconds: 1, Name: "a",
	})

	deadline := time.Now().Add(15 * time.Second)
	for {
		sa, _ = a.Status(context.Background(), &agentv1.MeshStatus{Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD})
		sb, _ = b.Status(context.Background(), &agentv1.MeshStatus{Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD})
		if len(sa.Peers) == 1 && len(sb.Peers) == 1 && sa.Peers[0].LastHandshake != nil && sb.Peers[0].LastHandshake != nil {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("no handshake within 15 s:\n a=%+v\n b=%+v", sa, sb)
		}
		time.Sleep(250 * time.Millisecond)
	}
	if sa.Peers[0].TxBytes == 0 || sa.Peers[0].RxBytes == 0 {
		t.Fatalf("no traffic counted: %+v", sa.Peers[0])
	}
	if sa.Peers[0].PublicKey != sb.PublicKey {
		t.Fatal("peer key mismatch")
	}

	// The kernel carries the MTU and the connected route of the mesh range, and nothing else was added.
	link, err := netlink.LinkByName("aethtest0")
	if err != nil {
		t.Fatal(err)
	}
	if link.Attrs().MTU != DefaultMTU {
		t.Fatalf("mtu %d", link.Attrs().MTU)
	}
	routes, err := netlink.RouteList(link, netlink.FAMILY_V4)
	if err != nil {
		t.Fatal(err)
	}
	for _, r := range routes {
		if r.Dst == nil || r.Dst.String() != "10.210.0.0/16" {
			t.Fatalf("unexpected route on the mesh interface: %+v", r)
		}
	}

	// Declarative: applying without the peer removes it; an address change replaces the old one.
	st := applyReal(t, a, "10.210.0.7/16", 51831)
	if len(st.Peers) != 0 || len(st.Addresses) != 1 || st.Addresses[0] != "10.210.0.7/16" {
		t.Fatalf("after re-apply: %+v", st)
	}
	if st.PublicKey != sa.PublicKey {
		t.Fatal("the identity must survive a re-apply")
	}
}

func TestRealRemoveDeletesTheLinkAndKeepsTheKey(t *testing.T) {
	m := realManager(t, "aethtest0")
	st := applyReal(t, m, "10.210.0.1/16", 51833)
	if err := m.Remove(context.Background(), &agentv1.MeshRemove{}); err != nil {
		t.Fatal(err)
	}
	if _, err := netlink.LinkByName("aethtest0"); err == nil {
		t.Fatal("the interface is still there")
	}
	status, _ := m.Status(context.Background(), &agentv1.MeshStatus{Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD})
	if status.Up || status.PublicKey != st.PublicKey {
		t.Fatalf("status after removal: %+v", status)
	}
	if err := m.Remove(context.Background(), &agentv1.MeshRemove{}); err != nil {
		t.Fatalf("removing twice must be a no-op: %v", err)
	}
	if again := applyReal(t, m, "10.210.0.1/16", 51833); again.PublicKey != st.PublicKey {
		t.Fatal("a rejoining server must keep its identity")
	}
}

func TestRealNeverTouchesAForeignInterface(t *testing.T) {
	dummy := &netlink.Dummy{LinkAttrs: netlink.LinkAttrs{Name: "aethtest2"}}
	if err := netlink.LinkAdd(dummy); err != nil {
		t.Skipf("cannot create a dummy interface: %v", err)
	}
	t.Cleanup(func() { _ = netlink.LinkDel(dummy) })

	m := &Manager{KeyPath: filepath.Join(t.TempDir(), "mesh.key"), Backend: &linuxBackend{skipOverlapCheck: true}, InterfaceName: "aethtest2"}
	_, err := m.Apply(context.Background(), &agentv1.MeshApply{
		Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD, Address: "10.210.0.1/16", Cidr: "10.210.0.0/16", ListenPort: 51834,
	})
	if err == nil || !strings.Contains(err.Error(), "not a WireGuard") {
		t.Fatalf("err %v", err)
	}
	if err := m.Remove(context.Background(), &agentv1.MeshRemove{}); err == nil {
		t.Fatal("deleting a foreign interface must be refused")
	}
	l, err := netlink.LinkByName("aethtest2")
	if err != nil || l.Type() != "dummy" {
		t.Fatalf("the foreign interface was changed: %v", err)
	}
	addrs, _ := netlink.AddrList(l, netlink.FAMILY_V4)
	if len(addrs) != 0 {
		t.Fatalf("an address was added to a foreign interface: %v", addrs)
	}
}

func TestRealOverlapWithAnotherInterfaceIsRefused(t *testing.T) {
	dummy := &netlink.Dummy{LinkAttrs: netlink.LinkAttrs{Name: "aethtest3"}}
	if err := netlink.LinkAdd(dummy); err != nil {
		t.Skipf("cannot create a dummy interface: %v", err)
	}
	t.Cleanup(func() { _ = netlink.LinkDel(dummy) })
	ip, mask, _ := net.ParseCIDR("10.210.5.1/24")
	if err := netlink.AddrAdd(dummy, &netlink.Addr{IPNet: &net.IPNet{IP: ip, Mask: mask.Mask}}); err != nil {
		t.Skipf("cannot add an address: %v", err)
	}

	m := &Manager{KeyPath: filepath.Join(t.TempDir(), "mesh.key"), Backend: NewBackend(), InterfaceName: "aethera0"}
	if err := m.WireGuardUnavailable(); err != nil {
		t.Skip(err)
	}
	t.Cleanup(func() { _ = m.Backend.Remove("aethera0") })
	_, err := m.Apply(context.Background(), &agentv1.MeshApply{
		Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD, Address: "10.210.0.1/16", Cidr: "10.210.0.0/16", ListenPort: 51835,
	})
	if err == nil || !strings.Contains(err.Error(), "overlaps") {
		t.Fatalf("err %v", err)
	}
	if _, rerr := m.Backend.Read("aethera0"); !errors.Is(rerr, ErrNoLink) {
		t.Fatal("no interface must be created when the range collides")
	}
}
