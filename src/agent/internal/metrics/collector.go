package metrics

import (
	"context"
	"os/exec"
	"strings"
	"sync"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

// Collector builds MetricsReport and DiscoveryReport messages.
type Collector struct {
	Docker docker.API
	// StatsConcurrency bounds parallel container stats calls (default 8).
	StatsConcurrency int
}

// Metrics builds one MetricsReport. A Docker failure still yields host
// metrics (the control plane shows Docker unavailable separately).
func (c *Collector) Metrics(ctx context.Context) (*agentv1.MetricsReport, error) {
	rep := &agentv1.MetricsReport{CollectedAt: timestamppb.Now()}
	rep.Host = HostMetrics(ctx)
	if c.Docker != nil {
		rep.Containers = c.containerMetrics(ctx)
	}
	return rep, nil
}

func (c *Collector) containerMetrics(ctx context.Context) []*agentv1.ContainerMetrics {
	list, err := c.Docker.ContainerList(ctx, true, nil, "")
	if err != nil {
		return nil
	}
	out := make([]*agentv1.ContainerMetrics, len(list))
	conc := c.StatsConcurrency
	if conc <= 0 {
		conc = 8
	}
	sem := make(chan struct{}, conc)
	var wg sync.WaitGroup
	for i, ci := range list {
		cm := &agentv1.ContainerMetrics{
			ContainerId: ci.GetId(), Name: ci.GetName(), RestartCount: ci.GetRestartCount(),
			State: ci.GetState(), Health: ci.GetHealth(), StartedAt: ci.GetStartedAt(), Labels: ci.GetLabels(),
		}
		out[i] = cm
		if ci.GetState() != agentv1.ContainerState_CONTAINER_STATE_RUNNING {
			continue
		}
		wg.Add(1)
		sem <- struct{}{}
		go func() {
			defer wg.Done()
			defer func() { <-sem }()
			sctx, cancel := context.WithTimeout(ctx, 8*time.Second)
			defer cancel()
			st, err := c.Docker.ContainerStats(sctx, ci.GetId())
			if err != nil {
				return
			}
			cm.CpuPercent = st.CPUPercent
			cm.MemoryUsedBytes, cm.MemoryLimitBytes = st.MemUsedBytes, st.MemLimitBytes
			cm.NetRxBytes, cm.NetTxBytes = st.NetRxBytes, st.NetTxBytes
			cm.BlockReadBytes, cm.BlockWriteBytes = st.BlockRead, st.BlockWrite
			cm.Pids = st.PIDs
		}()
	}
	wg.Wait()
	return out
}

// Discovery builds a DiscoveryReport (spec section 55).
func (c *Collector) Discovery(ctx context.Context) (*agentv1.DiscoveryReport, error) {
	rep := &agentv1.DiscoveryReport{
		CollectedAt: timestamppb.Now(),
		Host:        HostFacts(ctx),
		Disks:       Disks(ctx),
		Interfaces:  Interfaces(ctx),
		Docker:      &agentv1.DockerInfo{Status: agentv1.DockerStatus_DOCKER_STATUS_UNSPECIFIED},
	}
	if c.Docker != nil {
		status, detail := c.Docker.Status(ctx)
		rep.Docker.Status = status
		if status == agentv1.DockerStatus_DOCKER_STATUS_RUNNING {
			if info, err := c.Docker.Info(ctx); err == nil {
				info.Status = status
				rep.Docker = info
			}
			rep.Containers, _ = c.Docker.ContainerList(ctx, true, nil, "")
			rep.Networks, _ = c.Docker.NetworkList(ctx, nil)
			rep.Volumes, _ = c.Docker.VolumeList(ctx, nil)
		} else {
			rep.Docker.Error = detail
		}
	}
	rep.Tools = discoverTools(ctx, rep.Docker)
	return rep, nil
}

// discoverTools looks for helper tools. Every invocation uses a fixed argv.
func discoverTools(ctx context.Context, di *agentv1.DockerInfo) []*agentv1.ToolInfo {
	var tools []*agentv1.ToolInfo
	probe := func(name string, args ...string) {
		path, err := exec.LookPath(name)
		if err != nil {
			return
		}
		cctx, cancel := context.WithTimeout(ctx, 5*time.Second)
		defer cancel()
		out, _ := exec.CommandContext(cctx, path, args...).Output()
		tools = append(tools, &agentv1.ToolInfo{Name: name, Version: firstLine(string(out)), Path: path})
	}
	probe("git", "--version")
	probe("nixpacks", "--version")
	if path, err := exec.LookPath("docker"); err == nil {
		for _, sub := range []struct {
			name string
			args []string
		}{
			{"docker-compose", []string{"compose", "version", "--short"}},
			{"buildx", []string{"buildx", "version"}},
		} {
			cctx, cancel := context.WithTimeout(ctx, 5*time.Second)
			out, err := exec.CommandContext(cctx, path, sub.args...).Output()
			cancel()
			if err == nil {
				v := firstLine(string(out))
				tools = append(tools, &agentv1.ToolInfo{Name: sub.name, Version: v, Path: path})
				if sub.name == "docker-compose" {
					di.ComposeVersion = v
				} else {
					di.BuildxVersion = v
				}
			}
		}
	}
	return tools
}

func firstLine(s string) string {
	s = strings.TrimSpace(s)
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		s = s[:i]
	}
	return s
}
