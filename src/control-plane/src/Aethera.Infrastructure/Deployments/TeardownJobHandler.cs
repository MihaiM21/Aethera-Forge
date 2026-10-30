using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// <c>application.teardown</c> (lock key <c>app:&lt;id&gt;</c>, so it waits for a deployment in flight): the workload was deleted, so its
/// containers go: stopped gracefully, then removed (a compose application: <c>compose down</c>, without volumes). The proxy routes are labels of the
/// containers and disappear with them. <b>Volumes are kept</b>; the job's result lists them.
/// </summary>
public sealed class TeardownJobHandler : IJobHandler
{
    public string Type => DeploymentService.TeardownJobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var resolver = sp.GetRequiredService<IServerTransportResolver>();
        var payload = context.GetPayload<TeardownPayload>();

        // The workload is soft-deleted already: read it past the query filter.
        var workload = await db.Workloads.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(w => w.Id == payload.WorkloadId, cancellationToken)
                       ?? throw JobFailedException.Permanent("workload.not_found", "The workload does not exist", failedStep: "start");
        // The server named at queue time: a deleted workload's own, or the old server of a workload that moved away (ADR 0012).
        var serverId = payload.ServerId != Guid.Empty ? payload.ServerId : workload.ServerId;
        var volumes = await db.Volumes.IgnoreQueryFilters().AsNoTracking().Where(v => v.WorkloadId == workload.Id && v.HostPath == null).ToListAsync(cancellationToken);
        var kept = volumes.Select(v => VolumeNames.DockerName(workload.Slug, workload.Id, v.Name)).Order(StringComparer.Ordinal).ToList();

        var deployments = await db.Deployments.AsNoTracking().Where(d => d.WorkloadId == workload.Id).OrderByDescending(d => d.Number).Take(20).ToListAsync(cancellationToken);
        DeploymentSnapshot? snapshot = null;
        foreach (var d in deployments)
        {
            if (DeploymentSnapshot.TryFromJson(d.ConfigSnapshotJson, out var parsed) && parsed is not null) { snapshot = parsed; break; }
        }

        var stopTimeout = TimeSpan.FromSeconds(Math.Max(1, snapshot?.Runtime.StopTimeoutSeconds ?? RuntimeSnapshot.DefaultStopTimeoutSeconds));
        var removed = 0;
        var composeDown = false;
        try
        {
            if (snapshot?.Compose is { } compose)
            {
                await context.Log.WriteSystemAsync($"Taking the compose project {DeploymentPlanBuilder.ComposeProjectName(snapshot)} down (volumes are kept).", cancellationToken);
                var project = new ComposeProjectSpec(DeploymentPlanBuilder.ComposeProjectName(snapshot), compose.Content);
                if (compose.FilePath is { Length: > 0 } path && snapshot.Git is { } git)
                    project = project with { Git = new GitSourceSpec(git.Url, git.Branch, git.CommitPin, Depth: 1), ComposePath = path };
                var down = await resolver.ExecuteAsync<ComposeDownCommand, ComposeOutcome>(
                    serverId, new ComposeDownCommand(project, RemoveVolumes: false, RemoveOrphans: true, Timeout: stopTimeout),
                    WorkloadContainers.Options(context, "teardown.compose_down"), cancellationToken);
                composeDown = down.Outcome.Succeeded;
                if (!down.Outcome.Succeeded && down.Outcome.ErrorCode != CommandErrorCode.NotFound)
                    await context.Log.WriteSystemAsync($"compose down did not succeed ({down.Outcome.ErrorMessage ?? down.Outcome.Status.ToString()}); removing the containers one by one.", cancellationToken);
            }

            // Whatever carries the workload's label (the live deployment, a failed one that left a container, a low-downtime candidate) and the
            // containers the deployments recorded.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var container in await sp.GetRequiredService<WorkloadContainers>().ListAsync(serverId, workload.Id, runningOnly: false, context, cancellationToken))
                names.Add(container.Id);
            // (A compose project that came down took its containers with it.)
            if (!composeDown)
                foreach (var id in deployments.SelectMany(d => d.ContainerIds)) names.Add(id);

            foreach (var name in names)
            {
                await context.Log.WriteSystemAsync($"Stopping and removing {name}", cancellationToken);
                // A stop that fails (not running, already gone) is fine: the forced removal below copes.
                var stop = await resolver.ExecuteAsync<ContainerStopCommand, DockerContainer>(
                    serverId, new ContainerStopCommand(name, stopTimeout), WorkloadContainers.Options(context, $"teardown.stop.{name}"), cancellationToken);
                var remove = await resolver.ExecuteAsync<ContainerRemoveCommand, Unit>(
                    serverId, new ContainerRemoveCommand(name, Force: !stop.Outcome.Succeeded), WorkloadContainers.Options(context, $"teardown.remove.{name}"), cancellationToken);
                if (!remove.Outcome.Succeeded && remove.Outcome.ErrorCode != CommandErrorCode.NotFound)
                    throw JobFailedException.Permanent("workload.teardown_failed", "A container could not be removed",
                        $"Removing {name} failed: {remove.Outcome.ErrorMessage ?? remove.Outcome.Status.ToString()}", "remove");
                removed++;
            }
        }
        catch (ServerTransportException ex)
        {
            throw ex.Transient
                ? JobFailedException.Transient(ex.Code, "The server could not be reached", ex.Message, "teardown", ex)
                : JobFailedException.Permanent(ex.Code, "The agent could not run the command", ex.Message, "teardown", ex);
        }

        await context.Log.WriteSystemAsync(kept.Count == 0 ? "Done. No named volumes were left behind." : $"Done. Volumes kept: {string.Join(", ", kept)}.", cancellationToken);
        context.SetResult(new { workloadId = workload.Id, containersRemoved = removed, volumesKept = kept });
    }
}
