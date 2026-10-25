namespace Aethera.Domain;

/// <summary>
/// A command run on a schedule inside a workload's running container (ADR 0010), like Coolify's scheduled tasks. The command line is run
/// as <c>sh -c "&lt;command&gt;"</c> in the container, never on the host.
/// </summary>
public class ScheduledTask : SoftDeletableEntity
{
    public Guid WorkloadId { get; set; }
    public Workload Workload { get; set; } = null!;
    public required string Name { get; set; }
    public required string Command { get; set; }

    /// <summary>Compose applications: the service whose container runs the command. Null = the workload's main container.</summary>
    public string? ComposeService { get; set; }

    /// <summary>Five-field cron expression, evaluated in <see cref="TimeZone"/>.</summary>
    public required string Schedule { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public int TimeoutSeconds { get; set; } = 3600;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}

public enum ScheduledTaskRunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,

    /// <summary>Not run: the workload was stopped or the previous run was still going.</summary>
    Skipped = 5,
}

public enum ScheduledTaskTrigger
{
    Scheduled = 0,
    Manual = 1,
}

public class ScheduledTaskRun : Entity
{
    public Guid TaskId { get; set; }
    public ScheduledTask Task { get; set; } = null!;
    public Guid WorkloadId { get; set; }
    public Guid? JobId { get; set; }
    public ScheduledTaskTrigger Trigger { get; set; }
    public ScheduledTaskRunStatus Status { get; set; } = ScheduledTaskRunStatus.Pending;
    public int? ExitCode { get; set; }

    /// <summary>End of the output (the full output is the job's log stream).</summary>
    public string? OutputTail { get; set; }
    public bool OutputTruncated { get; set; }
    public string? Error { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
