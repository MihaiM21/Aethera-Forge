using Aethera.Api.Tests.Agents;
using Aethera.Domain;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Tests.Ssh;

/// <summary>Metrics polling over SSH: the same samples an agent would send, "degraded: polling over SSH" state, and who gets polled.</summary>
[Collection("sshd")]
public sealed class SshMetricsPollerTests : IAsyncLifetime
{
    private SshTestHost? _host;
    private SshTestHost Host => _host!;

    public async Task InitializeAsync() => _host = await SshTestHost.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private SshMetricsPoller Poller => Host.Get<SshMetricsPoller>();

    private static readonly string ContainerId = new('c', 64);

    private static string PollOutput(double load = 0.5) => string.Join('\n',
        "##cpu", "cpu  1000 0 500 8000 0 0 0 0 0 0", "cpu  1100 0 540 8060 0 0 0 0 0 0",
        "##mem", "MemTotal: 2000000 kB", "MemAvailable: 500000 kB", "SwapTotal: 0 kB", "SwapFree: 0 kB",
        "##load", $"{load.ToString(System.Globalization.CultureInfo.InvariantCulture)} 0.40 0.30 1/100 1",
        "##uptime", "1000.5 2000.0", "##net", "  eth0: 1000 1 0 0 0 0 0 0 2000 1 0 0 0 0 0 0",
        "##df", "Filesystem 1024-blocks Used Available Capacity Mounted on", "/dev/vda1 10000 4000 6000 40% /",
        "##nproc", "2", "##docker", "27.3.1",
        "##stats", $$"""{"ID":"{{ContainerId}}","Name":"web","CPUPerc":"3.50%","MemUsage":"10MiB / 100MiB","NetIO":"1kB / 2kB","BlockIO":"0B / 0B","PIDs":"3"}""",
        "##ps", $$"""{"ID":"{{ContainerId}}","Names":"web","State":"running","Labels":"x=y"}""");

    [Fact]
    public void The_default_cadence_is_thirty_seconds_and_the_script_a_single_read_only_exec()
    {
        Assert.Equal(30, new SshOptions().MetricsPollSeconds);
        Assert.StartsWith("sh -c '", SshMetricsScript.Line);
    }

    [RequiresDatabaseFact]
    public async Task A_poll_stores_host_and_container_samples_and_the_docker_and_reachability_axes()
    {
        var id = await Host.SeedServerAsync();
        Host.Connector!.Connection.Handler = c => c.Line == SshMetricsScript.Line ? new RemoteResult(0, PollOutput(), "") : new RemoteResult(0, "", "");

        Assert.True(await Poller.PollServerAsync(id, CancellationToken.None));

        var rows = await Host.WithDbAsync(db => db.MetricSamples.AsNoTracking().Where(m => m.ServerId == id).ToListAsync());
        Assert.Equal(2, rows.Count);
        var host = rows.Single(r => r.ContainerId is null);
        Assert.Equal(MetricResolution.Raw, host.Resolution);
        Assert.Equal(100.0 * (200 - 60) / 200, host.CpuPercent!.Value, 6); // (total 200 ticks, 60 idle)
        Assert.Equal(2000000L * 1024, host.MemoryTotalBytes);
        Assert.Equal(1500000L * 1024, host.MemoryUsedBytes);
        Assert.Equal(4000L * 1024, host.DiskUsedBytes);
        Assert.Equal(1000, host.NetRxBytes);
        Assert.Equal(0.5, host.Load1);
        var container = rows.Single(r => r.ContainerId is not null);
        Assert.Equal(ContainerId, container.ContainerId);
        Assert.Equal(3.5, container.CpuPercent);
        Assert.Equal(10L * 1024 * 1024, container.MemoryUsedBytes);

        var server = await Host.LoadServerAsync(id);
        Assert.Equal(DockerStatus.Running, server.DockerStatus);
        Assert.Equal(ReachabilityStatus.Reachable, server.ReachabilityStatus);

        var state = await Host.Get<SshSettingsStore>().GetAsync<SshPollingState>(SshSettingsStore.PollingKey(id), CancellationToken.None);
        Assert.NotNull(state!.LastSuccessAt);
        Assert.Null(state.LastError);
        Assert.NotNull(state.Since);
    }

