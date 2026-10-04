using Aethera.Domain;
using Aethera.Infrastructure.Auditing;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Agents;

/// <summary>
/// Audit and resource-event writer for code that runs outside an HTTP request (the gRPC gateway, background services). Uses its own short
/// scope per write so a failure never poisons a long-lived context, and never throws: auditing must not break the gateway.
/// </summary>
public sealed class AgentAudit(IServiceScopeFactory scopes, IClock clock, ILogger<AgentAudit> logger)
{
    /// <summary>Appends an audit event. Metadata is redacted by key name (<see cref="Redaction"/>); callers still must not pass payloads.</summary>
    public async Task RecordAsync(
        string action, string resourceType, Guid? resourceId, object? metadata, AuditActorType actorType = AuditActorType.System,
        Guid? organizationId = null, Guid? actorUserId = null, string? ipAddress = null, string? requestId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var org = organizationId
                ?? (resourceType == "server" && resourceId is { } serverId
                    ? await db.Servers.IgnoreQueryFilters().Where(s => s.Id == serverId).Select(s => (Guid?)s.OrganizationId).FirstOrDefaultAsync(cancellationToken)
                    : null)
                ?? await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(cancellationToken);
            if (org is null)
            {
                logger.LogWarning("Audit event {Action} not recorded: no organization exists yet", action);
                return;
            }

            db.AuditEvents.Add(new AuditEvent
            {
                OrganizationId = org.Value, ActorType = actorUserId is not null ? AuditActorType.User : actorType, ActorUserId = actorUserId, Action = action,
                ResourceType = string.IsNullOrEmpty(resourceType) ? null : resourceType, ResourceId = resourceId,
                MetadataJson = Redaction.ToJsonObject(metadata), IpAddress = ipAddress, RequestId = requestId ?? Guid.NewGuid().ToString("N"), OccurredAt = clock.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Audit event {Action} could not be written", action);
        }
    }
}
