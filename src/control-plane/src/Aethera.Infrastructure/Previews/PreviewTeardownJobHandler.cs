using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Previews;

/// <summary>
/// <c>preview.teardown</c> (lock key <c>preview:&lt;id&gt;</c>, so it waits for a deployment of the preview in flight): removes everything the preview
/// put on its server: the containers (a compose project is taken down with its volumes), the named volumes, the images. The route lives in the
/// containers' labels and goes with them. Idempotent and resumable: every step tolerates what an earlier attempt already removed, and the preview
/// is set to Removed only at the end. The application's own containers and volumes carry other names and labels and are never touched.
/// </summary>
public sealed class PreviewTeardownJobHandler : IJobHandler
{
    public string Type => PreviewJobs.TeardownType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var resolver = sp.GetRequiredService<IServerTransportResolver>();
        var payload = context.GetPayload<PreviewTeardownPayload>();

        var preview = await db.Previews.FirstOrDefaultAsync(p => p.Id == payload.PreviewId, cancellationToken)
                      ?? throw JobFailedException.Permanent("preview.not_found", "The preview does not exist", failedStep: "start");
        if (preview.Status == PreviewStatus.Removed) return; // an earlier attempt finished
        if (preview.Status != PreviewStatus.Closing)
        {
            // The pull request was reopened (or pushed to) between the request and now: the new deployment owns the preview.
            await context.Log.WriteSystemAsync("The preview is no longer being removed; nothing to do.", cancellationToken);
            return;
        }

