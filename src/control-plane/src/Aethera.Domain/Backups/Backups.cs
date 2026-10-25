namespace Aethera.Domain;

public enum BackupDestinationKind
{
    /// <summary>An S3-compatible object store (AWS S3, Cloudflare R2, Backblaze B2, MinIO).</summary>
    S3 = 0,

    /// <summary>The agent's own backup directory on the workload's server. For tests: it does not leave the machine.</summary>
    Local = 1,
}

/// <summary>Where backups are stored (ADR 0010). The secret access key is a managed secret (<see cref="SecretPurpose.BackupCredential"/>).</summary>
public class BackupDestination : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }
    public BackupDestinationKind Kind { get; set; } = BackupDestinationKind.S3;

    /// <summary>https endpoint, e.g. <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>; null for <see cref="BackupDestinationKind.Local"/>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Null = <c>auto</c> (R2) / <c>us-east-1</c>.</summary>
    public string? Region { get; set; }
    public string? Bucket { get; set; }

    /// <summary>Key prefix inside the bucket, without leading or trailing slash (e.g. <c>aethera</c>).</summary>
    public string? PathPrefix { get; set; }
    public string? AccessKeyId { get; set; }
    public Guid? SecretAccessKeySecretId { get; set; }
    public Secret? SecretAccessKeySecret { get; set; }
    public bool ForcePathStyle { get; set; }

    /// <summary>Proposed for new backup policies.</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset? LastTestedAt { get; set; }

    /// <summary>Null when the last connection test passed.</summary>
    public string? LastTestError { get; set; }
}

public enum BackupKind
{
    /// <summary>A dump made by the database's own tool inside its container (<c>pg_dump</c>, <c>mongodump</c>, Redis RDB, ...).</summary>
    Database = 0,

    /// <summary>A gzipped tar of a named volume.</summary>
    Volume = 1,

    /// <summary>Aethera's own database, uploaded by the control plane itself.</summary>
    ControlPlane = 2,
}

/// <summary>What is backed up, where to and when (one per workload and kind; volume policies name their volume).</summary>
public class BackupPolicy : MutableEntity
{
    public Guid OrganizationId { get; set; }

    /// <summary>Null for <see cref="BackupKind.ControlPlane"/>.</summary>
    public Guid? WorkloadId { get; set; }
    public Workload? Workload { get; set; }
    public BackupKind Kind { get; set; }

    /// <summary>The volume of a <see cref="BackupKind.Volume"/> policy.</summary>
    public Guid? VolumeId { get; set; }
    public Volume? Volume { get; set; }

    public Guid DestinationId { get; set; }
    public BackupDestination Destination { get; set; } = null!;

    public bool Enabled { get; set; } = true;

    /// <summary>Five-field cron expression, evaluated in <see cref="TimeZone"/>.</summary>
    public string Schedule { get; set; } = "0 3 * * *";

    /// <summary>IANA time zone id.</summary>
    public string TimeZone { get; set; } = "UTC";

    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 6;

    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}

public enum BackupRunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

public enum BackupTrigger
{
    Scheduled = 0,
    Manual = 1,

    /// <summary>Taken automatically before a restore overwrites the workload's data.</summary>
    PreRestore = 2,
}

/// <summary>One backup: a stored, encrypted object (ADR 0010, format AEB1).</summary>
public class BackupRun : Entity
{
    public Guid OrganizationId { get; set; }
    public Guid PolicyId { get; set; }
    public BackupPolicy Policy { get; set; } = null!;

    /// <summary>Copied from the policy so the history survives a deleted workload.</summary>
    public Guid? WorkloadId { get; set; }
    public Guid DestinationId { get; set; }
    public BackupKind Kind { get; set; }
    public BackupTrigger Trigger { get; set; }
    public BackupRunStatus Status { get; set; } = BackupRunStatus.Pending;
    public Guid? JobId { get; set; }

    /// <summary>What produced the stream: <c>postgres</c>, <c>mongodb</c>, <c>redis</c>, <c>mysql</c>, <c>volume</c>, <c>aethera</c>.</summary>
    public string? Engine { get; set; }

    /// <summary>Object key in the destination (relative path for local destinations).</summary>
    public string? ObjectKey { get; set; }
    public long? RawSizeBytes { get; set; }
    public long? StoredSizeBytes { get; set; }
    public string? StoredSha256 { get; set; }

    /// <summary>Id of the key the object is encrypted with.</summary>
    public string? KeyId { get; set; }

    public string? Error { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Set when retention removed the stored object; the row stays as history.</summary>
    public DateTimeOffset? PrunedAt { get; set; }
}
