using Aethera.Api.Features.Deployments;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Engine.Deployments;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Previews;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Previews;

/// <summary>The latest deployment of a preview, for the list.</summary>
public sealed record PreviewDeploymentDto(
    Guid Id, int Number, DeploymentStatus Status, DeploymentStep? FailedStep, string? FailureCode, string? CommitSha, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>A pull request preview. <see cref="Url"/> is set once the preview has a host name.</summary>
public sealed record PreviewDto(
    Guid Id, Guid ApplicationId, int Number, string? Title, string? AuthorLogin, string SourceBranch, string? HeadSha, bool FromFork, GitProvider Provider,
    Guid ServerId, string? Hostname, string? Url, PreviewStatus Status, string? StatusReason, DateTimeOffset? StatusChangedAt, DateTimeOffset? LastEventAt,
    DateTimeOffset? ClosedAt, DateTimeOffset? RemovedAt, string? CommentError, Guid? CurrentDeploymentId, PreviewDeploymentDto? LastDeployment, DateTimeOffset CreatedAt);

/// <summary>
/// The preview settings of an application. <see cref="AllowForks"/> runs the code of anyone who can open a pull request with the application's secrets.
/// </summary>
public sealed record PreviewSettingsDto(
    bool Enabled, bool AllowForks, Guid? ServerId, int? MaxActive, int DefaultMaxActive, Guid EffectiveServerId, bool Supported, string? UnsupportedReason,
    bool WebhookConfigured, GitProvider? WebhookProvider, string? WebhookPath, string? HostnamePattern);

/// <param name="ServerId">Server the previews run on; null = the application's server. Only previews created afterwards use it.</param>
/// <param name="MaxActive">Previews running at once; null = 10.</param>
public sealed record UpdatePreviewSettingsRequest(bool Enabled, bool AllowForks, Guid? ServerId = null, int? MaxActive = null);

public sealed class UpdatePreviewSettingsValidator : AbstractValidator<UpdatePreviewSettingsRequest>
{
    public const int MaxActiveLimit = 100;

    public UpdatePreviewSettingsValidator()
    {
        RuleFor(x => x.MaxActive).InclusiveBetween(1, MaxActiveLimit).When(x => x.MaxActive is not null);
    }
}

internal static class PreviewEndpoints
{
    private static readonly SortDefinition<PreviewEnvironment> Sorts = new SortDefinition<PreviewEnvironment>("-number")
        .Add("number", p => p.Number).Add("lastEventAt", p => p.LastEventAt).Add("createdAt", p => p.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/applications/{id:guid}/previews").WithTags("Previews");
        group.MapGet("/", List).WithName("listApplicationPreviews").RequireRead();
        group.MapGet("/settings", GetSettings).WithName("getPreviewSettings").RequireRead();
        group.MapPut("/settings", PutSettings).WithName("updatePreviewSettings").Validate<UpdatePreviewSettingsRequest>().RequireWrite();
        group.MapGet("/{number:int}", Get).WithName("getApplicationPreview").RequireRead();
        group.MapPost("/{number:int}/redeploy", Redeploy).WithName("redeployApplicationPreview").RequireDeploy()
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/{number:int}", Remove).WithName("removeApplicationPreview").RequireDeploy()
            .Produces<PreviewDto>(StatusCodes.Status202Accepted);
    }

    /// <summary>Developer role and the <c>deploy</c> scope: a preview is a deployment.</summary>
    private static TBuilder RequireDeploy<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Deploy);

    // ---- list and read ---------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<PreviewDto>>> List(
        Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, string? status,
        bool? includeRemoved, CancellationToken ct)
    {
        http.RejectUnknownQuery("status", "includeRemoved");
        await RequireApplicationAsync(db, actor, id, ct);
        var statuses = ResourceHttp.ParseEnumFilter<PreviewStatus>(status, "status");
        var query = db.Previews.AsNoTracking().Where(p => p.ApplicationId == id);
        if (statuses is not null) query = query.Where(p => statuses.Contains(p.Status));
        else if (includeRemoved != true) query = query.Where(p => p.Status != PreviewStatus.Removed);
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"id={id}&status={status}&removed={includeRemoved}", ct);
        return TypedResults.Ok(new Page<PreviewDto>(await ToDtosAsync(db, items), next));
    }

    private static async Task<Ok<PreviewDto>> Get(Guid id, int number, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        await RequireApplicationAsync(db, actor, id, ct);
        var preview = await db.Previews.AsNoTracking().FirstOrDefaultAsync(p => p.ApplicationId == id && p.Number == number, ct)
                      ?? throw new ApiProblemException(ApiProblems.NotFound("preview", number));
        return TypedResults.Ok((await ToDtosAsync(db, [preview]))[0]);
    }

    // ---- settings --------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<PreviewSettingsDto>> GetSettings(Guid id, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var app = await LoadApplicationAsync(db, actor, id, tracking: false, ct);
        return TypedResults.Ok(await ToSettingsAsync(db, app, ct));
    }

    private static async Task<Ok<PreviewSettingsDto>> PutSettings(
        Guid id, UpdatePreviewSettingsRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var org = actor.Org();
        var app = await LoadApplicationAsync(db, actor, id, tracking: true, ct);
        if (request.Enabled && !SupportsPreviews(app, out var reason))
            throw new ApiProblemException(ApiProblems.Conflict("previews.unsupported", reason));
        // Running the code of a fork is a trust decision about every stranger on the internet (ADR 0006): administrators only.
        if (request.AllowForks && !app.Previews.AllowForks && !actor.IsAdmin())
            throw new ApiProblemException(ApiProblems.Forbidden("Only an administrator can allow previews of pull requests from forks: their code runs with the application's variables."));
        if (request.ServerId is { } serverId) await db.GetServerAsync(org, serverId, tracking: false, ct);

        var before = app.Previews;
        app.Previews = new PreviewConfig { Enabled = request.Enabled, AllowForks = request.AllowForks, ServerId = request.ServerId, MaxActive = request.MaxActive };
        await audit.RecordAsync("application.previews_configured", "application", app.Id,
            new { enabled = request.Enabled, allowForks = request.AllowForks, serverId = request.ServerId, maxActive = request.MaxActive, wasEnabled = before.Enabled, hadForks = before.AllowForks }, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ToSettingsAsync(db, app, ct));
    }

    private static bool SupportsPreviews(Application app, out string reason)
    {
        reason = "";
        if (app.GitSource is null)
            reason = "Only applications built from a git repository can have previews: a preview builds the code of a pull request.";
        return reason.Length == 0;
    }

    private static async Task<PreviewSettingsDto> ToSettingsAsync(AetheraDbContext db, Application app, CancellationToken ct)
    {
        var supported = SupportsPreviews(app, out var reason);
        var effectiveServerId = app.Previews.ServerId ?? app.ServerId;
        var webhook = await db.WebhookEndpoints.AsNoTracking().Where(e => e.WorkloadId == app.Id).Select(e => new { e.Id, e.Provider, e.Enabled }).FirstOrDefaultAsync(ct);

        string? pattern = null;
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == effectiveServerId, ct);
        if (server is not null && DefaultDomainService.KnownBaseDomain(server) is { } baseDomain)
        {
            var automatic = await db.Domains.AsNoTracking().Where(d => d.WorkloadId == app.Id && d.IsAutomatic).OrderBy(d => d.DeletedAt != null).Select(d => d.Hostname).FirstOrDefaultAsync(ct);
            pattern = PreviewNaming.Hostname(1, automatic, app.Slug, baseDomain)?.Replace("pr-1-", "pr-<number>-", StringComparison.Ordinal);
        }

        return new PreviewSettingsDto(
            app.Previews.Enabled, app.Previews.AllowForks, app.Previews.ServerId, app.Previews.MaxActive, PreviewConfig.DefaultMaxActive, effectiveServerId, supported,
            supported ? null : reason, webhook is { Enabled: true }, webhook?.Provider, webhook is null ? null : $"/webhooks/git/{webhook.Id}", pattern);
    }

    // ---- redeploy and remove ---------------------------------------------------------------------------------------------------------

    private static async Task<IResult> Redeploy(
        Guid id, int number, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, PreviewService previews, CancellationToken ct)
    {
        var (app, preview) = await LoadPreviewAsync(db, actor, id, number, ct);
        QueuedDeployment queued;
        try
        {
            queued = await previews.RedeployAsync(app, preview, ct);
        }
        catch (PreviewConflictException ex)
        {
            throw new ApiProblemException(ApiProblems.Conflict(ex.Code, ex.Message));
        }

        await audit.RecordAsync("preview.redeploy_requested", "preview", preview.Id,
            new { applicationId = app.Id, number, deploymentId = queued.Deployment.Id }, ct);
        return Results.Accepted($"/api/v1/deployments/{queued.Deployment.Id}", DeploymentEndpoints.ToDto(queued.Deployment, includeSteps: false, queued.JobId));
    }

    private static async Task<IResult> Remove(Guid id, int number, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, PreviewService previews, CancellationToken ct)
    {
        var (app, preview) = await LoadPreviewAsync(db, actor, id, number, ct);
        var job = await previews.RemoveAsync(app, preview, ct);
        if (job is not null)
            await audit.RecordAsync("preview.teardown_requested", "preview", preview.Id, new { applicationId = app.Id, number, jobId = job.Id }, ct);
        return Results.Accepted($"/api/v1/applications/{id}/previews/{number}", (await ToDtosAsync(db, [preview]))[0]);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------

    private static async Task RequireApplicationAsync(AetheraDbContext db, ICurrentActor actor, Guid id, CancellationToken ct)
    {
        if (!await db.ApplicationsOf(actor.Org()).AnyAsync(a => a.Id == id, ct))
            throw new ApiProblemException(ApiProblems.NotFound("application", id));
    }

    private static async Task<Application> LoadApplicationAsync(AetheraDbContext db, ICurrentActor actor, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.ApplicationsOf(actor.Org()).Include(a => a.GitSource).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("application", id));
    }

    private static async Task<(Application App, PreviewEnvironment Preview)> LoadPreviewAsync(AetheraDbContext db, ICurrentActor actor, Guid id, int number, CancellationToken ct)
    {
        var app = await LoadApplicationAsync(db, actor, id, tracking: true, ct);
        var preview = await db.Previews.FirstOrDefaultAsync(p => p.ApplicationId == id && p.Number == number, ct)
                      ?? throw new ApiProblemException(ApiProblems.NotFound("preview", number));
        return (app, preview);
    }

    private static async Task<IReadOnlyList<PreviewDto>> ToDtosAsync(AetheraDbContext db, IReadOnlyList<PreviewEnvironment> previews)
    {
        if (previews.Count == 0) return [];
        var ids = previews.Select(p => p.Id).ToList();
        var rows = await db.Deployments.AsNoTracking().Where(d => d.PreviewId != null && ids.Contains(d.PreviewId.Value))
            .Select(d => new { PreviewId = d.PreviewId!.Value, d.Id, d.Number, d.Status, d.FailedStep, d.FailureCode, d.CommitSha, d.CreatedAt, d.FinishedAt })
            .ToListAsync();
        var latest = rows.GroupBy(r => r.PreviewId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Number).First());

        // The scheme the preview was deployed with: read from the snapshot of the live deployment only.
        var currentIds = previews.Where(p => p.CurrentDeploymentId != null).Select(p => p.CurrentDeploymentId!.Value).ToList();
        var snapshots = currentIds.Count == 0 ? [] : await db.Deployments.AsNoTracking().Where(d => currentIds.Contains(d.Id)).Select(d => new { d.Id, d.ConfigSnapshotJson }).ToListAsync();
        var https = snapshots.ToDictionary(s => s.Id, s => DeploymentSnapshot.TryFromJson(s.ConfigSnapshotJson, out var snapshot) && snapshot!.Domains.FirstOrDefault()?.Https == true);

        return previews.Select(p =>
        {
            var d = latest.GetValueOrDefault(p.Id);
            var scheme = p.CurrentDeploymentId is { } current && https.GetValueOrDefault(current) ? "https" : "http";
            return new PreviewDto(
                p.Id, p.ApplicationId, p.Number, p.Title, p.AuthorLogin, p.SourceBranch, p.HeadSha, p.FromFork, p.Provider, p.ServerId, p.Hostname,
                p.Hostname is { Length: > 0 } host && p.Status != PreviewStatus.Removed ? $"{scheme}://{host}" : null, p.Status, p.StatusReason, p.StatusChangedAt,
                p.LastEventAt, p.ClosedAt, p.RemovedAt, p.CommentError, p.CurrentDeploymentId,
                d is null ? null : new PreviewDeploymentDto(d.Id, d.Number, d.Status, d.FailedStep, d.FailureCode, d.CommitSha, d.CreatedAt, d.FinishedAt), p.CreatedAt);
        }).ToList();
    }
}