    [RequiresDatabaseFact]
    public async Task A_failed_poll_records_why_and_keeps_the_last_success()
    {
        var id = await Host.SeedServerAsync();
        Host.Connector!.Connection.Handler = _ => new RemoteResult(0, PollOutput(), "");
        Assert.True(await Poller.PollServerAsync(id, CancellationToken.None));
        var first = await Host.Get<SshSettingsStore>().GetAsync<SshPollingState>(SshSettingsStore.PollingKey(id), CancellationToken.None);

        await Host.Get<SshConnectionPool>().EvictAsync(id);
        Host.Connector.Failure = new SshConnectException("The server could not be reached (ConnectionRefused).");
        Assert.False(await Poller.PollServerAsync(id, CancellationToken.None));

        var state = await Host.Get<SshSettingsStore>().GetAsync<SshPollingState>(SshSettingsStore.PollingKey(id), CancellationToken.None);
        Assert.Equal(first!.LastSuccessAt, state!.LastSuccessAt);
        Assert.Equal(first.Since, state.Since);
        Assert.Contains("could not be reached", state.LastError);
        Assert.True(state.LastAttemptAt >= state.LastSuccessAt);
    }

    [RequiresDatabaseFact]
    public async Task A_command_failure_on_the_host_is_a_failed_poll_not_an_exception()
    {
        var id = await Host.SeedServerAsync();
        Host.Connector!.Connection.Handler = _ => new RemoteResult(2, "", "sh: 1: cat: not found");
        Assert.False(await Poller.PollServerAsync(id, CancellationToken.None));
        var state = await Host.Get<SshSettingsStore>().GetAsync<SshPollingState>(SshSettingsStore.PollingKey(id), CancellationToken.None);
        Assert.Contains("metrics commands failed", state!.LastError);
    }

    [RequiresDatabaseFact]
    public async Task Only_servers_that_need_the_fallback_are_polled()
    {
        var polled = await Host.SeedServerAsync(host: "198.51.100.1");
        var noCredential = await Host.SeedServerAsync(host: "198.51.100.2", withCredential: false);
        var switchedOff = await Host.SeedServerAsync(host: "198.51.100.3");
        var disabled = await Host.SeedServerAsync(host: "198.51.100.4");
        var withAgent = await Host.SeedServerAsync(host: "198.51.100.5");
        await Host.Get<SshSettingsStore>().SetAsync(SshSettingsStore.FallbackKey(switchedOff), new SshFallbackSetting(false), CancellationToken.None);
        await Host.WithDbAsync(async db =>
        {
            (await db.Servers.FirstAsync(s => s.Id == disabled)).Lifecycle = ServerLifecycle.Disabled;
            await db.SaveChangesAsync();
            return 0;
        });
        Host.Connector!.Connection.Handler = _ => new RemoteResult(0, PollOutput(), "");

        await using var agent = FakeAgent.Start(Host.Services, withAgent);
        await GatewayFixture.EventuallyAsync(() => Task.FromResult(Host.Get<AgentSessionRegistry>().IsConnected(withAgent)), "the agent session");

        await Poller.PollAllAsync(CancellationToken.None);

        var hosts = Host.Connector.Attempts.Select(a => a.Target.Host).Distinct().ToList();
        Assert.Equal(["198.51.100.1"], hosts);
        Assert.NotEmpty(await Host.WithDbAsync(db => db.MetricSamples.AsNoTracking().Where(m => m.ServerId == polled).ToListAsync()));
        foreach (var other in new[] { noCredential, switchedOff, disabled })
            Assert.Empty(await Host.WithDbAsync(db => db.MetricSamples.AsNoTracking().Where(m => m.ServerId == other).ToListAsync()));
    }

    [RequiresSshdFact]
    public async Task Polling_a_real_host_reads_proc_df_and_the_docker_cli()
    {
        if (!TestDatabase.IsConfigured) return;
        await using var host = await SshTestHost.CreateAsync(fakeConnector: false);
        var id = await host!.SeedServerAsync(SshdEndpoint.Password, SshdEndpoint.Host, SshdEndpoint.Port, "root");
        var poller = host.Get<SshMetricsPoller>();

        Assert.True(await poller.PollServerAsync(id, CancellationToken.None));
        var rows = await host.WithDbAsync(db => db.MetricSamples.AsNoTracking().Where(m => m.ServerId == id).ToListAsync());
        var hostRow = rows.Single(r => r.ContainerId is null);
        Assert.InRange(hostRow.CpuPercent!.Value, 0, 100);
        Assert.True(hostRow.MemoryTotalBytes > 100_000_000, "meminfo was parsed");
        Assert.True(hostRow.MemoryUsedBytes > 0);
        Assert.True(hostRow.DiskTotalBytes > 0, "df -P was parsed");
        Assert.NotNull(hostRow.Load1);
        Assert.Contains(rows, r => r.ContainerId == new string('a', 64)); // from the docker stand-in's `docker stats` and `docker ps`
        Assert.Equal(DockerStatus.Running, (await host.LoadServerAsync(id)).DockerStatus);
    }
}
