using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Pki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.SelfOps;

/// <summary>
/// Publishes the fingerprint of the internal CA in the folder shared with the agent container of this machine, every few seconds. The
/// agent compares it with the CA its enrollment was made under: after a database reset (a new CA) an old enrollment can never connect, so
/// the agent forgets it and waits to be enrolled again by <see cref="LocalServerAutoEnroller"/>.
/// </summary>
public sealed class LocalAgentBeacon(IServiceScopeFactory scopes, UpdaterControl updater, IOptions<AgentGatewayOptions> options, ILogger<LocalAgentBeacon> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || AetheraHost.IsOpenApiGeneration) return;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await PublishOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "No shared folder for the agent container of this machine; not publishing the CA fingerprint");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing the CA fingerprint for the agent of this machine failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PublishOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var info = await scope.ServiceProvider.GetRequiredService<IInternalCa>().GetPublicInfoAsync(cancellationToken);
        updater.PublishCaFingerprint(info.FingerprintSha256);
    }
}
