namespace Aethera.Domain;

/// <summary>The resource a job acts on (ADR 0004 <c>resource_type</c>/<c>resource_id</c>), e.g. <c>("application", id)</c>.</summary>
public sealed record JobResource(string Type, Guid Id);

/// <summary>
/// Everything needed to enqueue a durable job (ADR 0004). Defaults match the table defaults: priority 0, one attempt,
/// runnable immediately, no lock, no idempotency key.
/// </summary>
/// <param name="Type">Handler type, <c>area.verb</c>: <c>application.deploy</c>, <c>server.prune</c>.</param>
/// <param name="Payload">
/// Inputs for the handler, serialized to JSON by the queue. Ids and references only; never secret values.
/// </param>
public sealed record JobRequest(string Type, object? Payload = null)
{
    /// <summary>The resource the job acts on; also what <c>GET /jobs?resourceType&amp;resourceId</c> filters by.</summary>
    public JobResource? Resource { get; init; }

    /// <summary>Serialization key (<c>app:&lt;id&gt;</c>, <c>service:&lt;id&gt;</c>): jobs sharing a key never run concurrently. Null = none.</summary>
    public string? LockKey { get; init; }

    /// <summary>Higher runs first. Always 0 in the MVP.</summary>
    public int Priority { get; init; }

    /// <summary>Executions allowed before the job fails for good (at least 1).</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>Earliest start. Null = now.</summary>
    public DateTimeOffset? RunAfter { get; init; }

    /// <summary>
    /// When set and a job with the same key already exists, that job is returned and nothing is enqueued (webhook delivery
    /// ids, <c>Idempotency-Key</c> headers).
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Retry chain: the job this one retries (ADR 0004 <c>parent_job_id</c>).</summary>
    public Guid? ParentJobId { get; init; }
}

/// <summary>Producer side of the durable job queue (ADR 0004). The worker side lives in the job system.</summary>
public interface IJobQueue
{
    /// <summary>
    /// Persists a new <see cref="JobStatus.Queued"/> job and wakes the workers. The creator (<c>created_by</c>) is taken
    /// from the current actor. Returns the existing job when the request's idempotency key was seen before.
    /// </summary>
    Task<Job> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default);
}
