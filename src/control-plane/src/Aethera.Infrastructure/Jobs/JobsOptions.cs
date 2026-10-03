namespace Aethera.Infrastructure.Jobs;

/// <summary>
/// Settings of the job system, bound from <c>Aethera:Jobs</c>. All durations are seconds (fractions allowed, which keeps tests fast).
/// </summary>
public sealed class JobsOptions
{
    public const string Section = "Aethera:Jobs";

    /// <summary>Concurrent job slots per host (ADR 0004: default 4; the <c>AETHERA_JOB_CONCURRENCY</c> variable is the fallback). 0 disables the worker host.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>Lease length. A running job must be heartbeated within this time or the reaper takes it back.</summary>
    public double LeaseSeconds { get; set; } = 60;

    /// <summary>Idle poll interval: the safety net for missed <c>NOTIFY</c>s.</summary>
    public double PollSeconds { get; set; } = 5;

    /// <summary>Lease renewal (and cancel check) interval. 0 = <see cref="LeaseSeconds"/> / 6 (10 s for a 60 s lease).</summary>
    public double HeartbeatSeconds { get; set; }

    /// <summary>How often each host looks for expired leases.</summary>
    public double ReaperSeconds { get; set; } = 15;

    /// <summary>Delay before a job that lost the advisory-lock race is claimed again (no attempt is consumed).</summary>
    public double LockContentionDelaySeconds { get; set; } = 2;

    /// <summary>Retry backoff: <c>min(Max, Base * 2^(attempt-1))</c> with +/-20 % jitter.</summary>
    public double BaseBackoffSeconds { get; set; } = 5;

    public double MaxBackoffSeconds { get; set; } = 300;

    /// <summary>Log chunks are sealed and written at least this often (milliseconds).</summary>
    public int LogFlushMilliseconds { get; set; } = 100;

    /// <summary>Chunks per multi-row insert.</summary>
    public int LogBatchSize { get; set; } = 100;

    /// <summary>Per-stream size cap; beyond it one "log truncated" marker is written and further data is dropped.</summary>
    public long LogMaxBytesPerStream { get; set; } = 50L * 1024 * 1024;

    /// <summary>Identifies this host in <c>locked_by</c>. Empty = <c>machine:pid:random</c>.</summary>
    public string? WorkerId { get; set; }

    public TimeSpan Lease => TimeSpan.FromSeconds(Math.Max(0.2, LeaseSeconds));

    public TimeSpan Poll => TimeSpan.FromSeconds(Math.Max(0.05, PollSeconds));

    public TimeSpan Heartbeat => TimeSpan.FromSeconds(Math.Max(0.05, HeartbeatSeconds > 0 ? HeartbeatSeconds : Lease.TotalSeconds / 6));

    public TimeSpan Reaper => TimeSpan.FromSeconds(Math.Max(0.05, ReaperSeconds));

    /// <summary><c>min(Max, Base * 2^(attempt-1))</c>, jittered by +/-20 %.</summary>
    public TimeSpan Backoff(int attempt, Random? random = null)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var seconds = Math.Min(MaxBackoffSeconds, BaseBackoffSeconds * Math.Pow(2, exponent));
        var jitter = 0.8 + (random ?? Random.Shared).NextDouble() * 0.4;
        return TimeSpan.FromSeconds(Math.Max(0, seconds * jitter));
    }
}
