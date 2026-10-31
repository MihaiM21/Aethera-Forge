package ops

import (
	"context"
	"errors"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
	"github.com/mihaim21/aethera-forge/agent/internal/mesh"
)

// Capabilities of the private mesh (ADR 0012): advertised only when the host can do it (see mesh.Manager.Capabilities).
const (
	CapMeshWireGuard = mesh.CapWireGuard
	CapMeshTailscale = mesh.CapTailscale
)

// meshError keeps the protocol code of a mesh failure; everything else is classified by the dispatcher.
func meshError(err error) error {
	var me *mesh.Error
	if errors.As(err, &me) {
		return dispatch.Errorf(me.Code, "%s", me.Msg)
	}
	return err
}

func meshResult(st *agentv1.MeshState) *agentv1.CommandResult {
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Mesh{Mesh: st}}
}

func (d *Deps) meshApply(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	st, err := d.Mesh.Apply(ctx, env.Cmd.GetMeshApply())
	if err != nil {
		return nil, meshError(err)
	}
	return meshResult(st), nil
}

func (d *Deps) meshStatus(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	st, err := d.Mesh.Status(ctx, env.Cmd.GetMeshStatus())
	if err != nil {
		return nil, meshError(err)
	}
	return meshResult(st), nil
}

func (d *Deps) meshRemove(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	if err := d.Mesh.Remove(ctx, env.Cmd.GetMeshRemove()); err != nil {
		return nil, meshError(err)
	}
	return empty(), nil
}

// registerMesh wires the mesh arms when a mesh manager is configured. The arms carry no capability of their own
// (they serve two providers); the control plane only sends them to agents that advertise mesh.wireguard or
// mesh.tailscale, and the manager answers UNSUPPORTED for a provider the host cannot run.
func (d *Deps) registerMesh(r func(string, string, dispatch.Handler)) {
	if d.Mesh != nil {
		r("mesh_apply", "", d.meshApply)
		r("mesh_status", "", d.meshStatus)
		r("mesh_remove", "", d.meshRemove)
	}
}

// meshCapabilities lists mesh.wireguard (only when the kernel allows it) and mesh.tailscale (Linux).
func (d *Deps) meshCapabilities() []string {
	if d.Mesh == nil {
		return nil
	}
	return d.Mesh.Capabilities()
}
