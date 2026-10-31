package policy

import (
	"errors"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/mesh"
)

// checkMesh gates the mesh commands before they are acknowledged: one interface name, a private range, addresses
// and allowed IPs inside it (ADR 0012, "Security notes"). The manager validates again when it applies.
func checkMesh(cmd *agentv1.Command) (handled bool, err error) {
	switch r := cmd.GetRequest().(type) {
	case *agentv1.Command_MeshApply:
		// Tailscale applies only read an interface; the WireGuard fields do not matter there.
		if r.MeshApply.GetProvider() != agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD {
			return true, nil
		}
		_, perr := mesh.ParseApply(r.MeshApply, mesh.WireGuardInterface)
		return true, asViolation(perr)
	case *agentv1.Command_MeshStatus:
		if n := r.MeshStatus.GetInterfaceName(); n != "" && n != mesh.WireGuardInterface && n != mesh.TailscaleInterface {
			return true, violation("interface %q is not allowed", n)
		}
		return true, nil
	case *agentv1.Command_MeshRemove:
		if n := r.MeshRemove.GetInterfaceName(); n != "" && n != mesh.WireGuardInterface {
			return true, violation("interface %q is not allowed", n)
		}
		return true, nil
	}
	return false, nil
}

func asViolation(err error) error {
	if err == nil {
		return nil
	}
	var me *mesh.Error
	if errors.As(err, &me) {
		return &Violation{Code: me.Code, Msg: me.Msg}
	}
	return invalid("%v", err)
}
