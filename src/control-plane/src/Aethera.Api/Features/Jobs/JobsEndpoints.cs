using System.Text;
using System.Text.Json;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Jobs;

/// <summary>Problem codes raised by the job endpoints.</summary>
public static class JobProblemCodes
{
    public const string NotFound = "job.not_found";
    public const string AlreadyFinished = "job.already_finished";
    public const string NotRetryable = "job.not_retryable";
}

/// <summary>Body of <c>POST /jobs/echo</c>: the payload of a <c>system.echo</c> job plus how to enqueue it.</summary>
public sealed record EchoJobRequest(
    string[]? Lines,
    int? DelayMs,
    int? FailAt,
    bool? Retryable,
    int? MaxAttempts,
    string? LockKey,
    int? Priority);

public sealed class EchoJobRequestValidator : AbstractValidator<EchoJobRequest>
{
    public EchoJobRequestValidator()
    {
        RuleFor(x => x.Lines).Must(l => l is null || l.Length <= 1000).WithMessage("At most 1000 lines.").WithErrorCode("too_long");
        RuleForEach(x => x.Lines).Must(l => l is not null && l.Length <= 4096).WithMessage("Each line must be at most 4096 characters.").WithErrorCode("too_long");
        RuleFor(x => x.DelayMs).InclusiveBetween(0, 60_000);
        RuleFor(x => x.FailAt).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxAttempts).InclusiveBetween(1, 10);
        RuleFor(x => x.LockKey).MaximumLength(200);
        RuleFor(x => x.Priority).InclusiveBetween(-100, 100);
    }
}

public static class JobsEndpoints
{
    private static readonly string[] ListParameters =
    [
        "limit", "cursor", "sort", "status", "type", "resourceType", "resourceId",
        "createdAfter", "createdBefore", "startedAfter", "startedBefore", "finishedAfter", "finishedBefore",
    ];

    private static readonly string[] LogParameters = ["fromSequence", "limit", "download"];

