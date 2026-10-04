using System.Collections.Concurrent;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Agents.Status;

/// <summary>One status axis changed value; written as a <see cref="ResourceEvent"/> (<c>status.changed</c>) that later feeds alerts.</summary>
public readonly record struct StatusTransition(string Axis, string Old, string New, string? Detail = null);

/// <summary>
/// Debounce for the reachability probe (ADR 0002: "2 consecutive failed reachability probes"): one success flips to reachable at once,
/// but unreachable needs <c>threshold</c> failures in a row.
/// </summary>
public sealed class ReachabilityDebouncer(int threshold)
{
    private readonly ConcurrentDictionary<Guid, int> _failures = new();

    /// <summary>Returns the status to record, or null when the failure count has not reached the threshold yet (keep the current one).</summary>
    public ReachabilityStatus? Observe(Guid serverId, bool reachable)
    {
        if (reachable)
        {
            _failures[serverId] = 0;
            return ReachabilityStatus.Reachable;
        }

        var count = _failures.AddOrUpdate(serverId, 1, (_, current) => current + 1);
        return count >= Math.Max(1, threshold) ? ReachabilityStatus.Unreachable : null;
    }

    public void Forget(Guid serverId) => _failures.TryRemove(serverId, out _);
}

/// <summary>
/// Persists the server status axes (ADR 0002 "Failure model") and appends every change to <c>resource_events</c>. All writes are
/// short, in their own scope and retried on a concurrent edit of the same row (the REST API also updates servers).
/// </summary>
public sealed class ServerStatusService
{
    private const int MaxAttempts = 4;

    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;
    private readonly ILogger<ServerStatusService> _logger;
    private readonly ReachabilityDebouncer _debouncer;

    public ServerStatusService(IServiceScopeFactory scopes, IClock clock, IOptions<AgentGatewayOptions> options, ILogger<ServerStatusService> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
        _debouncer = new ReachabilityDebouncer(options.Value.ReachabilityFailureThreshold);
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the tracked server and saves. The delegate returns the status transitions it caused (empty list:
    /// saved without events when it changed other columns) or null for "nothing to save".
    /// </summary>
    public async Task ApplyAsync(Guid serverId, Func<Server, DateTimeOffset, List<StatusTransition>?> mutate, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == serverId, cancellationToken);
            if (server is null) return;

            var now = _clock.UtcNow;
            var transitions = mutate(server, now);
            if (transitions is null) return;
            foreach (var t in transitions)
                db.ResourceEvents.Add(new ResourceEvent { ResourceType = "server", ResourceId = serverId, Kind = "status.changed", Axis = t.Axis, OldValue = t.Old, NewValue = t.New, Detail = t.Detail, OccurredAt = now });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // The row changed under us (a PATCH, another status writer): decide again on the fresh row.
            }
        }
    }

    /// <summary>The agent stream is established: agent connected, machine reachable.</summary>
    public Task AgentConnectedAsync(Guid serverId, string? agentVersion, string? os, string? architecture, CancellationToken cancellationToken = default) =>
        ApplyAsync(serverId, (server, now) =>
        {
            var transitions = new List<StatusTransition>();
            var before = (agent: server.AgentStatus, reach: server.ReachabilityStatus);
            server.RecordHeartbeat(now);
            if (!string.IsNullOrEmpty(agentVersion)) server.AgentVersion = Truncate(agentVersion, 64);
            if (!string.IsNullOrEmpty(os) && server.Facts.Os is null) server.Facts.Os = Truncate(os, 100);
            if (!string.IsNullOrEmpty(architecture) && server.Facts.Architecture is null) server.Facts.Architecture = Truncate(architecture, 32);
            if (before.agent != server.AgentStatus) transitions.Add(new("agent", Name(before.agent), Name(server.AgentStatus)));
            if (before.reach != server.ReachabilityStatus) transitions.Add(new("reachability", Name(before.reach), Name(server.ReachabilityStatus)));
            _debouncer.Forget(serverId);
            return transitions;
        }, cancellationToken);

    /// <summary>The stream ended (closed, timed out, superseded by nothing). The machine may still answer: the reachability probe decides.</summary>
    public Task AgentDisconnectedAsync(Guid serverId, string reason, CancellationToken cancellationToken = default) =>
        ApplyAsync(serverId, (server, now) =>
        {
            var before = server.AgentStatus;
            if (!server.SetAgentStatus(AgentStatus.Unavailable, now)) return null;
            return [new("agent", Name(before), Name(AgentStatus.Unavailable), reason)];
        }, cancellationToken);

    /// <summary>A heartbeat (or any inbound message). The Docker axis is updated when the agent reports it; <c>last_heartbeat_at</c> is always bumped.</summary>
    public Task HeartbeatAsync(Guid serverId, DockerStatus docker, CancellationToken cancellationToken = default) =>
        ApplyAsync(serverId, (server, now) =>
        {
            var transitions = new List<StatusTransition>();
            var before = (agent: server.AgentStatus, docker: server.DockerStatus, reach: server.ReachabilityStatus);
            server.RecordHeartbeat(now, docker);
            if (before.agent != server.AgentStatus) transitions.Add(new("agent", Name(before.agent), Name(server.AgentStatus)));
            if (before.docker != server.DockerStatus) transitions.Add(new("docker", Name(before.docker), Name(server.DockerStatus)));
            if (before.reach != server.ReachabilityStatus) transitions.Add(new("reachability", Name(before.reach), Name(server.ReachabilityStatus)));
            return transitions;
        }, cancellationToken);

    /// <summary>The Docker axis from a daemon event or a discovery report.</summary>
    public Task DockerObservedAsync(Guid serverId, DockerStatus docker, string? detail = null, CancellationToken cancellationToken = default) =>
        ApplyAsync(serverId, (server, now) =>
        {
            var before = server.DockerStatus;
            if (docker == DockerStatus.Unknown || !server.SetDockerStatus(docker, now)) return null;
            return [new("docker", Name(before), Name(docker), detail)];
        }, cancellationToken);

    /// <summary>The control plane's own probe of the machine (only run while no agent session exists), debounced.</summary>
    public Task ReachabilityObservedAsync(Guid serverId, bool reachable, CancellationToken cancellationToken = default)
    {
        var decided = _debouncer.Observe(serverId, reachable);
        return ApplyAsync(serverId, (server, now) =>
        {
            if (decided is not { } status)
            {
                server.ReachabilityCheckedAt = now; // checked, but not yet confirmed down
                return [];
            }

            var before = server.ReachabilityStatus;
            return server.SetReachability(status, now) ? [new("reachability", Name(before), Name(status))] : [];
        }, cancellationToken);
    }

    private static string Name<T>(T value) where T : struct, Enum => ServerStatusMachine.Camel(value);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
