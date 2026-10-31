using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// Moving a workload to another server (ADR 0012) is a deployment on the target server whose job payload carries <see cref="DeployPayload.MoveFromServerId"/>.
/// <list type="bullet">
/// <item><b>Begin</b> (the deploy job starts): the workload and its domains switch to the target server, so the new deployment is frozen with them. Nothing
/// happens on the old server: it keeps serving until the new deployment is running.</item>
/// <item><b>Follow-up</b> (the deploy job ended, whatever the outcome): when the deployment is running, the old server's containers and images of the workload
/// are removed (an <c>application.teardown</c> job for the old server; volumes stay behind); when it failed or was cancelled, the workload and its domains
/// go back to the old server, which never stopped serving.</item>
/// </list>
/// Both are idempotent and run from the job, so a crash or a retried attempt resumes where it left off.
/// </summary>
public static class WorkloadMoves
{
    /// <summary>Idempotency key of the teardown a successful move queues for the old server.</summary>
    public static string TeardownKey(Guid deploymentId) => $"move-teardown:{deploymentId:N}";

    /// <summary>
    /// The domains of a workload that moves between servers follow it: they record the server of their workload, and an automatic domain is named after
    /// its server (<c>&lt;label&gt;.&lt;wildcard or ip.sslip.io&gt;</c>), so it is renamed to the new server's base. Pure over the loaded entities; returns
    /// the hostnames that could not be renamed because the new server has no known base.
    /// </summary>
    public static IReadOnlyList<string> RehomeDomains(IEnumerable<WorkloadDomain> domains, Server from, Server to)
    {
        var oldBase = DefaultDomainService.KnownBaseDomain(from);
        var newBase = DefaultDomainService.KnownBaseDomain(to);
        var stranded = new List<string>();
        foreach (var domain in domains.Where(d => !d.IsDeleted))
        {
            domain.ServerId = to.Id;
            if (!domain.IsAutomatic || oldBase is null || !domain.Hostname.EndsWith("." + oldBase, StringComparison.OrdinalIgnoreCase)) continue;
            if (newBase is null)
            {
                stranded.Add(domain.Hostname);
                continue;
            }

            domain.SetHostname(domain.Hostname[..^(oldBase.Length + 1)] + "." + newBase);
        }

        return stranded;
    }

