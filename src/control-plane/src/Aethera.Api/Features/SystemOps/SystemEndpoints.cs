using Aethera.Api.Features.Agents;
using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.SelfOps;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aethera.Api.Features.SystemOps;

public sealed record ReleaseDto(string Version, string Url, string? Notes, bool PreRelease, DateTimeOffset? PublishedAt);

/// <summary>What the updater container reports. <c>available</c> is false when no updater is running: updates are then done on the host with <c>aethera update</c>.</summary>
public sealed record UpdaterDto(bool Available, string State, string? TargetVersion, string? Message, DateTimeOffset? UpdatedAt);

public sealed record SystemVersionDto(
    string Current, ReleaseDto? Latest, bool UpdateAvailable, bool CheckEnabled, DateTimeOffset? CheckedAt, string? CheckError, UpdaterDto Updater);

/// <summary>Optional: the version to install. Only the newest release can be installed from here.</summary>
public sealed record StartUpdateRequest(string? Version = null);

public sealed record BackupDto(string Name, long SizeBytes, DateTimeOffset CreatedAt, string Kind);

public sealed record MaintenanceSettingsDto(
    bool Enabled, int LogRetentionDays, int EventRetentionDays, int AuditRetentionDays, int SessionRetentionDays,
    bool PruneEnabled, double PruneIntervalHours, int PruneKeepHours, bool BackupEnabled, double BackupIntervalHours, int BackupsKept);

public sealed record MaintenanceReportDto(
    DateTimeOffset At, long LogChunksDeleted, long EventsDeleted, long WebhookDeliveriesDeleted, long AuditEventsDeleted, long SessionsDeleted,
    long IdempotencyRecordsDeleted, int PruneJobsQueued, string? BackupTaken, int BackupsDeleted, IReadOnlyList<string> Errors);

public sealed record MaintenanceDto(
    MaintenanceSettingsDto Settings, bool BackupAvailable, IReadOnlyList<BackupDto> Backups, DateTimeOffset? LastBackupAt, DateTimeOffset? LastPruneAt, MaintenanceReportDto? LastRun);

public sealed record UpdateAgentRequest(string? Version = null);

