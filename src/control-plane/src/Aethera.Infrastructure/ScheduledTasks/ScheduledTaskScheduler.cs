using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.ScheduledTasks;

/// <summary>What one scheduler pass did.</summary>
public sealed record SchedulerTickResult(int Claimed, int Queued, int Skipped, int Healed, int Reaped);

/// <summary>
/// Wakes every 30 s (ADR 0010) and enqueues the tasks that are due. Several API instances may run it: due tasks are claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> in a transaction that also moves <c>next_run_at</c> to the next occurrence after <em>now</em>, so a task is
/// claimed by one instance only and occurrences missed while Aethera was down are not replayed (the one run that is due now is the single
/// catch-up). A run whose predecessor is still going is recorded as <see cref="ScheduledTaskRunStatus.Skipped"/> by
/// <see cref="ScheduledTaskService"/>. <see cref="TickAsync"/> is public for tests.
/// </summary>
public sealed class ScheduledTaskScheduler(
    IServiceScopeFactory scopes, IClock clock, IOptions<ScheduledTaskOptions> options, ILogger<ScheduledTaskScheduler> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || AetheraHost.IsOpenApiGeneration) return;
        try
        {
            // Start-up is busy (migrations, agents reconnecting): the first pass can wait a moment.
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, options.Value.TickSeconds)), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(0.05, options.Value.TickSeconds)));
            do
            {
                try { await TickAsync(clock.UtcNow, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "The scheduled-task scheduler pass failed"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<SchedulerTickResult> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var reaped = await ReapAbandonedRunsAsync(now, ct);
        var healed = await HealAsync(now, ct);

        int claimed = 0, queued = 0, skipped = 0;
        while (true)
        {
            var batch = await ClaimDueAsync(now, ct);
            claimed += batch.Count;
            foreach (var taskId in batch)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var queue = await scope.ServiceProvider.GetRequiredService<ScheduledTaskService>().QueueRunAsync(taskId, ScheduledTaskTrigger.Scheduled, ct);
                    if (queue.Run.Status == ScheduledTaskRunStatus.Skipped) skipped++;
                    else queued++;
                }
                catch (KeyNotFoundException)
                {
                    // Deleted between the claim and now.
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Could not queue a run of scheduled task {TaskId}", taskId);
                }
            }

            if (batch.Count < BatchSize) break;
        }

        return new SchedulerTickResult(claimed, queued, skipped, healed, reaped);
    }

    /// <summary>
    /// Takes the due tasks (enabled, not deleted, workload not deleted) and sets their next occurrence after <paramref name="now"/>, all in
    /// one transaction: concurrent instances skip the rows this one holds.
    /// </summary>
    private async Task<List<Guid>> ClaimDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT t.id AS "Value"
            FROM scheduled_tasks t
            JOIN workloads w ON w.id = t.workload_id
            WHERE t.enabled AND t.deleted_at IS NULL AND w.deleted_at IS NULL AND t.next_run_at <= {now}
            ORDER BY t.next_run_at
            LIMIT {BatchSize}
            FOR UPDATE OF t SKIP LOCKED
            """).ToListAsync(ct);
        if (ids.Count == 0) return [];

        var due = await db.ScheduledTasks.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        foreach (var task in due)
        {
            if (CronSchedule.TryParse(task.Schedule, task.TimeZone, out var schedule, out var error))
            {
                task.NextRunAt = schedule.Next(now);
            }
            else
            {
                task.NextRunAt = null;
                logger.LogWarning("Scheduled task {TaskId} has a schedule this host cannot evaluate ({Error}); it is not scheduled again until it is edited", task.Id, error);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ids;
    }

    /// <summary>Enabled tasks without a next occurrence (a time zone that became unknown and is known again, a task enabled by a script).</summary>
    private async Task<int> HealAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var tasks = await db.ScheduledTasks.Where(t => t.Enabled && t.NextRunAt == null).Take(BatchSize).ToListAsync(ct);
        var healed = 0;
        foreach (var task in tasks)
        {
            if (!CronSchedule.TryParse(task.Schedule, task.TimeZone, out var schedule, out _) || schedule.Next(now) is not { } next) continue;
            task.NextRunAt = next;
            healed++;
        }

        if (healed == 0) return 0;
        try
        {
            await db.SaveChangesAsync(ct);
            return healed;
        }
        catch (DbUpdateConcurrencyException)
        {
            return 0; // edited meanwhile: the next pass looks again
        }
    }

    /// <summary>
    /// Runs that never reached a final state because their job ended without finishing them (worker lost, job cancelled while queued) or
    /// that were never given a job: marked failed, so they stop counting as "still running".
    /// </summary>
    private async Task<int> ReapAbandonedRunsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var jobless = now - TimeSpan.FromMinutes(5);
        var abandoned = await db.ScheduledTaskRuns
            .Where(r => (r.Status == ScheduledTaskRunStatus.Pending || r.Status == ScheduledTaskRunStatus.Running)
                        && ((r.JobId != null && db.Jobs.Any(j => j.Id == r.JobId && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled)))
                            || (r.JobId == null && r.CreatedAt < jobless)))
            .Take(BatchSize).ToListAsync(ct);
        foreach (var run in abandoned)
        {
            run.Status = ScheduledTaskRunStatus.Failed;
            run.Error = "The run did not finish: the worker stopped or the job was cancelled.";
            run.FinishedAt = now;
        }

        if (abandoned.Count > 0) await db.SaveChangesAsync(ct);
        return abandoned.Count;
    }
}
