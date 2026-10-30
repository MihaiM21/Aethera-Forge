using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Mesh;

public sealed class MeshOptions
{
    public const string Section = "Aethera:Mesh";

    /// <summary>How often the status of every server is refreshed and the configuration is checked for drift.</summary>
    public double StatusSeconds { get; set; } = 60;
}

/// <summary>
/// The upkeep of the mesh between reconciles: a cheap read-only <c>MeshStatus</c> of every connected server (so <c>Up</c> turns <c>Degraded</c>
/// when a peer goes silent), and a check that what the servers were last given still matches the servers that exist now. A server that was deleted,
/// moved to another address or given another endpoint changes that fingerprint, which queues a reconcile; so does the first check after a start.
/// </summary>
public sealed class MeshMonitor(
    IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<MeshOptions> options, ILogger<MeshMonitor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, string> _fingerprints = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (AetheraHost.IsOpenApiGeneration) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(0.05, options.Value.StatusSeconds)));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Refreshing the mesh status failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One round. Public for tests.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var setting = await MeshSettingsStore.GetAsync(db, ct);
        var servers = await db.Servers.AsNoTracking()
            .Where(s => s.Transport == ServerTransport.Agent && s.Lifecycle == ServerLifecycle.Active).OrderBy(s => s.Id).ToListAsync(ct);

        var scheduler = scope.ServiceProvider.GetRequiredService<MeshReconcileScheduler>();
        foreach (var group in servers.GroupBy(s => s.OrganizationId))
        {
            var members = group.ToList();
            var active = setting.Enabled || members.Any(s => s.MeshProvider != MeshProvider.None);
            if (!active)
            {
                _fingerprints.TryRemove(group.Key, out _);
                continue;
            }

            var fingerprint = Fingerprint(setting, members);
            if (!_fingerprints.TryGetValue(group.Key, out var known) || known != fingerprint)
            {
                await scheduler.EnqueueAsync(group.Key, known is null ? "startup" : "drift", ct);
                _fingerprints[group.Key] = fingerprint;
                continue; // the reconcile reads the status itself
            }

            if (setting.Enabled) await RefreshAsync(scope.ServiceProvider, setting, members, ct);
        }

        foreach (var gone in _fingerprints.Keys.Except(servers.Select(s => s.OrganizationId)).ToList()) _fingerprints.TryRemove(gone, out _);
    }

    /// <summary>Reads the status of every connected server now, without the drift check. Public for tests.</summary>
    public async Task RefreshStatusAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var setting = await MeshSettingsStore.GetAsync(db, ct);
        if (!setting.Enabled) return;
        var servers = await db.Servers.AsNoTracking()
            .Where(s => s.Transport == ServerTransport.Agent && s.Lifecycle == ServerLifecycle.Active).ToListAsync(ct);
        foreach (var group in servers.GroupBy(s => s.OrganizationId)) await RefreshAsync(scope.ServiceProvider, setting, group.ToList(), ct);
    }

    private async Task RefreshAsync(IServiceProvider services, MeshSetting setting, List<Server> members, CancellationToken ct)
    {
        var db = services.GetRequiredService<AetheraDbContext>();
        var transport = services.GetRequiredService<IServerTransportResolver>();
        var reconciler = services.GetRequiredService<MeshReconciler>();
        var clock = services.GetRequiredService<IClock>();
        var peerCount = members.Count(s => s.MeshPublicKey is not null && s.MeshAddress is not null);

        foreach (var server in members.Where(s => s.AgentStatus == AgentStatus.Connected && s.MeshProvider == setting.Provider && s.MeshStatus != MeshStatus.Disabled))
        {
            var kind = setting.Provider == MeshProvider.Tailscale ? MeshProviderKind.Tailscale : MeshProviderKind.WireGuard;
            if (kind == MeshProviderKind.WireGuard && (server.MeshPublicKey is null || server.MeshStatus == MeshStatus.Pending)) continue;
            try
            {
                var interfaceName = kind == MeshProviderKind.Tailscale ? MeshReconciler.TailscaleInterface : MeshReconciler.WireGuardInterface;
                var key = $"mesh:status:{server.Id:N}:{Guid.NewGuid():N}";
                var outcome = (await transport.ExecuteAsync<MeshStatusCommand, MeshStateInfo>(
                    server.Id, new MeshStatusCommand(kind, interfaceName), CommandOptions.For(key), ct)).Outcome;
                if (!outcome.Succeeded) continue;

                var state = outcome.EnsureSucceeded();
                var expected = Math.Max(0, peerCount - (server.MeshPublicKey is not null && server.MeshAddress is not null ? 1 : 0));
                var (status, handshake, error) = MeshReconciler.Evaluate(state, expected, clock.UtcNow);
                var address = kind == MeshProviderKind.Tailscale
                    ? state.Addresses.Select(a => a.Split('/')[0]).FirstOrDefault(a => MeshCidr.TryParseAddress(a, out _)) ?? server.MeshAddress
                    : server.MeshAddress;
                if (status != server.MeshStatus || error != server.MeshError || handshake != server.MeshHandshakeAt || address != server.MeshAddress)
                    await reconciler.WriteAsync(server.Id, new MeshServerState(setting.Provider, address, server.MeshPublicKey, status, error, handshake), ct);
            }
            catch (ServerTransportException ex)
            {
                logger.LogDebug("Mesh status of server {ServerId} could not be read: {Error}", server.Id, ex.Message);
            }
        }
    }

    /// <summary>Everything a peer list is built from; any change means the servers hold an outdated configuration.</summary>
    internal static string Fingerprint(MeshSetting setting, IEnumerable<Server> servers)
    {
        var sb = new StringBuilder().Append(setting.Provider).Append('|').Append(setting.Cidr).Append('|').Append(setting.ListenPort);
        foreach (var s in servers.OrderBy(s => s.Id))
            sb.Append('\n').Append(s.Id).Append('|').Append(s.PublicIp).Append('|').Append(s.Host).Append('|').Append(s.MeshEndpoint).Append('|')
                .Append(s.MeshListenPort).Append('|').Append(s.MeshAddress).Append('|').Append(s.MeshPublicKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
