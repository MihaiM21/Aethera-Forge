using System.Text.Json;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// A deploy job that is cancelled before a worker started it never runs its handler, so nothing would end its deployment: it would stay "Queued"
/// (or, waiting for a retry, "InProgress") forever. The code that cancels jobs calls this afterwards.
/// </summary>
public static class DeploymentCancellation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Ends the deployment of <paramref name="job"/> as Cancelled when the job is a cancelled <c>application.deploy</c> whose deployment has not
    /// finished, and gives the workload its status back when it was waiting for a retry. Saves its changes. Returns true when it changed anything.
    /// </summary>
    public static async Task<bool> OnJobCancelledAsync(AetheraDbContext db, IClock clock, Job job, CancellationToken ct)
    {
        if (job.Type != DeploymentService.DeployJobType || job.Status != JobStatus.Cancelled) return false;
        var payload = JsonSerializer.Deserialize<DeployPayload>(job.PayloadJson, Json);
        if (payload is null || payload.DeploymentId == Guid.Empty) return false;

        var deployment = await db.Deployments.Include(d => d.Steps).FirstOrDefaultAsync(d => d.Id == payload.DeploymentId, ct);
        if (deployment is not { Status: DeploymentStatus.Queued or DeploymentStatus.InProgress }) return false;

        var wasStarted = deployment.Status == DeploymentStatus.InProgress;
        deployment.MarkCancelled(clock.UtcNow);
        await db.SaveChangesAsync(ct);

        if (wasStarted && deployment.PreviewId is { } previewId)
        {
            // A pull request preview (ADR 0012) gives its own status back, never the application's.
            if (await db.Previews.FirstOrDefaultAsync(p => p.Id == previewId, ct) is { Status: PreviewStatus.Deploying } preview)
            {
                var previousPreview = preview.CurrentDeploymentId is { } live ? await db.Deployments.FirstOrDefaultAsync(d => d.Id == live, ct) : null;
                await Previews.PreviewDeployments.RestoreAsync(db, clock, preview, previousPreview, "Deployment cancelled.", failed: false);
            }

            return true;
        }

        if (wasStarted && await db.Workloads.FirstOrDefaultAsync(w => w.Id == deployment.WorkloadId, ct) is { Status: WorkloadStatus.Deploying } workload)
        {
            var previous = workload.CurrentDeploymentId is { } current ? await db.Deployments.FirstOrDefaultAsync(d => d.Id == current, ct) : null;
            await DeployJobHandler.RestoreStatusAsync(db, clock, workload, previous, "Deployment cancelled.", failed: false);
        }

        return true;
    }
}
