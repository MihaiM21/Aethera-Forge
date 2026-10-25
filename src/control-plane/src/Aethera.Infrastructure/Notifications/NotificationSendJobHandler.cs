using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Notifications;

/// <summary>
/// <c>notification.send</c>: delivers one event to one channel. A transient failure (connection refused, timeout, 4xx from the mail server, 5xx
/// from a webhook) is retried with the queue's backoff up to the job's attempts; the delivery row is written when the outcome is final, so the
/// history shows one line per notification, not one per attempt.
/// </summary>
public sealed class NotificationSendJobHandler : IJobHandler
{
    public string Type => Notifier.SendJobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var payload = context.GetPayload<NotificationPayload>();

        var channel = await db.NotificationChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == payload.ChannelId && c.OrganizationId == context.Job.OrganizationId, cancellationToken);
        if (channel is null || !channel.Enabled) return; // deleted or switched off since it was queued: nothing to send

        var result = await sp.GetRequiredService<NotificationDispatcher>().DeliverAsync(channel, payload, context.JobId, cancellationToken);
        if (result.Ok)
        {
            await RecordAsync(db, clock, payload, NotificationDeliveryStatus.Sent, null, cancellationToken);
            context.SetResult(new { channelId = channel.Id, status = "sent" });
            return;
        }

        var willRetry = result.Retryable && context.Attempt < context.Job.MaxAttempts;
        if (!willRetry) await RecordAsync(db, clock, payload, NotificationDeliveryStatus.Failed, result.Error, CancellationToken.None);
        throw result.Retryable
            ? JobFailedException.Transient("notification.delivery_failed", "The notification could not be delivered", result.Error, "send")
            : JobFailedException.Permanent("notification.delivery_failed", "The notification could not be delivered", result.Error, "send");
    }

    private static async Task RecordAsync(
        AetheraDbContext db, IClock clock, NotificationPayload payload, NotificationDeliveryStatus status, string? error, CancellationToken cancellationToken)
    {
        var alertId = payload.AlertId;
        // The alert may have been removed since; the foreign key would refuse the row.
        if (alertId is { } id && !await db.Alerts.AsNoTracking().AnyAsync(a => a.Id == id, CancellationToken.None)) alertId = null;
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            OrganizationId = payload.OrganizationId,
            ChannelId = payload.ChannelId,
            AlertId = alertId,
            EventType = payload.Type,
            Status = status,
            Error = error is null ? null : (error.Length <= 2000 ? error : error[..2000]),
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
