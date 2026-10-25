using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Notifications;

/// <summary>
/// The deployment detector (a hook, called by the deploy job handler when it ends): a deployment that failed opens the alert
/// <c>deploy:&lt;workload id&gt;</c> and raises <c>deployment.failed</c>; one that is running resolves it and raises <c>deployment.succeeded</c>.
/// It reads the outcome from the stored deployment, so it does not matter how the handler left (returned, threw a job failure, was cancelled:
/// a cancelled or unfinished deployment says nothing). Never throws.
/// </summary>
public static class DeploymentNotifications
{
    public static string AlertKey(Guid workloadId) => $"deploy:{workloadId}";

    public static async Task PublishOutcomeAsync(IServiceProvider services, Guid deploymentId)
    {
        try
        {
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var d = await db.Deployments.AsNoTracking().Where(x => x.Id == deploymentId)
                .Select(x => new { x.Id, x.Status, x.Number, x.WorkloadId, x.EnvironmentId, x.FailureReason, x.FailureCode, x.FailedStep, x.CommitSha, x.Trigger })
                .FirstOrDefaultAsync(CancellationToken.None);
            if (d is null || d.Status is not (DeploymentStatus.Running or DeploymentStatus.Failed)) return;

            var w = await db.Workloads.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == d.WorkloadId)
                .Select(x => new { x.Name, IsService = x is Service, Environment = x.Environment.Name, Project = x.Environment.Project.Name, x.Environment.Project.OrganizationId })
                .FirstOrDefaultAsync(CancellationToken.None);
            if (w is null) return;

            var details = new Dictionary<string, string>
            {
                ["resource"] = w.Name, ["project"] = w.Project, ["environment"] = w.Environment, ["deployment"] = "#" + d.Number, ["trigger"] = d.Trigger.ToString().ToLowerInvariant(),
            };
            if (!string.IsNullOrEmpty(d.CommitSha)) details["commit"] = d.CommitSha.Length > 12 ? d.CommitSha[..12] : d.CommitSha;
            var failed = d.Status == DeploymentStatus.Failed;
            if (failed)
            {
                if (d.FailedStep is { } step) details["failedStep"] = step.ToString();
                if (d.FailureCode is not null) details["code"] = d.FailureCode;
            }

            var notifier = scope.ServiceProvider.GetRequiredService<INotifier>();
            await notifier.PublishAsync(new NotificationEvent(
                w.OrganizationId, failed ? NotificationEvents.DeploymentFailed : NotificationEvents.DeploymentSucceeded,
                failed ? AlertSeverity.Critical : AlertSeverity.Info,
                failed ? $"Deployment #{d.Number} of {w.Name} failed" : $"Deployment #{d.Number} of {w.Name} succeeded",
                failed ? d.FailureReason : null)
            {
                ResourceType = w.IsService ? "service" : "application", ResourceId = d.WorkloadId, EnvironmentId = d.EnvironmentId,
                Path = $"/deployments/{d.Id}", AlertKey = AlertKey(d.WorkloadId), Resolves = !failed, Details = details,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("Aethera.Notifications").LogWarning(ex, "Deployment notification for {Deployment} failed", deploymentId);
        }
    }
}
