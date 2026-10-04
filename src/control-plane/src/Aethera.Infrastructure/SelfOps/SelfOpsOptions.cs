namespace Aethera.Infrastructure.SelfOps;

/// <summary>
/// Update checks and one-click updates, bound from <c>Aethera:Updates</c>. Checking only reads the public GitHub releases API; nothing is
/// sent about the installation except an <c>User-Agent</c> naming Aethera and its version.
/// </summary>
public sealed class UpdateOptions
{
    public const string Section = "Aethera:Updates";

    /// <summary>Look for new releases. Off for air-gapped installs; the version page then only shows the running version.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>owner/name</c> of the GitHub repository whose releases are checked.</summary>
    public string Repository { get; set; } = "MihaiM21/aethera-forge";

    /// <summary>Base URL of the GitHub API (a test server in tests, a mirror behind a proxy in some networks).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    public double IntervalHours { get; set; } = 6;

    /// <summary>Offer pre-releases (<c>v1.2.0-rc.1</c>) as updates.</summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>
    /// The folder shared with the updater container (a volume mounted at the same path in both). The control plane writes
    /// <c>request.json</c>; the updater writes <c>status.json</c> and touches <c>alive</c>. No network surface and no Docker access in the API.
    /// </summary>
    public string ControlDirectory { get; set; } = "/data/updater";

    public TimeSpan Interval => TimeSpan.FromHours(Math.Max(0.01, IntervalHours));
}

/// <summary>Scheduled housekeeping, bound from <c>Aethera:Maintenance</c>. A retention of 0 days keeps that kind of data forever.</summary>
public sealed class MaintenanceOptions
{
    public const string Section = "Aethera:Maintenance";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the pass looks at what is due (each task also has its own interval).</summary>
    public double IntervalMinutes { get; set; } = 60;

    /// <summary>Build and deploy log lines (<c>log_chunks</c>) older than this are deleted.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Status and lifecycle events and webhook deliveries.</summary>
    public int EventRetentionDays { get; set; } = 90;

    /// <summary>Audit events. Default one year; the audit log is the one thing people want to keep, so 0 disables deletion.</summary>
    public int AuditRetentionDays { get; set; } = 365;

    /// <summary>Expired or revoked login sessions and finished idempotency records.</summary>
    public int SessionRetentionDays { get; set; } = 7;

    /// <summary>Prune connected servers on a schedule (never volumes; rollback images are always kept).</summary>
    public bool PruneEnabled { get; set; } = true;

    public double PruneIntervalHours { get; set; } = 168;

    /// <summary>Unused images and build cache younger than this are kept (so a recent build is not thrown away right before it is needed).</summary>
    public int PruneKeepHours { get; set; } = 168;

    /// <summary>Back up the Aethera database with <c>pg_dump</c> (needs <c>pg_dump</c> next to the API: the container image has it).</summary>
    public bool BackupEnabled { get; set; } = true;

    public double BackupIntervalHours { get; set; } = 24;

    public string BackupDirectory { get; set; } = "/data/backups";

    /// <summary>Scheduled backups kept; the pre-migration backups have their own count (<see cref="PreMigrationBackupsKept"/>).</summary>
    public int BackupsKept { get; set; } = 7;

    public int PreMigrationBackupsKept { get; set; } = 5;

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(0.01, IntervalMinutes));
}
