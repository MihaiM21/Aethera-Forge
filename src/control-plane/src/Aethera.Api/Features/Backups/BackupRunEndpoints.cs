using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Backups;

/// <summary>The history of backups and their restore (ADR 0010).</summary>
internal static class BackupRunEndpoints
{
    private static readonly SortDefinition<BackupRun> Sorts = new SortDefinition<BackupRun>("-createdAt").Add("createdAt", r => r.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/backup-runs").WithTags("Backups");
        group.MapGet("/", List).WithName("listBackupRuns").RequireRead();
        group.MapGet("/{id:guid}", Get).WithName("getBackupRun").RequireRead();
        group.MapPost("/{id:guid}/restore", Restore).WithName("restoreBackupRun")
            .Validate<RestoreBackupRequest>().RequireAdmin(Scopes.Deploy)
            .WithSummary("Restore a backup into the same service (overwriting its data, after a safety backup) or into a new service")
            .Produces<JobDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static IQueryable<BackupRun> RunsOf(AetheraDbContext db, ICurrentActor actor)
    {
        var org = actor.Org();
        var admin = actor.IsAdmin();
        // The runs of Aethera's own database are administrator business: they name object keys of the whole installation.
        return db.BackupRuns.Where(r => r.OrganizationId == org && (admin || r.Kind != BackupKind.ControlPlane));
    }

    private static async Task<Ok<Page<BackupRunResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, Guid? workloadId, Guid? policyId,
        string? status, CancellationToken ct)
    {
        http.RejectUnknownQuery("workloadId", "policyId", "status");
        var statuses = ResourceHttp.ParseEnumFilter<BackupRunStatus>(status, "status");
        var query = RunsOf(db, actor).AsNoTracking();
        if (workloadId is { } w) query = query.Where(r => r.WorkloadId == w);
        if (policyId is { } p) query = query.Where(r => r.PolicyId == p);
        if (statuses is not null) query = query.Where(r => statuses.Contains(r.Status));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workloadId={workloadId}&policyId={policyId}&status={status}", ct);
        return TypedResults.Ok(new Page<BackupRunResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Ok<BackupRunResponse>> Get(Guid id, AetheraDbContext db, ICurrentActor actor, CancellationToken ct) =>
        TypedResults.Ok(ToResponse(await RunsOf(db, actor).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
                                   ?? throw new ApiProblemException(ApiProblems.NotFound("backup_run", id))));

    private static async Task<IResult> Restore(
        Guid id, RestoreBackupRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IJobQueue jobs, CancellationToken ct)
    {
        var org = actor.Org();
        var run = await db.BackupRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.OrganizationId == org, ct)
                  ?? throw new ApiProblemException(ApiProblems.NotFound("backup_run", id));
        if (run.Kind == BackupKind.ControlPlane)
            throw new ApiProblemException(ApiProblems.Conflict(BackupProblemCodes.RunNotRestorable,
                "Aethera's own database is restored on the host: download the object from the destination, run `aethera backup-decrypt` and then `aethera restore`. See docs/backups.md."));
        if (run.Status != BackupRunStatus.Succeeded || run.PrunedAt is not null)
            throw new ApiProblemException(ApiProblems.Conflict(BackupProblemCodes.RunNotRestorable,
                run.PrunedAt is not null ? "This backup was removed by the retention policy: the stored object no longer exists." : "Only a backup that succeeded can be restored."));

        var workload = run.WorkloadId is { } workloadId ? await db.Workloads.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workloadId, ct) : null;
        if (workload is null)
            throw new ApiProblemException(ApiProblems.Conflict(BackupProblemCodes.RunNotRestorable, "The application or service this backup belongs to no longer exists."));

        if (request.Target == "new")
        {
            if (run.Kind != BackupKind.Database || workload is not Service)
                throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/target", "unsupported",
                    "Only the backup of a service's database can be restored into a new service. Restore a volume backup into the same workload.")]));
            var slug = WorkloadSupport.ResolveSlug(null, request.NewServiceName!.Trim(), "/newServiceName");
            await WorkloadSupport.EnsureSlugFreeAsync(db, workload.EnvironmentId, slug, "service", null, ct);
        }

        Confirmation.Require(request.Confirm, workload.Name);

        var job = await jobs.EnqueueAsync(new JobRequest(BackupJobs.RestoreType, new BackupRestorePayload { RunId = run.Id, Target = request.Target!, NewServiceName = request.NewServiceName?.Trim() })
        {
            OrganizationId = org, Resource = new JobResource("application", workload.Id), LockKey = BackupJobs.LockKey(org, workload.Id), MaxAttempts = 1,
        }, ct);
        await audit.RecordAsync("backup.restore_requested", "backup_run", run.Id,
            new { workload = workload.Name, workloadId = workload.Id, target = request.Target, newServiceName = request.NewServiceName, jobId = job.Id }, ct);
        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, ct);
        return Results.Accepted(dto.Links.Self, dto);
    }

    internal static BackupRunResponse ToResponse(BackupRun r) => new(
        r.Id, r.PolicyId, r.WorkloadId, r.DestinationId, r.Kind, r.Trigger, r.Status, r.JobId, r.Engine, r.ObjectKey, r.RawSizeBytes, r.StoredSizeBytes, r.StoredSha256,
        r.KeyId, r.Error, r.CreatedAt, r.StartedAt, r.FinishedAt, r.StartedAt is { } s && r.FinishedAt is { } f ? (long)(f - s).TotalMilliseconds : null, r.PrunedAt,
        r.Status == BackupRunStatus.Succeeded && r.PrunedAt is null && r.Kind != BackupKind.ControlPlane);
}

/// <summary>Payload of <c>backup.restore</c>: ids and choices only.</summary>
public sealed class BackupRestorePayload
{
    public Guid RunId { get; set; }

    /// <summary><c>same</c> or <c>new</c>.</summary>
    public string Target { get; set; } = "same";

    public string? NewServiceName { get; set; }
}
