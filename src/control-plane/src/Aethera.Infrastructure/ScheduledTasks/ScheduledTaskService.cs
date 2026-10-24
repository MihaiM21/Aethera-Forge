using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.ScheduledTasks;

/// <summary>Settings of scheduled tasks, bound from <c>Aethera:ScheduledTasks</c>.</summary>
public sealed class ScheduledTaskOptions
{
    public const string Section = "Aethera:ScheduledTasks";

    /// <summary>False stops the scheduler (tasks can still be run by hand).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the scheduler looks for due tasks (ADR 0010: 30 s). Fractions allowed, which keeps tests fast.</summary>
    public double TickSeconds { get; set; } = 30;

    /// <summary>Runs kept per task; older ones are deleted when a run finishes.</summary>
    public int KeepRuns { get; set; } = 100;
}

/// <summary>Payload of <c>scheduled_task.run</c>: only the run id; the task is read when the job starts.</summary>
public sealed class ScheduledTaskRunPayload
{
    public Guid RunId { get; set; }
}

/// <summary>A manual run was asked for while the previous run of the same task is still going (mapped to a 409 by the API).</summary>
public sealed class ScheduledTaskBusyException(string message) : Exception(message);

/// <summary>A run that was just created and the job that executes it (null for a run that was skipped without a job).</summary>
public sealed record QueuedTaskRun(ScheduledTaskRun Run, Job? Job);

/// <summary>
/// Producer side of scheduled tasks (API "run now", scheduler): creates the run row and enqueues <c>scheduled_task.run</c>. Runs of the same
/// task never overlap: while an earlier run is pending or running, a scheduled trigger records a <see cref="ScheduledTaskRunStatus.Skipped"/>
/// run and a manual one is refused. The check and the insert happen under an advisory lock of the task, so two API instances (or the scheduler
/// and a click) cannot both pass it.
/// </summary>
public sealed class ScheduledTaskService(AetheraDbContext db, IJobQueue jobs, IClock clock, ICurrentActor actor, Microsoft.Extensions.Options.IOptions<ScheduledTaskOptions> options)
{
    public const string RunJobType = "scheduled_task.run";

    /// <summary>Time a run may exceed its task's timeout before it is no longer counted as running (the job engine reaps it by then).</summary>
    public static readonly TimeSpan StaleMargin = TimeSpan.FromMinutes(10);

    /// <summary>Advisory lock serializing the overlap check and the insert of runs of one task (not a job lock key: a running job would hold that for hours).</summary>
    private static string QueueLockKey(Guid taskId) => $"scheduled_task_queue:{taskId:D}";

    /// <summary>The persisted log stream of a run: the agent's output of the command (not the job's own <c>job:&lt;id&gt;</c> stream, whose sequence numbers it would collide with).</summary>
    public static string LogStream(Guid runId) => ScheduledTaskStreams.StreamId(runId);

