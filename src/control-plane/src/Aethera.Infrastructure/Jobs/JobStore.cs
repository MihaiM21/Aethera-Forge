using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aethera.Infrastructure.Jobs;

/// <summary>Result of a lease heartbeat.</summary>
public readonly record struct Heartbeat(bool LeaseHeld, bool CancelRequested);

/// <summary>
/// The SQL of the job system (ADR 0004): claim, heartbeat, release, reap and the guarded final transitions. Anything that changes a
/// job's lifecycle goes through the <see cref="Job"/> state-machine methods; the few statements that cannot (claim, heartbeat and
/// "release without consuming an attempt" are single atomic UPDATEs) mirror them in SQL and say so.
/// </summary>
public sealed class JobStore(IServiceScopeFactory scopes, IServiceProvider services, IClock clock)
{
    // Resolved on use: hosts that are built but never reach the database (OpenAPI generation, tests) must start without a connection string.
    private NpgsqlDataSource dataSource => services.GetRequiredService<NpgsqlDataSource>();

    public const string WakeChannel = "aethera_jobs";
    public const string CancelChannel = "aethera_job_cancel";

    /// <summary>
    /// ADR 0004 claim: the best queued job whose <c>run_after</c> has passed and whose lock key no running job holds, locked with
    /// <c>FOR UPDATE SKIP LOCKED</c> so concurrent claimers (other workers, other instances) never pick the same row. Mirrors <c>Job.Claim</c>.
    /// Returns a detached snapshot, or null when nothing is runnable.
    /// </summary>
    public async Task<Job?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var seconds = lease.TotalSeconds;
        var claimed = db.Jobs.FromSql($"""
            WITH next AS (
              SELECT j.id
              FROM jobs j
              WHERE j.status = 'queued'
                AND j.run_after <= now()
                AND (j.lock_key IS NULL OR NOT EXISTS (
                      SELECT 1 FROM jobs r WHERE r.status = 'running' AND r.lock_key = j.lock_key))
              ORDER BY j.priority DESC, j.run_after, j.id
              FOR UPDATE SKIP LOCKED
              LIMIT 1
            )
            UPDATE jobs j
            SET status = 'running',
                attempt = j.attempt + 1,
                started_at = COALESCE(j.started_at, now()),
                locked_by = {workerId},
                lease_expires_at = now() + make_interval(secs => {seconds}),
                updated_at = now()
            FROM next
            WHERE j.id = next.id
            RETURNING j.*, j.xmin
            """).AsNoTracking().AsAsyncEnumerable();
        await foreach (var job in claimed.WithCancellation(cancellationToken)) return job;
        return null;
    }

    /// <summary>
    /// Extends the lease and reads the cancel flag in one statement. <c>LeaseHeld = false</c> means the job is no longer ours (the
    /// reaper took it, it was finished elsewhere). Mirrors <c>Job.ExtendLease</c>.
    /// </summary>
    public async Task<Heartbeat> HeartbeatAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE jobs SET lease_expires_at = now() + make_interval(secs => $3), updated_at = now() "
            + "WHERE id = $1 AND status = 'running' AND locked_by = $2 RETURNING cancel_requested_at IS NOT NULL");
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(lease.TotalSeconds);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool cancelRequested ? new Heartbeat(true, cancelRequested) : new Heartbeat(false, false);
    }

    /// <summary>
    /// Puts a job this worker holds back to Queued <b>without consuming the attempt</b> (advisory-lock race, graceful shutdown).
    /// <c>Job.Claim</c> has no inverse, so this one transition is SQL. A job with a pending cancel request is not released
    /// (<paramref name="unlessCancelRequested"/>): the caller finishes it as cancelled. Returns whether it was released.
    /// </summary>
    public async Task<bool> ReleaseAsync(Guid jobId, string workerId, TimeSpan delay, bool unlessCancelRequested, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE jobs SET status = 'queued', attempt = GREATEST(attempt - 1, 0), "
            + "started_at = CASE WHEN attempt <= 1 THEN NULL ELSE started_at END, "
            + "run_after = now() + make_interval(secs => $3), locked_by = NULL, lease_expires_at = NULL, updated_at = now() "
            + "WHERE id = $1 AND status = 'running' AND locked_by = $2 AND ($4 = false OR cancel_requested_at IS NULL)");
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(delay.TotalSeconds);
        command.Parameters.AddWithValue(unlessCancelRequested);
        var released = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (released) await NotifyAsync(WakeChannel, jobId.ToString(), cancellationToken);
        return released;
    }

    /// <summary>
    /// Applies <paramref name="transition"/> (a <see cref="Job"/> state-machine call) to the freshly loaded row, guarded by optimistic
    /// concurrency (<c>xmin</c>) and by ownership. Returns the saved job, or null when this worker no longer holds the job
    /// (lease lost, cancelled or finished elsewhere), in which case nothing is written.
    /// </summary>
    public async Task<Job?> TransitionAsync(Guid jobId, string workerId, Action<Job> transition, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            if (job is null || job.Status != JobStatus.Running || job.LockedBy != workerId) return null;

            transition(job);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return job;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A heartbeat or a cancel request touched the row between the read and the write: reload and decide again.
            }
        }

        throw new InvalidOperationException($"Could not update job {jobId}: it keeps changing.");
    }

    /// <summary>
    /// Reaper pass: running jobs whose lease expired, locked with <c>FOR UPDATE SKIP LOCKED</c> (one instance wins each), handed to
    /// <c>Job.ExpireLease</c>, which re-queues resumable jobs with attempts left, ends cancel-requested ones as Cancelled and fails the
    /// rest with <c>job.worker_lost</c>. Returns each reaped job and whether it was re-queued.
    /// </summary>
    public async Task<IReadOnlyList<(Job Job, bool Requeued)>> ReapExpiredAsync(Func<string, bool> isResumable, int limit, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.UtcNow;
        var expired = await db.Jobs.FromSql($"""
            SELECT j.*, j.xmin FROM jobs j
            WHERE j.status = 'running' AND j.lease_expires_at < {now}
            ORDER BY j.lease_expires_at
            LIMIT {limit}
            FOR UPDATE SKIP LOCKED
            """).AsTracking().ToListAsync(cancellationToken);

        var reaped = new List<(Job, bool)>(expired.Count);
        foreach (var job in expired)
        {
            var requeued = job.ExpireLease(now, isResumable(job.Type));
            reaped.Add((job, requeued));
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return reaped;
    }

    /// <summary>How long until the earliest <b>future</b> <c>run_after</c> of a queued job (retry backoff, delayed jobs), or null when there is none.</summary>
    public async Task<TimeSpan?> UntilNextRunnableAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXTRACT(EPOCH FROM (min(run_after) - now()))::float8 FROM jobs WHERE status = 'queued' AND run_after > now()");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is double seconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary><c>pg_notify</c>; best effort (polling covers a lost notification).</summary>
    public async Task NotifyAsync(string channel, string payload, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT pg_notify($1, $2)");
            command.Parameters.AddWithValue(channel);
            command.Parameters.AddWithValue(payload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The poll interval is the safety net.
        }
    }
}