    /// <summary>The deploy job of a move starts: the workload and its domains belong to the target server from now on.</summary>
    internal static async Task BeginAsync(
        AetheraDbContext db, Workload app, Deployment deployment, DeployPayload payload, JobContext context, CancellationToken ct)
    {
        if (payload.MoveFromServerId is not { } from || app.ServerId == deployment.ServerId) return;
        var servers = await db.Servers.Where(s => s.Id == from || s.Id == deployment.ServerId).ToListAsync(ct);
        var source = servers.FirstOrDefault(s => s.Id == from);
        var target = servers.FirstOrDefault(s => s.Id == deployment.ServerId);
        if (source is null || target is null) return; // a server vanished: the deployment fails on its own terms

        IReadOnlyList<string> stranded = [];
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            app.ServerId = target.Id;
            stranded = RehomeDomains(app.Domains, source, target);
        }, ct);
        await context.Log.WriteSystemAsync($"Moving {app.Name} from {source.Name} to {target.Name}. {source.Name} keeps serving until the new deployment is running.", ct);
        foreach (var host in stranded)
            await context.Log.WriteSystemAsync($"The automatic domain {host} still points at {source.Name}: {target.Name} has no wildcard domain or public IP to name a new one from.", ct);
    }

    /// <summary>Runs after the deploy job ended: cleans up the old server on success, puts the workload back on failure. Never throws.</summary>
    internal static async Task FollowUpAsync(JobContext context, DeployPayload payload)
    {
        if (payload.MoveFromServerId is not { } from) return;
        try
        {
            await using var scope = context.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var d = await db.Deployments.AsNoTracking().Where(x => x.Id == payload.DeploymentId)
                .Select(x => new { x.Status, x.ServerId, x.WorkloadId, x.IsRollbackPoint }).FirstOrDefaultAsync(CancellationToken.None);
            if (d is null) return;

            if (d.IsRollbackPoint && d.Status is DeploymentStatus.Running or DeploymentStatus.Superseded or DeploymentStatus.Stopped)
                await CleanUpOldServerAsync(scope.ServiceProvider, db, context, d.WorkloadId, from, payload.DeploymentId);
            else if (d.Status is DeploymentStatus.Failed or DeploymentStatus.Cancelled)
                await RevertAsync(db, context, d.WorkloadId, d.ServerId, from);
            // Queued or still in progress (a retry is coming): the next run of the job decides.
        }
        catch (Exception ex)
        {
            try
            {
                await context.Log.WriteSystemAsync($"The follow-up of the move did not finish ({ex.GetType().Name}); the old server was left as it is.", CancellationToken.None);
            }
            catch (Exception)
            {
                // Narration only.
            }
        }
    }

    private static async Task CleanUpOldServerAsync(IServiceProvider sp, AetheraDbContext db, JobContext context, Guid workloadId, Guid from, Guid deploymentId)
    {
        var isService = await db.Workloads.AsNoTracking().OfType<Service>().AnyAsync(s => s.Id == workloadId, CancellationToken.None);
        var jobs = sp.GetRequiredService<IJobQueue>();
        await jobs.EnqueueAsync(new JobRequest(DeploymentService.TeardownJobType, new TeardownPayload { WorkloadId = workloadId, ServerId = from })
        {
            OrganizationId = context.Job.OrganizationId, Resource = new JobResource(isService ? "service" : "application", workloadId),
            LockKey = DeploymentService.LockKey(workloadId), IdempotencyKey = TeardownKey(deploymentId), MaxAttempts = 3,
        }, CancellationToken.None);
        await context.Log.WriteSystemAsync("The new deployment is running. The containers on the old server are being removed (its volumes stay there).", CancellationToken.None);

        // The images the workload left on the old server (best effort; the maintenance prune catches up).
        var resolver = sp.GetRequiredService<IServerTransportResolver>();
        var images = await db.Images.Where(i => i.WorkloadId == workloadId && i.ServerId == from && i.RemovedAt == null && i.Deployment!.ImageRef != null)
            .Select(i => new { i.Id, Image = i.Deployment!.ImageRef! }).ToListAsync(CancellationToken.None);
        foreach (var image in images)
        {
            try
            {
                var removed = await resolver.ExecuteAsync<ImageRemoveCommand, Unit>(
                    from, new ImageRemoveCommand(image.Image, Force: false), CommandOptions.For($"move-image:{deploymentId:N}:{image.Id:N}"), CancellationToken.None);
                if (removed.Outcome.Succeeded || removed.Outcome.ErrorCode == CommandErrorCode.NotFound)
                    (await db.Images.FindAsync([image.Id], CancellationToken.None))!.RemovedAt = DateTimeOffset.UtcNow;
            }
            catch (ServerTransportException)
            {
                // The old server is unreachable: the images stay until it is back.
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task RevertAsync(AetheraDbContext db, JobContext context, Guid workloadId, Guid targetServerId, Guid from)
    {
        var app = await DeployJobHandler.LoadWorkloadAsync(db, workloadId, CancellationToken.None);
        if (app is null || app.ServerId != targetServerId) return; // already back, or moved again meanwhile
        var servers = await db.Servers.Where(s => s.Id == from || s.Id == targetServerId).ToListAsync(CancellationToken.None);
        var source = servers.FirstOrDefault(s => s.Id == from);
        var target = servers.FirstOrDefault(s => s.Id == targetServerId);
        if (source is null || target is null) return;

        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            app.ServerId = source.Id;
            RehomeDomains(app.Domains, target, source);
        }, CancellationToken.None);
        await context.Log.WriteSystemAsync($"The move failed. {app.Name} is back on {source.Name}, which kept serving.", CancellationToken.None);
    }
}
