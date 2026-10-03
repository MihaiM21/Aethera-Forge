namespace Aethera.Domain;

public enum BuildStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
}

/// <summary>Build attempt belonging to a deployment. Rollbacks/redeploys of existing images have no build.</summary>
public class Build : MutableEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public int Attempt { get; set; } = 1;

    /// <summary>Engine name, see <see cref="BuildEngines"/>.</summary>
    public required string Engine { get; set; }

    public BuildStatus Status { get; private set; } = BuildStatus.Running;
    public string? Platform { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public long? DurationMs { get; private set; }
    public bool? CacheHit { get; set; }
    public int? CacheHitLayers { get; set; }
    public int? CacheTotalLayers { get; set; }
    public string? CommitSha { get; set; }
    public string? ResultImage { get; set; }
    public string? ResultImageDigest { get; set; }
    public long? ResultImageSizeBytes { get; set; }

    /// <summary>Job whose log chunks hold the build output.</summary>
    public Guid? JobId { get; set; }

    public void Start(DateTimeOffset now)
    {
        StartedAt = now;
        Status = BuildStatus.Running;
        UpdatedAt = now;
    }

    public void Finish(BuildStatus status, DateTimeOffset now)
    {
        if (status == BuildStatus.Running) throw new DomainRuleException("Finish requires a terminal status.");
        if (Status != BuildStatus.Running) throw new DomainRuleException($"Build already finished ({Status}).");
        Status = status;
        FinishedAt = now;
        DurationMs = StartedAt is { } s ? (long)(now - s).TotalMilliseconds : null;
        UpdatedAt = now;
    }
}
