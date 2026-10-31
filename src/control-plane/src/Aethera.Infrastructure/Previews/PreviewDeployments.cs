using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Previews;

/// <summary>
/// The parts of a deploy job that differ for a pull request preview (ADR 0012), called by <see cref="DeployJobHandler"/>: the frozen snapshot, and
/// the status of the <see cref="PreviewEnvironment"/> in place of the workload's. A preview deployment never writes the workload's
/// <c>Status</c>, <c>CurrentDeploymentId</c> or image records.
/// </summary>
public static class PreviewDeployments
{
    /// <summary>Other deployments of the preview whose image is looked at when one is promoted.</summary>
    private const int ImageCleanupWindow = 10;

    /// <summary>
    /// A deployment that waited for its job while the pull request was closed ends Cancelled and does nothing. Returns true when it did.
    /// </summary>
    public static async Task<bool> CancelIfClosedAsync(AetheraDbContext db, IClock clock, Deployment deployment, PreviewEnvironment preview, JobContext context)
    {
        if (preview.Status is not (PreviewStatus.Closing or PreviewStatus.Removed)) return false;
        if (!deployment.IsTerminal)
        {
            deployment.MarkCancelled(clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await context.Log.WriteSystemAsync("The pull request was closed; this preview deployment was cancelled.", CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Freezes the snapshot of a preview deployment and starts it. A deployment resumed after a crash keeps the snapshot it started with.
    /// </summary>
    /// <exception cref="UnsupportedSourceException">The application or the server cannot host a preview.</exception>
    public static async Task<DeploymentSnapshot> FreezeAsync(
        IServiceProvider sp, AetheraDbContext db, IClock clock, Deployment deployment, Workload app, PreviewEnvironment preview, JobContext context, CancellationToken ct)
    {
        if (deployment.Status == DeploymentStatus.InProgress && DeploymentSnapshot.TryFromJson(deployment.ConfigSnapshotJson, out var resumed))
            return resumed!;

        var options = sp.GetRequiredService<IOptions<ProxyOptions>>().Value;
        var result = await PreviewSnapshotFactory.CreateAsync(db, options, deployment, app, ct);
        deployment.Start(result.Snapshot.ToJson(), clock.UtcNow);
        await db.SaveChangesAsync(ct);

        var git = result.Snapshot.Git!;
        await context.Log.WriteSystemAsync(
            $"Preview of pull request #{preview.Number}: {git.Branch}{(git.CommitPin is { Length: >= 7 } sha ? $" at {sha[..7]}" : "")}{(preview.FromFork ? " (from a fork)" : "")}.", ct);
        foreach (var note in result.Notes) await context.Log.WriteSystemAsync(note, ct);
        return result.Snapshot;
    }

    /// <summary>The preview is deploying (what the workload's "Deploying" is for production).</summary>
    public static async Task BeginAsync(AetheraDbContext db, IClock clock, PreviewEnvironment preview, Deployment deployment, CancellationToken ct) =>
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            if (preview.Status is PreviewStatus.Closing or PreviewStatus.Removed) return;
            preview.Status = PreviewStatus.Deploying;
            preview.StatusChangedAt = clock.UtcNow;
            preview.StatusReason = $"Deployment #{deployment.Number} in progress";
        }, ct);

    /// <summary>
    /// The deployment did not become the live one: the preview keeps serving the previous deployment when it still runs, else it is Failed with
    /// the reason. A preview that is being removed stays so.
    /// </summary>
    public static async Task RestoreAsync(AetheraDbContext db, IClock clock, PreviewEnvironment preview, Deployment? previous, string reason, bool failed)
    {
        var now = clock.UtcNow;
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            if (preview.Status is PreviewStatus.Closing or PreviewStatus.Removed) return;
            if (previous is not null && previous.Status == DeploymentStatus.Running)
            {
                preview.Status = PreviewStatus.Running;
                preview.StatusReason = failed ? $"Latest deployment failed; still serving #{previous.Number}. {reason}" : null;
            }
            else
            {
                preview.Status = PreviewStatus.Failed;
                preview.StatusReason = reason;
            }

            preview.StatusChangedAt = now;
        }, CancellationToken.None);
    }

    /// <summary>The deployment is live: it becomes the preview's current one and the previous one is superseded. The previous containers were replaced by the strategy.</summary>
    public static async Task PromoteAsync(
        AetheraDbContext db, IServiceProvider sp, IClock clock, PreviewEnvironment preview, Deployment deployment, Deployment? previous, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var supersede = previous is not null && previous.Id != deployment.Id;
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            if (supersede && previous!.Status is DeploymentStatus.Running or DeploymentStatus.Stopped) previous.MarkSuperseded(now);
            preview.CurrentDeploymentId = deployment.Id;
            if (preview.Status is PreviewStatus.Closing or PreviewStatus.Removed) return; // closed meanwhile: the teardown that waits for the lock takes it all down
            preview.Status = PreviewStatus.Running;
            preview.StatusChangedAt = now;
            preview.StatusReason = null;
        }, ct);
        await CleanupImagesAsync(db, sp, preview, deployment, ct);
    }

    /// <summary>Removes the images of the preview's other deployments (the superseded ones and those that failed after their build). Best effort.</summary>
    private static async Task CleanupImagesAsync(AetheraDbContext db, IServiceProvider sp, PreviewEnvironment preview, Deployment current, CancellationToken ct)
    {
        try
        {
            var images = await db.Deployments.AsNoTracking()
                .Where(d => d.PreviewId == preview.Id && d.Id != current.Id && d.ImageRef != null && d.ImageRef != current.ImageRef)
                .OrderByDescending(d => d.Number).Take(ImageCleanupWindow).Select(d => d.ImageRef!).Distinct().ToListAsync(ct);
            var resolver = sp.GetRequiredService<IServerTransportResolver>();
            foreach (var image in images)
            {
                await resolver.ExecuteAsync<ImageRemoveCommand, Unit>(preview.ServerId, new ImageRemoveCommand(image, Force: false), CommandOptions.For($"preview-cleanup:{current.Id}:{image}"), ct);
                // Built on another server (ADR 0012): its local copy goes too; nothing is deleted from the registry.
                if (current.BuildServerId is { } buildServerId && buildServerId != preview.ServerId)
                    await resolver.ExecuteAsync<ImageRemoveCommand, Unit>(buildServerId, new ImageRemoveCommand(image, Force: false), CommandOptions.For($"preview-cleanup-build:{current.Id}:{image}"), ct);
            }
        }
        catch (Exception)
        {
            // Housekeeping: the teardown removes whatever is left.
        }
    }
}
