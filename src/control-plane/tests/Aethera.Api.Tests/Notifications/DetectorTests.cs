using System.Net;
using System.Text.Json;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Notifications;
using Aethera.Infrastructure.Notifications.Detectors;
using Aethera.Infrastructure.SelfOps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Tests.Notifications;

/// <summary>Each detector against seeded state with a fake clock: what it opens, what it keeps quiet about, and what resolves.</summary>
[Collection(NotificationsCollection.Name)]
public sealed class DetectorTests(NotificationsFixture fixture)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private sealed record Stack(Tenant Tenant, Guid Org, Guid ServerId, Guid EnvironmentId, Guid ProjectId, Guid AppId);

    private async Task<Stack> NewStackAsync()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, projectId, appId) = await tenant.CreateStackAsync();
        var stack = new Stack(tenant, tenant.Identity.OrganizationId, Guid.Parse(serverId), Guid.Parse(envId), Guid.Parse(projectId), Guid.Parse(appId));
        await fixture.Factory.WithDbAsync(async db =>
        {
            var server = await db.Servers.FirstAsync(s => s.Id == stack.ServerId);
            server.Lifecycle = ServerLifecycle.Active;
            await db.SaveChangesAsync();
        });
        await fixture.Factory.AddChannelAsync(stack.Org);
        return stack;
    }

    private Task SetServerAsync(Guid serverId, Action<Server> change) => fixture.Factory.WithDbAsync(async db =>
    {
        var server = await db.Servers.FirstAsync(s => s.Id == serverId);
        change(server);
        await db.SaveChangesAsync();
    });

    private Task SetWorkloadAsync(Guid id, Action<Workload> change) => fixture.Factory.WithDbAsync(async db =>
    {
        var workload = await db.Workloads.FirstAsync(w => w.Id == id);
        change(workload);
        await db.SaveChangesAsync();
    });

    private async Task<Alert?> OpenAlertAsync(Guid org, string key) =>
        (await fixture.Factory.AlertsAsync(org)).SingleOrDefault(a => a.DedupKey == key && a.State == AlertState.Open);

    private async Task<Alert?> AnyAlertAsync(Guid org, string key) =>
        (await fixture.Factory.AlertsAsync(org)).SingleOrDefault(a => a.DedupKey == key);

    // ---- servers -------------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task ServerUnreachable_NeedsMoreThanTwoMinutes_ThenRecoveryResolves()
    {
        var s = await NewStackAsync();
        var key = ServerHealthDetector.KeyOf(s.ServerId);
        var detector = fixture.Detector<ServerHealthDetector>();
        var down = fixture.Clock.UtcNow;
        await SetServerAsync(s.ServerId, server => server.SetAgentStatus(AgentStatus.Unavailable, down));

        fixture.Clock.Advance(TimeSpan.FromSeconds(110));
        await detector.RunAsync(default);
        Assert.Null(await AnyAlertAsync(s.Org, key));

        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await detector.RunAsync(default);
        var alert = await OpenAlertAsync(s.Org, key);
        Assert.NotNull(alert);
        Assert.Equal(NotificationEvents.ServerUnreachable, alert.Type);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(s.ServerId, alert.ResourceId);
        Assert.Single(await fixture.Factory.SendsAsync(s.Org));

        fixture.Clock.Advance(10 * Minute);
        await detector.RunAsync(default); // still down: a refresh, no second mail
        Assert.Single(await fixture.Factory.SendsAsync(s.Org));

        await SetServerAsync(s.ServerId, server => server.RecordHeartbeat(fixture.Clock.UtcNow));
        await detector.RunAsync(default);
        Assert.Equal(AlertState.Resolved, (await AnyAlertAsync(s.Org, key))!.State);
        var sends = await fixture.Factory.SendsAsync(s.Org);
        Assert.Equal(NotificationKinds.Resolved, sends[^1].Kind);
        Assert.Equal(NotificationEvents.ServerRecovered, sends[^1].Type);
    }

    [RequiresDatabaseFact]
    public async Task ServerUnreachable_IgnoresServersInMaintenanceAndThoseNeverConnected()
    {
        var s = await NewStackAsync();
        var detector = fixture.Detector<ServerHealthDetector>();
        var long_ago = fixture.Clock.UtcNow - TimeSpan.FromHours(1);
        await SetServerAsync(s.ServerId, server =>
        {
            server.SetAgentStatus(AgentStatus.Unavailable, long_ago);
            server.Lifecycle = ServerLifecycle.Maintenance;
        });
        await detector.RunAsync(default);
        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));

        await SetServerAsync(s.ServerId, server => server.Lifecycle = ServerLifecycle.Pending);
        await detector.RunAsync(default);
        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));
    }

    [RequiresDatabaseFact]
    public async Task ServerUnreachable_AnSshOnlyServerWhoseProbeFails_Counts()
    {
        var s = await NewStackAsync();
        await SetServerAsync(s.ServerId, server =>
        {
            server.SetAgentStatus(AgentStatus.NotInstalled, fixture.Clock.UtcNow - TimeSpan.FromHours(2));
            server.SetReachability(ReachabilityStatus.Unreachable, fixture.Clock.UtcNow - TimeSpan.FromMinutes(5));
        });

        await fixture.Detector<ServerHealthDetector>().RunAsync(default);

        Assert.NotNull(await OpenAlertAsync(s.Org, ServerHealthDetector.KeyOf(s.ServerId)));
    }

    [RequiresDatabaseFact]
    public async Task ServerAlert_OfADeletedServer_IsClosedQuietly()
    {
        var s = await NewStackAsync();
        var key = ServerHealthDetector.KeyOf(s.ServerId);
        await SetServerAsync(s.ServerId, server => server.SetAgentStatus(AgentStatus.Unavailable, fixture.Clock.UtcNow - TimeSpan.FromHours(1)));
        var detector = fixture.Detector<ServerHealthDetector>();
        await detector.RunAsync(default);
        Assert.NotNull(await OpenAlertAsync(s.Org, key));
        var sentBefore = (await fixture.Factory.SendsAsync(s.Org)).Count;

        await SetServerAsync(s.ServerId, server => server.MarkDeleted(fixture.Clock.UtcNow));
        await detector.RunAsync(default);

        Assert.Equal(AlertState.Resolved, (await AnyAlertAsync(s.Org, key))!.State);
        Assert.Equal(sentBefore, (await fixture.Factory.SendsAsync(s.Org)).Count);
    }

    // ---- disk and memory ---------------------------------------------------------------------------------------------------------------------

    private async Task SeedSamplesAsync(Guid serverId, double diskPercent, double memoryPercent, TimeSpan from, TimeSpan every)
    {
        var now = fixture.Clock.UtcNow;
        await fixture.Factory.WithDbAsync(async db =>
        {
            for (var at = from; at <= TimeSpan.Zero; at += every)
                db.MetricSamples.Add(new MetricSample
                {
                    ServerId = serverId, Timestamp = now + at, DiskUsedBytes = (long)(diskPercent * 10_000_000), DiskTotalBytes = 1_000_000_000,
                    MemoryUsedBytes = (long)(memoryPercent * 10_000_000), MemoryTotalBytes = 1_000_000_000,
                });
            await db.SaveChangesAsync();
        });
    }

    [RequiresDatabaseFact]
    public async Task Disk_MustStayAbove85PercentForFiveMinutes_AndResolvesWhenItDrops()
    {
        var s = await NewStackAsync();
        var key = ResourceUsageDetector.DiskPrefix + s.ServerId;
        var detector = fixture.Detector<ResourceUsageDetector>();

        // Only three minutes of high readings: not yet.
        await SeedSamplesAsync(s.ServerId, 90, 10, -3 * Minute, TimeSpan.FromSeconds(30));
        await detector.RunAsync(default);
        Assert.Null(await AnyAlertAsync(s.Org, key));

        // Six minutes of readings, all high.
        fixture.Clock.Advance(3 * Minute);
        await SeedSamplesAsync(s.ServerId, 90, 10, -3 * Minute, TimeSpan.FromSeconds(30));
        await detector.RunAsync(default);
        var alert = await OpenAlertAsync(s.Org, key);
        Assert.NotNull(alert);
        Assert.Equal(NotificationEvents.ServerDiskHigh, alert.Type);
        Assert.Contains("90", alert.Title);
        Assert.Null(await AnyAlertAsync(s.Org, ResourceUsageDetector.MemoryPrefix + s.ServerId));
        Assert.Single(await fixture.Factory.SendsAsync(s.Org));

        // Back under the threshold.
        fixture.Clock.Advance(2 * Minute);
        await SeedSamplesAsync(s.ServerId, 60, 10, -TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
        await detector.RunAsync(default);
        Assert.Equal(AlertState.Resolved, (await AnyAlertAsync(s.Org, key))!.State);
        Assert.Equal(NotificationKinds.Resolved, (await fixture.Factory.SendsAsync(s.Org))[^1].Kind);
    }

    [RequiresDatabaseFact]
    public async Task Memory_UsesItsOwnThreshold_AndOneDipBreaksTheStreak()
    {
        var s = await NewStackAsync();
        var detector = fixture.Detector<ResourceUsageDetector>();

        // 88 % memory is fine for memory (limit 90) even though it would not be for disk.
        await SeedSamplesAsync(s.ServerId, 10, 88, -6 * Minute, TimeSpan.FromSeconds(30));
        await detector.RunAsync(default);
        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));

        fixture.Clock.Advance(7 * Minute);
        await SeedSamplesAsync(s.ServerId, 10, 95, -6 * Minute, TimeSpan.FromSeconds(30));
        await fixture.Factory.WithDbAsync(async db =>
        {
            // One reading in the middle of the window drops below the limit.
            db.MetricSamples.Add(new MetricSample { ServerId = s.ServerId, Timestamp = fixture.Clock.UtcNow - 3 * Minute + TimeSpan.FromSeconds(5), MemoryUsedBytes = 500_000_000, MemoryTotalBytes = 1_000_000_000 });
            await db.SaveChangesAsync();
        });
        await detector.RunAsync(default);
        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));

        fixture.Clock.Advance(7 * Minute);
        await SeedSamplesAsync(s.ServerId, 10, 95, -6 * Minute, TimeSpan.FromSeconds(30));
        await detector.RunAsync(default);
        Assert.Equal(NotificationEvents.ServerMemoryHigh, (await OpenAlertAsync(s.Org, ResourceUsageDetector.MemoryPrefix + s.ServerId))!.Type);
    }

    [RequiresDatabaseFact]
    public async Task Usage_StaleSamples_NeitherOpenNorResolve()
    {
        var s = await NewStackAsync();
        var key = ResourceUsageDetector.DiskPrefix + s.ServerId;
        var detector = fixture.Detector<ResourceUsageDetector>();
        await SeedSamplesAsync(s.ServerId, 92, 10, -6 * Minute, TimeSpan.FromSeconds(30));
        await detector.RunAsync(default);
        Assert.NotNull(await OpenAlertAsync(s.Org, key));

        fixture.Clock.Advance(10 * Minute); // the agent stopped reporting
        await detector.RunAsync(default);
        Assert.NotNull(await OpenAlertAsync(s.Org, key));
    }

    // ---- containers -----------------------------------------------------------------------------------------------------------------------------

    private ContainerDetector NewContainerDetector() => new(
        fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Factory.Services.GetRequiredService<Notifier>(), fixture.Clock,
        fixture.Factory.Services.GetRequiredService<IOptions<NotificationOptions>>());

    private async Task AddEventAsync(Guid workloadId, string kind, DateTimeOffset at, string? detail = "web-1234 exit=137") =>
        await fixture.Factory.WithDbAsync(async db =>
        {
            db.ResourceEvents.Add(new ResourceEvent { ResourceType = "workload", ResourceId = workloadId, Kind = kind, Detail = detail, OccurredAt = at });
            await db.SaveChangesAsync();
        });

    [RequiresDatabaseFact]
    public async Task Container_DiedWhileItShouldRun_OpensAnAlert_AndStartedResolvesIt()
    {
        var s = await NewStackAsync();
        var key = ContainerDetector.StoppedPrefix + s.AppId;
        var detector = NewContainerDetector();
        await SetWorkloadAsync(s.AppId, w => { w.DesiredState = DesiredState.Running; w.Status = WorkloadStatus.Running; w.StatusChangedAt = fixture.Clock.UtcNow - TimeSpan.FromHours(1); });
        await detector.RunAsync(default); // sets the watermark

        fixture.Clock.Advance(Minute);
        await AddEventAsync(s.AppId, "container.died", fixture.Clock.UtcNow - TimeSpan.FromSeconds(10));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await detector.RunAsync(default);

        var alert = await OpenAlertAsync(s.Org, key);
        Assert.NotNull(alert);
        Assert.Equal(NotificationEvents.ContainerStopped, alert.Type);
        Assert.Equal(s.AppId, alert.ResourceId);
        Assert.Single(await fixture.Factory.SendsAsync(s.Org));

        fixture.Clock.Advance(Minute);
        await AddEventAsync(s.AppId, "container.started", fixture.Clock.UtcNow - TimeSpan.FromSeconds(10), "web-1234");
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await detector.RunAsync(default);
        Assert.Equal(AlertState.Resolved, (await AnyAlertAsync(s.Org, key))!.State);
    }

    [RequiresDatabaseFact]
    public async Task Container_OomKill_IsNamedAsSuch()
    {
        var s = await NewStackAsync();
        var detector = NewContainerDetector();
        await SetWorkloadAsync(s.AppId, w => { w.DesiredState = DesiredState.Running; w.Status = WorkloadStatus.Running; w.StatusChangedAt = fixture.Clock.UtcNow - TimeSpan.FromHours(1); });
        await detector.RunAsync(default);

        fixture.Clock.Advance(Minute);
        await AddEventAsync(s.AppId, "container.oom", fixture.Clock.UtcNow - TimeSpan.FromSeconds(10), "web-1234 exit=137 oom");
        await detector.RunAsync(default);

        var alert = await OpenAlertAsync(s.Org, ContainerDetector.StoppedPrefix + s.AppId);
        Assert.Contains("out of memory", alert!.Title);
    }

    [RequiresDatabaseFact]
    public async Task Container_StopsThatAreExpected_AreIgnored()
    {
        var s = await NewStackAsync();
        var detector = NewContainerDetector();
        await SetWorkloadAsync(s.AppId, w => { w.DesiredState = DesiredState.Running; w.Status = WorkloadStatus.Running; w.StatusChangedAt = fixture.Clock.UtcNow; });
        await detector.RunAsync(default);

        // Before the workload's last status change (a deployment or restart that we did): ignored.
        fixture.Clock.Advance(Minute);
        await AddEventAsync(s.AppId, "container.died", fixture.Clock.UtcNow - TimeSpan.FromSeconds(50));
        await SetWorkloadAsync(s.AppId, w => w.StatusChangedAt = fixture.Clock.UtcNow - TimeSpan.FromSeconds(5));
        await detector.RunAsync(default);
        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));

        // The user stopped the workload.
        fixture.Clock.Advance(Minute);
        await SetWorkloadAsync(s.AppId, w => { w.DesiredState = DesiredState.Stopped; w.Status = WorkloadStatus.Stopped; });
        await AddEventAsync(s.AppId, "container.stopped", fixture.Clock.UtcNow - TimeSpan.FromSeconds(10), "web-1234 exit=0");
        await detector.RunAsync(default);
        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));

        // A deployment is in progress.
        fixture.Clock.Advance(Minute);
        await SetWorkloadAsync(s.AppId, w => { w.DesiredState = DesiredState.Running; w.Status = WorkloadStatus.Deploying; w.StatusChangedAt = fixture.Clock.UtcNow - TimeSpan.FromHours(1); });
        await AddEventAsync(s.AppId, "container.died", fixture.Clock.UtcNow - TimeSpan.FromSeconds(10));
        await detector.RunAsync(default);
        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));
    }

    [RequiresDatabaseFact]
    public async Task Container_RestartLoop_RaisesOneAlert_AndTheDeathsInsideItAreNotReportedTwice()
    {
        var s = await NewStackAsync();
        var detector = NewContainerDetector();
        await SetWorkloadAsync(s.AppId, w => { w.DesiredState = DesiredState.Running; w.Status = WorkloadStatus.Running; w.StatusChangedAt = fixture.Clock.UtcNow - TimeSpan.FromHours(1); });
        await detector.RunAsync(default);

        fixture.Clock.Advance(Minute);
        for (var i = 0; i < 3; i++)
        {
            var at = fixture.Clock.UtcNow - TimeSpan.FromSeconds(40 - i * 10);
            await AddEventAsync(s.AppId, "container.died", at);
            await AddEventAsync(s.AppId, "container.restarting", at + TimeSpan.FromSeconds(1), "web-1234");
        }

        await detector.RunAsync(default);

        Assert.NotNull(await OpenAlertAsync(s.Org, ContainerDetector.RestartPrefix + s.AppId));
        Assert.Null(await AnyAlertAsync(s.Org, ContainerDetector.StoppedPrefix + s.AppId));
        Assert.Equal(NotificationEvents.ContainerRestarting, (await fixture.Factory.SendsAsync(s.Org)).Single().Type);

        // Ten quiet minutes: the loop is over.
        fixture.Clock.Advance(11 * Minute);
        await detector.RunAsync(default);
        Assert.Equal(AlertState.Resolved, (await AnyAlertAsync(s.Org, ContainerDetector.RestartPrefix + s.AppId))!.State);
    }

    [RequiresDatabaseFact]
    public async Task Container_TwoRestarts_AreNotALoop()
    {
        var s = await NewStackAsync();
        var detector = NewContainerDetector();
        await SetWorkloadAsync(s.AppId, w => { w.DesiredState = DesiredState.Running; w.Status = WorkloadStatus.Running; w.StatusChangedAt = fixture.Clock.UtcNow - TimeSpan.FromHours(1); });
        await detector.RunAsync(default);
        fixture.Clock.Advance(Minute);
        for (var i = 0; i < 2; i++) await AddEventAsync(s.AppId, "container.restarting", fixture.Clock.UtcNow - TimeSpan.FromSeconds(30 - i * 10), "web-1234");

        await detector.RunAsync(default);

        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));
    }

    // ---- certificates -----------------------------------------------------------------------------------------------------------------------------

    private async Task<(Guid DomainId, string Host)> AddLiveDomainAsync(Stack s, bool https = true, string? host = null)
    {
        host ??= $"app-{Guid.NewGuid():N}".Substring(0, 12) + ".example.com";
        Guid domainId = default;
        await fixture.Factory.WithDbAsync(async db =>
        {
            var deployment = Deployment.Queue(s.AppId, s.EnvironmentId, s.ServerId, 1, DeploymentTrigger.Manual, fixture.Clock.UtcNow);
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
            var workload = await db.Workloads.FirstAsync(w => w.Id == s.AppId);
            workload.CurrentDeploymentId = deployment.Id;
            workload.DesiredState = DesiredState.Running;
            var domain = new WorkloadDomain { WorkloadId = s.AppId, ServerId = s.ServerId, HttpsEnabled = https };
            domain.SetHostname(host);
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            domainId = domain.Id;
        });
        return (domainId, host);
    }

    [RequiresDatabaseFact]
    public async Task Certificate_ExpiringWithinTwoWeeks_Alerts_AndRenewalResolves()
    {
        var s = await NewStackAsync();
        var (domainId, host) = await AddLiveDomainAsync(s);
        var detector = fixture.Detector<CertificateDetector>();
        var key = CertificateDetector.ExpiringPrefix + domainId;

        fixture.Probe.Results[host] = new CertificateProbeResult(true, fixture.Clock.UtcNow + TimeSpan.FromDays(30));
        await detector.RunAsync(default);
        Assert.Null(await AnyAlertAsync(s.Org, key));
        var domain = await fixture.Factory.WithDbAsync(db => db.Domains.AsNoTracking().SingleAsync(d => d.Id == domainId));
        Assert.Equal(CertificateStatus.Issued, domain.CertificateStatus);
        Assert.NotNull(domain.CertificateExpiresAt);
        Assert.Contains($"{host}:443", fixture.Probe.Probed);

        fixture.Clock.Advance(TimeSpan.FromDays(17));
        await detector.RunAsync(default);
        var alert = await OpenAlertAsync(s.Org, key);
        Assert.NotNull(alert);
        Assert.Equal(NotificationEvents.CertificateExpiring, alert.Type);
        Assert.Contains("13 day", alert.Title);
        Assert.Single(await fixture.Factory.SendsAsync(s.Org));

        fixture.Probe.Results[host] = new CertificateProbeResult(true, fixture.Clock.UtcNow + TimeSpan.FromDays(89));
        await detector.RunAsync(default);
        Assert.Equal(AlertState.Resolved, (await AnyAlertAsync(s.Org, key))!.State);
    }

    [RequiresDatabaseFact]
    public async Task Certificate_Error_AlertsOnlyAfter24Hours_AndRecoveryResolves()
    {
        var s = await NewStackAsync();
        var (domainId, host) = await AddLiveDomainAsync(s);
        var detector = fixture.Detector<CertificateDetector>();
        var key = CertificateDetector.FailedPrefix + domainId;

        fixture.Probe.Results[host] = new CertificateProbeResult(true, fixture.Clock.UtcNow + TimeSpan.FromDays(90), "The certificate is not trusted (self-signed, unknown issuer or expired).");
        await detector.RunAsync(default);
        var domain = await fixture.Factory.WithDbAsync(db => db.Domains.AsNoTracking().SingleAsync(d => d.Id == domainId));
        Assert.Equal(CertificateStatus.Failed, domain.CertificateStatus);
        Assert.Contains("not trusted", domain.CertificateError);
        Assert.Null(await AnyAlertAsync(s.Org, key));

        fixture.Clock.Advance(TimeSpan.FromHours(23));
        await detector.RunAsync(default);
        Assert.Null(await AnyAlertAsync(s.Org, key));

        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await detector.RunAsync(default);
        var alert = await OpenAlertAsync(s.Org, key);
        Assert.NotNull(alert);
        Assert.Equal(NotificationEvents.CertificateFailed, alert.Type);

        fixture.Probe.Results[host] = new CertificateProbeResult(true, fixture.Clock.UtcNow + TimeSpan.FromDays(90));
        await detector.RunAsync(default);
        Assert.Equal(AlertState.Resolved, (await AnyAlertAsync(s.Org, key))!.State);
        Assert.Equal(CertificateStatus.Issued, (await fixture.Factory.WithDbAsync(db => db.Domains.AsNoTracking().SingleAsync(d => d.Id == domainId))).CertificateStatus);
    }

    [RequiresDatabaseFact]
    public async Task Certificate_ExpiredCertificate_IsRecordedAsExpired_AndAlerts()
    {
        var s = await NewStackAsync();
        var (domainId, host) = await AddLiveDomainAsync(s);
        fixture.Probe.Results[host] = new CertificateProbeResult(true, fixture.Clock.UtcNow - TimeSpan.FromDays(1));

        await fixture.Detector<CertificateDetector>().RunAsync(default);

        Assert.Equal(CertificateStatus.Expired, (await fixture.Factory.WithDbAsync(db => db.Domains.AsNoTracking().SingleAsync(d => d.Id == domainId))).CertificateStatus);
        Assert.Contains("expired", (await OpenAlertAsync(s.Org, CertificateDetector.ExpiringPrefix + domainId))!.Title);
    }

    [RequiresDatabaseFact]
    public async Task Certificate_NoAnswer_ChangesNothing_AndOnlyLiveHttpsDomainsAreProbed()
    {
        var s = await NewStackAsync();
        var (domainId, host) = await AddLiveDomainAsync(s);
        var wildcard = "*.example.net";
        await fixture.Factory.WithDbAsync(async db =>
        {
            var plain = new WorkloadDomain { WorkloadId = s.AppId, ServerId = s.ServerId, HttpsEnabled = false };
            plain.SetHostname("plain-" + Guid.NewGuid().ToString("N")[..8] + ".example.com");
            var star = new WorkloadDomain { WorkloadId = s.AppId, ServerId = s.ServerId, HttpsEnabled = true };
            star.SetHostname(wildcard);
            db.Domains.AddRange(plain, star);
            await db.SaveChangesAsync();
        });
        fixture.Probe.Probed.Clear();

        await fixture.Detector<CertificateDetector>().RunAsync(default); // no result registered: "no answer"

        Assert.Equal(CertificateStatus.None, (await fixture.Factory.WithDbAsync(db => db.Domains.AsNoTracking().SingleAsync(d => d.Id == domainId))).CertificateStatus);
        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));
        Assert.Contains($"{host}:443", fixture.Probe.Probed);
        Assert.DoesNotContain(fixture.Probe.Probed, p => p.StartsWith("plain-", StringComparison.Ordinal) || p.StartsWith('*'));
    }

    [RequiresDatabaseFact]
    public async Task Certificate_DetectorRunsOncePerDay_WhenScheduled()
    {
        var detector = fixture.Detector<CertificateDetector>();
        Assert.Equal(TimeSpan.FromHours(24), detector.Interval);
        var service = new MonitoringDetectorService([new CountingDetector(TimeSpan.FromHours(24))], fixture.Factory.Services.GetRequiredService<IOptions<NotificationOptions>>(), fixture.Clock, NullLogger<MonitoringDetectorService>.Instance);
        await service.RunDueAsync(default);
        await service.RunDueAsync(default);
        fixture.Clock.Advance(TimeSpan.FromHours(23));
        await service.RunDueAsync(default);
        Assert.Equal(1, CountingDetector.Runs);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await service.RunDueAsync(default);
        Assert.Equal(2, CountingDetector.Runs);
    }

    private sealed class CountingDetector(TimeSpan interval) : IDetector
    {
        public static int Runs;
        public string Name => "counting";
        public TimeSpan Interval { get; } = interval;
        public Task RunAsync(CancellationToken cancellationToken) { Runs++; return Task.CompletedTask; }
    }

    // ---- updates ------------------------------------------------------------------------------------------------------------------------------

    private sealed class ReleaseHandler(Func<string> tag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { tag_name = tag(), html_url = "https://example.com/release", draft = false, prerelease = false, assets = Array.Empty<object>() })),
            });
    }

    [RequiresDatabaseFact]
    public async Task Update_IsAnnouncedOncePerVersion_AndOlderAlertsCloseQuietly()
    {
        var tenant = await fixture.NewTenantAsync();
        var org = tenant.Identity.OrganizationId;
        await fixture.Factory.AddChannelAsync(org, [NotificationEvents.UpdateAvailable]);
        var latest = "9.1.0";
        var checker = new ReleaseChecker(new HttpClient(new ReleaseHandler(() => "v" + latest)), Options.Create(new UpdateOptions()), fixture.Clock, NullLogger<ReleaseChecker>.Instance) { CurrentVersion = "0.1.0" };
        var detector = new UpdateDetector(fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Factory.Services.GetRequiredService<Notifier>(), checker);

        await detector.RunAsync(default); // nothing checked yet
        Assert.Empty(await fixture.Factory.AlertsAsync(org));

        await checker.CheckAsync(default);
        await detector.RunAsync(default);
        await detector.RunAsync(default);
        fixture.Clock.Advance(TimeSpan.FromHours(30));
        await detector.RunAsync(default);

        var alert = Assert.Single(await fixture.Factory.AlertsAsync(org));
        Assert.Equal("update:9.1.0", alert.DedupKey);
        Assert.Equal(AlertSeverity.Info, alert.Severity);
        Assert.Contains("9.1.0", alert.Title);
        var send = Assert.Single(await fixture.Factory.SendsAsync(org));
        Assert.Equal(NotificationEvents.UpdateAvailable, send.Type);

        latest = "9.2.0";
        await checker.CheckAsync(default);
        await detector.RunAsync(default);
        var alerts = await fixture.Factory.AlertsAsync(org);
        Assert.Equal(2, alerts.Count);
        Assert.Equal(AlertState.Resolved, alerts.Single(a => a.DedupKey == "update:9.1.0").State);
        Assert.Equal(AlertState.Open, alerts.Single(a => a.DedupKey == "update:9.2.0").State);
        Assert.Equal(2, (await fixture.Factory.SendsAsync(org)).Count); // 9.1.0 closing sent nothing
    }

    // ---- deployments --------------------------------------------------------------------------------------------------------------------------

    private async Task<Guid> AddDeploymentAsync(Stack s, int number, bool failed)
    {
        Guid id = default;
        await fixture.Factory.WithDbAsync(async db =>
        {
            var now = fixture.Clock.UtcNow;
            var d = Deployment.Queue(s.AppId, s.EnvironmentId, s.ServerId, number, DeploymentTrigger.Manual, now);
            d.Start("{}", now);
            if (failed) d.MarkFailed(DeploymentStep.Build, "build.failed", "The build exited with code 1.", now);
            else d.MarkRunning(now);
            db.Deployments.Add(d);
            await db.SaveChangesAsync();
            id = d.Id;
        });
        return id;
    }

    [RequiresDatabaseFact]
    public async Task Deployment_FailureOpensAnAlertForTheWorkload_AndASuccessResolvesIt()
    {
        var s = await NewStackAsync();
        await fixture.Factory.AddChannelAsync(s.Org, [NotificationEvents.DeploymentFailed, NotificationEvents.DeploymentSucceeded]);
        var key = DeploymentNotifications.AlertKey(s.AppId);

        var failed = await AddDeploymentAsync(s, 1, failed: true);
        await DeploymentNotifications.PublishOutcomeAsync(fixture.Factory.Services, failed);
        var alert = await OpenAlertAsync(s.Org, key);
        Assert.NotNull(alert);
        Assert.Equal(NotificationEvents.DeploymentFailed, alert.Type);
        Assert.Contains("#1", alert.Title);
        Assert.Equal("The build exited with code 1.", alert.Message);
        var first = (await fixture.Factory.SendsAsync(s.Org)).Where(p => p.Kind == NotificationKinds.Opened).ToList();
        Assert.Equal(2, first.Count); // the default channel and the one that also takes success
        Assert.All(first, p => Assert.Equal($"/deployments/{failed}", p.Path));

        var ok = await AddDeploymentAsync(s, 2, failed: false);
        await DeploymentNotifications.PublishOutcomeAsync(fixture.Factory.Services, ok);
        Assert.Equal(AlertState.Resolved, (await AnyAlertAsync(s.Org, key))!.State);
        Assert.Contains(await fixture.Factory.SendsAsync(s.Org), p => p.Kind == NotificationKinds.Resolved);
    }

    [RequiresDatabaseFact]
    public async Task Deployment_UnfinishedOrUnknown_PublishesNothing_AndNeverThrows()
    {
        var s = await NewStackAsync();
        await fixture.Factory.WithDbAsync(async db =>
        {
            var d = Deployment.Queue(s.AppId, s.EnvironmentId, s.ServerId, 7, DeploymentTrigger.Manual, fixture.Clock.UtcNow);
            d.Start("{}", fixture.Clock.UtcNow);
            db.Deployments.Add(d);
            await db.SaveChangesAsync();
            await DeploymentNotifications.PublishOutcomeAsync(fixture.Factory.Services, d.Id);
        });
        await DeploymentNotifications.PublishOutcomeAsync(fixture.Factory.Services, Guid.NewGuid());

        Assert.Empty(await fixture.Factory.AlertsAsync(s.Org));
    }
}
