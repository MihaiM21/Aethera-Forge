namespace Aethera.Domain;

public enum EmailTransport
{
    Smtp = 0,
    Resend = 1,
}

public enum SmtpEncryption
{
    None = 0,
    StartTls = 1,

    /// <summary>Implicit TLS (usually port 465).</summary>
    Tls = 2,
}

/// <summary>
/// How an organization sends email (one row per organization). Passwords and API keys are managed secrets
/// (<see cref="SecretPurpose.NotificationCredential"/>).
/// </summary>
public class EmailSettings : MutableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public bool Enabled { get; set; }
    public EmailTransport Transport { get; set; } = EmailTransport.Smtp;
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public SmtpEncryption SmtpEncryption { get; set; } = SmtpEncryption.StartTls;
    public string? SmtpUsername { get; set; }
    public Guid? SmtpPasswordSecretId { get; set; }
    public Guid? ResendApiKeySecretId { get; set; }
    public required string FromAddress { get; set; }
    public string? FromName { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
    public string? LastTestError { get; set; }
}

public enum NotificationChannelKind
{
    Email = 0,

    /// <summary>A signed JSON POST (HMAC-SHA256 over the body with the channel's secret).</summary>
    Webhook = 1,
}

/// <summary>Where notifications go and which events it wants (<see cref="NotificationEvents"/>).</summary>
public class NotificationChannel : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }
    public NotificationChannelKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Email: send to every administrator and owner of the organization.</summary>
    public bool SendToMembers { get; set; } = true;

    /// <summary>Email: extra addresses.</summary>
    public List<string> EmailRecipients { get; set; } = [];

    public string? WebhookUrl { get; set; }

    /// <summary>Webhook signing secret (managed, <see cref="SecretPurpose.NotificationCredential"/>).</summary>
    public Guid? WebhookSecretId { get; set; }

    /// <summary>Event types this channel receives (<see cref="NotificationEvents"/>).</summary>
    public List<string> Events { get; set; } = [];

    /// <summary>Only events of workloads in this project; null = every project.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Only events of workloads in this environment; null = every environment.</summary>
    public Guid? EnvironmentId { get; set; }
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

public enum AlertState
{
    Open = 0,
    Resolved = 1,
}

/// <summary>
/// An incident: opened by a failure event, refreshed while it continues, resolved by the matching recovery event. At most one open alert
/// per (organization, <see cref="DedupKey"/>).
/// </summary>
public class Alert : Entity
{
    public Guid OrganizationId { get; set; }

    /// <summary>The event type that opened it, e.g. <c>server.unreachable</c>.</summary>
    public required string Type { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;
    public AlertState State { get; set; } = AlertState.Open;

    /// <summary>e.g. <c>server.unreachable:&lt;server id&gt;</c>.</summary>
    public required string DedupKey { get; set; }
    public string? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }
    public required string Title { get; set; }
    public string? Message { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }
    public int NotificationCount { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}

public enum NotificationDeliveryStatus
{
    Sent = 0,
    Failed = 1,
}

/// <summary>One attempt to send one event to one channel (history and diagnostics).</summary>
public class NotificationDelivery : Entity
{
    public Guid OrganizationId { get; set; }
    public Guid ChannelId { get; set; }
    public Guid? AlertId { get; set; }
    public required string EventType { get; set; }
    public NotificationDeliveryStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
