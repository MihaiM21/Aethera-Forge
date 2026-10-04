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
        // Applications and services deploy through the same engine: the id is a workload id, the route only decides which kind it must be.
        MapWorkload(api, "applications", "Application", "application");
        MapWorkload(api, "services", "Service", "service");

        var deployments = api.MapGroup("/deployments").WithTags("Deployments");
        deployments.MapGet("/{id:guid}", Get).WithName("getDeployment").RequireRead();
        deployments.MapPost("/{id:guid}/rollback", Rollback).WithName("rollbackToDeployment").RequireDeploy()
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapWorkload(IEndpointRouteBuilder api, string prefix, string noun, string kind)
    {
        var group = api.MapGroup($"/{prefix}").WithTags("Deployments");
        group.MapGet("/{id:guid}/deployments", (Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, string? status, CancellationToken ct) =>
            List(id, kind, http, db, actor, cursors, page, sort, status, ct)).WithName($"list{noun}Deployments").RequireRead();
        group.MapPost("/{id:guid}/deployments", (Guid id, DeployRequest? request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct) =>
            Queue(id, kind, DeploymentTrigger.Manual, db, actor, audit, svc, ct)).WithName($"deploy{noun}").RequireDeploy()
            .Produces<DeploymentDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/{id:guid}/redeploy", (Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct) =>
            Queue(id, kind, DeploymentTrigger.Redeploy, db, actor, audit, svc, ct)).WithName($"redeploy{noun}").RequireDeploy().Produces<DeploymentDto>(StatusCodes.Status202Accepted);
        foreach (var (action, name) in new[] { (LifecycleAction.Stop, "stop"), (LifecycleAction.Start, "start"), (LifecycleAction.Restart, "restart") })
        {
            var captured = action;
            group.MapPost($"/{{id:guid}}/{name}", (Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct) =>
                Lifecycle(id, kind, captured, db, actor, audit, svc, ct)).WithName($"{name}{noun}").RequireDeploy().Produces<JobDto>(StatusCodes.Status202Accepted);
        }
    }

    /// <summary>Developer role and the <c>deploy</c> scope.</summary>
    private static TBuilder RequireDeploy<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Deploy);

    private static IQueryable<Deployment> DeploymentsOf(AetheraDbContext db, Guid org) =>
        db.Deployments.Where(d => d.Workload.Environment.Project.OrganizationId == org);

    private static async Task<Ok<Page<DeploymentDto>>> List(
        Guid id, string kind, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, PageRequest page, string? sort, string? status, CancellationToken ct)
    {
        http.RejectUnknownQuery("status");
        await RequireWorkloadAsync(db, actor, id, kind, ct);
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

    private static async Task<IResult> Queue(
        Guid applicationId, string kind, DeploymentTrigger trigger, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct)
    {
        await RequireWorkloadAsync(db, actor, applicationId, kind, ct);
        var queued = await Translate(() => svc.DeployAsync(applicationId, trigger, null, ct));
        var deployment = queued.Deployment;
        await audit.RecordAsync($"{kind}.deploy_requested", kind, applicationId, new { deploymentId = deployment.Id, number = deployment.Number, trigger }, ct);
        return Results.Accepted($"/api/v1/deployments/{deployment.Id}", ToDto(deployment, includeSteps: false, queued.JobId));
    }

    private static async Task<IResult> Rollback(Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct)
    {
        var target = await DeploymentsOf(db, actor.Org()).AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
                     ?? throw new ApiProblemException(ApiProblems.NotFound("deployment", id));
        var queued = await Translate(() => svc.RollbackAsync(target.WorkloadId, id, ct));
        var deployment = queued.Deployment;
        await audit.RecordAsync($"{await KindOfAsync(db, target.WorkloadId, ct)}.rollback_requested", await KindOfAsync(db, target.WorkloadId, ct), target.WorkloadId,
            new { deploymentId = deployment.Id, rollbackTo = id, targetNumber = target.Number }, ct);
        return Results.Accepted($"/api/v1/deployments/{deployment.Id}", ToDto(deployment, includeSteps: false, queued.JobId));
    }

    private static async Task<IResult> Lifecycle(
        Guid id, string kind, LifecycleAction action, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DeploymentService svc, CancellationToken ct)
    {
        await RequireWorkloadAsync(db, actor, id, kind, ct);
        var job = await Translate(() => svc.LifecycleAsync(id, action, ct));
        await audit.RecordAsync($"{kind}.{action.ToString().ToLowerInvariant()}_requested", kind, id, new { jobId = job.Id }, ct);
        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, ct);
        return Results.Accepted(dto.Links.Self, dto);
    }

    /// <summary>404 unless the workload exists in the callers organization and is of the kind the route names.</summary>
    internal static async Task RequireWorkloadAsync(AetheraDbContext db, ICurrentActor actor, Guid id, string kind, CancellationToken ct)
    {
        var org = actor.Org();
        var query = db.Workloads.AsNoTracking().Where(w => w.Id == id && w.Environment.Project.OrganizationId == org);
        var exists = kind == "service" ? await query.OfType<Service>().AnyAsync(ct) : await query.OfType<Application>().AnyAsync(ct);
        if (!exists) throw new ApiProblemException(ApiProblems.NotFound(kind, id));
    }

    private static async Task<string> KindOfAsync(AetheraDbContext db, Guid workloadId, CancellationToken ct) =>
        await db.Workloads.AsNoTracking().OfType<Service>().AnyAsync(s => s.Id == workloadId, ct) ? "service" : "application";

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
