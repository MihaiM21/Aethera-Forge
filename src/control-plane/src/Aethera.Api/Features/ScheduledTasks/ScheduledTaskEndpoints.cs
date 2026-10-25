using System.Text;
using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Scheduling;
using Aethera.Infrastructure.ScheduledTasks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.ScheduledTasks;

internal static class ScheduledTaskEndpoints
{
    private const int DefaultLogLimit = 500;
    private const int MaxLogLimit = 2000;
    private const int PreviewCount = 3;

    private static readonly SortDefinition<ScheduledTask> Sorts = new SortDefinition<ScheduledTask>("name")
        .Add("name", t => t.Name).Add("createdAt", t => t.CreatedAt);

    private static readonly SortDefinition<ScheduledTaskRun> RunSorts = new SortDefinition<ScheduledTaskRun>("-createdAt")
        .Add("createdAt", r => r.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        foreach (var (collection, noun, isApplication) in new[] { ("applications", "Application", true), ("services", "Service", false) })
        {
            var app = isApplication;
            var nested = api.MapGroup($"/{collection}/{{workloadId:guid}}/scheduled-tasks").WithTags("Scheduled tasks");
            nested.MapGet("/", (Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, IClock clock, KeysetCursor cursors,
                    [AsParameters] PageRequest page, string? sort, string? q, bool? enabled, CancellationToken ct) =>
                    List(app, workloadId, http, db, actor, clock, cursors, page, sort, q, enabled, ct))
                .WithName($"list{noun}ScheduledTasks").RequireRead();
            nested.MapPost("/", (Guid workloadId, CreateScheduledTaskRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock,
                    CancellationToken ct) => Create(app, workloadId, request, db, actor, audit, clock, ct))
                .WithName($"create{noun}ScheduledTask").Validate<CreateScheduledTaskRequest>().RequireWrite()
                .Produces<ScheduledTaskResponse>(StatusCodes.Status201Created);
        }

        var tasks = api.MapGroup("/scheduled-tasks").WithTags("Scheduled tasks");
        tasks.MapGet("/preview", Preview).WithName("previewSchedule").RequireRead()
            .WithSummary("The next three occurrences of a cron schedule in a time zone");
        tasks.MapGet("/{id:guid}", Get).WithName("getScheduledTask").RequireRead();
        tasks.MapPatch("/{id:guid}", Update).WithName("updateScheduledTask")
            .Accepts<UpdateScheduledTaskRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateScheduledTaskRequest>().RequireWrite();
        tasks.MapDelete("/{id:guid}", Delete).WithName("deleteScheduledTask").RequireWrite();
        tasks.MapPost("/{id:guid}/run", RunNow).WithName("runScheduledTask")
            .WithSummary("Run the task now")
            .RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Deploy)
            .Produces<RunScheduledTaskResponse>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);
        tasks.MapGet("/{id:guid}/runs", ListRuns).WithName("listScheduledTaskRuns").RequireRead();

        var runs = api.MapGroup("/scheduled-task-runs").WithTags("Scheduled tasks");
        runs.MapGet("/{id:guid}", GetRun).WithName("getScheduledTaskRun").RequireRead();
        runs.MapGet("/{id:guid}/logs", RunLogs).WithName("getScheduledTaskRunLogs").RequireRead()
            .WithSummary("Read the output of a run, paged by sequence, or download it as text")
            .Produces<LogPageDto>().Produces<string>(StatusCodes.Status200OK, "text/plain");
    }

    // ---- scoping --------------------------------------------------------------------------------------------------------------------

    private static IQueryable<ScheduledTask> TasksOf(AetheraDbContext db, Guid org) =>
        db.ScheduledTasks.Where(t => t.Workload.Environment.Project.OrganizationId == org);

    private static IQueryable<ScheduledTaskRun> RunsOf(AetheraDbContext db, Guid org) =>
        db.ScheduledTaskRuns.Where(r => r.Task.Workload.Environment.Project.OrganizationId == org);

    private static async Task<ScheduledTask> FindAsync(AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = TasksOf(db, org).Include(t => t.Workload);
        return await (tracking ? query.AsTracking() : query.AsNoTracking()).FirstOrDefaultAsync(t => t.Id == id, ct)
               ?? throw new ApiProblemException(ApiProblems.NotFound("scheduled_task", id));
    }

    /// <summary>The workload of the URL (404 <c>application.not_found</c> / <c>service.not_found</c> otherwise) and whether it is a compose application.</summary>
    private static async Task<(Workload Workload, bool IsCompose)> RequireWorkloadAsync(AetheraDbContext db, Guid org, bool isApplication, Guid id, CancellationToken ct)
    {
        Workload? workload = isApplication
            ? await db.ApplicationsOf(org).FirstOrDefaultAsync(a => a.Id == id, ct)
            : await db.ServicesOf(org).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (workload is null) throw new ApiProblemException(ApiProblems.NotFound(isApplication ? "application" : "service", id));
        return (workload, workload is Application { SourceKind: ApplicationSourceKind.Compose });
    }

    private static void CheckComposeService(bool isCompose, string? composeService)
    {
        if (isCompose && string.IsNullOrWhiteSpace(composeService))
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/composeService", "required",
                "This is a compose application: name the service whose container runs the command.")]));
        if (!isCompose && composeService is not null)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/composeService", "not_allowed",
                "Only compose applications have services; the command runs in the workload's container.")]));
    }

    private static async Task EnsureNameFreeAsync(AetheraDbContext db, Guid workloadId, string name, Guid? exceptId, CancellationToken ct)
    {
        if (await db.ScheduledTasks.AnyAsync(t => t.WorkloadId == workloadId && t.Name == name && t.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("scheduled_task", $"A scheduled task named '{name}' already exists on this workload."));
    }

    // ---- reads ----------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<ScheduledTaskResponse>>> List(
        bool isApplication, Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, IClock clock, KeysetCursor cursors,
        PageRequest page, string? sort, string? q, bool? enabled, CancellationToken ct)
    {
        http.RejectUnknownQuery("q", "enabled");
        var org = actor.Org();
        await RequireWorkloadAsync(db, org, isApplication, workloadId, ct);
        var query = TasksOf(db, org).Include(t => t.Workload).AsNoTracking().Where(t => t.WorkloadId == workloadId);
        if (enabled is { } e) query = query.Where(t => t.Enabled == e);
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(t => EF.Functions.ILike(t.Name, like, "\\") || EF.Functions.ILike(t.Command, like, "\\"));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"w={workloadId}&q={q}&e={enabled}", ct);
        return TypedResults.Ok(new Page<ScheduledTaskResponse>(await ToResponsesAsync(db, items, clock.UtcNow, ct), next));
    }

    private static async Task<Ok<ScheduledTaskResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IClock clock, CancellationToken ct)
    {
        var task = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(task.RowVersion);
        return TypedResults.Ok((await ToResponsesAsync(db, [task], clock.UtcNow, ct))[0]);
    }

    private static Ok<SchedulePreviewResponse> Preview(HttpContext http, IClock clock, string? schedule, string? timeZone)
    {
        http.RejectUnknownQuery("schedule", "timeZone");
        var now = clock.UtcNow;
        if (ScheduledTaskRules.CheckSchedule(schedule, timeZone, now) is { } problem)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.ForParameter(problem.Pointer.TrimStart('/'), problem.Code, problem.Message)]));
        return TypedResults.Ok(new SchedulePreviewResponse(schedule!.Trim(), string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone, NextOccurrences(schedule, timeZone, now)));
    }

    private static async Task<Ok<Page<ScheduledTaskRunResponse>>> ListRuns(
        Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, string? status,
        CancellationToken ct)
    {
        http.RejectUnknownQuery("status");
        var org = actor.Org();
        _ = await FindAsync(db, org, id, tracking: false, ct);
        var statuses = ResourceHttp.ParseEnumFilter<ScheduledTaskRunStatus>(status, "status");
        var query = RunsOf(db, org).AsNoTracking().Where(r => r.TaskId == id);
        if (statuses is not null) query = query.Where(r => statuses.Contains(r.Status));
        var (items, next) = await RunSorts.PageAsync(query, sort, page, cursors, $"t={id}&s={status}", ct);
        return TypedResults.Ok(new Page<ScheduledTaskRunResponse>(items.Select(ToRunResponse).ToList(), next));
    }

    private static async Task<Ok<ScheduledTaskRunResponse>> GetRun(Guid id, AetheraDbContext db, ICurrentActor actor, CancellationToken ct) =>
        TypedResults.Ok(ToRunResponse(await FindRunAsync(db, actor.Org(), id, ct)));

    private static async Task<ScheduledTaskRun> FindRunAsync(AetheraDbContext db, Guid org, Guid id, CancellationToken ct) =>
        await RunsOf(db, org).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new ApiProblemException(ApiProblems.NotFound("scheduled_task_run", id));

    private static async Task<IResult> RunLogs(
        Guid id, HttpContext http, AetheraDbContext db, LogReader reader, ICurrentActor actor, long? fromSequence, int? limit, bool? download, CancellationToken ct)
    {
        http.RejectUnknownQuery("fromSequence", "limit", "download");
        if (fromSequence is < 0) return ApiProblems.InvalidParameter("fromSequence", "Must be zero or greater.", "range");
        if (limit is < 1 or > MaxLogLimit) return ApiProblems.InvalidParameter("limit", $"Must be between 1 and {MaxLogLimit}.", "range");

        var run = await FindRunAsync(db, actor.Org(), id, ct);
        var streamId = ScheduledTaskStreams.StreamId(id);
        var from = fromSequence ?? 0;
        if (download == true)
        {
            return Results.Stream(async stream =>
            {
                await foreach (var line in reader.StreamAsync(streamId, from, cancellationToken: http.RequestAborted))
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(line.Text), http.RequestAborted);
            }, "text/plain; charset=utf-8", $"task-run-{id:D}.log");
        }

        var take = limit ?? DefaultLogLimit;
        var lines = await reader.ReadAsync(streamId, from, take + 1, ct);
        var hasMore = lines.Count > take;
        var items = hasMore ? lines.Take(take).ToList() : lines;
        var next = items.Count > 0 ? items[^1].Sequence + 1 : from;
        return Results.Ok(new LogPageDto(items, next, hasMore, ScheduledTaskStreams.EndedReason(run.Status) is not null && !hasMore));
    }

    // ---- writes ---------------------------------------------------------------------------------------------------------------------

    private static async Task<Created<ScheduledTaskResponse>> Create(
        bool isApplication, Guid workloadId, CreateScheduledTaskRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock,
        CancellationToken ct)
    {
        var (workload, isCompose) = await RequireWorkloadAsync(db, actor.Org(), isApplication, workloadId, ct);
        CheckComposeService(isCompose, request.ComposeService);
        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(db, workload.Id, name, null, ct);

        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone;
        var schedule = CronSchedule.Parse(request.Schedule!, timeZone);
        var now = clock.UtcNow;
        var enabled = request.Enabled ?? true;
        var task = new ScheduledTask
        {
            WorkloadId = workload.Id, Name = name, Command = request.Command!, ComposeService = isCompose ? request.ComposeService : null,
            Schedule = schedule.Expression, TimeZone = timeZone, TimeoutSeconds = request.TimeoutSeconds ?? ScheduledTaskRules.DefaultTimeoutSeconds,
            Enabled = enabled, NextRunAt = enabled ? schedule.Next(now) : null,
        };
        db.ScheduledTasks.Add(task);
        await audit.RecordAsync("scheduled_task.created", "scheduled_task", task.Id, new
        {
            workloadId = workload.Id, name, command = task.Command, composeService = task.ComposeService, schedule = task.Schedule, timeZone = task.TimeZone,
            timeoutSeconds = task.TimeoutSeconds, enabled,
        }, ct);
        task.Workload = workload;
        return TypedResults.Created(ResourceHttp.Path("scheduled-tasks", task.Id), (await ToResponsesAsync(db, [task], now, ct))[0]);
    }

    private static async Task<Ok<ScheduledTaskResponse>> Update(
        Guid id, PatchRequest<UpdateScheduledTaskRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock,
        CancellationToken ct)
    {
        var org = actor.Org();
        var task = await FindAsync(db, org, id, tracking: true, ct);
        http.CheckIfMatch(task.RowVersion);
        var body = patch.Body;
        var changed = new List<string>();
        var now = clock.UtcNow;

        if (patch.Has("name") && body.Name is { } name)
        {
            name = name.Trim();
            if (name != task.Name) await EnsureNameFreeAsync(db, task.WorkloadId, name, id, ct);
            task.Name = name;
            changed.Add("name");
        }

        if (patch.Has("command") && body.Command is { } command) { task.Command = command; changed.Add("command"); }
        if (patch.Has("composeService"))
        {
            var isCompose = task.Workload is Application { SourceKind: ApplicationSourceKind.Compose };
            CheckComposeService(isCompose, body.ComposeService);
            task.ComposeService = body.ComposeService;
            changed.Add("composeService");
        }

        var scheduleChanged = false;
        if (patch.Has("schedule") && body.Schedule is { } expression)
        {
            task.Schedule = CronSchedule.Parse(expression, task.TimeZone).Expression;
            changed.Add("schedule");
            scheduleChanged = true;
        }

        if (patch.Has("timeZone") && body.TimeZone is { } timeZone) { task.TimeZone = timeZone; changed.Add("timeZone"); scheduleChanged = true; }
        if (patch.Has("timeoutSeconds") && body.TimeoutSeconds is { } timeout) { task.TimeoutSeconds = timeout; changed.Add("timeoutSeconds"); }
        var enabledChanged = patch.Has("enabled") && body.Enabled is { } enabled && enabled != task.Enabled;
        if (patch.Has("enabled") && body.Enabled is { } newEnabled) { task.Enabled = newEnabled; changed.Add("enabled"); }

        if (scheduleChanged || enabledChanged)
        {
            task.NextRunAt = task.Enabled && CronSchedule.TryParse(task.Schedule, task.TimeZone, out var parsed, out _) ? parsed.Next(now) : null;
        }

        await audit.RecordAsync("scheduled_task.updated", "scheduled_task", id, new
        {
            workloadId = task.WorkloadId, name = task.Name, changed, command = changed.Contains("command") ? task.Command : null,
            schedule = task.Schedule, timeZone = task.TimeZone, enabled = task.Enabled,
        }, ct);
        http.SetETag(task.RowVersion);
        return TypedResults.Ok((await ToResponsesAsync(db, [task], now, ct))[0]);
    }

    private static async Task<NoContent> Delete(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var task = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(task.RowVersion);
        task.MarkDeleted(clock.UtcNow);
        task.NextRunAt = null;
        await audit.RecordAsync("scheduled_task.deleted", "scheduled_task", id, new { workloadId = task.WorkloadId, name = task.Name }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RunNow(
        Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, ScheduledTaskService service, CancellationToken ct)
    {
        var task = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        QueuedTaskRun queued;
        try
        {
            queued = await service.QueueRunAsync(task.Id, ScheduledTaskTrigger.Manual, ct);
        }
        catch (ScheduledTaskBusyException ex)
        {
            return ApiProblems.Conflict("scheduled_task.already_running", ex.Message);
        }
        catch (KeyNotFoundException)
        {
            throw new ApiProblemException(ApiProblems.NotFound("scheduled_task", id));
        }

        await audit.RecordAsync("scheduled_task.run_requested", "scheduled_task", id,
            new { workloadId = task.WorkloadId, name = task.Name, runId = queued.Run.Id, jobId = queued.Job?.Id }, ct);
        var job = await JobMapper.ToDtoWithPositionAsync(db, queued.Job!, ct);
        return Results.Accepted($"/api/v1/scheduled-task-runs/{queued.Run.Id}", new RunScheduledTaskResponse(ToRunResponse(queued.Run), job));
    }

    // ---- mapping --------------------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<DateTimeOffset> NextOccurrences(string? expression, string? timeZone, DateTimeOffset now)
    {
        if (!CronSchedule.TryParse(expression, timeZone, out var schedule, out _)) return [];
        var list = new List<DateTimeOffset>();
        var cursor = now;
        while (list.Count < PreviewCount && schedule.Next(cursor) is { } next)
        {
            list.Add(next);
            cursor = next;
        }

        return list;
    }

    private static async Task<List<ScheduledTaskResponse>> ToResponsesAsync(AetheraDbContext db, IReadOnlyList<ScheduledTask> tasks, DateTimeOffset now, CancellationToken ct)
    {
        var ids = tasks.Select(t => t.Id).ToList();
        var lastRuns = ids.Count == 0 ? [] : (await db.ScheduledTaskRuns.AsNoTracking()
            .Where(r => ids.Contains(r.TaskId) && r.CreatedAt == db.ScheduledTaskRuns.Where(x => x.TaskId == r.TaskId).Max(x => x.CreatedAt))
            .ToListAsync(ct)).GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Id).First());
        return tasks.Select(t => new ScheduledTaskResponse(
            t.Id, t.WorkloadId, t.Workload is Service ? "service" : "application", t.Workload.Name, t.Name, t.Command, t.ComposeService, t.Schedule, t.TimeZone,
            t.TimeoutSeconds, t.Enabled, t.NextRunAt, t.LastRunAt, NextOccurrences(t.Schedule, t.TimeZone, now),
            lastRuns.TryGetValue(t.Id, out var run) ? ToSummary(run) : null, t.CreatedAt, t.UpdatedAt)).ToList();
    }

    private static long? DurationMs(ScheduledTaskRun r) => r.StartedAt is { } s && r.FinishedAt is { } f ? (long)(f - s).TotalMilliseconds : null;

    private static ScheduledTaskRunSummary ToSummary(ScheduledTaskRun r) =>
        new(r.Id, r.Trigger, r.Status, r.ExitCode, r.CreatedAt, r.StartedAt, r.FinishedAt, DurationMs(r), r.JobId);

    internal static ScheduledTaskRunResponse ToRunResponse(ScheduledTaskRun r) => new(
        r.Id, r.TaskId, r.WorkloadId, r.JobId, r.Trigger, r.Status, r.ExitCode, r.OutputTail, r.OutputTruncated, r.Error, r.CreatedByUserId, r.CreatedAt,
        r.StartedAt, r.FinishedAt, DurationMs(r), ScheduledTaskStreams.StreamId(r.Id));
}
