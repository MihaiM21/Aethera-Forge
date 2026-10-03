using System.Text.Json;
using System.Threading.Channels;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aethera.Infrastructure.Jobs;

/// <summary>
/// Publishes job state changes to <c>/hubs/jobs</c> subscribers through the <see cref="ILiveBus"/>. Best effort and decoupled: callers
/// (the worker, the API) only enqueue the event, a pump publishes it, so a slow or unreachable Redis can never delay a job or a request.
/// REST is the source of truth and a missed event only delays the UI until its next resync.
/// </summary>
public sealed class JobEvents : IAsyncDisposable
{
    private readonly ILiveBus _bus;
    private readonly ILogger<JobEvents> _logger;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    public JobEvents(ILiveBus bus, ILogger<JobEvents> logger)
    {
        _bus = bus;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    public Task PublishUpdatedAsync(Job job)
    {
        _queue.Writer.TryWrite(Create("updated", job).ToJson());
        return Task.CompletedTask;
    }

    public Task PublishProgressAsync(Job job, int percent, string? message)
    {
        _queue.Writer.TryWrite((Create("progress", job) with { Percent = percent, Message = message }).ToJson());
        return Task.CompletedTask;
    }

    private async Task PumpAsync()
    {
        await foreach (var json in _queue.Reader.ReadAllAsync())
        {
            try { await _bus.PublishAsync(LiveChannels.JobEvents, json); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not publish a job event"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* shutting down: pending events are dropped */ }
    }

    private static JobBusEvent Create(string kind, Job job) =>
        new(kind, job.Id, job.Type, job.Status, job.ResourceType, job.ResourceId, job.OrganizationId);
}

/// <summary>
/// <see cref="IJobQueue"/> over Postgres (ADR 0004): inserts the <c>jobs</c> row, honours the idempotency key and wakes idle workers with
/// <c>NOTIFY aethera_jobs</c>. It uses the caller's scoped <see cref="AetheraDbContext"/>, so when the caller has uncommitted changes (a
/// deployment row) or an open transaction, the job is saved atomically with them; the notification is delivered at commit.
/// </summary>
public sealed class PostgresJobQueue(AetheraDbContext db, IClock clock, IServiceProvider services, JobEvents events, WorkerWakeSignal wake) : IJobQueue
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<Job> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Type);

        if (request.IdempotencyKey is { } key && await FindByKeyAsync(key, cancellationToken) is { } existing)
            return existing;

        var actor = services.GetService<ICurrentActor>() is { IsAuthenticated: true } current ? current : null;
        var organizationId = request.OrganizationId ?? actor?.OrganizationId
            ?? throw new InvalidOperationException(
                "A job needs an organization: set JobRequest.OrganizationId when there is no authenticated actor.");

        var retryNo = 0;
        if (request.ParentJobId is { } parentId)
        {
            // A retry of a job: idempotency keys of agent commands change with retry_no (ADR 0004 section 3).
            retryNo = await db.Jobs.AsNoTracking().Where(j => j.Id == parentId).Select(j => (int?)j.RetryNo).FirstOrDefaultAsync(cancellationToken) + 1 ?? 0;
        }

        var now = clock.UtcNow;
        var job = new Job
        {
            Type = request.Type.Trim(),
            OrganizationId = organizationId,
            Priority = request.Priority,
            ResourceType = request.Resource?.Type,
            ResourceId = request.Resource?.Id,
            LockKey = request.LockKey,
            PayloadJson = JsonSerializer.Serialize(request.Payload ?? new { }, PayloadJson),
            MaxAttempts = Math.Max(1, request.MaxAttempts),
            RetryNo = retryNo,
            RunAfter = request.RunAfter ?? now,
            ParentJobId = request.ParentJobId,
            IdempotencyKey = request.IdempotencyKey,
            CreatedBy = actor?.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (request.IdempotencyKey is { } raceKey
            && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two requests with the same key raced; the loser returns the winner's job.
            db.Entry(job).State = EntityState.Detached;
            var winner = await FindByKeyAsync(raceKey, cancellationToken);
            if (winner is null) throw;
            return winner;
        }

        await db.Database.ExecuteSqlAsync($"SELECT pg_notify({JobStore.WakeChannel}, {job.Id.ToString()})", cancellationToken);
        wake.Pulse();
        await events.PublishUpdatedAsync(job);
        return job;
    }

    private Task<Job?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.IdempotencyKey == key, cancellationToken);
}

/// <summary>Wakes this host's idle worker slots immediately (the in-process half of the wake-up; <c>LISTEN</c> is the cross-instance half).</summary>
public sealed class WorkerWakeSignal
{
    private TaskCompletionSource _current = New();

    /// <summary>The task of the current epoch. Capture it <b>before</b> looking for work, then wait on it: a pulse in between is not lost.</summary>
    public Task Current => Volatile.Read(ref _current).Task;

    public void Pulse() => Interlocked.Exchange(ref _current, New()).TrySetResult();

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
