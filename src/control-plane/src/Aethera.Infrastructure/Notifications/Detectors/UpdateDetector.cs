using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.SelfOps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Notifications.Detectors;

/// <summary>
/// <c>update.available</c>: reads what the release checker found and announces each new version once (an info alert keyed by the version, so
/// it opens once and never reminds). Alerts of older versions, or of the version that is running now, are closed quietly.
/// </summary>
public sealed class UpdateDetector(IServiceScopeFactory scopes, Notifier notifier, ReleaseChecker? checker = null) : IDetector
{
    public const string KeyPrefix = "update:";

    private string? _handled;

    public string Name => "updates";

    public TimeSpan Interval => TimeSpan.Zero;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (checker is null || !checker.Enabled) return;
        var latest = checker.UpdateAvailable ? checker.Latest : null;
        var marker = latest?.Version ?? "";
        if (_handled == marker) return; // nothing new since the last look: no database work every tick

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();

        var current = KeyPrefix + latest?.Version;
        var stale = await db.Alerts.AsNoTracking()
            .Where(a => a.State == AlertState.Open && a.DedupKey.StartsWith(KeyPrefix) && a.DedupKey != current)
            .Select(a => new { a.OrganizationId, a.DedupKey }).ToListAsync(cancellationToken);
        foreach (var alert in stale) await notifier.ResolveQuietlyAsync(alert.OrganizationId, alert.DedupKey, cancellationToken);

        if (latest is not null)
        {
            var details = new Dictionary<string, string> { ["resource"] = "Aethera", ["current"] = checker.CurrentVersion, ["latest"] = latest.Version };
            if (!string.IsNullOrEmpty(latest.Url)) details["release"] = latest.Url;
            foreach (var organizationId in await db.Organizations.AsNoTracking().Select(o => o.Id).ToListAsync(cancellationToken))
            {
                await notifier.PublishAsync(new NotificationEvent(organizationId, NotificationEvents.UpdateAvailable, AlertSeverity.Info,
                    $"Aethera {latest.Version} is available", $"This instance runs {checker.CurrentVersion}. Update from Settings > System.")
                {
                    ResourceType = "system", Path = "/settings", AlertKey = KeyPrefix + latest.Version, Details = details,
                }, cancellationToken);
            }
        }

        _handled = marker;
    }
}
