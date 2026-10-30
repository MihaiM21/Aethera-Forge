package docker

import (
	"testing"

	"github.com/docker/docker/api/types/container"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

func TestApplyResourcesSetsSharesAndTheOOMScoreOfThePriority(t *testing.T) {
	for _, c := range []struct {
		name            string
		in              *agentv1.ResourceLimits
		shares, oom     int64
		nanoCPU, memory int64
	}{
		{"nothing", nil, 0, 0, 0, 0},
		{"normal", &agentv1.ResourceLimits{CpuLimitCores: 1.5, MemoryLimitBytes: 256 << 20}, 0, 0, 1_500_000_000, 256 << 20},
		{"high", &agentv1.ResourceLimits{CpuReservationCores: 2, OomScoreAdj: -500}, 2048, -500, 0, 0},
		{"low", &agentv1.ResourceLimits{CpuReservationCores: 0.5, OomScoreAdj: 500}, 512, 500, 0, 0},
	} {
		host := &container.HostConfig{}
		applyResources(host, c.in)
		if host.Resources.CPUShares != c.shares || int64(host.OomScoreAdj) != c.oom || host.Resources.NanoCPUs != c.nanoCPU || host.Resources.Memory != c.memory {
			t.Errorf("%s: shares=%d oom=%d nano=%d memory=%d", c.name, host.Resources.CPUShares, host.OomScoreAdj, host.Resources.NanoCPUs, host.Resources.Memory)
		}
	}
}
