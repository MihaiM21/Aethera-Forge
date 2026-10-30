using Aethera.Domain;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Mesh;

/// <summary>Payload of <c>mesh.reconcile</c>: ids only. The mesh of one organization is reconciled at a time.</summary>
public sealed class MeshReconcilePayload
{
    public Guid OrganizationId { get; set; }

    /// <summary>Why it was queued (<c>settings</c>, <c>agent_connected</c>, <c>endpoint</c>, <c>manual</c>, <c>drift</c>), for the job list.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// <c>mesh.reconcile</c> (ADR 0012, lock key <c>mesh:&lt;organization&gt;</c>): pushes the mesh the setting asks for to every connected server of
/// the organization. Failures of single servers are recorded on the servers; the job itself only fails (and is retried) when an agent could not
/// be reached.
/// </summary>
public sealed class MeshReconcileJobHandler : IJobHandler
{
    public const string JobType = "mesh.reconcile";

    public string Type => JobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.GetPayload<MeshReconcilePayload>();
        var reconciler = context.Services.GetRequiredService<MeshReconciler>();
        await context.Log.WriteSystemAsync($"Reconciling the private network ({payload.Reason ?? "requested"}).", cancellationToken);

        var result = await reconciler.ReconcileAsync(
            payload.OrganizationId, context.IdempotencyKey, line => context.Log.WriteSystemAsync(line, cancellationToken).AsTask(), cancellationToken);
        context.SetResult(new { result.Rounds, result.Applied, result.Failed, result.Removed, result.Skipped });
        await context.Log.WriteSystemAsync(
            $"Done: {result.Applied} server(s) configured in {result.Rounds} round(s), {result.Failed} failed, {result.Skipped} not reachable.", cancellationToken);

        if (result.HadTransientFailure && context.Attempt < context.Job.MaxAttempts)
            throw JobFailedException.Transient("mesh.agent_unreachable", "A server could not be reached", "Some servers were offline; they are retried.", "mesh");
    }
}

/// <summary>Queues <c>mesh.reconcile</c> jobs. A job that is still waiting already covers a new request, so triggers never pile up.</summary>
public sealed class MeshReconcileScheduler(AetheraDbContext db, IJobQueue jobs, IClock clock)
{
    /// <summary>Coalesces servers that connect together (an update restarting every agent) into one job.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    /// <summary>Queues a reconcile for one organization. False when one is already waiting.</summary>
    public async Task<bool> EnqueueAsync(Guid organizationId, string reason, CancellationToken ct, bool immediately = true)
    {
        if (await db.Jobs.AsNoTracking().AnyAsync(j => j.Type == MeshReconcileJobHandler.JobType && j.OrganizationId == organizationId && j.Status == JobStatus.Queued, ct))
            return false;

        await jobs.EnqueueAsync(new JobRequest(MeshReconcileJobHandler.JobType, new MeshReconcilePayload { OrganizationId = organizationId, Reason = reason })
        {
            OrganizationId = organizationId,
            Resource = new JobResource("mesh", organizationId),
            LockKey = $"mesh:{organizationId:N}",
            MaxAttempts = 3,
            RunAfter = immediately ? null : clock.UtcNow + Debounce,
        }, ct);
        return true;
    }

    /// <summary>Queues a reconcile for every organization that has an active server with an agent (the mesh setting is instance wide).</summary>
    public async Task<int> EnqueueAllAsync(string reason, CancellationToken ct)
    {
        var organizations = await db.Servers.AsNoTracking()
            .Where(s => s.Transport == ServerTransport.Agent && s.Lifecycle == ServerLifecycle.Active).Select(s => s.OrganizationId).Distinct().ToListAsync(ct);
        var queued = 0;
        foreach (var organization in organizations)
            if (await EnqueueAsync(organization, reason, ct)) queued++;
        return queued;
    }
}

/// <summary>
/// A server's agent connected: while the mesh is on its interface may be gone (a reboot removes WireGuard interfaces) or never have existed, so
/// the mesh is reconciled; while it is off a server that still has an interface is cleaned up.
/// </summary>
public sealed class MeshConnectedHook(IServiceScopeFactory scopes, ILogger<MeshConnectedHook> logger) : IAgentConnectedHook
{
    public void OnConnected(Guid serverId) => _ = Task.Run(async () =>
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var server = await db.Servers.AsNoTracking().Where(s => s.Id == serverId && s.Transport == ServerTransport.Agent)
                .Select(s => new { s.OrganizationId, s.MeshProvider }).FirstOrDefaultAsync(timeout.Token);
            if (server is null) return;
            var setting = await MeshSettingsStore.GetAsync(db, timeout.Token);
            if (!setting.Enabled && server.MeshProvider == MeshProvider.None) return;
            await scope.ServiceProvider.GetRequiredService<MeshReconcileScheduler>()
                .EnqueueAsync(server.OrganizationId, "agent_connected", timeout.Token, immediately: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Server {ServerId}: the mesh could not be queued after the agent connected: {Error}", serverId, ex.Message);
        }
    });
}
