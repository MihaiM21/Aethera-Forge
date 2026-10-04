package metrics

import (
	"context"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
)

func TestHostFactsAreFilledIn(t *testing.T) {
	f := HostFacts(context.Background())
	if f.GetHostname() == "" || f.GetArchitecture() == "" || f.GetCpuCoresLogical() == 0 || f.GetMemoryTotalBytes() <= 0 {
		t.Fatalf("host facts incomplete: %v", f)
	}
}

func TestMetricsReportContainsHostAndContainerSamples(t *testing.T) {
	fake := dockertest.New()
	fake.AddContainer(&agentv1.ContainerInfo{Id: "run", Name: "web", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING,
		Labels: map[string]string{"aethera.app": "x"}, RestartCount: 2})
	fake.AddContainer(&agentv1.ContainerInfo{Id: "stop", Name: "job", State: agentv1.ContainerState_CONTAINER_STATE_EXITED})
	c := &Collector{Docker: fake}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	rep, err := c.Metrics(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if rep.GetCollectedAt() == nil || rep.GetHost().GetMemoryTotalBytes() <= 0 || rep.GetHost().GetCpuCores() == 0 {
		t.Fatalf("host metrics incomplete: %v", rep.GetHost())
	}
	byName := map[string]*agentv1.ContainerMetrics{}
	for _, cm := range rep.GetContainers() {
		byName[cm.GetName()] = cm
	}
	if len(byName) != 2 {
		t.Fatalf("containers = %v", rep.GetContainers())
	}
	if w := byName["web"]; w.GetCpuPercent() != 12.5 || w.GetMemoryUsedBytes() != 1<<20 || w.GetRestartCount() != 2 || w.GetLabels()["aethera.app"] != "x" {
		t.Fatalf("running container sample = %v", w)
	}
	if j := byName["job"]; j.GetCpuPercent() != 0 || j.GetState() != agentv1.ContainerState_CONTAINER_STATE_EXITED {
		t.Fatalf("stopped container sample = %v", j)
	}
}

func TestMetricsSurviveDockerOutage(t *testing.T) {
	fake := dockertest.New()
	fake.SetStatus(agentv1.DockerStatus_DOCKER_STATUS_STOPPED)
	rep, err := (&Collector{Docker: fake}).Metrics(context.Background())
	if err != nil || rep.GetHost() == nil || len(rep.GetContainers()) != 0 {
		t.Fatalf("rep=%v err=%v", rep, err)
	}
}

func TestDiscoveryReportsDockerState(t *testing.T) {
	fake := dockertest.New()
	fake.AddContainer(&agentv1.ContainerInfo{Id: "c", Name: "web"})
	fake.Networks["n"] = &agentv1.NetworkInfo{Name: "n"}
	fake.Volumes["v"] = &agentv1.VolumeInfo{Name: "v"}
	rep, err := (&Collector{Docker: fake}).Discovery(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if rep.GetDocker().GetStatus() != agentv1.DockerStatus_DOCKER_STATUS_RUNNING || rep.GetDocker().GetVersion() == "" {
		t.Fatalf("docker = %v", rep.GetDocker())
	}
	if len(rep.GetContainers()) != 1 || len(rep.GetNetworks()) != 1 || len(rep.GetVolumes()) != 1 {
		t.Fatalf("inventory = %v", rep)
	}
	if rep.GetHost().GetHostname() == "" {
		t.Fatal("no host facts")
	}

	fake.SetStatus(agentv1.DockerStatus_DOCKER_STATUS_PERMISSION_DENIED)
	rep, _ = (&Collector{Docker: fake}).Discovery(context.Background())
	if rep.GetDocker().GetStatus() != agentv1.DockerStatus_DOCKER_STATUS_PERMISSION_DENIED || len(rep.GetContainers()) != 0 {
		t.Fatalf("docker down report = %v", rep.GetDocker())
	}
}
