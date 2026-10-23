using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Backups;

/// <summary>Backup policies of workloads and of Aethera's own database (ADR 0010).</summary>
internal static class BackupPolicyEndpoints
{
    private static readonly SortDefinition<BackupPolicy> Sorts = new SortDefinition<BackupPolicy>("-createdAt")
        .Add("createdAt", p => p.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var policies = api.MapGroup("/backup-policies").WithTags("Backups");
        policies.MapGet("/", List).WithName("listBackupPolicies").RequireRead();
        policies.MapGet("/{id:guid}", Get).WithName("getBackupPolicy").RequireRead();
        policies.MapDelete("/{id:guid}", Delete).WithName("deleteBackupPolicy").RequireWrite();
        policies.MapPost("/{id:guid}/run", Run).WithName("runBackupPolicy").RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Deploy)
            .WithSummary("Back up now")
            .Produces<JobDto>(StatusCodes.Status202Accepted);

        foreach (var (prefix, noun, application) in new[] { ("applications", "Application", true), ("services", "Service", false) })
        {
            var group = api.MapGroup($"/{prefix}/{{id:guid}}/backup-policies").WithTags("Backups");
            group.MapGet("/", (Guid id, AetheraDbContext db, ICurrentActor actor, IClock clock, CancellationToken ct) => ForWorkload(id, application, db, actor, clock, ct))
                .WithName($"list{noun}BackupPolicies").RequireRead()
                .WithSummary("The backup policies of a workload and what can be backed up (its database, each named volume)");
            group.MapPut("/", (Guid id, PutBackupPolicyRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct) =>
                    Put(id, application, request, db, actor, audit, clock, ct))
                .WithName($"put{noun}BackupPolicy").Validate<PutBackupPolicyRequest>().RequireWrite()
                .WithSummary("Create or replace the policy of the database or of one volume");
        }

