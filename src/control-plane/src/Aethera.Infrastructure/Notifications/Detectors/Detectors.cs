using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Notifications.Detectors;

/// <summary>
/// Looks at existing state (server status, metric samples, resource events, domains, the release checker) and reports what deserves an alert to
/// the <see cref="INotifier"/>. Detectors are idempotent: every run states what is wrong now; the alert lifecycle de-duplicates and decides who is
/// told. Each run opens its own scope, so they are plain singletons that tests call directly with a fake clock.
/// </summary>
public interface IDetector
{
    string Name { get; }

    /// <summary>Minimum time between runs; zero = every tick of the detector loop.</summary>
    TimeSpan Interval { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Runs the registered detectors on a timer. A failing detector is logged and tried again; it never stops the others.</summary>
public sealed class MonitoringDetectorService(
    IEnumerable<IDetector> detectors, IOptions<NotificationOptions> options, IClock clock, ILogger<MonitoringDetectorService> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _lastRun = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (AetheraHost.IsOpenApiGeneration || !options.Value.DetectorsEnabled) return;
        try
        {
            // Not at the very first second: start-up (migrations, agent reconnects) has priority, and a crash loop must not spam alerts.
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, options.Value.DetectorIntervalSeconds)), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(0.05, options.Value.DetectorIntervalSeconds)));
            do
            {
                await RunDueAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One pass over the detectors that are due. Public for tests.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        foreach (var detector in detectors)
        {
            var now = clock.UtcNow;
            if (_lastRun.TryGetValue(detector.Name, out var last) && now - last < detector.Interval) continue;
            _lastRun[detector.Name] = now;
            try
            {
                await detector.RunAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The {Detector} detector failed", detector.Name);
            }
        }
    }
}

