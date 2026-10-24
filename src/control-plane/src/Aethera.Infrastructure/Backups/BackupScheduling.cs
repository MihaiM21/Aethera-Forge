using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Backups;

/// <summary>Settings of the backup scheduler, bound from <c>Aethera:Backups</c>.</summary>
public sealed class BackupOptions
{
    public const string Section = "Aethera:Backups";

    /// <summary>Off by default in the Testing environment (tests call <see cref="BackupScheduler.RunOnceAsync"/>).</summary>
    public bool? SchedulerEnabled { get; set; }

    public double IntervalSeconds { get; set; } = 60;

    /// <summary>How many due policies one pass takes.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>A scheduled backup is skipped while an earlier run of the policy is still pending or running and younger than this.</summary>
    public double StaleRunHours { get; set; } = 12;
}

/// <summary>Producer side of backups: creates the run row and enqueues <c>backup.run</c> (API "back up now", the scheduler).</summary>
public sealed class BackupJobService(AetheraDbContext db, IJobQueue jobs, ICurrentActor actor)
{
    /// <summary>
    /// Queues one backup of <paramref name="policy"/>. The job takes the workload's lock key, so it waits for a running deployment of the same
    /// workload and the other way round. Saves the run and the job together.
    /// </summary>
    public async Task<(BackupRun Run, Job Job)> EnqueueRunAsync(BackupPolicy policy, BackupTrigger trigger, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var run = BackupRunner.NewRun(policy, Guid.CreateVersion7(), trigger, actor.UserId);
        db.BackupRuns.Add(run);
        var job = await jobs.EnqueueAsync(new JobRequest(BackupJobs.RunType, new BackupRunPayload { PolicyId = policy.Id, RunId = run.Id, Trigger = trigger })
        {
            OrganizationId = policy.OrganizationId,
            Resource = policy.WorkloadId is { } workloadId ? new JobResource("application", workloadId) : new JobResource("backup_policy", policy.Id),
            LockKey = BackupJobs.LockKey(policy.OrganizationId, policy.WorkloadId),
            MaxAttempts = 3,
            IdempotencyKey = idempotencyKey,
        }, cancellationToken);
        return (run, job);
    }
}

/// <summary>
/// Every minute: takes the policies that are due (<c>enabled</c>, <c>next_run_at &lt;= now</c>) with <c>FOR UPDATE SKIP LOCKED</c> inside one
/// transaction, enqueues a <c>backup.run</c> job for each and moves <c>next_run_at</c> to the next occurrence, so two API instances never enqueue
/// the same backup (ADR 0010). A missed occurrence (the control plane was down) runs once and does not pile up.
/// </summary>
public class BackupScheduler(IServiceScopeFactory scopes, IClock clock, IOptions<BackupOptions> options, IHostEnvironment environment, ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (AetheraHost.IsOpenApiGeneration || !(settings.SchedulerEnabled ?? !environment.IsEnvironment("Testing"))) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // start-up is busy: migrations, agents reconnecting
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds)));
            do
            {
                try
                {
                    var queued = await RunOnceAsync(clock.UtcNow, stoppingToken);
                    if (queued > 0) logger.LogInformation("Queued {Count} scheduled backup(s)", queued);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The backup scheduler pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One pass. Returns how many backups were queued.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<BackupJobService>();
        var batch = Math.Max(1, options.Value.BatchSize);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var due = await db.BackupPolicies
            .FromSql($"SELECT *, xmin FROM backup_policies WHERE enabled AND next_run_at <= {now} ORDER BY next_run_at LIMIT {batch} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var queued = 0;
        var stale = now.AddHours(-Math.Max(0.01, options.Value.StaleRunHours));
        foreach (var policy in due)
        {
            if (!CronSchedule.TryParse(policy.Schedule, policy.TimeZone, out var schedule, out _))
            {
                policy.Enabled = false; // a stored schedule that no longer parses must not retry every minute
                policy.NextRunAt = null;
                continue;
            }

            policy.NextRunAt = schedule.Next(now);
            if (policy.WorkloadId is { } workloadId && !await db.Workloads.AnyAsync(w => w.Id == workloadId, cancellationToken))
            {
                policy.Enabled = false; // the workload was deleted
                policy.NextRunAt = null;
                continue;
            }

            var busy = await db.BackupRuns.AnyAsync(r => r.PolicyId == policy.Id && (r.Status == BackupRunStatus.Pending || r.Status == BackupRunStatus.Running) && r.CreatedAt > stale, cancellationToken);
            if (busy) continue;

            await service.EnqueueRunAsync(policy, BackupTrigger.Scheduled, $"backup:{policy.Id}:{now.UtcTicks / TimeSpan.TicksPerMinute}", cancellationToken);
            queued++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (queued > 0) scope.ServiceProvider.GetRequiredService<WorkerWakeSignal>().Pulse();
        return queued;
    }
}
