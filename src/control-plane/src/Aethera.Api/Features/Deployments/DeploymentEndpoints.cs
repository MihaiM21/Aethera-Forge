using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Deployments;

public sealed record DeploymentStepDto(DeploymentStep Step, StepStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? ErrorCode, string? ErrorMessage);

public sealed record DeploymentDto(
    Guid Id, Guid ApplicationId, int Number, DeploymentStatus Status, DeploymentTrigger Trigger, string Strategy, DeploymentStep? CurrentStep,
    DeploymentStep? FailedStep, string? FailureCode, string? FailureReason, string? Ref, string? CommitSha, string? CommitMessage, string? CommitAuthor,
    string? ImageRef, string? ImageDigest, bool IsRollbackPoint, bool CanRollbackTo, Guid? RollbackOfDeploymentId, Guid? JobId, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, IReadOnlyList<DeploymentStepDto>? Steps);

/// <summary>Optional body of <c>POST /applications/{id}/deployments</c>.</summary>
public sealed record DeployRequest(string? Reason = null);

internal static class DeploymentEndpoints
{
    private static readonly SortDefinition<Deployment> Sorts = new SortDefinition<Deployment>("-number")
        .Add("number", d => d.Number).Add("createdAt", d => d.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var apps = api.MapGroup("/applications").WithTags("Deployments");
        apps.MapGet("/{id:guid}/deployments", List).WithName("listDeployments").RequireRead();
        apps.MapPost("/{id:guid}/deployments", Deploy).WithName("deployApplication").RequireDeploy()
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound);
        apps.MapPost("/{id:guid}/redeploy", Redeploy).WithName("redeployApplication").RequireDeploy().Produces<DeploymentDto>(StatusCodes.Status202Accepted);
        apps.MapPost("/{id:guid}/stop", (Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct) =>
            Lifecycle(id, LifecycleAction.Stop, db, actor, audit, svc, ct)).WithName("stopApplication").RequireDeploy().Produces<JobDto>(StatusCodes.Status202Accepted);
        apps.MapPost("/{id:guid}/start", (Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct) =>
            Lifecycle(id, LifecycleAction.Start, db, actor, audit, svc, ct)).WithName("startApplication").RequireDeploy().Produces<JobDto>(StatusCodes.Status202Accepted);
        apps.MapPost("/{id:guid}/restart", (Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct) =>
            Lifecycle(id, LifecycleAction.Restart, db, actor, audit, svc, ct)).WithName("restartApplication").RequireDeploy().Produces<JobDto>(StatusCodes.Status202Accepted);

