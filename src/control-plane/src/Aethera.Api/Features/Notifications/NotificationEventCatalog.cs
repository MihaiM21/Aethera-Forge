using Aethera.Domain;

namespace Aethera.Api.Features.Notifications;

/// <summary>One notification event as the UI lists it: grouped, labelled, with a default.</summary>
public sealed record NotificationEventInfo(string Type, string Group, string Label, string Description, bool Default);

public sealed record NotificationEventsResponse(IReadOnlyList<NotificationEventInfo> Items);

/// <summary>The descriptive side of <see cref="NotificationEvents"/> (the names themselves live in the domain).</summary>
public static class NotificationEventCatalog
{
    private static readonly Dictionary<string, (string Group, string Label, string Description)> Texts = new()
    {
        [NotificationEvents.DeploymentSucceeded] = ("Deployments", "Deployment succeeded", "A deployment reached running. Off by default: noisy with auto-deploy."),
        [NotificationEvents.DeploymentFailed] = ("Deployments", "Deployment failed", "A build or deployment failed. The previous version keeps serving where it can."),
        [NotificationEvents.BackupSucceeded] = ("Backups", "Backup succeeded", "A backup finished and was stored. Off by default."),
        [NotificationEvents.BackupFailed] = ("Backups", "Backup failed", "A scheduled or manual backup could not be completed."),
        [NotificationEvents.TaskSucceeded] = ("Scheduled tasks", "Task succeeded", "A scheduled task exited with code 0. Off by default."),
        [NotificationEvents.TaskFailed] = ("Scheduled tasks", "Task failed", "A scheduled task failed, timed out or exited with an error."),
        [NotificationEvents.ServerUnreachable] = ("Servers", "Server unreachable", "A server's agent has been disconnected for more than 2 minutes."),
        [NotificationEvents.ServerRecovered] = ("Servers", "Server recovered", "A server that was unreachable is connected again."),
        [NotificationEvents.ServerDiskHigh] = ("Servers", "Disk almost full", "The root filesystem has been above 85 % for 5 minutes."),
        [NotificationEvents.ServerMemoryHigh] = ("Servers", "Memory high", "Memory use has been above 90 % for 5 minutes."),
        [NotificationEvents.ContainerStopped] = ("Containers", "Container stopped", "A managed container died, was killed (out of memory) or stopped while it should be running."),
        [NotificationEvents.ContainerRestarting] = ("Containers", "Container restarting", "A container restarted at least 3 times in 10 minutes."),
        [NotificationEvents.CertificateExpiring] = ("Certificates", "Certificate expiring", "A domain's certificate expires within 14 days."),
        [NotificationEvents.CertificateFailed] = ("Certificates", "Certificate invalid", "A domain's certificate has been invalid for 24 hours."),
        [NotificationEvents.UpdateAvailable] = ("Updates", "Update available", "A new Aethera release is available."),
    };

    public static IReadOnlyList<NotificationEventInfo> All { get; } = NotificationEvents.All
        .Select(type =>
        {
            var (group, label, description) = Texts[type];
            return new NotificationEventInfo(type, group, label, description, NotificationEvents.Defaults.Contains(type));
        })
        .ToList();
}
