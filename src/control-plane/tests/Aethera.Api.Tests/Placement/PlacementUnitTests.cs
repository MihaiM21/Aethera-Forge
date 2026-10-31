using Aethera.Domain;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Placement;

namespace Aethera.Api.Tests.Placement;

/// <summary>The rules and the ranking of placement without a database (ADR 0012, "Roles and placement").</summary>
public sealed class PlacementUnitTests
{
    private const long Gib = 1L << 30;
    private static readonly Guid Org = Guid.NewGuid();

    private static ServerCapacity Cap(
        string name, ServerRole[]? roles = null, long memoryGib = 8, long committedGib = 0, int workloads = 0, long? diskFreeGib = null,
        ServerLifecycle lifecycle = ServerLifecycle.Active, bool connected = true, ServerTransport transport = ServerTransport.Agent, int cores = 4, double cpuCommitted = 0) =>
        new(Guid.NewGuid(), name, lifecycle, transport, roles ?? [], connected, cores, memoryGib * Gib, 100 * Gib,
            diskFreeGib is { } free ? new CapacityUsage(10, 1 * Gib, memoryGib * Gib, (100 - free) * Gib, 100 * Gib, DateTimeOffset.UtcNow) : null,
            0, 0, cpuCommitted, committedGib * Gib, 0, committedGib * Gib, workloads, false, []);

    private static PlacementRequest Request(PlacementKind kind = PlacementKind.Application, long? memoryGib = null, double? cpu = null) =>
        new(Org, kind, cpu, memoryGib * Gib);

    private static string[] Names(IEnumerable<PlacementCandidate> candidates) => candidates.Select(c => c.ServerName).ToArray();

    // ---- roles -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PlacementKind.Application, ServerRole.Worker)]
    [InlineData(PlacementKind.StatelessService, ServerRole.Worker)]
    [InlineData(PlacementKind.StatefulService, ServerRole.Storage)]
    [InlineData(PlacementKind.Build, ServerRole.Build)]
    public void Each_kind_needs_its_role(PlacementKind kind, ServerRole role) => Assert.Equal(role, PlacementRules.RequiredRole(kind));

    [Fact]
    public void A_server_without_roles_accepts_everything()
    {
        foreach (var kind in Enum.GetValues<PlacementKind>())
            Assert.Equal(RoleFit.Open, PlacementRules.Fit([], kind));
    }

    [Fact]
    public void A_server_with_the_role_matches_one_without_it_is_excluded()
    {
        Assert.Equal(RoleFit.Matches, PlacementRules.Fit([ServerRole.Worker, ServerRole.Build], PlacementKind.Application));
        Assert.Equal(RoleFit.Excluded, PlacementRules.Fit([ServerRole.Worker], PlacementKind.StatefulService));
        Assert.Equal(RoleFit.Excluded, PlacementRules.Fit([ServerRole.Storage], PlacementKind.Application));
        Assert.Equal(RoleFit.Excluded, PlacementRules.Fit([ServerRole.Ci], PlacementKind.Application));
    }

    [Fact]
    public void The_control_planes_machine_still_accepts_workloads_so_a_one_server_install_keeps_working()
    {
        // The installer gives the local server master + build: applications and databases must still be placeable on it.
        var fit = PlacementRules.Fit([ServerRole.Master, ServerRole.Build], PlacementKind.Application);
        Assert.Equal(RoleFit.MasterFallback, fit);
        Assert.Equal(RoleFit.MasterFallback, PlacementRules.Fit([ServerRole.Master], PlacementKind.StatefulService));
        Assert.Equal(RoleFit.Matches, PlacementRules.Fit([ServerRole.Master, ServerRole.Build], PlacementKind.Build));
    }

    [Fact]
    public void A_role_warning_only_exists_for_a_server_whose_roles_exclude_the_kind()
    {
        Assert.Null(PlacementRules.RoleWarning("a", [], PlacementKind.Application));
        Assert.Null(PlacementRules.RoleWarning("a", [ServerRole.Master, ServerRole.Build], PlacementKind.Application));
        var warning = PlacementRules.RoleWarning("db-box", [ServerRole.Worker], PlacementKind.StatefulService);
        Assert.NotNull(warning);
        Assert.Contains("db-box", warning);
        Assert.Contains("storage", warning);
    }

