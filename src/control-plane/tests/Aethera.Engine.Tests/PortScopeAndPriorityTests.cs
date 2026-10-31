using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Tests;

/// <summary>Private-network ports and workload priority in the plan (ADR 0012).</summary>
public class PortScopeAndPriorityTests
{
    private static DeploymentSnapshot Snapshot(PortBindScope scope, RuntimeSnapshot? runtime = null) => new()
    {
        WorkloadId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), Slug = "db", SourceKind = ApplicationSourceKind.DockerImage,
        Image = new ImageSnapshot("postgres", "17", ImagePullPolicy.IfNotPresent, null),
        Runtime = runtime ?? new RuntimeSnapshot(),
        Ports = [new(5432, PortProtocol.Tcp, 5432, false, scope), new(9187, PortProtocol.Tcp, null, false, PortBindScope.Private)],
    };

    private static PlanContext Context(string? meshAddress) => new()
    {
        ServerId = Guid.NewGuid(), DeploymentId = Guid.NewGuid(), DeploymentNumber = 1,
        ResolveSecret = (_, _) => throw new InvalidOperationException("no secrets"), Proxy = new TraefikProxyProvider(), MeshAddress = meshAddress,
    };

    [Theory]
    [InlineData(PortBindScope.Public, null)]
    [InlineData(PortBindScope.Loopback, "127.0.0.1")]
    [InlineData(PortBindScope.Private, "10.210.0.4")]
    public void A_published_port_binds_to_the_address_of_its_scope(PortBindScope scope, string? hostIp)
    {
        var port = Assert.Single(DeploymentPlanBuilder.Build(Snapshot(scope), Context("10.210.0.4")).Container.Ports!);
        Assert.Equal(5432, port.HostPort);
        Assert.Equal(hostIp, port.HostIp);
    }

    [Fact]
    public void A_private_port_on_a_server_without_a_mesh_address_fails_the_network_step()
    {
        var failure = Assert.Throws<DeploymentFailure>(() => DeploymentPlanBuilder.Build(Snapshot(PortBindScope.Private), Context(null)));
        Assert.Equal(DeploymentStep.Network, failure.Step);
        Assert.Equal(FailureCodes.NoMeshAddress, failure.Code);
    }

    [Fact]
    public void An_unpublished_private_port_needs_no_mesh_address() =>
        Assert.Single(DeploymentPlanBuilder.Build(Snapshot(PortBindScope.Public), Context(null)).Container.Ports!);

    [Theory]
    [InlineData(WorkloadPriority.Normal, null, 0, 0)]
    [InlineData(WorkloadPriority.Normal, 0.5, 0.5, 0)]
    [InlineData(WorkloadPriority.High, null, 2, -500)]
    [InlineData(WorkloadPriority.High, 0.5, 1, -500)]
    [InlineData(WorkloadPriority.Low, null, 0.5, 500)]
    [InlineData(WorkloadPriority.Low, 2.0, 1, 500)]
    public void Priority_scales_the_cpu_shares_and_sets_the_oom_score(WorkloadPriority priority, double? reservation, double expectedReservation, int expectedOom)
    {
        var limits = DeploymentPlanBuilder.Resources(new RuntimeSnapshot { Priority = priority, CpuReservation = reservation, CpuLimit = 1.5, MemoryLimitBytes = 256 << 20 });
        Assert.Equal(expectedReservation, limits.CpuReservationCores);
        Assert.Equal(expectedOom, limits.OomScoreAdj);
        Assert.Equal(1.5, limits.CpuLimitCores);
        Assert.Equal(256 << 20, limits.MemoryLimitBytes);
    }

    [Fact]
    public void Snapshots_from_before_r2_read_as_public_ports_and_normal_priority()
    {
        const string json = """{"version":1,"workloadId":"11111111-2222-3333-4444-555555555555","environmentId":"11111111-2222-3333-4444-555555555556","slug":"api","sourceKind":"git","runtime":{"cpuLimit":1},"ports":[{"containerPort":80,"protocol":"tcp","publishedPort":8080,"isHttp":true}]}""";
        var snapshot = DeploymentSnapshot.FromJson(json);
        Assert.Equal(PortBindScope.Public, Assert.Single(snapshot.Ports).BindScope);
        Assert.Equal(WorkloadPriority.Normal, snapshot.Runtime.Priority);
    }
}