    /// <exception cref="KeyNotFoundException">The task (or its workload) no longer exists.</exception>
    /// <exception cref="ScheduledTaskBusyException">A manual run was asked for while the task is running.</exception>
    public async Task<QueuedTaskRun> QueueRunAsync(Guid taskId, ScheduledTaskTrigger trigger, CancellationToken ct = default)
    {
        ScheduledTaskRun run;
        Guid organizationId;
        bool skipped;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({QueueLockKey(taskId)}, 0))", ct);
            var task = await db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct)
                       ?? throw new KeyNotFoundException("The scheduled task does not exist.");
            organizationId = await db.Workloads.AsNoTracking().Where(w => w.Id == task.WorkloadId)
                .Select(w => (Guid?)w.Environment.Project.OrganizationId).FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("The workload of the scheduled task does not exist.");

            var now = clock.UtcNow;
            var cutoff = now - TimeSpan.FromSeconds(task.TimeoutSeconds) - StaleMargin;
            skipped = await db.ScheduledTaskRuns.AnyAsync(
                r => r.TaskId == taskId && (r.Status == ScheduledTaskRunStatus.Pending || r.Status == ScheduledTaskRunStatus.Running) && r.CreatedAt > cutoff, ct);
            if (skipped && trigger == ScheduledTaskTrigger.Manual)
                throw new ScheduledTaskBusyException("The previous run of this task is still running.");

            run = new ScheduledTaskRun
            {
                TaskId = taskId, WorkloadId = task.WorkloadId, Trigger = trigger, CreatedAt = now, CreatedByUserId = actor.UserId,
                Status = skipped ? ScheduledTaskRunStatus.Skipped : ScheduledTaskRunStatus.Pending,
                Error = skipped ? "The previous run is still running." : null,
                FinishedAt = skipped ? now : null,
            };
            db.ScheduledTaskRuns.Add(run);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (skipped)
        {
            await PruneRunsAsync(taskId, ct);
            return new QueuedTaskRun(run, null);
        }

        try
        {
            var job = await jobs.EnqueueAsync(new JobRequest(RunJobType, new ScheduledTaskRunPayload { RunId = run.Id })
            {
                OrganizationId = organizationId, Resource = new JobResource("scheduled_task", taskId),
            }, ct);
            run.JobId = job.Id;
            await db.SaveChangesAsync(ct);
            return new QueuedTaskRun(run, job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No job will ever run it: do not leave a pending run that blocks the next ones.
            run.Status = ScheduledTaskRunStatus.Failed;
            run.Error = "The run could not be queued.";
            run.FinishedAt = clock.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Keeps the newest <see cref="ScheduledTaskOptions.KeepRuns"/> runs of the task and deletes the rest with their output.</summary>
    public async Task PruneRunsAsync(Guid taskId, CancellationToken ct = default)
    {
        var keep = Math.Max(1, options.Value.KeepRuns);
        var obsolete = await db.ScheduledTaskRuns.AsNoTracking()
            .Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip(keep).Select(r => r.Id).ToListAsync(ct);
        if (obsolete.Count == 0) return;
        var streams = obsolete.Select(LogStream).ToList();
        await db.LogChunks.Where(c => streams.Contains(c.StreamId)).ExecuteDeleteAsync(ct);
        await db.ScheduledTaskRuns.Where(r => obsolete.Contains(r.Id)).ExecuteDeleteAsync(ct);
    }
}

/// <summary>The log stream id of a task run (<c>task:&lt;runId&gt;</c>): what the command printed inside the container.</summary>
public static class ScheduledTaskStreams
{
    public const string Prefix = "task:";

    public static string StreamId(Guid runId) => Prefix + runId.ToString("D");

    public static bool TryParse(string streamId, out Guid runId)
    {
        runId = default;
        return streamId.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParse(streamId.AsSpan(Prefix.Length), out runId);
    }

    /// <summary>The <c>ended</c> reason of the stream of a run in <paramref name="status"/>, or null while it is still being written.</summary>
    public static string? EndedReason(ScheduledTaskRunStatus status) => status switch
    {
        ScheduledTaskRunStatus.Succeeded or ScheduledTaskRunStatus.Skipped => "succeeded",
        ScheduledTaskRunStatus.Failed => "failed",
        ScheduledTaskRunStatus.Cancelled => "cancelled",
        _ => null,
    };
}

/// <summary>Authorizes <c>task:&lt;runId&gt;</c> streams for the log hub: the run must belong to a task of the caller's organization.</summary>
public sealed class ScheduledTaskLogStreamAuthorizer(IServiceScopeFactory scopes) : ILogStreamAuthorizer
{
    public bool Handles(string streamId) => ScheduledTaskStreams.TryParse(streamId, out _);

    public async Task<LogStreamAccess> AuthorizeAsync(string streamId, Guid organizationId, CancellationToken cancellationToken)
    {
        if (!ScheduledTaskStreams.TryParse(streamId, out var runId)) return LogStreamAccess.Denied;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var status = await db.ScheduledTaskRuns.AsNoTracking()
            .Where(r => r.Id == runId && r.Task.Workload.Environment.Project.OrganizationId == organizationId)
            .Select(r => (ScheduledTaskRunStatus?)r.Status).FirstOrDefaultAsync(cancellationToken);
        return status is null ? LogStreamAccess.Denied : new LogStreamAccess(true, ScheduledTaskStreams.EndedReason(status.Value));
    }
}