/// <summary>Version, update and maintenance endpoints (WP5.2).</summary>
internal static class SystemEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var system = api.MapGroup("/system").WithTags("System");

        system.MapGet("/version", GetVersion).WithName("getSystemVersion")
            .WithSummary("The running version, the newest release and the state of the updater")
            .WithDescription("`latest` comes from the last release check (GitHub, every few hours; `checkEnabled` false = not looked up). `updater.available` is false when no updater container is running.")
            .RequireRead().Produces<SystemVersionDto>();
        system.MapPost("/version/check", CheckNow).WithName("checkSystemVersion")
            .WithSummary("Look for a new release now")
            .RequireAdmin(Scopes.Admin).Produces<SystemVersionDto>();
        system.MapPost("/update", StartUpdate).WithName("startSystemUpdate")
            .WithSummary("Update the control plane to the newest release")
            .WithDescription("Asks the updater container to back up the database, pull the new images and restart the stack. The API restarts: poll `GET /system/version` until it answers again.")
            .RequireAdmin(Scopes.Admin).Produces<UpdaterDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        system.MapGet("/maintenance", GetMaintenance).WithName("getSystemMaintenance")
            .WithSummary("Retention settings, database backups and the last maintenance run")
            .RequireAdmin(Scopes.Read).Produces<MaintenanceDto>();
        system.MapPost("/maintenance/run", RunMaintenance).WithName("runSystemMaintenance")
            .WithSummary("Run maintenance now: retention, a database backup and a prune of every connected server")
            .RequireAdmin(Scopes.Admin).Produces<MaintenanceReportDto>();

        api.MapPost("/servers/{id:guid}/agent/update", UpdateAgent).WithName("updateServerAgent").WithTags("Servers")
            .WithSummary("Update the agent on a server to the newest release")
            .WithDescription("A job: the agent downloads the release binary over HTTPS, verifies its SHA-256, swaps itself and rolls back if the new one does not reconnect.")
            .RequireAdmin(Scopes.ServersWrite).Produces<JobDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<SystemVersionDto> BuildVersion(ReleaseChecker checker, UpdaterControl updater, CancellationToken ct)
    {
        var latest = checker.Latest;
        var status = await updater.GetStatusAsync(ct);
        return new SystemVersionDto(
            checker.CurrentVersion,
            latest is null ? null : new ReleaseDto(latest.Version, latest.Url, latest.Notes, latest.PreRelease, latest.PublishedAt),
            checker.UpdateAvailable, checker.Enabled, checker.CheckedAt, checker.LastError,
            new UpdaterDto(status.Available, status.State, status.TargetVersion, status.Message, status.UpdatedAt));
    }

    private static async Task<Ok<SystemVersionDto>> GetVersion(ReleaseChecker checker, UpdaterControl updater, CancellationToken ct) =>
        TypedResults.Ok(await BuildVersion(checker, updater, ct));

    private static async Task<Ok<SystemVersionDto>> CheckNow(ReleaseChecker checker, UpdaterControl updater, CancellationToken ct)
    {
        await checker.CheckAsync(ct);
        return TypedResults.Ok(await BuildVersion(checker, updater, ct));
    }

    private static async Task<IResult> StartUpdate(
        StartUpdateRequest? request, ReleaseChecker checker, UpdaterControl updater, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var latest = checker.Latest ?? await checker.CheckAsync(ct);
        if (latest is null || !checker.UpdateAvailable)
            return new ApiProblem(StatusCodes.Status422UnprocessableEntity, "system.no_update_available", "This is already the newest release.");
        if (request?.Version is { Length: > 0 } wanted && !(SemanticVersion.TryParse(wanted, out var w) && w == SemanticVersion.Parse(latest.Version)))
            return new ApiProblem(StatusCodes.Status422UnprocessableEntity, "system.version_unavailable", $"Only the newest release ({latest.Version}) can be installed from here.");

        if (!updater.IsAvailable())
            return new ApiProblem(StatusCodes.Status409Conflict, "system.updater_unavailable",
                "No updater is running next to the control plane. Run `aethera update` on the host (installations made with install.sh have it).");
        var status = await updater.GetStatusAsync(ct);
        if (UpdaterControl.InFlight(status))
            return new ApiProblem(StatusCodes.Status409Conflict, "system.update_in_progress", "An update is already in progress.");

        try
        {
            await updater.RequestAsync(latest.Version, actor.UserId, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The shared folder exists (the updater is alive) but this process cannot write to it.
            return new ApiProblem(StatusCodes.Status409Conflict, "system.updater_unavailable", "The request could not be handed to the updater: the shared folder is not writable.");
        }
        await audit.RecordAsync("system.update_requested", "system", null, new { from = checker.CurrentVersion, to = latest.Version }, ct);
        return Results.Accepted("/api/v1/system/version", (await updater.GetStatusAsync(ct)) is var s ? new UpdaterDto(s.Available, s.State, s.TargetVersion, s.Message, s.UpdatedAt) : null);
    }

    private static async Task<Ok<MaintenanceDto>> GetMaintenance(MaintenanceService maintenance, Microsoft.Extensions.Options.IOptions<MaintenanceOptions> options, CancellationToken ct)
    {
        var o = options.Value;
        var state = await maintenance.GetStateAsync(ct);
        var backup = maintenance.CreateBackup();
        var backups = backup?.List().Select(b => new BackupDto(b.Name, b.SizeBytes, b.CreatedAt, b.Kind)).ToList() ?? [];
        return TypedResults.Ok(new MaintenanceDto(
            new MaintenanceSettingsDto(o.Enabled, o.LogRetentionDays, o.EventRetentionDays, o.AuditRetentionDays, o.SessionRetentionDays, o.PruneEnabled, o.PruneIntervalHours, o.PruneKeepHours, o.BackupEnabled, o.BackupIntervalHours, o.BackupsKept),
            backup?.IsAvailable() ?? false, backups, state.LastBackupAt, state.LastPruneAt, state.LastRun is { } r ? ToDto(r) : null));
    }

    private static async Task<Ok<MaintenanceReportDto>> RunMaintenance(MaintenanceService maintenance, IClock clock, IAuditLog audit, CancellationToken ct)
    {
        var report = await maintenance.RunOnceAsync(clock.UtcNow, force: true, ct);
        await audit.RecordAsync("system.maintenance_run", "system", null,
            new { report.PruneJobsQueued, report.BackupTaken, report.LogChunksDeleted, report.AuditEventsDeleted, errors = report.Errors.Count }, ct);
        return TypedResults.Ok(ToDto(report));
    }

    private static MaintenanceReportDto ToDto(MaintenanceReport r) => new(
        r.At, r.LogChunksDeleted, r.EventsDeleted, r.WebhookDeliveriesDeleted, r.AuditEventsDeleted, r.SessionsDeleted, r.IdempotencyRecordsDeleted,
        r.PruneJobsQueued, r.BackupTaken, r.BackupsDeleted, r.Errors);

    private static async Task<IResult> UpdateAgent(
        Guid id, UpdateAgentRequest? request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IJobQueue queue, CancellationToken ct)
    {
        var org = actor.Org();
        await db.GetServerAsync(org, id, tracking: false, ct);
        var job = await queue.EnqueueAsync(new JobRequest(AgentUpdateJobHandler.JobType, new AgentUpdatePayload { ServerId = id, Version = request?.Version })
        {
            OrganizationId = org, Resource = new JobResource("server", id), LockKey = $"server:{id}:maintenance", MaxAttempts = 1,
        }, ct);
        await audit.RecordAsync("server.agent_update_requested", "server", id, new { jobId = job.Id, request?.Version }, ct);
        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, ct);
        return Results.Accepted(dto.Links.Self, dto);
    }
}

public static class SystemModule
{
    public static IEndpointRouteBuilder MapSystem(this IEndpointRouteBuilder api)
    {
        SystemEndpoints.Map(api);
        return api;
    }
}
