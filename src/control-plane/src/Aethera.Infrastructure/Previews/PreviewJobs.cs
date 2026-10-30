using Aethera.Domain;
using Aethera.Infrastructure.Auditing;
using Aethera.Infrastructure.Persistence;

namespace Aethera.Infrastructure.Previews;

/// <summary>Job types, lock keys and payloads of pull request previews (ADR 0012).</summary>
public static class PreviewJobs
{
    public const string TeardownType = "preview.teardown";
    public const string SweepType = "preview.sweep";

    /// <summary>Previews without a pull request event for this long are removed by <c>preview.sweep</c>.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>
    /// The lock of everything that changes one preview: its deployments and its teardown. It is not the application's lock (<c>app:&lt;id&gt;</c>),
    /// so a preview never makes a production deployment wait.
    /// </summary>
    public static string LockKey(Guid previewId) => $"preview:{previewId}";
}

/// <summary>Payload of <c>preview.teardown</c>: the preview whose containers, volumes and images go.</summary>
public sealed class PreviewTeardownPayload
{
    public Guid PreviewId { get; set; }
}

/// <summary>Payload of <c>preview.sweep</c>: the organization whose previews are swept.</summary>
public sealed class PreviewSweepPayload
{
    public Guid OrganizationId { get; set; }
}

/// <summary>Audit events of previews written where no HTTP actor exists (webhooks, jobs).</summary>
public static class PreviewAudit
{
    public static void Record(AetheraDbContext db, IClock clock, Guid organizationId, string action, Guid previewId, object? metadata, Guid? userId = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OrganizationId = organizationId, ActorType = userId is null ? AuditActorType.System : AuditActorType.User, ActorUserId = userId, Action = action,
            ResourceType = "preview", ResourceId = previewId, MetadataJson = Redaction.ToJsonObject(metadata), OccurredAt = clock.UtcNow,
        });
}
