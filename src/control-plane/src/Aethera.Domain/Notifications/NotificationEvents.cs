namespace Aethera.Domain;

/// <summary>The event catalogue (ADR 0010). Channels subscribe to these names; the UI groups them by the part before the dot.</summary>
public static class NotificationEvents
{
    public const string DeploymentSucceeded = "deployment.succeeded";
    public const string DeploymentFailed = "deployment.failed";
    public const string BackupSucceeded = "backup.succeeded";
    public const string BackupFailed = "backup.failed";
    public const string TaskSucceeded = "task.succeeded";
    public const string TaskFailed = "task.failed";
    public const string ServerUnreachable = "server.unreachable";
    public const string ServerRecovered = "server.recovered";
    public const string ServerDiskHigh = "server.disk_high";
    public const string ServerMemoryHigh = "server.memory_high";
    public const string ContainerStopped = "container.stopped";
    public const string ContainerRestarting = "container.restarting";
    public const string CertificateExpiring = "certificate.expiring";
    public const string CertificateFailed = "certificate.failed";
    public const string UpdateAvailable = "update.available";

    public static readonly IReadOnlyList<string> All =
    [
        DeploymentSucceeded, DeploymentFailed, BackupSucceeded, BackupFailed, TaskSucceeded, TaskFailed, ServerUnreachable, ServerRecovered,
        ServerDiskHigh, ServerMemoryHigh, ContainerStopped, ContainerRestarting, CertificateExpiring, CertificateFailed, UpdateAvailable,
    ];

    /// <summary>Subscribed by a new channel: everything except the success events (noisy with auto-deploy).</summary>
    public static readonly IReadOnlyList<string> Defaults =
        All.Where(e => e is not (DeploymentSucceeded or BackupSucceeded or TaskSucceeded)).ToList();
}

/// <summary>
/// Something worth telling people about. With <see cref="AlertKey"/> set, a non-resolving event opens (or refreshes) the alert with that key
/// and a resolving one closes it and sends a recovery message; without it the event is only delivered.
/// </summary>
public sealed record NotificationEvent(Guid OrganizationId, string Type, AlertSeverity Severity, string Title, string? Message = null)
{
    public string? ResourceType { get; init; }
    public Guid? ResourceId { get; init; }

    /// <summary>The workload's environment, for channel filters; null for server-level events.</summary>
    public Guid? EnvironmentId { get; init; }

    /// <summary>Link target inside the UI, e.g. <c>/deployments/&lt;id&gt;</c>.</summary>
    public string? Path { get; init; }
    public string? AlertKey { get; init; }
    public bool Resolves { get; init; }
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}

/// <summary>
/// The one way features report events (ADR 0010). Never throws for a delivery problem: notifications must not fail the job that raised
/// them. Implemented by the notifications module; a no-op until it is registered.
/// </summary>
public interface INotifier
{
    Task PublishAsync(NotificationEvent notification, CancellationToken cancellationToken);
}
