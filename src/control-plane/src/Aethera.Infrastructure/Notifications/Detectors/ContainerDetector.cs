using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Notifications.Detectors;

/// <summary>
/// <c>container.stopped</c> and <c>container.restarting</c> from the container events the agent reports (<c>resource_events</c> of the workload):
/// <list type="bullet">
/// <item>A managed container that died, was OOM-killed or was stopped while its workload should be running raises <c>container.stopped</c>. A stop that
/// belongs to a deployment, a lifecycle action or a restart loop does not count: events older than the workload's last status change are ignored,
/// events are looked at <see cref="NotificationOptions.ContainerSettleSeconds"/> after they happened, and a restart event right next to a death
/// hands it over to the restart-loop alert. A later <c>container.started</c> resolves the alert.</item>
/// <item>A workload with at least <see cref="NotificationOptions.RestartLoopCount"/> restarts within <see cref="NotificationOptions.RestartLoopMinutes"/>
/// raises <c>container.restarting</c>; it resolves when the count drops below the limit.</item>
/// </list>
/// Events are read once, in order, from a watermark kept in memory (the first run looks two minutes back).
/// </summary>
public sealed class ContainerDetector(IServiceScopeFactory scopes, Notifier notifier, IClock clock, IOptions<NotificationOptions> options) : IDetector
{
    public const string StoppedPrefix = "container-stopped:";
    public const string RestartPrefix = "container-restart:";

    private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(2);

    /// <summary>A death within this distance of a restart event is the restart loop's business.</summary>
    private static readonly TimeSpan RestartPairing = TimeSpan.FromSeconds(60);

    private DateTimeOffset? _since;

    public string Name => "containers";

    public TimeSpan Interval => TimeSpan.Zero;

    private sealed record WorkloadInfo(Guid Id, string Name, Guid OrganizationId, Guid EnvironmentId, string Project, string Environment, DesiredState Desired, WorkloadStatus Status, DateTimeOffset? StatusChangedAt, bool IsService);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.UtcNow;
        var upper = now - TimeSpan.FromSeconds(Math.Max(0, settings.ContainerSettleSeconds));
        var since = _since ?? now - Lookback;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();

        var openStopped = await DetectorSupport.OpenKeysAsync(db, StoppedPrefix, cancellationToken);
        var openRestart = await DetectorSupport.OpenKeysAsync(db, RestartPrefix, cancellationToken);

        string[] kinds = ["container.died", "container.oom", "container.stopped", "container.started"];
        var events = upper > since
            ? await db.ResourceEvents.AsNoTracking()
                .Where(e => e.ResourceType == "workload" && kinds.Contains(e.Kind) && e.OccurredAt > since && e.OccurredAt <= upper)
                .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToListAsync(cancellationToken)
            : [];

        var loopWindow = TimeSpan.FromMinutes(Math.Max(0.01, settings.RestartLoopMinutes));
        var restarts = await db.ResourceEvents.AsNoTracking()
            .Where(e => e.ResourceType == "workload" && e.Kind == "container.restarting" && e.OccurredAt > now - loopWindow - RestartPairing && e.OccurredAt <= now)
            .Select(e => new { e.ResourceId, e.OccurredAt }).ToListAsync(cancellationToken);

        var ids = events.Select(e => e.ResourceId).Concat(restarts.Select(r => r.ResourceId))
            .Concat(openRestart.Select(k => ParseId(k, RestartPrefix))).Concat(openStopped.Select(k => ParseId(k, StoppedPrefix)))
            .Where(id => id != Guid.Empty).Distinct().ToList();
        var workloads = ids.Count == 0 ? [] : (await db.Workloads.AsNoTracking().Where(w => ids.Contains(w.Id))
            .Select(w => new WorkloadInfo(w.Id, w.Name, w.Environment.Project.OrganizationId, w.EnvironmentId, w.Environment.Project.Name, w.Environment.Name,
                w.DesiredState, w.Status, w.StatusChangedAt, w is Service)).ToListAsync(cancellationToken)).ToDictionary(w => w.Id);

