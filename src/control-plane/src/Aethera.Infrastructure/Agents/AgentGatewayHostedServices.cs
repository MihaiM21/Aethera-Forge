using Aethera.Domain;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents;

/// <summary>
/// At start-up generates the internal CA on the very first run (ADR 0002: "generated on first start of the control plane") and loads the
/// revoked serials; afterwards re-reads them every 30 s so a revocation made through another API instance takes effect. On shutdown every
/// agent stream gets <c>Disconnect(SERVER_SHUTDOWN)</c> so agents reconnect with backoff instead of waiting for a timeout. A server stored as
/// "agent connected" that has no live session once the agents had time to reconnect (the API was killed or crashed, so nobody recorded the
/// disconnect) is marked unavailable; without this the status would stay "connected" for an agent that never comes back.
/// </summary>
public sealed class AgentGatewayLifetimeService(
    IInternalCa ca, AgentSessionRegistry registry, IServiceScopeFactory scopes, ServerStatusService status, IOptions<AgentGatewayOptions> options,
    ILogger<AgentGatewayLifetimeService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (AetheraHost.IsOpenApiGeneration || !options.Value.Enabled) return;
        try
        {
            var info = await ca.GetPublicInfoAsync(stoppingToken);
            await ca.RefreshRevocationsAsync(stoppingToken);
            logger.LogInformation("Agent gateway ready; CA fingerprint {Fingerprint}", info.FingerprintSha256);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The internal CA could not be loaded; agents cannot enroll or connect");
        }

        try
        {
            await Task.WhenAll(RefreshRevocationsLoopAsync(stoppingToken), ReconcileLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RefreshRevocationsLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ca.RefreshRevocationsAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Refreshing revoked certificates failed"); }
        }
    }

    private async Task ReconcileLoopAsync(CancellationToken stoppingToken)
    {
        // Agents that survived a restart reconnect within their backoff; give them as long as a heartbeat timeout (3 x 15 s) before judging.
        await Task.Delay(options.Value.HeartbeatTimeout, stoppingToken);
        using var timer = new PeriodicTimer(RefreshEvery);
        do
        {
            try { await ReconcileOrphanedSessionsAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Reconciling agent statuses failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Marks every server stored as agent-connected that has no live session as unavailable. Returns how many were fixed. Public for tests.</summary>
    public async Task<int> ReconcileOrphanedSessionsAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            ids = await db.Servers.AsNoTracking().Where(s => s.AgentStatus == AgentStatus.Connected).Select(s => s.Id).ToListAsync(cancellationToken);
        }

        var fixedCount = 0;
        foreach (var id in ids.Where(id => !registry.IsConnected(id)))
        {
            await status.AgentDisconnectedAsync(id, "no agent session after a control plane restart", cancellationToken);
            fixedCount++;
        }

        return fixedCount;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var serverId in registry.ConnectedServerIds)
            registry.Disconnect(serverId, P.DisconnectReason.ServerShutdown, "The control plane is shutting down.", TimeSpan.FromSeconds(5));
        await base.StopAsync(cancellationToken);
    }
}