    private const int DefaultLogLimit = 200;
    private const int MaxLogLimit = 1000;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder api)
    {
        var jobs = api.MapGroup("/jobs").WithTags("Jobs");

        jobs.MapGet("/", ListJobsAsync)
            .WithName("listJobs").WithSummary("List jobs")
            .RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Read)
            .Produces<Page<JobDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        jobs.MapGet("/{id:guid}", GetJobAsync)
            .WithName("getJob").WithSummary("Get a job")
            .RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Read)
            .Produces<JobDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapGet("/{id:guid}/logs", GetJobLogsAsync)
            .WithName("getJobLogs").WithSummary("Read a job's log, paged by sequence, or download it as text")
            .WithDescription("JSON pages by default (`fromSequence`, `limit`). With `download=true` the whole log from `fromSequence` is streamed as `text/plain`.")
            .RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Read)
            .Produces<LogPageDto>()
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapPost("/{id:guid}/cancel", CancelJobAsync)
            .WithName("cancelJob").WithSummary("Cancel a job")
            .WithDescription("A queued job is cancelled immediately; a running job is asked to stop and ends cancelled within one heartbeat.")
            .RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Deploy)
            .Produces<JobDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        jobs.MapPost("/{id:guid}/retry", RetryJobAsync)
            .WithName("retryJob").WithSummary("Retry a failed or cancelled job")
            .WithDescription("Creates a new job linked to the original (`parentJobId`); the original stays as it is.")
            .RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Deploy)
            .Produces<JobDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var configuration = api.ServiceProvider.GetRequiredService<IConfiguration>();
        var environment = api.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (EchoEndpointEnabled(configuration, environment))
        {
            jobs.MapPost("/echo", EnqueueEchoAsync)
                .WithName("enqueueEchoJob").WithSummary("Enqueue a system.echo job (development and smoke tests)")
                .RequireRole(AetheraPolicies.Admin).RequireScope(Scopes.Deploy)
                .Validate<EchoJobRequest>()
                .Produces<JobDto>(StatusCodes.Status202Accepted);
        }

        return api;
    }

    /// <summary><c>Aethera:Jobs:EnableEchoEndpoint</c>; unset means on in Development and Testing, off elsewhere.</summary>
    public static bool EchoEndpointEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        bool.TryParse(configuration[$"{JobsOptions.Section}:EnableEchoEndpoint"], out var enabled)
            ? enabled
            : environment.IsDevelopment() || environment.IsEnvironment("Testing");

    // ------------------------------------------------------------------ list

    private static async Task<IResult> ListJobsAsync(
        HttpContext http,
        AetheraDbContext db,
        KeysetCursor cursors,
        ICurrentActor actor,
        [AsParameters] PageRequest page,
        string? sort, string? status, string? type, string? resourceType, Guid? resourceId,
        DateTimeOffset? createdAfter, DateTimeOffset? createdBefore,
        DateTimeOffset? startedAfter, DateTimeOffset? startedBefore,
        DateTimeOffset? finishedAfter, DateTimeOffset? finishedBefore,
        CancellationToken cancellationToken)
    {
        RejectUnknownParameters(http, ListParameters);
        var request = page.Validated();
        var order = SortSpec.Parse(sort, ["createdAt"], "-createdAt");
        var descending = order.Fields[0].Descending;

        var statuses = ParseStatuses(status);
        var types = SplitList(type);
        var context = string.Join('|',
            order.Canonical,
            $"status={status}", $"type={type}", $"resourceType={resourceType}", $"resourceId={resourceId}",
            $"ca={createdAfter?.UtcTicks}", $"cb={createdBefore?.UtcTicks}", $"sa={startedAfter?.UtcTicks}", $"sb={startedBefore?.UtcTicks}",
            $"fa={finishedAfter?.UtcTicks}", $"fb={finishedBefore?.UtcTicks}", $"org={actor.OrganizationId}");

        IQueryable<Job> query = db.Jobs.AsNoTracking();
        if (request.Cursor is not null)
        {
            var position = cursors.Decode(request.Cursor, context);
            var createdAt = KeysetCursor.ParseDateTimeOffset(position.SortValues[0] ?? throw new ApiProblemException(ApiProblems.InvalidCursor()));
            // Row-value comparison: (created_at, id) strictly after the last row of the previous page in the sort direction.
            query = descending
                ? db.Jobs.FromSql($"SELECT j.*, j.xmin FROM jobs j WHERE (j.created_at, j.id) < ({createdAt}, {position.Id})").AsNoTracking()
                : db.Jobs.FromSql($"SELECT j.*, j.xmin FROM jobs j WHERE (j.created_at, j.id) > ({createdAt}, {position.Id})").AsNoTracking();
        }

        query = query.VisibleTo(actor.OrganizationId ?? Guid.Empty);
        if (statuses.Count > 0) query = query.Where(j => statuses.Contains(j.Status));
        if (types.Count > 0) query = query.Where(j => types.Contains(j.Type));
        if (resourceType is not null) query = query.Where(j => j.ResourceType == resourceType);
        if (resourceId is { } rid) query = query.Where(j => j.ResourceId == rid);
        if (createdAfter is { } ca) query = query.Where(j => j.CreatedAt >= ca);
        if (createdBefore is { } cb) query = query.Where(j => j.CreatedAt < cb);
        if (startedAfter is { } sa) query = query.Where(j => j.StartedAt >= sa);
        if (startedBefore is { } sb) query = query.Where(j => j.StartedAt < sb);
        if (finishedAfter is { } fa) query = query.Where(j => j.FinishedAt >= fa);
        if (finishedBefore is { } fb) query = query.Where(j => j.FinishedAt < fb);

        query = descending
            ? query.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            : query.OrderBy(j => j.CreatedAt).ThenBy(j => j.Id);

        var fetched = await query.Take(request.Limit + 1).ToListAsync(cancellationToken);
        var positions = await JobMapper.QueuePositionsAsync(db, fetched, cancellationToken);
        var dtos = fetched.Select(j => JobMapper.ToDto(j, positions.TryGetValue(j.Id, out var p) ? p : null)).ToList();
        return Results.Ok(Page<JobDto>.FromOverfetch(dtos, request.Limit,
            last => cursors.Encode(new KeysetPosition([KeysetCursor.Format(last.CreatedAt)], last.Id), context)));
    }

    private static List<JobStatus> ParseStatuses(string? status)
    {
        var result = new List<JobStatus>();
        foreach (var item in SplitList(status))
        {
            if (!Enum.TryParse<JobStatus>(item, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                throw new ApiProblemException(ApiProblems.InvalidParameter(
                    "status", $"Unknown status '{item}'. Use queued, running, succeeded, failed or cancelled.", "invalid_enum"));
            result.Add(parsed);
        }

        return result;
    }

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private static void RejectUnknownParameters(HttpContext http, string[] allowed)
    {
        foreach (var key in http.Request.Query.Keys)
        {
            if (!allowed.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ApiProblemException(ApiProblems.InvalidParameter(key, $"Unknown parameter '{key}'.", "unknown_parameter"));
        }
    }

    // ------------------------------------------------------------------- get

    private static async Task<IResult> GetJobAsync(Guid id, AetheraDbContext db, ICurrentActor actor, CancellationToken cancellationToken)
    {
        var job = await FindVisibleAsync(db, actor, id, tracking: false, cancellationToken);
        return job is null ? NotFound(id) : Results.Ok(await JobMapper.ToDtoWithPositionAsync(db, job, cancellationToken));
    }

    private static ApiProblem NotFound(Guid id) => ApiProblems.NotFound("job", id);

    private static Task<Job?> FindVisibleAsync(AetheraDbContext db, ICurrentActor actor, Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var jobs = tracking ? db.Jobs.AsTracking() : db.Jobs.AsNoTracking();
        return jobs.VisibleTo(actor.OrganizationId ?? Guid.Empty).FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    // ------------------------------------------------------------------ logs

    private static async Task<IResult> GetJobLogsAsync(
        Guid id, HttpContext http, AetheraDbContext db, LogReader reader, ICurrentActor actor,
        long? fromSequence, int? limit, bool? download, CancellationToken cancellationToken)
    {
        RejectUnknownParameters(http, LogParameters);
        if (fromSequence is < 0) return ApiProblems.InvalidParameter("fromSequence", "Must be zero or greater.", "range");
        if (limit is < 1 or > MaxLogLimit) return ApiProblems.InvalidParameter("limit", $"Must be between 1 and {MaxLogLimit}.", "range");

        var job = await FindVisibleAsync(db, actor, id, tracking: false, cancellationToken);
        if (job is null) return NotFound(id);

        var streamId = JobStreams.StreamId(id);
        var from = fromSequence ?? 0;
        if (download == true)
        {
            return Results.Stream(async stream =>
            {
                await foreach (var line in reader.StreamAsync(streamId, from, cancellationToken: http.RequestAborted))
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(line.Text), http.RequestAborted);
            }, "text/plain; charset=utf-8", $"job-{id:D}.log");
        }

        var take = limit ?? DefaultLogLimit;
        var lines = await reader.ReadAsync(streamId, from, take + 1, cancellationToken);
        var hasMore = lines.Count > take;
        var items = hasMore ? lines.Take(take).ToList() : lines;
        var next = items.Count > 0 ? items[^1].Sequence + 1 : from;
        return Results.Ok(new LogPageDto(items, next, hasMore, job.IsTerminal && !hasMore));
    }

    // ---------------------------------------------------------------- cancel

    private static async Task<IResult> CancelJobAsync(
        Guid id, AetheraDbContext db, IClock clock, JobStore store, JobEvents events, ICurrentActor actor, CancellationToken cancellationToken)
    {
        Job? job = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            job = await FindVisibleAsync(db, actor, id, tracking: true, cancellationToken);
            if (job is null) return NotFound(id);
            if (job.IsTerminal) return ApiProblems.Conflict(JobProblemCodes.AlreadyFinished, $"Job {id} already finished ({job.Status}).");

            job.RequestCancel(clock.UtcNow);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A worker claimed or finished it meanwhile: decide again on the fresh row.
                db.ChangeTracker.Clear();
                job = null;
            }
        }

        if (job is null) return ApiProblems.Conflict(ProblemCodes.ConcurrencyConflict, "The job keeps changing; try again.");

        // A running job learns from the notification (immediately) or from its next heartbeat (at the latest).
        if (job.Status == JobStatus.Running) await store.NotifyAsync(JobStore.CancelChannel, job.Id.ToString(), cancellationToken);
        await events.PublishUpdatedAsync(job);

        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, cancellationToken);
        return Results.Accepted(dto.Links.Self, dto);
    }

    // ----------------------------------------------------------------- retry

    private static async Task<IResult> RetryJobAsync(
        Guid id, AetheraDbContext db, IJobQueue queue, ICurrentActor actor, CancellationToken cancellationToken)
    {
        var original = await FindVisibleAsync(db, actor, id, tracking: false, cancellationToken);
        if (original is null) return NotFound(id);
        if (original.Status is not (JobStatus.Failed or JobStatus.Cancelled))
            return ApiProblems.Conflict(JobProblemCodes.NotRetryable, $"Only failed or cancelled jobs can be retried; job {id} is {original.Status}.");

        var payload = JsonSerializer.Deserialize<JsonElement>(original.PayloadJson);
        var retry = await queue.EnqueueAsync(new JobRequest(original.Type, payload)
        {
            Resource = original.ResourceType is not null && original.ResourceId is { } resourceId ? new JobResource(original.ResourceType, resourceId) : null,
            LockKey = original.LockKey,
            Priority = original.Priority,
            MaxAttempts = original.MaxAttempts,
            ParentJobId = original.Id,
            OrganizationId = original.OrganizationId,
        }, cancellationToken);

        var dto = await JobMapper.ToDtoWithPositionAsync(db, retry, cancellationToken);
        return Results.Accepted(dto.Links.Self, dto);
    }

    // ------------------------------------------------------------------ echo

    private static async Task<IResult> EnqueueEchoAsync(EchoJobRequest body, AetheraDbContext db, IJobQueue queue, CancellationToken cancellationToken)
    {
        var payload = new EchoPayload
        {
            Lines = body.Lines ?? [],
            DelayMs = body.DelayMs ?? 0,
            FailAt = body.FailAt,
            Retryable = body.Retryable ?? true,
        };
        var job = await queue.EnqueueAsync(new JobRequest(EchoJobHandler.JobType, payload)
        {
            MaxAttempts = body.MaxAttempts ?? 1,
            LockKey = body.LockKey,
            Priority = body.Priority ?? 0,
        }, cancellationToken);

        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, cancellationToken);
        return Results.Accepted(dto.Links.Self, dto);
    }
}