        var system = api.MapGroup("/system/backup-policy").WithTags("Backups");
        system.MapGet("/", GetSystem).WithName("getSystemBackupPolicy").RequireAdmin(Scopes.Read);
        system.MapPut("/", PutSystem).WithName("putSystemBackupPolicy").Validate<PutSystemBackupPolicyRequest>().RequireAdmin();
    }

    // ---- reads --------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<BackupPolicyResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, IClock clock, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, Guid? workloadId,
        CancellationToken ct)
    {
        http.RejectUnknownQuery("workloadId");
        var org = actor.Org();
        var query = db.BackupPolicies.Where(p => p.OrganizationId == org && p.Kind != BackupKind.ControlPlane).AsNoTracking();
        if (workloadId is { } w) query = query.Where(p => p.WorkloadId == w);
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workloadId={workloadId}", ct);
        return TypedResults.Ok(new Page<BackupPolicyResponse>(await ToResponsesAsync(db, items, clock.UtcNow, ct), next));
    }

    private static async Task<Ok<BackupPolicyResponse>> Get(Guid id, AetheraDbContext db, ICurrentActor actor, IClock clock, CancellationToken ct)
    {
        var policy = await FindAsync(db, actor, id, ct);
        return TypedResults.Ok((await ToResponsesAsync(db, [policy], clock.UtcNow, ct))[0]);
    }

    private static async Task<Ok<WorkloadBackupsResponse>> ForWorkload(Guid id, bool application, AetheraDbContext db, ICurrentActor actor, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        await WorkloadSupport.RequireWorkloadRouteAsync(db, org, application, id, ct);
        var workload = await db.Workloads.AsNoTracking().Include(w => w.Volumes).FirstAsync(w => w.Id == id, ct);
        var policies = await db.BackupPolicies.AsNoTracking().Where(p => p.WorkloadId == id).OrderBy(p => p.CreatedAt).ToListAsync(ct);

        var targets = new List<BackupTargetResponse>();
        if (workload is Service service)
        {
            var engine = BackupEngines.ForTemplate(service.TemplateKey);
            targets.Add(new BackupTargetResponse(BackupKind.Database, null, service.TemplateKey, null, engine is not null, engine?.Name,
                engine is null ? $"Database backups are not available for the '{service.TemplateKey}' template. Back up its volumes instead." : null));
        }
        else
            targets.Add(new BackupTargetResponse(BackupKind.Database, null, "Database", null, false, null, "Applications have volume backups only."));

        foreach (var volume in workload.Volumes.OrderBy(v => v.CreatedAt))
        {
            var bind = volume.HostPath is { Length: > 0 };
            targets.Add(new BackupTargetResponse(BackupKind.Volume, volume.Id, volume.Name, volume.MountPath, !bind, "volume",
                bind ? "Bind mounts are not backed up; only named volumes are." : null));
        }

        return TypedResults.Ok(new WorkloadBackupsResponse(await ToResponsesAsync(db, policies, clock.UtcNow, ct), targets));
    }

    private static async Task<Ok<SystemBackupPolicyResponse>> GetSystem(AetheraDbContext db, ICurrentActor actor, IClock clock, CancellationToken ct)
    {
        var policy = await db.BackupPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.OrganizationId == actor.Org() && p.Kind == BackupKind.ControlPlane, ct);
        return TypedResults.Ok(new SystemBackupPolicyResponse(
            policy is null ? null : (await ToResponsesAsync(db, [policy], clock.UtcNow, ct))[0], BackupPolicyRules.DefaultSchedule));
    }

    // ---- writes -------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<BackupPolicyResponse>> Put(
        Guid workloadId, bool application, PutBackupPolicyRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        await WorkloadSupport.RequireWorkloadRouteAsync(db, org, application, workloadId, ct);
        var workload = await db.Workloads.Include(w => w.Volumes).FirstAsync(w => w.Id == workloadId, ct);
        var errors = new List<FieldError>();
        var destination = await db.BackupDestinations.FirstOrDefaultAsync(d => d.OrganizationId == org && d.Id == request.DestinationId, ct);
        if (destination is null) errors.Add(FieldError.AtPointer("/destinationId", "not_found", "No backup destination with this id exists."));

        var kind = request.Kind!.Value;
        Volume? volume = null;
        if (kind == BackupKind.Database)
        {
            if (workload is not Service service || !BackupEngines.Supports(service.TemplateKey))
                errors.Add(FieldError.AtPointer("/kind", BackupProblemCodes.PolicyNotSupported, workload is Service s
                    ? $"Database backups are not available for the '{s.TemplateKey}' template."
                    : "Applications have volume backups only."));
        }
        else
        {
            volume = workload.Volumes.FirstOrDefault(v => v.Id == request.VolumeId);
            if (volume is null) errors.Add(FieldError.AtPointer("/volumeId", "not_found", "This workload has no volume with this id."));
            else if (volume.HostPath is { Length: > 0 }) errors.Add(FieldError.AtPointer("/volumeId", BackupProblemCodes.PolicyNotSupported, "Bind mounts are not backed up; only named volumes are."));
        }

        var now = clock.UtcNow;
        BackupPolicyRules.ValidateSchedule(request.Schedule, request.TimeZone, "", errors, now);
        if (errors.Count > 0) throw new ApiProblemException(ApiProblems.Validation(errors));

        Guid? volumeId = kind == BackupKind.Volume ? volume!.Id : null;
        var policy = await db.BackupPolicies.FirstOrDefaultAsync(p => p.WorkloadId == workloadId && p.Kind == kind && p.VolumeId == volumeId, ct);
        var created = policy is null;
        policy ??= new BackupPolicy { OrganizationId = org, WorkloadId = workloadId, Kind = kind, VolumeId = volume?.Id, DestinationId = destination!.Id };
        Apply(policy, destination!, request.Enabled, request.Schedule, request.TimeZone, request.KeepDaily, request.KeepWeekly, request.KeepMonthly, now);
        if (created) db.BackupPolicies.Add(policy);

        await audit.RecordAsync(created ? "backup_policy.created" : "backup_policy.updated", "backup_policy", policy.Id, Describe(policy, workload.Name), ct);
        return TypedResults.Ok((await ToResponsesAsync(db, [policy], now, ct))[0]);
    }

    private static async Task<Ok<SystemBackupPolicyResponse>> PutSystem(
        PutSystemBackupPolicyRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        var errors = new List<FieldError>();
        var destination = await db.BackupDestinations.FirstOrDefaultAsync(d => d.OrganizationId == org && d.Id == request.DestinationId, ct);
        if (destination is null) errors.Add(FieldError.AtPointer("/destinationId", "not_found", "No backup destination with this id exists."));
        else if (destination.Kind != BackupDestinationKind.S3)
            errors.Add(FieldError.AtPointer("/destinationId", "s3_required", "Aethera's own database can only be backed up to an S3-compatible destination."));
        var now = clock.UtcNow;
        BackupPolicyRules.ValidateSchedule(request.Schedule, request.TimeZone, "", errors, now);
        if (errors.Count > 0) throw new ApiProblemException(ApiProblems.Validation(errors));

        var policy = await db.BackupPolicies.FirstOrDefaultAsync(p => p.OrganizationId == org && p.Kind == BackupKind.ControlPlane, ct);
        var created = policy is null;
        policy ??= new BackupPolicy { OrganizationId = org, Kind = BackupKind.ControlPlane, DestinationId = destination!.Id };
        Apply(policy, destination!, request.Enabled, request.Schedule, request.TimeZone, request.KeepDaily, request.KeepWeekly, request.KeepMonthly, now);
        if (created) db.BackupPolicies.Add(policy);
        await audit.RecordAsync(created ? "backup_policy.created" : "backup_policy.updated", "backup_policy", policy.Id, Describe(policy, "Aethera"), ct);
        return TypedResults.Ok(new SystemBackupPolicyResponse((await ToResponsesAsync(db, [policy], now, ct))[0], BackupPolicyRules.DefaultSchedule));
    }

    private static async Task<NoContent> Delete(Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var policy = await FindAsync(db, actor, id, ct, tracking: true);
        // Removing the policy removes its run history rows (the stored objects stay in the destination until someone deletes them there).
        db.BackupPolicies.Remove(policy);
        await audit.RecordAsync("backup_policy.deleted", "backup_policy", id, new { kind = policy.Kind, workloadId = policy.WorkloadId }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Run(Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, BackupJobService jobs, CancellationToken ct)
    {
        var policy = await FindAsync(db, actor, id, ct, tracking: false, includeSystem: true);
        if (policy.Kind == BackupKind.ControlPlane && !actor.IsAdmin()) return ApiProblems.Forbidden("Only an Administrator can back up Aethera's own database.");
        var (run, job) = await jobs.EnqueueRunAsync(policy, BackupTrigger.Manual, null, ct);
        await audit.RecordAsync("backup_policy.run_requested", "backup_policy", id, new { runId = run.Id, jobId = job.Id, kind = policy.Kind }, ct);
        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, ct);
        return Results.Accepted(dto.Links.Self, dto);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    private static void Apply(
        BackupPolicy policy, BackupDestination destination, bool? enabled, string? schedule, string? timeZone, int? daily, int? weekly, int? monthly, DateTimeOffset now)
    {
        var scheduleChanged = policy.NextRunAt is null || (schedule ?? BackupPolicyRules.DefaultSchedule) != policy.Schedule || (timeZone ?? "UTC") != policy.TimeZone;
        policy.DestinationId = destination.Id;
        policy.Destination = destination;
        policy.Enabled = enabled ?? true;
        policy.Schedule = (schedule ?? BackupPolicyRules.DefaultSchedule).Trim();
        policy.TimeZone = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim();
        policy.KeepDaily = daily ?? 7;
        policy.KeepWeekly = weekly ?? 4;
        policy.KeepMonthly = monthly ?? 6;
        if (!policy.Enabled) policy.NextRunAt = null;
        else if (scheduleChanged || policy.NextRunAt is null)
            policy.NextRunAt = CronSchedule.Parse(policy.Schedule, policy.TimeZone).Next(now);
    }

    private static object Describe(BackupPolicy p, string name) => new
    {
        name, kind = p.Kind, volumeId = p.VolumeId, destinationId = p.DestinationId, enabled = p.Enabled, schedule = p.Schedule, timeZone = p.TimeZone,
        keepDaily = p.KeepDaily, keepWeekly = p.KeepWeekly, keepMonthly = p.KeepMonthly,
    };

    /// <summary>A workload policy of the organization (404 otherwise). Aethera's own policy is only reachable through <c>/system/backup-policy</c> unless <paramref name="includeSystem"/>.</summary>
    private static async Task<BackupPolicy> FindAsync(AetheraDbContext db, ICurrentActor actor, Guid id, CancellationToken ct, bool tracking = false, bool includeSystem = false)
    {
        var org = actor.Org();
        var query = db.BackupPolicies.Where(p => p.OrganizationId == org && (includeSystem || p.Kind != BackupKind.ControlPlane));
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("backup_policy", id));
    }


    internal static async Task<IReadOnlyList<BackupPolicyResponse>> ToResponsesAsync(AetheraDbContext db, IReadOnlyCollection<BackupPolicy> policies, DateTimeOffset now, CancellationToken ct)
    {
        if (policies.Count == 0) return [];
        var ids = policies.Select(p => p.Id).ToList();
        var destinationIds = policies.Select(p => p.DestinationId).Distinct().ToList();
        var destinations = await db.BackupDestinations.AsNoTracking().Where(d => destinationIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var volumeIds = policies.Where(p => p.VolumeId is not null).Select(p => p.VolumeId!.Value).ToList();
        var volumes = volumeIds.Count == 0 ? [] : await db.Volumes.AsNoTracking().Where(v => volumeIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var runs = await db.BackupRuns.AsNoTracking().Where(r => ids.Contains(r.PolicyId))
            .GroupBy(r => r.PolicyId)
            .Select(g => new
            {
                Policy = g.Key,
                Last = g.OrderByDescending(r => r.CreatedAt).Select(r => (BackupRunStatus?)r.Status).FirstOrDefault(),
                Success = g.Where(r => r.Status == BackupRunStatus.Succeeded).Max(r => r.FinishedAt),
            }).ToDictionaryAsync(x => x.Policy, ct);

        return policies.Select(p =>
        {
            var next = CronSchedule.TryParse(p.Schedule, p.TimeZone, out var schedule, out _) ? NextRuns(schedule, now) : [];
            runs.TryGetValue(p.Id, out var summary);
            return new BackupPolicyResponse(p.Id, p.WorkloadId, p.Kind, p.VolumeId, p.VolumeId is { } v && volumes.TryGetValue(v, out var vn) ? vn : null, p.DestinationId,
                destinations.GetValueOrDefault(p.DestinationId, ""), p.Enabled, p.Schedule, p.TimeZone, p.KeepDaily, p.KeepWeekly, p.KeepMonthly, p.NextRunAt, p.LastRunAt,
                next, summary?.Last, summary?.Success, p.CreatedAt, p.UpdatedAt);
        }).ToList();
    }

    private static IReadOnlyList<DateTimeOffset> NextRuns(CronSchedule schedule, DateTimeOffset now)
    {
        var result = new List<DateTimeOffset>();
        var cursor = now;
        for (var i = 0; i < 3 && schedule.Next(cursor) is { } next; i++)
        {
            result.Add(next);
            cursor = next;
        }

        return result;
    }
}
