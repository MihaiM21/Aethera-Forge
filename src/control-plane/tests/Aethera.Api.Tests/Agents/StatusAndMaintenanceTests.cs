using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Jobs;
using Aethera.Infrastructure.Agents.Monitoring;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Agents.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DomainDockerStatus = Aethera.Domain.DockerStatus;

namespace Aethera.Api.Tests.Agents;

public sealed class ServerStatusMachineTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly ApplicationSummary NoApps = new(0, 0, 0, 0);

    private static Server NewServer() => new() { Name = "s", Host = "h" };

    private static Server Healthy()
    {
        var server = NewServer();
        server.RecordHeartbeat(T, DomainDockerStatus.Running);
        return server;
    }

    [Fact]
    public void Nothing_observed_is_unknown_everywhere_and_attributed_to_nobody()
    {
        var health = ServerStatusMachine.Derive(NewServer(), NoApps);
        Assert.All(new[] { health.Server, health.Agent, health.Docker, health.Application }, a => Assert.Equal(AxisHealth.Unknown, a.Health));
        Assert.Null(health.FirstFailingLayer);
    }

    [Fact]
    public void A_connected_agent_with_a_running_daemon_and_healthy_apps_is_available_on_every_axis()
    {
        var health = ServerStatusMachine.Derive(Healthy(), new ApplicationSummary(2, 0, 2, 0));
        Assert.All(new[] { health.Server, health.Agent, health.Docker, health.Application }, a => Assert.Equal(AxisHealth.Available, a.Health));
        Assert.Null(health.FirstFailingLayer);
    }

    [Fact]
    public void An_unavailable_agent_on_a_reachable_host_blocks_docker_and_applications()
    {
        var server = Healthy();
        server.SetAgentStatus(AgentStatus.Unavailable, T.AddMinutes(1));

        var health = ServerStatusMachine.Derive(server, new ApplicationSummary(3, 0, 3, 0));

        Assert.Equal(AxisHealth.Available, health.Server.Health); // the machine answered when the agent was last seen
        Assert.Equal(AxisHealth.Unavailable, health.Agent.Health);
        Assert.Equal(AxisHealth.Unknown, health.Docker.Health);
        Assert.Equal("agent", health.Docker.BlockedBy);
        Assert.True(health.Docker.Stale); // the last known value is kept, flagged stale
        Assert.Equal(AxisHealth.Unknown, health.Application.Health);
        Assert.Equal("agent", health.Application.BlockedBy); // "unknown: agent unavailable", not "stopped"
        Assert.Equal("agent", health.FirstFailingLayer);
    }

    [Fact]
    public void An_unreachable_machine_is_the_first_failing_layer()
    {
        var server = Healthy();
        server.SetAgentStatus(AgentStatus.Unavailable, T.AddMinutes(1));
        server.SetReachability(ReachabilityStatus.Unreachable, T.AddMinutes(2));

        var health = ServerStatusMachine.Derive(server, new ApplicationSummary(1, 0, 1, 0));

        Assert.Equal(AxisHealth.Unavailable, health.Server.Health);
        Assert.Equal(AxisHealth.Unknown, health.Agent.Health);
        Assert.Equal("server", health.Agent.BlockedBy);
        Assert.Equal("server", health.Docker.BlockedBy);
        Assert.Equal("server", health.Application.BlockedBy);
        Assert.Equal("server", health.FirstFailingLayer);
    }

    [Fact]
    public void A_connected_agent_outranks_an_old_unreachable_probe_result()
    {
        var server = Healthy();
        server.SetReachability(ReachabilityStatus.Unreachable, T);
        server.SetAgentStatus(AgentStatus.Connected, T);
        Assert.Equal(AxisHealth.Available, ServerStatusMachine.Derive(server, NoApps).Server.Health);
    }

    [Theory]
    [InlineData(DomainDockerStatus.Stopped)]
    [InlineData(DomainDockerStatus.Unreachable)]
    [InlineData(DomainDockerStatus.NotInstalled)]
    [InlineData(DomainDockerStatus.PermissionDenied)]
    public void A_broken_docker_daemon_is_unavailable_and_blocks_only_the_applications(DomainDockerStatus docker)
    {
        var server = Healthy();
        server.SetDockerStatus(docker, T.AddMinutes(1));

        var health = ServerStatusMachine.Derive(server, new ApplicationSummary(2, 0, 2, 0));

        Assert.Equal(AxisHealth.Available, health.Agent.Health); // docker down is not agent down
        Assert.Equal(AxisHealth.Unavailable, health.Docker.Health);
        Assert.Equal("docker", health.Application.BlockedBy);
        Assert.Equal("docker", health.FirstFailingLayer);
    }

    [Fact]
    public void Failing_workloads_make_the_application_axis_unavailable_when_everything_below_is_fine()
    {
        var health = ServerStatusMachine.Derive(Healthy(), new ApplicationSummary(4, 1, 3, 0));
        Assert.Equal(AxisHealth.Unavailable, health.Application.Health);
        Assert.Null(health.Application.BlockedBy);
        Assert.Equal("application", health.FirstFailingLayer);
    }

    [Fact]
    public void An_ssh_only_server_has_no_agent_installed_and_keeps_polled_docker_state()
    {
        var server = NewServer();
        server.SetAgentStatus(AgentStatus.NotInstalled, T);
        server.SetDockerStatus(DomainDockerStatus.Running, T);
        var health = ServerStatusMachine.Derive(server, new ApplicationSummary(1, 0, 1, 0));
        Assert.Equal(AxisHealth.NotInstalled, health.Agent.Health);
        Assert.Equal(AxisHealth.Available, health.Docker.Health);
        Assert.Equal(AxisHealth.Available, health.Application.Health);
        Assert.Null(health.FirstFailingLayer);
    }

    [Fact]
    public void Summarizing_counts_a_deliberately_stopped_workload_as_fine()
    {
        var summary = ServerStatusMachine.Summarize(
        [
            (WorkloadStatus.Running, DesiredState.Running),
            (WorkloadStatus.Stopped, DesiredState.Stopped),   // the user stopped it
            (WorkloadStatus.Stopped, DesiredState.Running),   // it should be running
            (WorkloadStatus.Failed, DesiredState.Running),
            (WorkloadStatus.Unhealthy, DesiredState.Running),
            (WorkloadStatus.Unknown, DesiredState.Running),
            (WorkloadStatus.NotDeployed, DesiredState.Running),
        ]);
        Assert.Equal(new ApplicationSummary(7, 3, 3, 1), summary);
    }

    [Fact]
    public void The_reachability_debounce_needs_consecutive_failures_but_one_success_resets_it()
    {
        var id = Guid.NewGuid();
        var debounce = new ReachabilityDebouncer(2);
        Assert.Null(debounce.Observe(id, false));
        Assert.Equal(ReachabilityStatus.Unreachable, debounce.Observe(id, false));
        Assert.Equal(ReachabilityStatus.Reachable, debounce.Observe(id, true));
        Assert.Null(debounce.Observe(id, false)); // the count started over
        Assert.Equal(ReachabilityStatus.Unreachable, debounce.Observe(id, false));
        Assert.Equal(ReachabilityStatus.Unreachable, debounce.Observe(id, false));
    }
}

