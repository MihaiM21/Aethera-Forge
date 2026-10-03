using System.Net.Sockets;
using Aethera.Domain;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Agents.Monitoring;

/// <summary>Opens a TCP connection to see whether a machine answers (replaced by a fake in tests).</summary>
public interface IReachabilityProbe
{
    Task<bool> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class TcpReachabilityProbe : IReachabilityProbe
{
    public async Task<bool> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutSource.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
}

/// <summary>
/// The "server unavailable" axis (ADR 0002 "Failure model"): while a server has no agent session, a TCP connect to its probe port (the
/// configured port, else the SSH port of a server that has SSH credentials) is tried every 30 s. With no probe target the axis stays
/// <c>unknown</c>, never <c>unavailable</c>. Two consecutive failures are needed to report the machine unreachable.
/// </summary>
public sealed class ReachabilityProbeService(
    IServiceScopeFactory scopes, AgentSessionRegistry registry, ServerStatusService status, IReachabilityProbe probe,
    IOptions<AgentGatewayOptions> options, ILogger<ReachabilityProbeService> logger) : BackgroundService
{
    private readonly AgentGatewayOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (AetheraHost.IsOpenApiGeneration || !_options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(0.05, _options.ReachabilityProbeSeconds)));
        do
        {
            try { await ProbeAllAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Reachability probing failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One round over every server that currently has no agent session. Public for tests.</summary>
    public async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, string Host, int? Port)> targets;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var servers = await db.Servers.AsNoTracking()
                .Where(s => s.Lifecycle != ServerLifecycle.Disabled && s.AgentStatus != AgentStatus.Connected)
                .Select(s => new { s.Id, s.Host, s.ReachabilityProbePort, s.SshPort, HasSsh = s.SshCredentialSecretId != null })
                .ToListAsync(cancellationToken);
            targets = servers.Select(s => (s.Id, s.Host, ProbePort(s.ReachabilityProbePort, s.SshPort, s.HasSsh))).ToList();
        }

        foreach (var (id, host, port) in targets)
        {
            if (port is not { } p || registry.IsConnected(id)) continue;
            var reachable = await probe.ProbeAsync(host, p, TimeSpan.FromSeconds(_options.ReachabilityConnectTimeoutSeconds), cancellationToken);
            await status.ReachabilityObservedAsync(id, reachable, cancellationToken);
        }
    }

    /// <summary>The probe target: the configured port, else the SSH port when the server can be reached over SSH, else none.</summary>
    public static int? ProbePort(int? configured, int sshPort, bool hasSshCredentials) => configured ?? (hasSshCredentials ? sshPort : null);
}
