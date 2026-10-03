using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents;

/// <summary>
/// At start-up generates the internal CA on the very first run (ADR 0002: "generated on first start of the control plane") and loads the
/// revoked serials; afterwards re-reads them every 30 s so a revocation made through another API instance takes effect. On shutdown every
/// agent stream gets <c>Disconnect(SERVER_SHUTDOWN)</c> so agents reconnect with backoff instead of waiting for a timeout.
/// </summary>
public sealed class AgentGatewayLifetimeService(IInternalCa ca, AgentSessionRegistry registry, IOptions<AgentGatewayOptions> options, ILogger<AgentGatewayLifetimeService> logger) : BackgroundService
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

        using var timer = new PeriodicTimer(RefreshEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await ca.RefreshRevocationsAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Refreshing revoked certificates failed"); }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var serverId in registry.ConnectedServerIds)
            registry.Disconnect(serverId, P.DisconnectReason.ServerShutdown, "The control plane is shutting down.", TimeSpan.FromSeconds(5));
        await base.StopAsync(cancellationToken);
    }
}
