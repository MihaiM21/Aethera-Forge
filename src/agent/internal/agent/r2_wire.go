package agent

import (
	"github.com/mihaim21/aethera-forge/agent/internal/mesh"
	"github.com/mihaim21/aethera-forge/agent/internal/ops"
)

// wireR2 enables the private mesh (ADR 0012). The manager is always wired; which of mesh.wireguard and
// mesh.tailscale the agent advertises depends on the host: WireGuard only when the kernel lets the agent create
// a WireGuard link (CAP_NET_ADMIN and the module), Tailscale on Linux.
func (a *Agent) wireR2(deps *ops.Deps) {
	m := &mesh.Manager{KeyPath: a.o.Dir.MeshKeyPath(), Logger: a.log}
	if err := m.WireGuardUnavailable(); err != nil {
		a.log.Info("mesh.wireguard is not available on this host", "reason", err.Error())
	}
	deps.Mesh = m
}