    [Fact]
    public void The_kind_of_a_workload_follows_its_data()
    {
        Assert.Equal(PlacementKind.StatefulService, PlacementRules.KindOfTemplate(1));
        Assert.Equal(PlacementKind.StatelessService, PlacementRules.KindOfTemplate(0));
        var service = new Service { Name = "pg", Slug = "pg", TemplateKey = "postgres", Image = "postgres:17" };
        Assert.Equal(PlacementKind.StatelessService, PlacementRules.KindOf(service));
        service.Volumes.Add(new Volume { Name = "data", MountPath = "/var/lib/postgresql/data" });
        Assert.Equal(PlacementKind.StatefulService, PlacementRules.KindOf(service));
        Assert.Equal(PlacementKind.Application, PlacementRules.KindOf(new Application { Name = "web", Slug = "web" }));
    }

    [Fact]
    public void A_workloads_share_is_its_reservation_else_its_limit()
    {
        Assert.Equal(512L << 20, PlacementRules.MemoryOf(new RuntimeConfig { MemoryReservationBytes = 512L << 20, MemoryLimitBytes = 1 * Gib }));
        Assert.Equal(1 * Gib, PlacementRules.MemoryOf(new RuntimeConfig { MemoryLimitBytes = 1 * Gib }));
        Assert.Null(PlacementRules.MemoryOf(new RuntimeConfig()));
        Assert.Equal(0.5, PlacementRules.CpuOf(new RuntimeConfig { CpuReservation = 0.5, CpuLimit = 2 }));
        Assert.Equal(2, PlacementRules.CpuOf(new RuntimeConfig { CpuLimit = 2 }));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1536L, "1.5 KiB")]
    [InlineData(512L << 20, "512 MiB")]
    [InlineData(3L << 29, "1.5 GiB")]
    public void Bytes_read_like_people_say_them(long bytes, string text) => Assert.Equal(text, PlacementRules.Bytes(bytes));

    // ---- eligibility -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_server_that_is_not_active_is_not_eligible_and_says_why()
    {
        var ranked = PlacementService.Rank([Cap("pending", lifecycle: ServerLifecycle.Pending), Cap("ok")], Request());

        Assert.Equal(["ok", "pending"], Names(ranked));
        Assert.True(ranked[0].Eligible);
        Assert.False(ranked[1].Eligible);
        Assert.Contains("pending", ranked[1].Reasons[0]);
    }

    [Fact]
    public void A_server_whose_agent_is_not_connected_is_not_eligible()
    {
        var ranked = PlacementService.Rank([Cap("away", connected: false), Cap("ok")], Request());

        Assert.False(ranked.Single(c => c.ServerName == "away").Eligible);
        Assert.Contains("agent is not connected", ranked.Single(c => c.ServerName == "away").Reasons[0]);
    }

    [Fact]
    public void An_ssh_server_that_cannot_be_reached_says_so_in_ssh_terms()
    {
        var ranked = PlacementService.Rank([Cap("ssh-box", connected: false, transport: ServerTransport.Ssh)], Request());
        Assert.Contains("SSH", ranked[0].Reasons[0]);
    }

    [Fact]
    public void A_role_that_excludes_the_kind_makes_the_server_not_eligible()
    {
        var servers = new[] { Cap("apps", [ServerRole.Worker]), Cap("data", [ServerRole.Storage]) };

        Assert.Equal(["apps"], Names(PlacementService.Rank(servers, Request(PlacementKind.Application)).Where(c => c.Eligible)));
        Assert.Equal(["data"], Names(PlacementService.Rank(servers, Request(PlacementKind.StatefulService)).Where(c => c.Eligible)));
        Assert.DoesNotContain(PlacementService.Rank(servers, Request(PlacementKind.Build)), c => c.Eligible);
    }

    [Fact]
    public void A_server_without_the_unreserved_memory_is_not_eligible()
    {
        var servers = new[] { Cap("full", memoryGib: 4, committedGib: 3), Cap("roomy", memoryGib: 4, committedGib: 1) };

        var ranked = PlacementService.Rank(servers, Request(memoryGib: 2));

        Assert.Equal(["roomy"], Names(ranked.Where(c => c.Eligible)));
        Assert.Contains("unreserved", ranked.Single(c => c.ServerName == "full").Reasons[0]);
    }

    [Fact]
    public void A_server_with_unknown_memory_is_not_rejected_for_lack_of_it()
    {
        var unknown = Cap("new") with { MemoryBytes = null };
        var ranked = PlacementService.Rank([unknown], Request(memoryGib: 2));
        Assert.True(ranked[0].Eligible);
        Assert.Contains(ranked[0].Reasons, r => r.Contains("memory is not known"));
    }

    [Fact]
    public void Nothing_is_eligible_when_there_are_no_servers_or_none_fit()
    {
        Assert.Empty(PlacementService.Rank([], Request()));
        Assert.All(PlacementService.Rank([Cap("a", lifecycle: ServerLifecycle.Disabled), Cap("b", connected: false)], Request()), c => Assert.False(c.Eligible));
    }

    // ---- ranking ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Eligible_servers_are_ranked_by_unreserved_memory_first()
    {
        var servers = new[] { Cap("small", memoryGib: 4), Cap("big", memoryGib: 16), Cap("busy", memoryGib: 16, committedGib: 13) };

        var ranked = PlacementService.Rank(servers, Request());

        Assert.Equal(["big", "small", "busy"], Names(ranked));
        Assert.True(ranked[0].Score > ranked[1].Score && ranked[1].Score > ranked[2].Score);
        Assert.Equal(16 * Gib, ranked[0].MemoryFreeBytes);
        Assert.Equal(3 * Gib, ranked.Single(c => c.ServerName == "busy").MemoryFreeBytes);
    }

    [Fact]
    public void Equal_memory_is_broken_by_free_disk_then_by_fewer_workloads_then_by_name()
    {
        var byDisk = PlacementService.Rank([Cap("low-disk", diskFreeGib: 10), Cap("high-disk", diskFreeGib: 80)], Request());
        Assert.Equal(["high-disk", "low-disk"], Names(byDisk));

        var byCount = PlacementService.Rank([Cap("crowded", workloads: 9), Cap("calm", workloads: 2)], Request());
        Assert.Equal(["calm", "crowded"], Names(byCount));

        var byName = PlacementService.Rank([Cap("b"), Cap("a")], Request());
        Assert.Equal(["a", "b"], Names(byName));
    }

    [Fact]
    public void A_server_with_the_role_is_preferred_to_the_control_planes_machine_even_with_less_memory()
    {
        var servers = new[] { Cap("local", [ServerRole.Master, ServerRole.Build], memoryGib: 32), Cap("worker", [ServerRole.Worker], memoryGib: 4) };

        var ranked = PlacementService.Rank(servers, Request());

        Assert.Equal(["worker", "local"], Names(ranked));
        Assert.All(ranked, c => Assert.True(c.Eligible));
        Assert.Contains(ranked[1].Reasons, r => r.Contains("control plane's own machine"));
    }

    [Fact]
    public void The_control_planes_machine_alone_takes_applications_and_databases()
    {
        var local = Cap("local", [ServerRole.Master, ServerRole.Build]);
        foreach (var kind in new[] { PlacementKind.Application, PlacementKind.StatefulService, PlacementKind.StatelessService })
            Assert.True(PlacementService.Rank([local], Request(kind)).Single().Eligible);
    }

    [Fact]
    public void Every_candidate_has_reasons_and_blockers_come_first()
    {
        var ranked = PlacementService.Rank([Cap("away", connected: false), Cap("ok", [ServerRole.Worker])], Request());

        Assert.All(ranked, c => Assert.NotEmpty(c.Reasons));
        Assert.Contains("worker role", ranked.Single(c => c.ServerName == "ok").Reasons[0]);
        Assert.Contains("agent is not connected", ranked.Single(c => c.ServerName == "away").Reasons[0]);
    }

    [Fact]
    public void An_over_committed_cpu_is_noted_but_does_not_rule_a_server_out()
    {
        var ranked = PlacementService.Rank([Cap("hot", cores: 2, cpuCommitted: 1.8)], Request(cpu: 1));
        Assert.True(ranked[0].Eligible);
        Assert.Contains(ranked[0].Reasons, r => r.Contains("CPU would be over-committed"));
    }

    // ---- build slots -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Build_slot_keys_are_stable_and_distinct_per_server_and_slot()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(BuildSlotGate.KeyOf(a, 0), BuildSlotGate.KeyOf(a, 0));
        Assert.NotEqual(BuildSlotGate.KeyOf(a, 0), BuildSlotGate.KeyOf(a, 1));
        Assert.NotEqual(BuildSlotGate.KeyOf(a, 0), BuildSlotGate.KeyOf(b, 0));
    }
}