        var deployments = api.MapGroup("/deployments").WithTags("Deployments");
        deployments.MapGet("/{id:guid}", Get).WithName("getDeployment").RequireRead();
        deployments.MapPost("/{id:guid}/rollback", Rollback).WithName("rollbackToDeployment").RequireDeploy()
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>Developer role and the <c>deploy</c> scope.</summary>
    private static TBuilder RequireDeploy<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Deploy);

    private static IQueryable<Deployment> DeploymentsOf(AetheraDbContext db, Guid org) =>
        db.Deployments.Where(d => d.Workload.Environment.Project.OrganizationId == org);

    private static async Task<Ok<Page<DeploymentDto>>> List(
        Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, string? status, CancellationToken ct)
    {
        http.RejectUnknownQuery("status");
        await RequireApplicationAsync(db, actor, id, ct);
        var statuses = ResourceHttp.ParseEnumFilter<DeploymentStatus>(status, "status");
        var query = DeploymentsOf(db, actor.Org()).AsNoTracking().Where(d => d.WorkloadId == id);
        if (statuses is not null) query = query.Where(d => statuses.Contains(d.Status));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"id={id}&status={status}", ct);
        return TypedResults.Ok(new Page<DeploymentDto>(items.Select(d => ToDto(d, includeSteps: false)).ToList(), next));
    }

    private static async Task<Ok<DeploymentDto>> Get(Guid id, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var d = await DeploymentsOf(db, actor.Org()).AsNoTracking().Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new ApiProblemException(ApiProblems.NotFound("deployment", id));
        return TypedResults.Ok(ToDto(d, includeSteps: true));
    }

    private static Task<IResult> Deploy(Guid id, DeployRequest? request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct) =>
        Queue(id, DeploymentTrigger.Manual, db, actor, audit, svc, ct);

    private static Task<IResult> Redeploy(Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct) =>
        Queue(id, DeploymentTrigger.Redeploy, db, actor, audit, svc, ct);

    private static async Task<IResult> Queue(
        Guid applicationId, DeploymentTrigger trigger, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct)
    {
        await RequireApplicationAsync(db, actor, applicationId, ct);
        var queued = await Translate(() => svc.DeployAsync(applicationId, trigger, null, ct));
        var deployment = queued.Deployment;
        await audit.RecordAsync("application.deploy_requested", "application", applicationId, new { deploymentId = deployment.Id, number = deployment.Number, trigger }, ct);
        return Results.Accepted($"/api/v1/deployments/{deployment.Id}", ToDto(deployment, includeSteps: false, queued.JobId));
    }

    private static async Task<IResult> Rollback(Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct)
    {
        var target = await DeploymentsOf(db, actor.Org()).AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
                     ?? throw new ApiProblemException(ApiProblems.NotFound("deployment", id));
        var queued = await Translate(() => svc.RollbackAsync(target.WorkloadId, id, ct));
        var deployment = queued.Deployment;
        await audit.RecordAsync("application.rollback_requested", "application", target.WorkloadId,
            new { deploymentId = deployment.Id, rollbackTo = id, targetNumber = target.Number }, ct);
        return Results.Accepted($"/api/v1/deployments/{deployment.Id}", ToDto(deployment, includeSteps: false, queued.JobId));
    }

    private static async Task<IResult> Lifecycle(
        Guid id, LifecycleAction action, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct)
    {
        await RequireApplicationAsync(db, actor, id, ct);
        var job = await Translate(() => svc.LifecycleAsync(id, action, ct));
        await audit.RecordAsync($"application.{action.ToString().ToLowerInvariant()}_requested", "application", id, new { jobId = job.Id }, ct);
        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, ct);
        return Results.Accepted(dto.Links.Self, dto);
    }

    private static async Task RequireApplicationAsync(AetheraDbContext db, ICurrentActor actor, Guid id, CancellationToken ct)
    {
        if (!await db.ApplicationsOf(actor.Org()).AsNoTracking().AnyAsync(a => a.Id == id, ct))
            throw new ApiProblemException(ApiProblems.NotFound("application", id));
    }

    private static async Task<T> Translate<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DeploymentConflictException ex)
        {
            throw new ApiProblemException(ApiProblems.Conflict(ex.Code, ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            throw new ApiProblemException(ApiProblems.NotFound(ex.Message));
        }
    }

    internal static DeploymentDto ToDto(Deployment d, bool includeSteps, Guid? jobId = null) => new(
        d.Id, d.WorkloadId, d.Number, d.Status, d.Trigger, d.Strategy, d.CurrentStep, d.FailedStep, d.FailureCode, d.FailureReason, d.Ref, d.CommitSha,
        d.CommitMessage, d.CommitAuthor, d.ImageRef, d.ImageDigest, d.IsRollbackPoint, d.CanRollbackTo, d.RollbackOfDeploymentId, d.JobId ?? jobId, d.CreatedAt,
        d.StartedAt, d.FinishedAt, d.Duration is { } t ? (long)t.TotalMilliseconds : null,
        includeSteps
            ? d.Steps.OrderBy(s => s.Step).Select(s => new DeploymentStepDto(s.Step, s.Status, s.StartedAt, s.FinishedAt, s.ErrorCode, s.ErrorMessage)).ToList()
            : null);
}
