using System.Text.Json;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aethera.Infrastructure.Notifications;

/// <summary>
/// The <see cref="INotifier"/> (ADR 0010). <see cref="PublishAsync"/> keeps the alert lifecycle (one open alert per organization and key; a
/// refresh only touches <c>last_seen_at</c>; a resolve closes it), decides whether anybody must be told (on open, on resolve, as a reminder
/// every <see cref="NotificationOptions.ReminderHours"/> while a warning or critical alert stays open), picks the enabled channels that subscribe
/// to the event and match the project/environment filter, and queues one <c>notification.send</c> job per channel. No network I/O happens here,
/// and nothing that goes wrong is ever thrown at the caller. It works in its own DI scope and database context, so it neither sees nor flushes
/// the caller's pending changes.
/// </summary>
public sealed class Notifier(IServiceScopeFactory scopes, IClock clock, IOptions<NotificationOptions> options, ILogger<Notifier> logger) : INotifier
{
    public const string SendJobType = "notification.send";

    /// <summary>A publish never keeps its caller waiting longer than this (database trouble must not stall a deployment).</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions DetailsJson = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(NotificationEvent notification, CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Budget);
            await PublishCoreAsync(notification, budget.Token);
        }
        catch (Exception ex)
        {
            // Includes cancellation: a notification that could not be recorded is logged, never a failure of the job that raised it.
            logger.LogWarning(ex, "Notification {Type} could not be published", notification.Type);
        }
    }

    private async Task PublishCoreAsync(NotificationEvent n, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var now = clock.UtcNow;

        var kind = NotificationKinds.Event;
        var notify = true;
        Alert? alert = null;

        if (n.AlertKey is { Length: > 0 } rawKey)
        {
            var key = rawKey.Length <= 300 ? rawKey : rawKey[..300];
            alert = await FindOpenAsync(db, n.OrganizationId, key, ct);
            if (!n.Resolves)
            {
                if (alert is null)
                {
                    alert = NewAlert(n, key, now);
                    db.Alerts.Add(alert);
                    kind = NotificationKinds.Opened;
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                    {
                        // Someone opened the same alert a moment ago: this event is a refresh of theirs.
                        db.Entry(alert).State = EntityState.Detached;
                        alert = await FindOpenAsync(db, n.OrganizationId, key, ct);
                        if (alert is null) return;
                        (kind, notify) = Refresh(alert, n, now);
                        await db.SaveChangesAsync(ct);
                    }
                }
                else
                {
                    (kind, notify) = Refresh(alert, n, now);
                    await db.SaveChangesAsync(ct);
                }
            }
            else if (alert is not null)
            {
                alert.State = AlertState.Resolved;
                alert.ResolvedAt = now;
                alert.LastSeenAt = now;
                kind = NotificationKinds.Resolved;
                notify = alert.NotificationCount > 0; // nobody heard about the problem, so nobody needs to hear that it is over
                await db.SaveChangesAsync(ct);
            }
            else
            {
                // A recovery with nothing open is silent, except a success event, which is also a plain event of its own ("deployment succeeded").
                notify = n.Type is NotificationEvents.DeploymentSucceeded or NotificationEvents.BackupSucceeded or NotificationEvents.TaskSucceeded;
            }
        }

        if (!notify) return;

        var channels = await SelectChannelsAsync(db, n, kind == NotificationKinds.Resolved ? alert?.Type : null, ct);
        if (channels.Count == 0) return;

        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var sent = 0;
        foreach (var channel in channels)
        {
            try
            {
                await queue.EnqueueAsync(new JobRequest(SendJobType, ToPayload(n, channel.Id, alert?.Id, kind, now))
                {
                    OrganizationId = n.OrganizationId,
                    Resource = new JobResource("notification_channel", channel.Id),
                    MaxAttempts = Math.Max(1, options.Value.MaxDeliveryAttempts),
                }, ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not queue notification {Type} for channel {Channel}", n.Type, channel.Id);
            }
        }

        if (sent > 0 && alert is not null)
        {
            alert.LastNotifiedAt = now;
            alert.NotificationCount++;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Resolves the open alert <paramref name="key"/> without telling anybody (an update alert that is no longer relevant, a deleted server). Returns whether one was open.</summary>
    public async Task<bool> ResolveQuietlyAsync(Guid organizationId, string key, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var alert = await FindOpenAsync(db, organizationId, key, cancellationToken);
        if (alert is null) return false;
        alert.State = AlertState.Resolved;
        alert.ResolvedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private (string Kind, bool Notify) Refresh(Alert alert, NotificationEvent n, DateTimeOffset now)
    {
        alert.LastSeenAt = now;
        alert.Severity = n.Severity;
        alert.Title = Cut(n.Title, 300);
        alert.Message = n.Message is null ? null : Cut(n.Message, 4000);
        alert.DetailsJson = DetailsOf(n);
        alert.ResourceType ??= n.ResourceType;
        alert.ResourceId ??= n.ResourceId;

        // An alert that has never been announced (nobody subscribed when it opened) is announced as soon as someone is.
        if (alert.NotificationCount == 0 || alert.LastNotifiedAt is null)
            return (NotificationKinds.Opened, true);
        var remind = alert.Severity != AlertSeverity.Info && now - alert.LastNotifiedAt.Value >= options.Value.Reminder;
        return (NotificationKinds.Reminder, remind);
    }

    private static Alert NewAlert(NotificationEvent n, string key, DateTimeOffset now) => new()
    {
        OrganizationId = n.OrganizationId,
        Type = Cut(n.Type, 100),
        Severity = n.Severity,
        DedupKey = key,
        ResourceType = n.ResourceType is null ? null : Cut(n.ResourceType, 100),
        ResourceId = n.ResourceId,
        Title = Cut(n.Title, 300),
        Message = n.Message is null ? null : Cut(n.Message, 4000),
        DetailsJson = DetailsOf(n),
        OpenedAt = now,
        LastSeenAt = now,
    };

    private static string DetailsOf(NotificationEvent n)
    {
        var details = new Dictionary<string, string>(NotificationMessageBuilder.SafeDetails(n.Details));
        if (n.Path is not null) details["path"] = n.Path;
        return JsonSerializer.Serialize(details, DetailsJson);
    }

    private static Task<Alert?> FindOpenAsync(AetheraDbContext db, Guid organizationId, string key, CancellationToken ct) =>
        db.Alerts.FirstOrDefaultAsync(a => a.OrganizationId == organizationId && a.DedupKey == key && a.State == AlertState.Open, ct);

    /// <summary>
    /// The enabled channels of the organization that subscribe to the event (or, for a recovery, to the event that opened the alert) and whose
    /// project/environment filter matches the event's workload. A filtered channel never receives an event that has no workload.
    /// </summary>
    private static async Task<List<NotificationChannel>> SelectChannelsAsync(AetheraDbContext db, NotificationEvent n, string? openedType, CancellationToken ct)
    {
        var channels = await db.NotificationChannels.AsNoTracking().Where(c => c.OrganizationId == n.OrganizationId && c.Enabled).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        if (channels.Count == 0) return channels;

        Guid? projectId = null;
        if (n.EnvironmentId is { } environmentId)
            projectId = await db.Environments.AsNoTracking().Where(e => e.Id == environmentId).Select(e => (Guid?)e.ProjectId).FirstOrDefaultAsync(ct);

        return channels.Where(c =>
            (c.Events.Contains(n.Type) || (openedType is not null && c.Events.Contains(openedType)))
            && (c.ProjectId is null || (projectId is not null && c.ProjectId == projectId))
            && (c.EnvironmentId is null || (n.EnvironmentId is not null && c.EnvironmentId == n.EnvironmentId))).ToList();
    }

    private static NotificationPayload ToPayload(NotificationEvent n, Guid channelId, Guid? alertId, string kind, DateTimeOffset now) => new()
    {
        OrganizationId = n.OrganizationId,
        ChannelId = channelId,
        AlertId = alertId,
        Type = n.Type,
        Kind = kind,
        Severity = n.Severity,
        Title = Cut(n.Title, 300),
        Message = n.Message is null ? null : Cut(n.Message, 4000),
        ResourceType = n.ResourceType,
        ResourceId = n.ResourceId,
        Path = n.Path,
        Details = NotificationMessageBuilder.SafeDetails(n.Details).ToDictionary(d => d.Key, d => d.Value),
        OccurredAt = now,
    };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
