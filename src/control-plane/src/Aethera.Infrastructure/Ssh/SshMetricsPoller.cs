using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Agents.Transport;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;
using Aethera.Infrastructure.Ssh.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Ssh;

/// <summary>
/// Polls servers that have no agent session over SSH (ADR 0002: every 30 s, read-only commands) and feeds the result into the same metrics
/// ingestion path an agent's <c>MetricsReport</c> takes. The server is flagged as polled ("degraded: polling over SSH") in <c>ssh.polling.*</c>.
/// </summary>
public sealed class SshMetricsPoller(IServiceProvider services, IOptions<SshOptions> options, TimeProvider time, ILogger<SshMetricsPoller> logger) : BackgroundService
{
    private readonly SshOptions _options = options.Value;

    // Resolved on use: the build-time OpenAPI generator constructs hosted services without a master key or a database.
    private IServiceScopeFactory scopes => services.GetRequiredService<IServiceScopeFactory>();
    private SshConnectionPool pool => services.GetRequiredService<SshConnectionPool>();
    private AgentSessionRegistry registry => services.GetRequiredService<AgentSessionRegistry>();
    private AgentMetricsIngestor ingestor => services.GetRequiredService<AgentMetricsIngestor>();
    private ServerStatusService status => services.GetRequiredService<ServerStatusService>();
    private ISshFallbackPolicy policy => services.GetRequiredService<ISshFallbackPolicy>();
    private SshSettingsStore settings => services.GetRequiredService<SshSettingsStore>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (AetheraHost.IsOpenApiGeneration || !_options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(0.05, _options.MetricsPollSeconds)), time);
        do
        {
            try
            {
                await PollAllAsync(stoppingToken);
                await pool.CloseIdleAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SSH metrics polling failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One round over every server that may be polled. Public for tests.</summary>
    public async Task PollAllAsync(CancellationToken cancellationToken)
    {
        List<Guid> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            candidates = await db.Servers.AsNoTracking()
                .Where(s => s.Lifecycle != ServerLifecycle.Disabled && s.SshCredentialSecretId != null && s.AgentStatus != AgentStatus.Connected)
                .Select(s => s.Id).ToListAsync(cancellationToken);
        }

        using var gate = new SemaphoreSlim(Math.Max(1, _options.MetricsPollParallelism));
        await Task.WhenAll(candidates.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (registry.IsConnected(id) || !await policy.AllowsFallbackAsync(id, cancellationToken)) return;
                await PollServerAsync(id, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    /// <summary>Polls one server; failures are recorded in the polling state, never thrown.</summary>
    public async Task<bool> PollServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var key = SshSettingsStore.PollingKey(serverId);
        var previous = await settings.GetAsync<SshPollingState>(key, cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.MetricsPollTimeoutSeconds));
            PolledMetrics polled;
            using (var lease = await pool.AcquireAsync(serverId, timeout.Token))
            {
                RemoteResult result;
                try
                {
                    result = await lease.Connection.ExecuteAsync(new RemoteCommand(SshMetricsScript.Line), new RemoteExecOptions { Timeout = TimeSpan.FromSeconds(_options.MetricsPollTimeoutSeconds), MaxStdoutBytes = 4 * 1024 * 1024 }, timeout.Token);
                }
                catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
                {
                    lease.MarkBroken();
                    throw new ServerTransportException(TransportErrors.Unreachable, "The SSH connection was lost.", inner: ex);
                }

                if (!result.Succeeded) throw new ServerTransportException(TransportErrors.CommandFailed, "The metrics commands failed: " + DockerErrors.Summarize(result.Stderr, result.Stdout));
                polled = SshMetricsParsers.Parse(result.Stdout);
            }

            await ingestor.IngestAsync(registry.State(serverId), SshMetricsParsers.ToReport(polled, now), cancellationToken);
            await status.DockerObservedAsync(serverId, polled.DockerStatus, "polled over SSH", cancellationToken);
            await status.ReachabilityObservedAsync(serverId, reachable: true, cancellationToken); // an SSH session proves the machine answers
            await settings.SetAsync(key, new SshPollingState(now, now, null, previous?.Since ?? now), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is ServerTransportException or SshConnectException or TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Polling server {ServerId} over SSH failed: {Reason}", serverId, ex.Message);
            await settings.SetAsync(key, new SshPollingState(previous?.LastSuccessAt, now, ex is OperationCanceledException ? "The poll timed out." : ex.Message, previous?.Since ?? now), cancellationToken);
            return false;
        }
    }
}