/// <summary>Small queries every detector needs.</summary>
internal static class DetectorSupport
{
    public static async Task<HashSet<string>> OpenKeysAsync(AetheraDbContext db, string prefix, CancellationToken cancellationToken) =>
        (await db.Alerts.AsNoTracking().Where(a => a.State == AlertState.Open && a.DedupKey.StartsWith(prefix)).Select(a => a.DedupKey).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

    public static string Percent(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " %";

    public static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>
/// <c>server.unreachable</c> / <c>server.recovered</c>: an active server whose agent session has been missing for more than
/// <see cref="NotificationOptions.ServerUnreachableMinutes"/> (an SSH-only server: whose reachability probe has failed that long). Reads the status
/// the agent gateway and the reachability probe maintain; servers in maintenance, pending or disabled never alert.
/// </summary>
public sealed class ServerHealthDetector(IServiceScopeFactory scopes, Notifier notifier, IClock clock, IOptions<NotificationOptions> options) : IDetector
{
    public const string KeyPrefix = "server-down:";

    public string Name => "servers";

    public TimeSpan Interval => TimeSpan.Zero;

    public static string KeyOf(Guid serverId) => KeyPrefix + serverId;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var grace = TimeSpan.FromMinutes(Math.Max(0, options.Value.ServerUnreachableMinutes));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();

        var servers = await db.Servers.AsNoTracking()
            .Select(s => new
            {
                s.Id, s.OrganizationId, s.Name, s.Lifecycle, s.AgentStatus, s.AgentStatusChangedAt, s.ReachabilityStatus, s.ReachabilityChangedAt, s.LastHeartbeatAt,
            })
            .ToListAsync(cancellationToken);
        var open = await db.Alerts.AsNoTracking().Where(a => a.State == AlertState.Open && a.DedupKey.StartsWith(KeyPrefix))
            .Select(a => new { a.OrganizationId, a.DedupKey }).ToListAsync(cancellationToken);
        var openKeys = open.Select(a => a.DedupKey).ToHashSet(StringComparer.Ordinal);

        foreach (var s in servers.Where(s => s.Lifecycle == ServerLifecycle.Active))
        {
            DateTimeOffset? since = s.AgentStatus == AgentStatus.Unavailable ? s.AgentStatusChangedAt
                : s.AgentStatus == AgentStatus.NotInstalled && s.ReachabilityStatus == ReachabilityStatus.Unreachable ? s.ReachabilityChangedAt
                : null;
            var key = KeyOf(s.Id);
            var details = new Dictionary<string, string> { ["resource"] = s.Name };
            if (s.LastHeartbeatAt is { } heartbeat) details["lastHeartbeat"] = heartbeat.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

            if (since is { } down && now - down > grace)
            {
                await notifier.PublishAsync(new NotificationEvent(s.OrganizationId, NotificationEvents.ServerUnreachable, AlertSeverity.Critical,
                    $"Server {s.Name} is unreachable",
                    s.AgentStatus == AgentStatus.Unavailable
                        ? $"The agent on {s.Name} has not been connected since {down.UtcDateTime:yyyy-MM-dd HH:mm} UTC. Deployments and metrics for it are paused."
                        : $"{s.Name} has not answered since {down.UtcDateTime:yyyy-MM-dd HH:mm} UTC.")
                {
                    ResourceType = "server", ResourceId = s.Id, Path = $"/servers/{s.Id}", AlertKey = key, Details = details,
                }, cancellationToken);
            }
            else if (openKeys.Contains(key) && s.AgentStatus == AgentStatus.Connected)
            {
                await notifier.PublishAsync(new NotificationEvent(s.OrganizationId, NotificationEvents.ServerRecovered, AlertSeverity.Info,
                    $"Server {s.Name} is back", $"The agent on {s.Name} is connected again.")
                {
                    ResourceType = "server", ResourceId = s.Id, Path = $"/servers/{s.Id}", AlertKey = key, Resolves = true, Details = details,
                }, cancellationToken);
            }
        }

        // The server was deleted while its alert was open: nothing is left to recover.
        var known = servers.Select(s => s.Id).ToHashSet();
        foreach (var alert in open)
        {
            if (Guid.TryParse(alert.DedupKey[KeyPrefix.Length..], out var id) && !known.Contains(id))
                await notifier.ResolveQuietlyAsync(alert.OrganizationId, alert.DedupKey, cancellationToken);
        }
    }
}

/// <summary>
/// <c>server.disk_high</c> (root filesystem above <see cref="NotificationOptions.DiskThresholdPercent"/>) and <c>server.memory_high</c> (above
/// <see cref="NotificationOptions.MemoryThresholdPercent"/>) for <see cref="NotificationOptions.SustainedMinutes"/>, from the host's raw
/// <c>metric_samples</c>. Both resolve as soon as the latest sample is back under the threshold.
/// </summary>
public sealed class ResourceUsageDetector(IServiceScopeFactory scopes, Notifier notifier, IClock clock, IOptions<NotificationOptions> options) : IDetector
{
    public const string DiskPrefix = "server-disk:";
    public const string MemoryPrefix = "server-memory:";

    /// <summary>How far the first sample of the window may lie after the window's start (the agent reports every few seconds).</summary>
    private static readonly TimeSpan Coverage = TimeSpan.FromSeconds(60);

    /// <summary>A server that stopped reporting for this long is neither flagged nor cleared by stale data.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    public string Name => "usage";

    public TimeSpan Interval => TimeSpan.Zero;

    private sealed record Sample(DateTimeOffset At, double? Disk, double? Memory);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.UtcNow;
        var window = TimeSpan.FromMinutes(Math.Max(0.01, settings.SustainedMinutes));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();

        var from = now - window - Coverage;
        var rows = await db.MetricSamples.AsNoTracking()
            .Where(m => m.ContainerId == null && m.Resolution == MetricResolution.Raw && m.Timestamp >= from)
            .Select(m => new { m.ServerId, m.Timestamp, m.DiskUsedBytes, m.DiskTotalBytes, m.MemoryUsedBytes, m.MemoryTotalBytes })
            .ToListAsync(cancellationToken);
        var servers = (await db.Servers.AsNoTracking().Where(s => s.Lifecycle == ServerLifecycle.Active)
            .Select(s => new { s.Id, s.OrganizationId, s.Name }).ToListAsync(cancellationToken)).ToDictionary(s => s.Id);
        var openDisk = await DetectorSupport.OpenKeysAsync(db, DiskPrefix, cancellationToken);
        var openMemory = await DetectorSupport.OpenKeysAsync(db, MemoryPrefix, cancellationToken);

        var perServer = rows.GroupBy(r => r.ServerId).ToDictionary(
            g => g.Key,
            g => g.OrderBy(r => r.Timestamp).Select(r => new Sample(
                r.Timestamp, Ratio(r.DiskUsedBytes, r.DiskTotalBytes), Ratio(r.MemoryUsedBytes, r.MemoryTotalBytes))).ToList());

        foreach (var server in servers.Values)
        {
            perServer.TryGetValue(server.Id, out var samples);
            samples ??= [];
            var latestRow = rows.Where(r => r.ServerId == server.Id).OrderByDescending(r => r.Timestamp).FirstOrDefault();

            await EvaluateAsync(server.OrganizationId, server.Id, server.Name, samples, s => s.Disk, settings.DiskThresholdPercent, window, now, openDisk,
                DiskPrefix, NotificationEvents.ServerDiskHigh, "Disk usage", "root filesystem", latestRow?.DiskUsedBytes, latestRow?.DiskTotalBytes, cancellationToken);
            await EvaluateAsync(server.OrganizationId, server.Id, server.Name, samples, s => s.Memory, settings.MemoryThresholdPercent, window, now, openMemory,
                MemoryPrefix, NotificationEvents.ServerMemoryHigh, "Memory usage", "memory", latestRow?.MemoryUsedBytes, latestRow?.MemoryTotalBytes, cancellationToken);
        }
    }

    private async Task EvaluateAsync(
        Guid organizationId, Guid serverId, string serverName, List<Sample> samples, Func<Sample, double?> metric, double threshold, TimeSpan window, DateTimeOffset now,
        HashSet<string> openKeys, string prefix, string eventType, string label, string what, long? used, long? total, CancellationToken cancellationToken)
    {
        var key = prefix + serverId;
        var values = samples.Select(s => (s.At, Value: metric(s))).Where(s => s.Value is not null).Select(s => (s.At, Value: s.Value!.Value)).ToList();
        if (values.Count == 0) return;
        var latest = values[^1];
        if (now - latest.At > Fresh) return; // no recent data: leave the alert as it is

        var windowStart = now - window;
        var inWindow = values.Where(v => v.At >= windowStart).ToList();
        var sustained = inWindow.Count >= 2 && inWindow[0].At <= windowStart + Coverage && inWindow.All(v => v.Value > threshold);

        var details = new Dictionary<string, string>
        {
            ["resource"] = serverName, ["usage"] = DetectorSupport.Percent(latest.Value), ["threshold"] = DetectorSupport.Percent(threshold),
        };
        if (used is { } u && total is { } t) { details["used"] = DetectorSupport.Bytes(u); details["total"] = DetectorSupport.Bytes(t); }

        if (sustained)
        {
            await notifier.PublishAsync(new NotificationEvent(organizationId, eventType, latest.Value >= 95 ? AlertSeverity.Critical : AlertSeverity.Warning,
                $"{label} on {serverName} is {DetectorSupport.Percent(latest.Value)}",
                $"The {what} of {serverName} has been above {DetectorSupport.Percent(threshold)} for {window.TotalMinutes:0.#} minutes.")
            {
                ResourceType = "server", ResourceId = serverId, Path = $"/servers/{serverId}", AlertKey = key, Details = details,
            }, cancellationToken);
        }
        else if (openKeys.Contains(key) && latest.Value < threshold)
        {
            await notifier.PublishAsync(new NotificationEvent(organizationId, eventType, AlertSeverity.Info,
                $"{label} on {serverName} is back to {DetectorSupport.Percent(latest.Value)}",
                $"The {what} of {serverName} is below {DetectorSupport.Percent(threshold)} again.")
            {
                ResourceType = "server", ResourceId = serverId, Path = $"/servers/{serverId}", AlertKey = key, Resolves = true, Details = details,
            }, cancellationToken);
        }
    }

    private static double? Ratio(long? used, long? total) => used is { } u && total is > 0 ? u * 100.0 / total.Value : null;
}