[Collection(GatewayCollection.Name)]
public sealed class StatusServiceTests(GatewayFixture fixture)
{
    private sealed class FakeProbe : IReachabilityProbe
    {
        public Func<string, int, bool> Answer { get; set; } = (_, _) => false;

        public List<(string Host, int Port)> Calls { get; } = [];

        public Task<bool> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add((host, port));
            return Task.FromResult(Answer(host, port));
        }
    }

    private ReachabilityProbeService Service(FakeProbe probe) => new(
        fixture.Get<IServiceScopeFactory>(), fixture.Get<AgentSessionRegistry>(), fixture.Get<ServerStatusService>(), probe,
        fixture.Get<IOptions<AgentGatewayOptions>>(), NullLogger<ReachabilityProbeService>.Instance);

    [RequiresDatabaseFact]
    public async Task Transitions_are_written_as_events_only_when_a_value_changes()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var status = fixture.Get<ServerStatusService>();
        await status.AgentConnectedAsync(serverId, "1.0.0", "linux", "amd64");
        await status.AgentConnectedAsync(serverId, "1.0.0", "linux", "amd64"); // same state again
        await status.HeartbeatAsync(serverId, DomainDockerStatus.Running);
        await status.HeartbeatAsync(serverId, DomainDockerStatus.Running);
        await status.AgentDisconnectedAsync(serverId, "stream closed");
        await status.AgentDisconnectedAsync(serverId, "stream closed");

        var events = await fixture.WithDbAsync(db => db.ResourceEvents.AsNoTracking().Where(e => e.ResourceId == serverId && e.Kind == "status.changed").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(4, events.Count); // the repeated connects, heartbeats and disconnects changed nothing
        Assert.Equal(
            new HashSet<string> { "agent:unknown>connected", "reachability:unknown>reachable", "docker:unknown>running", "agent:connected>unavailable" },
            events.Select(e => $"{e.Axis}:{e.OldValue}>{e.NewValue}").ToHashSet());
    }

    [RequiresDatabaseFact]
    public async Task The_probe_needs_two_failures_to_declare_a_machine_unreachable_and_recovers_at_once()
    {
        var (_, serverId) = await fixture.SeedServerAsync(host: "probe-me.example.com", sshPort: 2222, withSsh: true);
        var probe = new FakeProbe();
        var service = Service(probe);

        await service.ProbeAllAsync(default);
        Assert.Equal(ReachabilityStatus.Unknown, (await fixture.LoadServerAsync(serverId)).ReachabilityStatus); // one failure is not enough
        Assert.NotNull((await fixture.LoadServerAsync(serverId)).ReachabilityCheckedAt);

        await service.ProbeAllAsync(default);
        var down = await fixture.LoadServerAsync(serverId);
        Assert.Equal(ReachabilityStatus.Unreachable, down.ReachabilityStatus);
        Assert.Contains(("probe-me.example.com", 2222), probe.Calls);

        probe.Answer = (_, _) => true;
        await service.ProbeAllAsync(default);
        Assert.Equal(ReachabilityStatus.Reachable, (await fixture.LoadServerAsync(serverId)).ReachabilityStatus);
    }

    [RequiresDatabaseFact]
    public async Task Without_a_probe_target_the_axis_stays_unknown_and_connected_servers_are_not_probed()
    {
        var (_, noTarget) = await fixture.SeedServerAsync(host: "no-target.example.com");
        var (_, connected) = await fixture.SeedServerAsync(host: "connected.example.com", withSsh: true);
        var (_, custom) = await fixture.SeedServerAsync(host: "custom-port.example.com");
        await fixture.WithDbAsync(async db =>
        {
            (await db.Servers.SingleAsync(s => s.Id == custom)).ReachabilityProbePort = 8443;
            return await db.SaveChangesAsync();
        });
        await using var agent = FakeAgent.Start(fixture, connected);
        await agent.ExpectWelcomeAsync();

        var probe = new FakeProbe();
        await Service(probe).ProbeAllAsync(default);

        Assert.DoesNotContain(probe.Calls, c => c.Host == "no-target.example.com");
        Assert.DoesNotContain(probe.Calls, c => c.Host == "connected.example.com");
        Assert.Contains(("custom-port.example.com", 8443), probe.Calls);
        Assert.Equal(ReachabilityStatus.Unknown, (await fixture.LoadServerAsync(noTarget)).ReachabilityStatus);
    }

    [RequiresDatabaseFact]
    public async Task A_server_stored_as_connected_without_a_live_session_is_marked_unavailable_after_a_restart()
    {
        // The API was killed: nobody recorded the disconnect, so the database still says "connected" (found by the agent end-to-end test).
        var (_, orphan) = await fixture.SeedServerAsync();
        var (_, live) = await fixture.SeedServerAsync();
        await fixture.Get<ServerStatusService>().AgentConnectedAsync(orphan, "1.0.0", "linux", "amd64");
        await fixture.Get<ServerStatusService>().AgentConnectedAsync(live, "1.0.0", "linux", "amd64");
        await using var agent = FakeAgent.Start(fixture, live);
        await agent.ExpectWelcomeAsync();

        var service = new AgentGatewayLifetimeService(
            fixture.Get<Aethera.Infrastructure.Agents.Pki.IInternalCa>(), fixture.Get<AgentSessionRegistry>(), fixture.Get<IServiceScopeFactory>(), fixture.Get<ServerStatusService>(),
            fixture.Get<IOptions<AgentGatewayOptions>>(), NullLogger<AgentGatewayLifetimeService>.Instance);
        Assert.True(await service.ReconcileOrphanedSessionsAsync(default) >= 1);

        Assert.Equal(AgentStatus.Unavailable, (await fixture.LoadServerAsync(orphan)).AgentStatus);
        Assert.Equal(AgentStatus.Connected, (await fixture.LoadServerAsync(live)).AgentStatus);
        var events = await fixture.WithDbAsync(db => db.ResourceEvents.AsNoTracking().Where(e => e.ResourceId == orphan && e.Axis == "agent" && e.NewValue == "unavailable").ToListAsync());
        Assert.Single(events);
    }

    [Fact]
    public void The_probe_target_is_the_configured_port_else_the_ssh_port_of_a_server_with_credentials()
    {
        Assert.Equal(8443, ReachabilityProbeService.ProbePort(8443, 22, true));
        Assert.Equal(2222, ReachabilityProbeService.ProbePort(null, 2222, true));
        Assert.Null(ReachabilityProbeService.ProbePort(null, 22, false));
    }
}

