using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Auditing;

/// <summary>
/// Writes <see cref="AuditEvent"/> rows through the scoped <see cref="AetheraDbContext"/>. Actor, request id and IP come from
/// <see cref="ICurrentActor"/>; metadata is redacted with <see cref="Redaction"/>.
/// </summary>
/// <remarks>
/// <c>audit_events.organization_id</c> is mandatory. When the actor has no organization (anonymous requests such as a failed
/// login), the single/first organization is used, which is exact for the one-organization installs of the MVP. When no
/// organization exists yet (first-run setup), nothing can be recorded and a warning is logged instead of failing the request.
/// </remarks>
public sealed class EfAuditLog(AetheraDbContext db, ICurrentActor actor, IClock clock, ILogger<EfAuditLog> logger) : IAuditLog
{
    public async Task RecordAsync(
        string action, string resourceType, Guid? resourceId, object? metadata, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var organizationId = actor.OrganizationId ?? await FindDefaultOrganizationAsync(cancellationToken);
        if (organizationId is null)
        {
            logger.LogWarning("Audit event {Action} not recorded: no organization exists yet", action);
            return;
        }

        db.AuditEvents.Add(new AuditEvent
        {
            OrganizationId = organizationId.Value,
            ActorType = actor.ApiTokenId is not null ? AuditActorType.ApiToken
                : actor.UserId is not null ? AuditActorType.User
                : AuditActorType.System,
            ActorUserId = actor.UserId,
            ActorApiTokenId = actor.ApiTokenId,
            Action = action,
            ResourceType = string.IsNullOrEmpty(resourceType) ? null : resourceType,
            ResourceId = resourceId,
            MetadataJson = Redaction.ToJsonObject(metadata),
            IpAddress = actor.IpAddress,
            RequestId = actor.RequestId,
            OccurredAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid?> FindDefaultOrganizationAsync(CancellationToken cancellationToken)
    {
        var id = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => (Guid?)o.Id).FirstOrDefaultAsync(cancellationToken);
        return id;
    }
}
