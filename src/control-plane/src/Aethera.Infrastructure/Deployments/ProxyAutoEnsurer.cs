using System.Collections.Concurrent;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Proxy;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// Makes every server's proxy present as soon as its agent connects (ADR 0011), like Coolify starts its proxy with the server: automatic
/// domains, the control plane's own domain and the first deployment then work without waiting for a deployment that has a domain. One ensure
/// per server at a time; a failure is logged and retried by the next connect or deployment.
/// </summary>
public sealed class ProxyAutoEnsurer(IServiceScopeFactory scopes, ILogger<ProxyAutoEnsurer> logger) : IAgentConnectedHook
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    public void OnConnected(Guid serverId)
    {
        if (!_running.TryAdd(serverId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                await EnsureAsync(scopes, serverId, timeout.Token);
            }
            catch (Exception ex) when (ex is ServerTransportException or OperationCanceledException or InvalidOperationException)
            {
                logger.LogWarning("Server {ServerId}: the proxy could not be ensured after the agent connected: {Error}", serverId, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Server {ServerId}: ensuring the proxy failed", serverId);
            }
            finally
            {
                _running.TryRemove(serverId, out _);
            }
        });
    }

    /// <summary>Builds and sends the ensure command of <paramref name="serverId"/>; returns null when the server's transport cannot run a proxy (SSH).</summary>
    public static async Task<ProxyEnsured?> EnsureAsync(IServiceScopeFactory scopes, Guid serverId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var command = await ProxyConfigurator.BuildEnsureCommandAsync(
            db, sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<IProxyProvider>(), sp.GetRequiredService<IOptions<ProxyOptions>>().Value,
            serverId, onSecret: null, ct);
        var resolver = sp.GetRequiredService<IServerTransportResolver>();
        var resolved = await resolver.ExecuteAsync<ProxyEnsureCommand, ProxyEnsured>(serverId, command, CommandOptions.For($"proxy:auto:{Guid.NewGuid():N}"), ct);
        if (resolved.Transport != TransportKind.Agent && !resolved.Outcome.Succeeded) return null;
        return resolved.Outcome.EnsureSucceeded();
    }
}