[Collection(GatewayCollection.Name)]
public sealed class MetricsRetentionTests(GatewayFixture fixture)
{
    private static MetricSample Sample(Guid serverId, DateTimeOffset at, MetricResolution resolution, double cpu, string? container = null, long net = 0) => new()
    {
        ServerId = serverId, ContainerId = container, Timestamp = at, Resolution = resolution, CpuPercent = cpu, MemoryUsedBytes = 100, MemoryTotalBytes = 1000,
        DiskUsedBytes = 10, DiskTotalBytes = 100, NetRxBytes = net, NetTxBytes = net, Load1 = cpu / 10,
    };

    private Task<List<MetricSample>> RowsAsync(Guid serverId) =>
        fixture.WithDbAsync(db => db.MetricSamples.AsNoTracking().Where(m => m.ServerId == serverId).OrderBy(m => m.Resolution).ThenBy(m => m.Timestamp).ToListAsync());

    [RequiresDatabaseFact]
    public async Task Old_raw_samples_are_rolled_up_into_five_minute_averages_and_deleted()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var service = fixture.Get<MetricsRetentionService>();
        var now = new DateTimeOffset(2026, 10, 3, 12, 7, 0, TimeSpan.Zero);
        var bucket = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero); // 27 hours old: past the 24 h raw retention

        await fixture.WithDbAsync(async db =>
        {
            db.MetricSamples.AddRange(
                Sample(serverId, bucket.AddSeconds(10), MetricResolution.Raw, 10, net: 100),
                Sample(serverId, bucket.AddSeconds(20), MetricResolution.Raw, 20, net: 200),
                Sample(serverId, bucket.AddSeconds(30), MetricResolution.Raw, 60, net: 300),
                Sample(serverId, bucket.AddMinutes(6), MetricResolution.Raw, 40, net: 400),                 // the next bucket
                Sample(serverId, bucket.AddSeconds(10), MetricResolution.Raw, 100, container: "c1", net: 50), // containers are rolled up separately
                Sample(serverId, now.AddHours(-1), MetricResolution.Raw, 5));                                // recent: untouched
            return await db.SaveChangesAsync();
        });

        var result = await service.RunOnceAsync(now, default);
        Assert.False(result.Skipped);
        Assert.Equal(3, result.RolledUpRaw); // rows written: three five-minute buckets (host x2, container x1)

        var rows = await RowsAsync(serverId);
        Assert.Equal(1, rows.Count(r => r.Resolution == MetricResolution.Raw)); // only the recent one remains
        var five = rows.Where(r => r.Resolution == MetricResolution.FiveMinutes).OrderBy(r => r.Timestamp).ThenBy(r => r.ContainerId).ToList();
        Assert.Equal(3, five.Count);

        var host = five.Single(r => r.ContainerId is null && r.Timestamp == bucket);
        Assert.Equal(30, host.CpuPercent); // (10 + 20 + 60) / 3
        Assert.Equal(300, host.NetRxBytes); // a cumulative counter keeps its highest value
        Assert.Equal(100, host.MemoryUsedBytes);
        Assert.Equal(3, host.Load1);
        Assert.Equal(40, five.Single(r => r.ContainerId is null && r.Timestamp == bucket.AddMinutes(5)).CpuPercent);
        Assert.Equal(100, five.Single(r => r.ContainerId == "c1").CpuPercent);

        // Idempotent: a second pass finds nothing left to move.
        var again = await service.RunOnceAsync(now, default);
        Assert.Equal(0, again.RolledUpRaw);
        Assert.Equal(rows.Count, (await RowsAsync(serverId)).Count);
    }

    [RequiresDatabaseFact]
    public async Task Old_five_minute_rows_become_hourly_rows_and_old_hourly_rows_are_deleted()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var service = fixture.Get<MetricsRetentionService>();
        var now = new DateTimeOffset(2026, 10, 3, 12, 7, 0, TimeSpan.Zero);
        var hour = new DateTimeOffset(2026, 9, 10, 14, 0, 0, TimeSpan.Zero); // 23 days old: past the 14 day 5 minute retention

        await fixture.WithDbAsync(async db =>
        {
            db.MetricSamples.AddRange(
                Sample(serverId, hour.AddMinutes(5), MetricResolution.FiveMinutes, 10),
                Sample(serverId, hour.AddMinutes(10), MetricResolution.FiveMinutes, 30),
                Sample(serverId, hour.AddHours(-1).AddMinutes(5), MetricResolution.FiveMinutes, 50),
                Sample(serverId, now.AddDays(-400), MetricResolution.OneHour, 1),   // past the one year hourly retention
                Sample(serverId, now.AddDays(-30), MetricResolution.OneHour, 2));   // kept
            return await db.SaveChangesAsync();
        });

        var result = await service.RunOnceAsync(now, default);
        Assert.Equal(2, result.RolledUpFiveMinute); // two hourly buckets
        Assert.Equal(1, result.DeletedHourly);

        var rows = await RowsAsync(serverId);
        Assert.DoesNotContain(rows, r => r.Resolution == MetricResolution.FiveMinutes);
        var hourly = rows.Where(r => r.Resolution == MetricResolution.OneHour).OrderBy(r => r.Timestamp).ToList();
        Assert.Equal(3, hourly.Count);
        Assert.Equal(20, hourly.Single(r => r.Timestamp == hour).CpuPercent);
        Assert.Equal(50, hourly.Single(r => r.Timestamp == hour.AddHours(-1)).CpuPercent);
        Assert.Contains(hourly, r => r.CpuPercent == 2);
    }

    [RequiresDatabaseFact]
    public async Task Recent_samples_are_never_touched()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var now = DateTimeOffset.UtcNow;
        await fixture.WithDbAsync(async db =>
        {
            db.MetricSamples.AddRange(Sample(serverId, now.AddMinutes(-3), MetricResolution.Raw, 1), Sample(serverId, now.AddHours(-23), MetricResolution.Raw, 2));
            return await db.SaveChangesAsync();
        });
        await fixture.Get<MetricsRetentionService>().RunOnceAsync(now, default);
        Assert.Equal(2, (await RowsAsync(serverId)).Count(r => r.Resolution == MetricResolution.Raw));
    }
}

