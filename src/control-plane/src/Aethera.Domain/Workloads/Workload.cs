namespace Aethera.Domain;

public enum DesiredState
{
    Running = 0,
    Stopped = 1,
}

/// <summary>
/// Observed state of the workload itself - the "application unavailable" axis (spec section 44, ADR 0002). It is only
/// meaningful when the server, agent and Docker axes are healthy; otherwise the API reports it as unknown (blocked by layer).
/// </summary>
public enum WorkloadStatus
{
    Unknown = 0,
    NotDeployed = 1,
    Deploying = 2,
    Running = 3,
    Unhealthy = 4,
    Stopped = 5,
    Failed = 6,
}

public enum RestartPolicy
{
    No = 0,
    Always = 1,
    OnFailure = 2,
    UnlessStopped = 3,
}

public enum PortProtocol
{
    Tcp = 0,
    Udp = 1,
}

public enum HealthCheckType
{
    None = 0,
    Http = 1,
    Tcp = 2,
    Container = 3,
}

/// <summary>Well-known deployment strategy names; the column is a free string so new strategies need no migration.</summary>
public static class DeploymentStrategies
{
    public const string Recreate = "recreate";
    public const string LowDowntime = "low-downtime";
}

public class HealthCheckConfig
{
    public HealthCheckType Type { get; set; } = HealthCheckType.None;
    public string? Path { get; set; }
    public int? Port { get; set; }
    public int IntervalSeconds { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 5;
    public int Retries { get; set; } = 3;
    public int StartPeriodSeconds { get; set; }
}

/// <summary>Runtime settings shared by applications and services (stored as columns of the workload row).</summary>
public class RuntimeConfig
{
    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.UnlessStopped;
    public string DeploymentStrategy { get; set; } = DeploymentStrategies.Recreate;

    /// <summary>CPU cores (fractional allowed, e.g. 0.5).</summary>
    public double? CpuLimit { get; set; }
    public double? CpuReservation { get; set; }
    public long? MemoryLimitBytes { get; set; }
    public long? MemoryReservationBytes { get; set; }
    public int? PidsLimit { get; set; }

    public HealthCheckConfig HealthCheck { get; set; } = new();
}

/// <summary>
/// Anything the engine can deploy and run on a server: an <see cref="Application"/> or a <see cref="Service"/>.
/// Mapped table-per-hierarchy (single <c>workloads</c> table).
/// </summary>
public abstract class Workload : SoftDeletableEntity
{
    public Guid EnvironmentId { get; set; }
    public ProjectEnvironment Environment { get; set; } = null!;
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Description { get; set; }

    public DesiredState DesiredState { get; set; } = DesiredState.Running;
    public WorkloadStatus Status { get; set; } = WorkloadStatus.Unknown;
    public DateTimeOffset? StatusChangedAt { get; set; }

    /// <summary>Last time the status was actually observed (staleness indicator).</summary>
    public DateTimeOffset? StatusObservedAt { get; set; }

    /// <summary>Why the status is what it is, e.g. "container exited (OOMKilled)".</summary>
    public string? StatusReason { get; set; }

    /// <summary>The deployment currently serving traffic, if any.</summary>
    public Guid? CurrentDeploymentId { get; set; }

    /// <summary>Last allocated per-workload deployment number. Bump via <see cref="AllocateDeploymentNumber"/>.</summary>
    public int DeploymentSequence { get; private set; }

    public RuntimeConfig Runtime { get; set; } = new();

    public List<WorkloadPort> Ports { get; set; } = [];
    public List<EnvironmentVariable> EnvironmentVariables { get; set; } = [];
    public List<Volume> Volumes { get; set; } = [];
    public List<WorkloadDomain> Domains { get; set; } = [];
    public List<WorkloadNetwork> Networks { get; set; } = [];

    /// <summary>
    /// Returns the next sequential deployment number. Concurrent allocations are rejected by the
    /// <c>xmin</c> concurrency token and by the unique (workload, number) index on deployments.
    /// </summary>
    public int AllocateDeploymentNumber() => ++DeploymentSequence;
}

public enum ApplicationSourceKind
{
    Git = 0,
    DockerImage = 1,
    Dockerfile = 2,
    Compose = 3,
    Static = 4,
    Nixpacks = 5,
}

/// <summary>
/// A deployable application. Git-based kinds (Git, Dockerfile, Static, Nixpacks, repo-backed Compose) use
/// <see cref="GitSource"/> + <see cref="BuildConfig"/>; DockerImage uses <see cref="ImageSource"/>;
/// Compose uses <see cref="ComposeSource"/>.
/// </summary>
public class Application : Workload
{
    public ApplicationSourceKind SourceKind { get; set; } = ApplicationSourceKind.Git;
    public GitSource? GitSource { get; set; }
    public BuildConfig? BuildConfig { get; set; }
    public ImageSource? ImageSource { get; set; }
    public ComposeSource? ComposeSource { get; set; }
}

/// <summary>Infrastructure from a template (PostgreSQL, Redis, Grafana, ...), runnable like an application.</summary>
public class Service : Workload
{
    public required string TemplateKey { get; set; }
    public string? TemplateVersion { get; set; }
    public required string Image { get; set; }

    /// <summary>Template-specific settings (jsonb), editable after creation.</summary>
    public string ConfigJson { get; set; } = "{}";
}
