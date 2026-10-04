using System.Text.Json;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Audit;

public sealed record AuditEventDto(
    Guid Id, AuditActorType ActorType, Guid? ActorUserId, Guid? ActorApiTokenId, string? ActorLabel, string Action, string? ResourceType,
    Guid? ResourceId, string? ResourceName, JsonElement? Metadata, string? IpAddress, string? RequestId, DateTimeOffset OccurredAt);

/// <summary><c>GET /audit-log</c>: the organization's audit trail for administrators (WP4.4).</summary>
internal static class AuditEndpoints
{
    private static readonly SortDefinition<AuditEvent> Sorts = new SortDefinition<AuditEvent>("-occurredAt")
        .Add("occurredAt", e => e.OccurredAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/audit-log", List).WithName("listAuditLog").WithTags("Audit")
            .WithSummary("List audit events (administrators)")
            .WithDescription("Filters: `action` (exact, or a prefix ending in `*`), `resourceType`, `resourceId`, `actorType`, `from`, `to`. Newest first.")
            .RequireAdmin(Scopes.Read);
    }

    private static async Task<Ok<Page<AuditEventDto>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort,
        string? action, string? resourceType, Guid? resourceId, AuditActorType? actorType, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        http.RejectUnknownQuery("action", "resourceType", "resourceId", "actorType", "from", "to");
        var query = db.AuditEvents.AsNoTracking().Where(e => e.OrganizationId == actor.Org());
        if (!string.IsNullOrWhiteSpace(action))
        {
            var a = action.Trim();
            query = a.EndsWith('*') ? query.Where(e => e.Action.StartsWith(a.Substring(0, a.Length - 1))) : query.Where(e => e.Action == a);
        }

        if (!string.IsNullOrWhiteSpace(resourceType)) query = query.Where(e => e.ResourceType == resourceType);
        if (resourceId is { } rid) query = query.Where(e => e.ResourceId == rid);
        if (actorType is { } at) query = query.Where(e => e.ActorType == at);
        if (from is { } f) query = query.Where(e => e.OccurredAt >= f);
        if (to is { } t) query = query.Where(e => e.OccurredAt <= t);

        var scope = $"a={action}&rt={resourceType}&ri={resourceId}&at={actorType}&f={from:O}&t={to:O}";
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, scope, ct);
        return TypedResults.Ok(new Page<AuditEventDto>(items.Select(ToDto).ToList(), next));
    }

    private static AuditEventDto ToDto(AuditEvent e) => new(
        e.Id, e.ActorType, e.ActorUserId, e.ActorApiTokenId, e.ActorLabel, e.Action, e.ResourceType, e.ResourceId, e.ResourceName,
        ParseMetadata(e.MetadataJson), e.IpAddress, e.RequestId, e.OccurredAt);

    private static JsonElement? ParseMetadata(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class AuditModule
{
    public static IEndpointRouteBuilder MapAuditLog(this IEndpointRouteBuilder api)
    {
        AuditEndpoints.Map(api);
        return api;
    }
}