[Collection(GatewayCollection.Name)]
public sealed class TransportResolverTests(GatewayFixture fixture)
{
    private sealed class AllowFallback(bool allowed) : ISshFallbackPolicy
    {
        public ValueTask<bool> AllowsFallbackAsync(Guid serverId, CancellationToken cancellationToken) => ValueTask.FromResult(allowed);
    }

    private ServerTransportResolver Resolver(bool fallbackAllowed, params IServerTransport[] transports) =>
        new(transports, new AllowFallback(fallbackAllowed), fixture.Get<IServiceScopeFactory>());

    private static FakeServerTransport Agent(bool up = true) => new() { Kind = TransportKind.Agent, Available = up };

    private static FakeServerTransport Ssh(bool up = true, TransportCapabilities? capabilities = null) =>
        new() { Kind = TransportKind.Ssh, Available = up, Capabilities = capabilities ?? (TransportCapabilities.ContainerOps | TransportCapabilities.ImageOps | TransportCapabilities.VolumeNetworkOps) };

    [Fact]
    public async Task The_agent_is_preferred_when_it_is_connected()
    {
        var resolved = await Resolver(true, Ssh(), Agent()).ResolveAsync(Guid.NewGuid(), TransportCapabilities.ContainerOps, default);
        Assert.Equal(TransportKind.Agent, resolved.Kind);
        Assert.False(resolved.UsedFallback);
    }

