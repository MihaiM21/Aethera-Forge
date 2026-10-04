using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// <c>application.lifecycle</c> (lock key <c>app:&lt;id&gt;</c>, so it never interleaves with a deployment): stop, start or restart the containers of
/// the live deployment. Stopping keeps the deployment a rollback point; the desired state is recorded so a restarting server does not undo it.
/// </summary>
public sealed class LifecycleJobHandler : IJobHandler
{
    public string Type => DeploymentService.LifecycleJobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var resolver = sp.GetRequiredService<IServerTransportResolver>();
        var payload = context.GetPayload<LifecyclePayload>();

        var app = await db.Workloads.FirstOrDefaultAsync(a => a.Id == payload.ApplicationId, cancellationToken)
                  ?? throw JobFailedException.Permanent("application.not_found", "The application no longer exists", failedStep: "start");
        var deployment = app.CurrentDeploymentId is { } id ? await db.Deployments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken) : null;
        if (deployment is null || deployment.ContainerIds.Count == 0)
            throw JobFailedException.Permanent("application.not_deployed", "The application has no running deployment", failedStep: "start");

        foreach (var container in deployment.ContainerIds)
        {
            await context.Log.WriteSystemAsync($"{payload.Action} {container}", cancellationToken);
            try
            {
                var options = ServerJobSupport(context, $"{payload.Action}.{container}");
                var outcome = payload.Action switch
                {
                    LifecycleAction.Stop => (await resolver.ExecuteAsync<ContainerStopCommand, DockerContainer>(app.ServerId, new ContainerStopCommand(container), options, cancellationToken)).Outcome.Succeeded,
                    LifecycleAction.Start => (await resolver.ExecuteAsync<ContainerStartCommand, DockerContainer>(app.ServerId, new ContainerStartCommand(container), options, cancellationToken)).Outcome.Succeeded,
                    _ => (await resolver.ExecuteAsync<ContainerRestartCommand, DockerContainer>(app.ServerId, new ContainerRestartCommand(container), options, cancellationToken)).Outcome.Succeeded,
                };
                if (!outcome) throw JobFailedException.Permanent("application.lifecycle_failed", $"Could not {payload.Action.ToString().ToLowerInvariant()} the application", $"The command failed for {container}.", payload.Action.ToString());
            }
            catch (ServerTransportException ex)
            {
                throw ex.Transient
                    ? JobFailedException.Transient(ex.Code, "The server could not be reached", ex.Message, payload.Action.ToString(), ex)
                    : JobFailedException.Permanent(ex.Code, "The agent could not run the command", ex.Message, payload.Action.ToString(), ex);
            }
        }

        var now = clock.UtcNow;
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            switch (payload.Action)
            {
                case LifecycleAction.Stop:
                    if (deployment.Status == DeploymentStatus.Running) deployment.MarkStopped(now);
                    app.DesiredState = DesiredState.Stopped;
                    app.Status = WorkloadStatus.Stopped;
                    break;
                case LifecycleAction.Start:
                    if (deployment.Status == DeploymentStatus.Stopped) deployment.MarkRunning(now);
                    app.DesiredState = DesiredState.Running;
                    app.Status = WorkloadStatus.Running;
                    break;
                default:
                    app.Status = WorkloadStatus.Running;
                    break;
            }
            app.StatusChangedAt = now;
            app.StatusObservedAt = now;
            app.StatusReason = null;
        }, cancellationToken);
    }

    private static CommandOptions ServerJobSupport(JobContext context, string step) => new()
    {
        IdempotencyKey = context.IdempotencyKey(step), JobId = context.JobId, OrganizationId = context.Job.OrganizationId, ActorUserId = context.Job.CreatedBy,
    };
}
