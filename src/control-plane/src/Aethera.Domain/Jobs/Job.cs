using System.Text.Json;

namespace Aethera.Domain;

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>Structured job error, stored as jsonb: <c>{code,title,detail,failedStep?,retryable}</c> (ADR 0004).</summary>
public sealed record JobError(string Code, string Title, string? Detail = null, string? FailedStep = null, bool Retryable = false)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// Durable unit of work claimed with <c>FOR UPDATE SKIP LOCKED</c> (ADR 0004). Higher <see cref="Priority"/> runs first;
/// jobs sharing a <see cref="LockKey"/> (e.g. <c>app:{id}</c>) never run concurrently (session advisory lock + NOT EXISTS filter).
/// "Cancelling" is not a status: it is Running with <see cref="CancelRequestedAt"/> set.
/// </summary>
public class Job : MutableEntity
{
    public const string WorkerLostCode = "job.worker_lost";

    public required string Type { get; set; }
    public JobStatus Status { get; private set; } = JobStatus.Queued;
    public int Priority { get; set; }
    public string? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }

    /// <summary>Serialisation key, null = none.</summary>
    public string? LockKey { get; set; }

    /// <summary>Inputs (ids and references only, never secret values).</summary>
    public string PayloadJson { get; set; } = "{}";

    public string? ResultJson { get; private set; }
    public string? ErrorJson { get; private set; }

    /// <summary>Executions started.</summary>
    public int Attempt { get; private set; }

    public int MaxAttempts { get; set; } = 1;

    /// <summary>Bumps only after a reported failure; part of agent idempotency keys <c>&lt;job&gt;:&lt;step&gt;:&lt;retry_no&gt;</c>.</summary>
    public int RetryNo { get; set; }

    public DateTimeOffset RunAfter { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Worker instance holding the lease.</summary>
    public string? LockedBy { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public DateTimeOffset? CancelRequestedAt { get; private set; }

    /// <summary>Retry chain / sub-jobs.</summary>
    public Guid? ParentJobId { get; set; }

    /// <summary>Unique when set (e.g. provider webhook delivery id).</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>User or API token id that created the job.</summary>
    public Guid? CreatedBy { get; set; }

    public bool CancelRequested => CancelRequestedAt is not null;
    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>Mirrors the SQL claim: Queued -> Running with a lease.</summary>
    public void Claim(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status != JobStatus.Queued) throw new DomainRuleException($"Cannot claim a {Status} job.");
        Status = JobStatus.Running;
        Attempt++;
        StartedAt ??= now;
        LockedBy = workerId;
        LeaseExpiresAt = now + leaseDuration;
        UpdatedAt = now;
    }

    public void ExtendLease(DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status != JobStatus.Running) throw new DomainRuleException("Only running jobs hold a lease.");
        LeaseExpiresAt = now + leaseDuration;
        UpdatedAt = now;
    }

    public void Succeed(DateTimeOffset now, string? resultJson = null)
    {
        if (Status != JobStatus.Running) throw new DomainRuleException($"Cannot complete a {Status} job.");
        Status = JobStatus.Succeeded;
        ResultJson = resultJson;
        ErrorJson = null;
        Release(now);
    }

    /// <summary>
    /// Records a failure. A retryable error with attempts left (and no cancel request) re-queues the job after
    /// <paramref name="retryDelay"/> and returns true; anything else ends it.
    /// </summary>
    public bool Fail(JobError error, DateTimeOffset now, TimeSpan retryDelay)
    {
        if (Status != JobStatus.Running) throw new DomainRuleException($"Cannot fail a {Status} job.");
        ErrorJson = error.ToJson();
        if (error.Retryable && Attempt < MaxAttempts && !CancelRequested)
        {
            Status = JobStatus.Queued;
            RunAfter = now + retryDelay;
            RetryNo++;
            LockedBy = null;
            LeaseExpiresAt = null;
            UpdatedAt = now;
            return true;
        }
        Status = CancelRequested ? JobStatus.Cancelled : JobStatus.Failed;
        Release(now);
        return false;
    }

    /// <summary>
    /// Reaper: the lease expired (worker died). Resumable jobs with attempts left go back to Queued without a new
    /// attempt being consumed; otherwise they fail with <c>job.worker_lost</c>. A pending cancel request ends the job Cancelled. Returns true when re-queued.
    /// </summary>
    public bool ExpireLease(DateTimeOffset now, bool resumable)
    {
        if (Status != JobStatus.Running) throw new DomainRuleException($"Cannot expire the lease of a {Status} job.");
        if (LeaseExpiresAt is { } expires && expires > now) throw new DomainRuleException("Lease has not expired.");
        if (CancelRequested)
        {
            // A pending cancel wins over requeue/worker_lost: the worker is gone, so nothing is left to stop cooperatively.
            Status = JobStatus.Cancelled;
            Release(now);
            return false;
        }
        if (resumable && Attempt < MaxAttempts)
        {
            Status = JobStatus.Queued;
            RunAfter = now;
            LockedBy = null;
            LeaseExpiresAt = null;
            UpdatedAt = now;
            return true;
        }
        ErrorJson = new JobError(WorkerLostCode, "Worker lost", "The worker stopped renewing its lease.").ToJson();
        Status = JobStatus.Failed;
        Release(now);
        return false;
    }

    /// <summary>Queued jobs cancel immediately; running jobs are flagged and must stop cooperatively.</summary>
    public void RequestCancel(DateTimeOffset now)
    {
        switch (Status)
        {
            case JobStatus.Queued:
                Status = JobStatus.Cancelled;
                CancelRequestedAt = now;
                Release(now);
                break;
            case JobStatus.Running:
                CancelRequestedAt ??= now;
                UpdatedAt = now;
                break;
            default:
                throw new DomainRuleException($"Cannot cancel a {Status} job.");
        }
    }

    /// <summary>Called by the worker once a cancel-requested job has actually stopped.</summary>
    public void MarkCancelled(DateTimeOffset now)
    {
        if (Status != JobStatus.Running) throw new DomainRuleException($"Cannot cancel a {Status} job.");
        Status = JobStatus.Cancelled;
        CancelRequestedAt ??= now;
        Release(now);
    }

    private void Release(DateTimeOffset now)
    {
        FinishedAt = now;
        LockedBy = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }
}