    [Fact]
    public async Task Ssh_stands_in_when_the_agent_is_down_and_the_policy_allows_it()
    {
        var resolved = await Resolver(true, Agent(up: false), Ssh()).ResolveAsync(Guid.NewGuid(), TransportCapabilities.ContainerOps, default);
        Assert.Equal(TransportKind.Ssh, resolved.Kind);
        Assert.True(resolved.UsedFallback); // recorded on the job: "ran over SSH fallback"
    }

    [Fact]
    public async Task Ssh_is_not_used_when_the_server_may_not_fall_back_to_it()
    {
        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => Resolver(false, Agent(up: false), Ssh()).ResolveAsync(Guid.NewGuid(), TransportCapabilities.ContainerOps, default));
        Assert.Equal(TransportErrors.AgentUnavailable, ex.Code);
    }

    [Fact]
    public async Task A_capability_gap_fails_fast_instead_of_half_working()
    {
        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => Resolver(true, Agent(up: false), Ssh()).ResolveAsync(Guid.NewGuid(), TransportCapabilities.Builds, default));
        Assert.Equal(TransportErrors.Unsupported, ex.Code);
        Assert.Contains("agent", ex.Message);
    }

    [Fact]
    public async Task A_more_capable_transport_is_found_past_a_less_capable_one()
    {
        var resolved = await Resolver(true, Ssh(), Agent()).ResolveAsync(Guid.NewGuid(), TransportCapabilities.Builds, default);
        Assert.Equal(TransportKind.Agent, resolved.Kind);
    }

    [RequiresDatabaseFact]
    public async Task With_no_transport_the_answer_depends_on_whether_the_machine_answers()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var resolver = Resolver(true, Agent(up: false));
        Assert.Equal(TransportErrors.AgentUnavailable, (await Assert.ThrowsAsync<ServerTransportException>(() => resolver.ResolveAsync(serverId, TransportCapabilities.None, default))).Code);

        await fixture.Get<ServerStatusService>().ApplyAsync(serverId, (server, now) => { server.SetReachability(ReachabilityStatus.Unreachable, now); return []; });
        Assert.Equal(TransportErrors.Unreachable, (await Assert.ThrowsAsync<ServerTransportException>(() => resolver.ResolveAsync(serverId, TransportCapabilities.None, default))).Code);
    }

    [Fact]
    public async Task Executing_reports_how_the_command_was_carried()
    {
        var ssh = Ssh().On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => []);
        var outcome = await Resolver(true, Agent(up: false), ssh).ExecuteAsync<ContainerListCommand, IReadOnlyList<DockerContainer>>(
            Guid.NewGuid(), new ContainerListCommand(), CommandOptions.For("k"), default);
        Assert.True(outcome.Outcome.Succeeded);
        Assert.Equal(TransportKind.Ssh, outcome.Transport);
        Assert.True(outcome.UsedFallback);
        Assert.Single(ssh.Received<ContainerListCommand>());
    }

    [RequiresDatabaseFact]
    public async Task The_registered_resolver_picks_the_agent_transport_for_a_connected_server()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        var resolved = await fixture.Get<IServerTransportResolver>().ResolveAsync(serverId, TransportCapabilities.AllAgent, default);
        Assert.Equal(TransportKind.Agent, resolved.Kind);
    }
}

