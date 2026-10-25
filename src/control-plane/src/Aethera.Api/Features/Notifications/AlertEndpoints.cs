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

namespace Aethera.Api.Features.Notifications;

/// <summary>
/// An alert: opened by a failure event, refreshed (<c>lastSeenAt</c>) while it continues, resolved by the recovery. <c>resource</c> is the display
/// name of the affected thing and <c>path</c> its page in the UI; <c>details</c> are plain facts (never secrets).
/// </summary>
public sealed record AlertResponse(
    Guid Id, string Type, AlertSeverity Severity, AlertState State, string Title, string? Message, string? ResourceType, Guid? ResourceId, string? Resource, string? Path,
    IReadOnlyDictionary<string, string> Details, DateTimeOffset OpenedAt, DateTimeOffset LastSeenAt, DateTimeOffset? ResolvedAt, int NotificationCount,
    DateTimeOffset? AcknowledgedAt, Guid? AcknowledgedBy);

internal static class AlertEndpoints
{
    private static readonly SortDefinition<Alert> Sorts = new SortDefinition<Alert>("-openedAt")
        .Add("openedAt", a => a.OpenedAt).Add("lastSeenAt", a => a.LastSeenAt);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/alerts").WithTags("Alerts");
        group.MapGet("/", List).WithName("listAlerts").RequireRead();
        group.MapPost("/{id:guid}/acknowledge", Acknowledge).WithName("acknowledgeAlert").RequireWrite();
    }

    private static async Task<Ok<Page<AlertResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? state, string? sort, CancellationToken ct)
    {
        http.RejectUnknownQuery("state");
        AlertState? filter = null;
        if (!string.IsNullOrWhiteSpace(state))
            filter = EnumText.Parse<AlertState>(state) ?? throw new ApiProblemException(ApiProblems.InvalidParameter("state", "Must be open or resolved.", "invalid_enum"));

        var query = db.Alerts.AsNoTracking().Where(a => a.OrganizationId == actor.Org());
        if (filter is { } s) query = query.Where(a => a.State == s);
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"state={filter}", ct);
        return TypedResults.Ok(new Page<AlertResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Ok<AlertResponse>> Acknowledge(Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id && a.OrganizationId == actor.Org(), ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound("alert", id));
        // Acknowledging twice keeps the first acknowledgement.
        if (alert.AcknowledgedAt is null)
        {
            alert.AcknowledgedAt = clock.UtcNow;
            alert.AcknowledgedByUserId = actor.UserId;
            await audit.RecordAsync("alert.acknowledged", "alert", id, new { type = alert.Type }, ct);
        }

        return TypedResults.Ok(ToResponse(alert));
    }

    internal static AlertResponse ToResponse(Alert a)
    {
        var details = new Dictionary<string, string>();
        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, string>>(a.DetailsJson, Json) is { } parsed) details = parsed;
        }
        catch (JsonException)
        {
            // Stored details are written by us; an unreadable value just shows no details.
        }

        details.Remove("path", out var path);
        details.TryGetValue("resource", out var resource);
        details.Remove("resource");
        return new AlertResponse(a.Id, a.Type, a.Severity, a.State, a.Title, a.Message, a.ResourceType, a.ResourceId, resource, path, details, a.OpenedAt, a.LastSeenAt,
            a.ResolvedAt, a.NotificationCount, a.AcknowledgedAt, a.AcknowledgedByUserId);
    }
}
