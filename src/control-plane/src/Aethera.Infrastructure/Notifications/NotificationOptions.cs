namespace Aethera.Infrastructure.Notifications;

/// <summary>
/// Settings of notifications and alerts (ADR 0010), bound from <c>Aethera:Notifications</c>. The instance's public address, used for the links in
/// messages, is <c>Aethera:PublicUrl</c> (environment <c>Aethera__PublicUrl</c>), falling back to <c>https://$AETHERA_DOMAIN</c>.
/// </summary>
public sealed class NotificationOptions
{
    public const string Section = "Aethera:Notifications";
    public const string PublicUrlKey = "Aethera:PublicUrl";

    /// <summary>Base URL of this instance (<c>https://aethera.example.com</c>). Set from <c>Aethera:PublicUrl</c> / <c>AETHERA_DOMAIN</c>; null = messages carry no link.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>While an alert stays open and keeps being reported, a reminder goes out this often (hours). Only warnings and critical alerts remind.</summary>
    public double ReminderHours { get; set; } = 6;

    /// <summary>Executions of a <c>notification.send</c> job before the delivery is recorded as failed.</summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>
    /// Webhook targets: by default only <c>https</c> and only public addresses (loopback, private, link-local and metadata addresses are
    /// refused, also after DNS resolution). Setting this allows <c>http</c> and private/loopback targets (an internal chat bridge, a test).
    /// </summary>
    public bool AllowPrivateWebhookTargets { get; set; }

    public double WebhookTimeoutSeconds { get; set; } = 10;

    public double SmtpTimeoutSeconds { get; set; } = 20;

    /// <summary>Run the detectors (server down, disk, memory, containers, certificates, updates). Off in tests, which call them directly.</summary>
    public bool DetectorsEnabled { get; set; } = true;

    public double DetectorIntervalSeconds { get; set; } = 30;

    /// <summary>An agent session missing for longer than this makes the server unreachable (minutes).</summary>
    public double ServerUnreachableMinutes { get; set; } = 2;

    public double DiskThresholdPercent { get; set; } = 85;
    public double MemoryThresholdPercent { get; set; } = 90;

    /// <summary>Disk and memory must stay above their threshold for this long (minutes).</summary>
    public double SustainedMinutes { get; set; } = 5;

    /// <summary>Container events are looked at this long after they happened, so a stop that is part of a deployment or a restart has been recorded first.</summary>
    public double ContainerSettleSeconds { get; set; } = 20;

    public int RestartLoopCount { get; set; } = 3;
    public double RestartLoopMinutes { get; set; } = 10;

    public double CertificateWarnDays { get; set; } = 14;

    /// <summary>A certificate error that lasts this long raises <c>certificate.failed</c> (hours).</summary>
    public double CertificateFailedHours { get; set; } = 24;

    public double CertificateCheckHours { get; set; } = 24;
    public double CertificateTimeoutSeconds { get; set; } = 5;

    public TimeSpan Reminder => TimeSpan.FromHours(Math.Max(0.001, ReminderHours));
}
