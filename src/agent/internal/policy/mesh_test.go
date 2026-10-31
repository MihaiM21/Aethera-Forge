package policy

import (
	"context"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

func meshApplyCmd(mut func(*agentv1.MeshApply)) *agentv1.Command {
	a := &agentv1.MeshApply{
		Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD, InterfaceName: "aethera0", Address: "10.210.0.3/16", Cidr: "10.210.0.0/16",
		ListenPort: 51820,
		Peers: []*agentv1.MeshPeer{{
			PublicKey: "qUgSKcCfsNw3Kk2GsjSCIu7mDQqZg7AGT0hNQy7lIDg=", Endpoint: "198.51.100.7:51820", AllowedIps: []string{"10.210.0.4/32"},
		}},
	}
	if mut != nil {
		mut(a)
	}
	return &agentv1.Command{CommandId: "c1", Request: &agentv1.Command_MeshApply{MeshApply: a}}
}

func TestMeshCommandsAreGatedByPolicy(t *testing.T) {
	p := newPolicy(t, nil)
	ctx := context.Background()
	if err := p.CheckCommand(ctx, meshApplyCmd(nil)); err != nil {
		t.Fatalf("a valid mesh apply: %v", err)
	}
	cases := map[string]func(*agentv1.MeshApply){
		"other interface": func(a *agentv1.MeshApply) { a.InterfaceName = "eth0" },
		"public range":    func(a *agentv1.MeshApply) { a.Cidr, a.Address = "1.2.0.0/16", "1.2.0.3/16" },
		"wide allowed ip": func(a *agentv1.MeshApply) { a.Peers[0].AllowedIps = []string{"10.210.0.0/16"} },
		"default route":   func(a *agentv1.MeshApply) { a.Peers[0].AllowedIps = []string{"0.0.0.0/0"} },
	}
	for name, mut := range cases {
		err := p.CheckCommand(ctx, meshApplyCmd(mut))
		v, ok := AsViolation(err)
		if !ok || v.Code != agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION {
			t.Errorf("%s: err %v", name, err)
		}
	}

	// Tailscale applies only read an interface: the WireGuard fields are not checked.
	ts := &agentv1.Command{CommandId: "c2", Request: &agentv1.Command_MeshApply{MeshApply: &agentv1.MeshApply{Provider: agentv1.MeshProvider_MESH_PROVIDER_TAILSCALE}}}
	if err := p.CheckCommand(ctx, ts); err != nil {
		t.Fatalf("tailscale: %v", err)
	}

	rm := func(name string) *agentv1.Command {
		return &agentv1.Command{CommandId: "c3", Request: &agentv1.Command_MeshRemove{MeshRemove: &agentv1.MeshRemove{InterfaceName: name}}}
	}
	if err := p.CheckCommand(ctx, rm("aethera0")); err != nil {
		t.Fatal(err)
	}
	if _, ok := AsViolation(p.CheckCommand(ctx, rm("docker0"))); !ok {
		t.Fatal("removing another interface must be refused")
	}
	st := &agentv1.Command{CommandId: "c4", Request: &agentv1.Command_MeshStatus{MeshStatus: &agentv1.MeshStatus{InterfaceName: "eth0"}}}
	if _, ok := AsViolation(p.CheckCommand(ctx, st)); !ok {
		t.Fatal("reading another interface must be refused")
	}
}