[Collection(GatewayCollection.Name)]
public sealed class MaintenanceJobTests(GatewayFixture fixture)
{
    private static JobContext Context(IServiceProvider services, Guid organizationId, object payload, int retryNo = 0)
    {
        var job = new Job { Type = "server.prune", OrganizationId = organizationId, PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), RetryNo = retryNo };
        return new JobContext(job, services, new NullLogSink(), new SecretRedactor(), CancellationToken.None);
    }

    private sealed class NullLogSink : ILogSink
    {
        public List<string> Lines { get; } = [];
        public string StreamId => "job:test";
        public ValueTask WriteAsync(Aethera.Domain.LogStream stream, string line, CancellationToken cancellationToken = default) { Lines.Add(line); return ValueTask.CompletedTask; }
        public ValueTask WriteSystemAsync(string line, CancellationToken cancellationToken = default) { Lines.Add(line); return ValueTask.CompletedTask; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [RequiresDatabaseFact]
    public async Task The_prune_job_sends_a_system_prune_with_the_chosen_scopes_and_reports_the_result()
    {
        var (org, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = Context(scope.ServiceProvider, org, new ServerPrunePayload { ServerId = serverId, DanglingImages = true, BuildCache = true, OlderThanHours = 24 });

        var run = new ServerPruneJobHandler().ExecuteAsync(context, CancellationToken.None);
        var command = await agent.NextCommandAsync();
        Assert.Equal(Aethera.Agent.V1.Command.RequestOneofCase.SystemPrune, command.RequestCase);
        Assert.True(command.SystemPrune.DanglingImages);
        Assert.True(command.SystemPrune.BuildCache);
        Assert.False(command.SystemPrune.Volumes);
        Assert.False(command.SystemPrune.StoppedContainers);
        Assert.Equal(TimeSpan.FromHours(24), command.SystemPrune.OlderThan.ToTimeSpan());
        Assert.Equal($"{context.JobId}:prune:0", command.IdempotencyKey); // ADR 0002: job id, step, retry number

        agent.Ack(command.CommandId);
        var result = new Aethera.Agent.V1.CommandResult { CommandId = command.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, Prune = new Aethera.Agent.V1.PruneResult { SpaceReclaimedBytes = 4096 } };
        result.Prune.Deleted.Add("image-1");
        agent.Result(result);
        await run;
        Assert.Contains("4096", string.Join('\n', ((NullLogSink)context.Log).Lines));
    }

    [RequiresDatabaseFact]
    public async Task A_prune_that_cannot_run_without_a_scope_fails_for_good()
    {
        var (org, serverId) = await fixture.SeedServerAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var ex = await Assert.ThrowsAsync<JobFailedException>(() => new ServerPruneJobHandler().ExecuteAsync(Context(scope.ServiceProvider, org, new ServerPrunePayload { ServerId = serverId }), CancellationToken.None));
        Assert.Equal("server.prune_no_scope", ex.Error.Code);
        Assert.False(ex.Error.Retryable);
    }

    [RequiresDatabaseFact]
    public async Task An_unavailable_agent_is_a_retryable_job_failure_and_a_full_disk_is_not()
    {
        var (org, serverId) = await fixture.SeedServerAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var payload = new ServerPrunePayload { ServerId = serverId, UnusedImages = true };
        var offline = await Assert.ThrowsAsync<JobFailedException>(() => new ServerPruneJobHandler().ExecuteAsync(Context(scope.ServiceProvider, org, payload), CancellationToken.None));
        Assert.Equal(TransportErrors.AgentUnavailable, offline.Error.Code);
        Assert.True(offline.Error.Retryable);

        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        var run = Assert.ThrowsAsync<JobFailedException>(() => new ServerPruneJobHandler().ExecuteAsync(Context(scope.ServiceProvider, org, payload), CancellationToken.None));
        var command = await agent.NextCommandAsync();
        agent.Ack(command.CommandId);
        agent.Result(new Aethera.Agent.V1.CommandResult { CommandId = command.CommandId, Status = Aethera.Agent.V1.CommandStatus.Failed, ErrorCode = Aethera.Agent.V1.ErrorCode.OutOfDisk, ErrorMessage = "no space left" });
        var failed = await run;
        Assert.Equal("server.out_of_disk", failed.Error.Code);
        Assert.False(failed.Error.Retryable);
    }

    [RequiresDatabaseFact]
    public async Task The_discovery_refresh_job_stores_the_fresh_report()
    {
        var (org, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = Context(scope.ServiceProvider, org, new ServerRefreshPayload { ServerId = serverId });

        var run = new ServerDiscoveryRefreshJobHandler().ExecuteAsync(context, CancellationToken.None);
        var command = await agent.NextCommandAsync();
        Assert.Equal(Aethera.Agent.V1.Command.RequestOneofCase.DiscoveryRefresh, command.RequestCase);
        agent.Ack(command.CommandId);
        var report = new Aethera.Agent.V1.DiscoveryReport { Host = new Aethera.Agent.V1.HostFacts { Hostname = "box", OsName = "Alpine", CpuCoresLogical = 2 }, Docker = new Aethera.Agent.V1.DockerInfo { Status = Aethera.Agent.V1.DockerStatus.Running, Version = "28.0.0" } };
        agent.Result(new Aethera.Agent.V1.CommandResult { CommandId = command.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, Discovery = report });
        await run;

        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).Facts.DockerVersion == "28.0.0", "discovery stored");
        Assert.Equal("Alpine", (await fixture.LoadServerAsync(serverId)).Facts.Os);
    }
}
