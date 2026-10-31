package docker

import (
	"github.com/docker/docker/api/types/container"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// applyResources maps the protocol's limits onto a container's host config. The workload priority (ADR 0012) arrives as CPU shares (from the
// reservation) and oom_score_adj: -500 shields the container from the OOM killer, 500 offers it first, 0 keeps Docker's default.
func applyResources(host *container.HostConfig, r *agentv1.ResourceLimits) {
	if r == nil {
		return
	}
	host.Resources.NanoCPUs = int64(r.GetCpuLimitCores() * 1e9)
	host.Resources.CPUShares = int64(r.GetCpuReservationCores() * 1024)
	host.Resources.Memory = r.GetMemoryLimitBytes()
	host.Resources.MemoryReservation = r.GetMemoryReservationBytes()
	host.Resources.MemorySwap = r.GetMemorySwapLimitBytes()
	host.Resources.CpusetCpus = r.GetCpusetCpus()
	host.OomScoreAdj = int(r.GetOomScoreAdj())
	if pl := r.GetPidsLimit(); pl != 0 {
		host.Resources.PidsLimit = &pl
	}
}
