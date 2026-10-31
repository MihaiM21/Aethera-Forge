package ops

import (
	"context"
	"errors"
	"net/netip"
	"path/filepath"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
	"github.com/mihaim21/aethera-forge/agent/internal/mesh"
)

type fakeMeshBackend struct{ probe error }

func (f fakeMeshBackend) Probe() error                { return f.probe }
func (f fakeMeshBackend) Apply(mesh.LinkConfig) error { return nil }
func (f fakeMeshBackend) Read(string) (*mesh.LinkState, error) {
	return &mesh.LinkState{Up: true, Addresses: []netip.Prefix{netip.MustParsePrefix("10.210.0.3/16")}, ListenPort: 51820}, nil
}
func (f fakeMeshBackend) Remove(string) error { return nil }

func meshDeps(t *testing.T, probe error) *Deps {
	t.Helper()
	d, _ := newDeps()
	d.Mesh = &mesh.Manager{KeyPath: filepath.Join(t.TempDir(), "mesh.key"), Backend: fakeMeshBackend{probe: probe}}
	return d
}

func meshApplyCommand() *agentv1.Command {
	return &agentv1.Command{CommandId: "m1", Request: &agentv1.Command_MeshApply{MeshApply: &agentv1.MeshApply{
		Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD, InterfaceName: "aethera0", Address: "10.210.0.3/16", Cidr: "10.210.0.0/16", ListenPort: 51820,
	}}}
}

func TestMeshCapabilitiesFollowTheHost(t *testing.T) {
	if caps := meshDeps(t, nil).Capabilities(); !hasCap(caps, CapMeshWireGuard) {
		t.Fatalf("capabilities %v", caps)
	}
	if caps := meshDeps(t, errors.New("no module")).Capabilities(); hasCap(caps, CapMeshWireGuard) {
		t.Fatalf("a host without WireGuard must not advertise it: %v", caps)
	}
	d, _ := newDeps()
	if caps := d.Capabilities(); hasCap(caps, CapMeshWireGuard) || hasCap(caps, CapMeshTailscale) {
		t.Fatalf("no mesh manager, no mesh capability: %v", caps)
	}
}

func TestMeshApplyReturnsTheState(t *testing.T) {
	d := meshDeps(t, nil)
	res, err := d.meshApply(context.Background(), envFor(meshApplyCommand()))
	if err != nil {
		t.Fatal(err)
	}
	st := res.GetMesh()
	if st == nil || !st.Up || st.PublicKey == "" || st.Addresses[0] != "10.210.0.3/16" {
		t.Fatalf("state %v", st)
	}
}

func TestMeshApplyMapsErrorCodes(t *testing.T) {
	d := meshDeps(t, errors.New("no module"))
	_, err := d.meshApply(context.Background(), envFor(meshApplyCommand()))
	var de *dispatch.Error
	if !errors.As(err, &de) || de.Code != agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED {
		t.Fatalf("err %v", err)
	}

	d = meshDeps(t, nil)
	cmd := meshApplyCommand()
	cmd.GetMeshApply().Cidr = "8.8.0.0/16"
	_, err = d.meshApply(context.Background(), envFor(cmd))
	if !errors.As(err, &de) || de.Code != agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION {
		t.Fatalf("err %v", err)
	}
}

func TestMeshStatusAndRemove(t *testing.T) {
	d := meshDeps(t, nil)
	st := &agentv1.Command{CommandId: "m2", Request: &agentv1.Command_MeshStatus{MeshStatus: &agentv1.MeshStatus{Provider: agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD}}}
	res, err := d.meshStatus(context.Background(), envFor(st))
	if err != nil || !res.GetMesh().GetUp() {
		t.Fatalf("%v %v", err, res)
	}
	rm := &agentv1.Command{CommandId: "m3", Request: &agentv1.Command_MeshRemove{MeshRemove: &agentv1.MeshRemove{InterfaceName: "aethera0"}}}
	if res, err := d.meshRemove(context.Background(), envFor(rm)); err != nil || res.GetEmpty() == nil {
		t.Fatalf("%v %v", err, res)
	}
}

func hasCap(caps []string, c string) bool {
	for _, x := range caps {
		if x == c {
			return true
		}
	}
	return false
}