        foreach (var e in events)
        {
            if (!workloads.TryGetValue(e.ResourceId, out var w)) continue;
            var key = StoppedPrefix + w.Id;

            if (e.Kind == "container.started")
            {
                if (openStopped.Remove(key))
                    await notifier.PublishAsync(Event(w, NotificationEvents.ContainerStopped, AlertSeverity.Info, $"{w.Name} is running again", "The container started again.", key, "workload", e.Detail, resolves: true), cancellationToken);
                continue;
            }

            if (w.Desired != DesiredState.Running || w.Status is WorkloadStatus.Deploying or WorkloadStatus.NotDeployed) continue;
            if (w.StatusChangedAt is { } changed && e.OccurredAt <= changed) continue; // part of a deployment, a restart or a stop that we caused
            if (restarts.Any(r => r.ResourceId == w.Id && (r.OccurredAt - e.OccurredAt).Duration() <= RestartPairing)) continue;

            var oom = e.Kind == "container.oom" || (e.Detail?.Contains("oom", StringComparison.OrdinalIgnoreCase) ?? false);
            await notifier.PublishAsync(Event(w, NotificationEvents.ContainerStopped, AlertSeverity.Critical,
                oom ? $"{w.Name} was killed (out of memory)" : $"{w.Name} stopped unexpectedly",
                oom ? "The container exceeded its memory limit and was killed by the kernel." : "The container exited while the workload should be running.",
                key, "workload", e.Detail), cancellationToken);
            openStopped.Add(key);
        }

        // Restart loops.
        var counts = restarts.Where(r => r.OccurredAt > now - loopWindow).GroupBy(r => r.ResourceId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (workloadId, count) in counts)
        {
            if (count < settings.RestartLoopCount || !workloads.TryGetValue(workloadId, out var w)) continue;
            if (w.Desired != DesiredState.Running || w.Status is WorkloadStatus.Deploying or WorkloadStatus.NotDeployed) continue;
            var key = RestartPrefix + w.Id;
            await notifier.PublishAsync(Event(w, NotificationEvents.ContainerRestarting, AlertSeverity.Critical, $"{w.Name} is restarting repeatedly",
                $"The container restarted {count} times in the last {loopWindow.TotalMinutes:0.#} minutes.", key, "workload", null,
                extra: ("restarts", count.ToString(System.Globalization.CultureInfo.InvariantCulture))), cancellationToken);
            openRestart.Add(key);
        }

        foreach (var key in openRestart.ToList())
        {
            var workloadId = ParseId(key, RestartPrefix);
            if (workloadId == Guid.Empty || counts.GetValueOrDefault(workloadId) >= settings.RestartLoopCount) continue;
            if (!workloads.TryGetValue(workloadId, out var w)) { await Org(db, key, cancellationToken); continue; }
            await notifier.PublishAsync(Event(w, NotificationEvents.ContainerRestarting, AlertSeverity.Info, $"{w.Name} stopped restarting",
                "The container is no longer restarting in a loop.", key, "workload", null, resolves: true), cancellationToken);
        }

        _since = upper > since ? upper : since;
    }

    private async Task Org(AetheraDbContext db, string key, CancellationToken cancellationToken)
    {
        // The workload was deleted: quietly close its alert.
        var alert = await db.Alerts.AsNoTracking().Where(a => a.State == AlertState.Open && a.DedupKey == key).Select(a => new { a.OrganizationId }).FirstOrDefaultAsync(cancellationToken);
        if (alert is not null) await notifier.ResolveQuietlyAsync(alert.OrganizationId, key, cancellationToken);
    }

    private static Guid ParseId(string key, string prefix) => Guid.TryParse(key[prefix.Length..], out var id) ? id : Guid.Empty;

    private static NotificationEvent Event(
        WorkloadInfo w, string type, AlertSeverity severity, string title, string message, string key, string resourceType, string? detail,
        bool resolves = false, (string Key, string Value)? extra = null)
    {
        var details = new Dictionary<string, string> { ["resource"] = w.Name, ["project"] = w.Project, ["environment"] = w.Environment };
        if (!string.IsNullOrWhiteSpace(detail)) details["container"] = detail;
        if (extra is { } pair) details[pair.Key] = pair.Value;
        return new NotificationEvent(w.OrganizationId, type, severity, title, message)
        {
            ResourceType = resourceType, ResourceId = w.Id, EnvironmentId = w.EnvironmentId, Path = $"/{(w.IsService ? "services" : "applications")}/{w.Id}", AlertKey = key, Resolves = resolves, Details = details,
        };
    }
}