        var deployments = await db.Deployments.Where(d => d.PreviewId == preview.Id).OrderByDescending(d => d.Number).Take(50).ToListAsync(cancellationToken);
        // Only snapshots that say they are a preview's: what is removed below is named by them.
        var snapshots = deployments.Select(d => DeploymentSnapshot.TryFromJson(d.ConfigSnapshotJson, out var s) ? s : null).OfType<DeploymentSnapshot>()
            .Where(s => s.Preview is { } p && p.PreviewId == preview.Id).ToList();
        var latest = snapshots.FirstOrDefault();
        var stopTimeout = TimeSpan.FromSeconds(Math.Max(1, latest?.Runtime.StopTimeoutSeconds ?? RuntimeSnapshot.DefaultStopTimeoutSeconds));
        var removedContainers = 0;
        var removedVolumes = 0;
        try
        {
            var composeDown = false;
            if (latest?.Compose is { } compose)
            {
                var projectName = DeploymentPlanBuilder.ComposeProjectName(latest);
                await context.Log.WriteSystemAsync($"Taking the compose project {projectName} down, with its volumes.", cancellationToken);
                var project = new ComposeProjectSpec(projectName, compose.Content);
                if (compose.FilePath is { Length: > 0 } path && latest.Git is { } git)
                    project = project with { Git = new GitSourceSpec(git.Url, git.Branch, git.CommitPin, Depth: 1), ComposePath = path };
                var down = await resolver.ExecuteAsync<ComposeDownCommand, ComposeOutcome>(
                    preview.ServerId, new ComposeDownCommand(project, RemoveVolumes: true, RemoveOrphans: true, Timeout: stopTimeout),
                    WorkloadContainers.Options(context, "teardown.compose_down"), cancellationToken);
                composeDown = down.Outcome.Succeeded;
                if (!down.Outcome.Succeeded && down.Outcome.ErrorCode != CommandErrorCode.NotFound)
                    await context.Log.WriteSystemAsync($"compose down did not succeed ({down.Outcome.ErrorMessage ?? down.Outcome.Status.ToString()}); removing the containers one by one.", cancellationToken);
            }

            // Whatever carries the preview's label (the live deployment, a failed one that left a container) and the containers the deployments recorded.
            var names = new HashSet<string>(StringComparer.Ordinal);
            var listed = await resolver.ExecuteAsync<ContainerListCommand, IReadOnlyList<DockerContainer>>(
                preview.ServerId, new ContainerListCommand(All: true, LabelFilters: [$"aethera.preview={preview.Id}"]),
                WorkloadContainers.Options(context, "teardown.list"), cancellationToken);
            foreach (var container in listed.Outcome.EnsureSucceeded()) names.Add(container.Id);
            if (!composeDown)
                foreach (var id in deployments.SelectMany(d => d.ContainerIds)) names.Add(id);

            foreach (var name in names)
            {
                await context.Log.WriteSystemAsync($"Stopping and removing {name}", cancellationToken);
                var stop = await resolver.ExecuteAsync<ContainerStopCommand, DockerContainer>(
                    preview.ServerId, new ContainerStopCommand(name, stopTimeout), WorkloadContainers.Options(context, $"teardown.stop.{name}"), cancellationToken);
                var remove = await resolver.ExecuteAsync<ContainerRemoveCommand, Unit>(
                    preview.ServerId, new ContainerRemoveCommand(name, Force: !stop.Outcome.Succeeded, RemoveAnonymousVolumes: true),
                    WorkloadContainers.Options(context, $"teardown.remove.{name}"), cancellationToken);
                if (!remove.Outcome.Succeeded && remove.Outcome.ErrorCode != CommandErrorCode.NotFound)
                    throw JobFailedException.Permanent("preview.teardown_failed", "A container could not be removed",
                        $"Removing {name} failed: {remove.Outcome.ErrorMessage ?? remove.Outcome.Status.ToString()}", "remove");
                if (remove.Outcome.Succeeded) removedContainers++;
            }

            // Volumes: the named ones of the preview's snapshots (they live in the preview's own namespace), and what a compose project created.
            var volumes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
                foreach (var v in snapshot.Volumes.Where(v => v.HostPath is not { Length: > 0 }))
                {
                    // Only a volume in the preview's own namespace: a name that points anywhere else is never removed.
                    var dockerName = VolumeNames.DockerName(snapshot.Slug, snapshot.WorkloadId, v.Name);
                    if (VolumeNames.IsOwn(dockerName, snapshot.Slug, snapshot.WorkloadId)) volumes.Add(dockerName);
                }
            if (latest?.Compose is not null)
            {
                var project = DeploymentPlanBuilder.ComposeProjectName(latest);
                var listedVolumes = await resolver.ExecuteAsync<VolumeListCommand, IReadOnlyList<DockerVolume>>(
                    preview.ServerId, new VolumeListCommand([$"com.docker.compose.project={project}"]), WorkloadContainers.Options(context, "teardown.volumes"), cancellationToken);
                if (listedVolumes.Outcome.Succeeded)
                    foreach (var v in listedVolumes.Outcome.EnsureSucceeded()) volumes.Add(v.Name);
            }

            foreach (var volume in volumes.Order(StringComparer.Ordinal))
            {
                var remove = await resolver.ExecuteAsync<VolumeRemoveCommand, Unit>(
                    preview.ServerId, new VolumeRemoveCommand(volume, Force: true), WorkloadContainers.Options(context, $"teardown.volume.{volume}"), cancellationToken);
                if (remove.Outcome.Succeeded) removedVolumes++;
                else if (remove.Outcome.ErrorCode != CommandErrorCode.NotFound)
                    await context.Log.WriteSystemAsync($"The volume {volume} could not be removed ({remove.Outcome.ErrorMessage ?? remove.Outcome.Status.ToString()}).", cancellationToken);
            }

            // Images: those the preview's deployments built or pulled. In use by another container (a pulled base image) they stay.
            foreach (var image in deployments.Where(d => d.ImageRef is { Length: > 0 }).Select(d => d.ImageRef!).Distinct(StringComparer.Ordinal))
            {
                var result = await resolver.ExecuteAsync<ImageRemoveCommand, Unit>(
                    preview.ServerId, new ImageRemoveCommand(image, Force: false), WorkloadContainers.Options(context, $"teardown.image.{image}"), cancellationToken);
                if (!result.Outcome.Succeeded && result.Outcome.ErrorCode != CommandErrorCode.NotFound)
                    await context.Log.WriteSystemAsync($"The image {image} was kept ({result.Outcome.ErrorMessage ?? result.Outcome.Status.ToString()}).", cancellationToken);
            }
        }
        catch (ServerTransportException ex)
        {
            throw ex.Transient
                ? JobFailedException.Transient(ex.Code, "The server could not be reached", ex.Message, "teardown", ex)
                : JobFailedException.Permanent(ex.Code, "The agent could not run the command", ex.Message, "teardown", ex);
        }

        var now = clock.UtcNow;
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            foreach (var d in deployments.Where(d => d.Status == DeploymentStatus.Running)) d.MarkStopped(now);
            preview.Status = PreviewStatus.Removed;
            preview.StatusReason = null;
            preview.StatusChangedAt = now;
            preview.RemovedAt = now;
        }, CancellationToken.None);
        var organizationId = context.Job.OrganizationId;
        PreviewAudit.Record(db, clock, organizationId, "preview.removed", preview.Id,
            new { applicationId = preview.ApplicationId, number = preview.Number, containers = removedContainers, volumes = removedVolumes });
        await db.SaveChangesAsync(CancellationToken.None);
        await context.Log.WriteSystemAsync($"Done. Removed {removedContainers} container(s) and {removedVolumes} volume(s).", cancellationToken);
        context.SetResult(new { previewId = preview.Id, containersRemoved = removedContainers, volumesRemoved = removedVolumes });

        // The comment on the pull request and the slot this preview held.
        await PreviewComments.PostRemovedAsync(sp, preview.Id);
        await sp.GetRequiredService<PreviewService>().PromotePendingAsync(preview.ApplicationId, CancellationToken.None);
    }
}
