using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Ingest;

/// <summary>
/// Handles <c>EventNotice</c>s: de-duplicates them (the agent replays its ring buffer after a reconnect), appends a
/// <see cref="ResourceEvent"/> for the server (and for the workload a container belongs to), feeds the Docker axis from daemon up/down
/// events and publishes a <see cref="ServerEvent"/> to subscribers.
/// </summary>
public sealed class AgentEventIngestor(IServiceScopeFactory scopes, ServerStatusService status, IClock clock)
{
    public static (ServerEventKind Kind, string ResourceKind)? Classify(P.EventType type) => type switch
    {
        P.EventType.ContainerStarted => (ServerEventKind.ContainerStarted, "container.started"),
        P.EventType.ContainerStopped => (ServerEventKind.ContainerStopped, "container.stopped"),
        P.EventType.ContainerDied => (ServerEventKind.ContainerDied, "container.died"),
        P.EventType.ContainerOom => (ServerEventKind.ContainerOom, "container.oom"),
        P.EventType.ContainerRestarting => (ServerEventKind.ContainerRestarting, "container.restarting"),
        P.EventType.ContainerHealthChanged => (ServerEventKind.ContainerHealthChanged, "container.health_changed"),
        P.EventType.ContainerRemoved => (ServerEventKind.ContainerRemoved, "container.removed"),
        P.EventType.DockerDaemonUp => (ServerEventKind.DockerDaemonUp, "docker.daemon_up"),
        P.EventType.DockerDaemonDown => (ServerEventKind.DockerDaemonDown, "docker.daemon_down"),
        P.EventType.DiskPressure => (ServerEventKind.DiskPressure, "disk.pressure"),
        _ => null,
    };

    public async Task HandleAsync(ServerAgentState state, P.EventNotice notice, CancellationToken cancellationToken)
    {
        if (Classify(notice.Type) is not { } classified) return; // unknown arm: ignored (additive protocol evolution)
        if (!state.FirstSightingOf(notice.EventId)) return;

        var occurredAt = notice.OccurredAt is { Seconds: > 0 } at ? at.ToDateTimeOffset() : clock.UtcNow;
        var detail = Detail(notice);

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            db.ResourceEvents.Add(new ResourceEvent { ResourceType = "server", ResourceId = state.ServerId, Kind = classified.ResourceKind, Detail = detail, OccurredAt = occurredAt });

            if (notice.Labels.Count > 0)
            {
                var known = await AgentMetricsIngestor.WorkloadIdsAsync(state, db, clock.UtcNow, cancellationToken);
                if (AgentMetricsIngestor.WorkloadOf(notice.Labels, known) is { } workloadId)
                    db.ResourceEvents.Add(new ResourceEvent { ResourceType = "workload", ResourceId = workloadId, Kind = classified.ResourceKind, Detail = detail, OccurredAt = occurredAt });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        switch (classified.Kind)
        {
            case ServerEventKind.DockerDaemonUp: await status.DockerObservedAsync(state.ServerId, DockerStatus.Running, "daemon event", cancellationToken); break;
            case ServerEventKind.DockerDaemonDown: await status.DockerObservedAsync(state.ServerId, DockerStatus.Stopped, "daemon event", cancellationToken); break;
        }

        state.Events.Publish(new ServerEvent(state.ServerId, classified.Kind, occurredAt, notice.ContainerId.Length == 0 ? null : notice.ContainerId,
            notice.ContainerName.Length == 0 ? null : notice.ContainerName, notice.Labels.ToDictionary(p => p.Key, p => p.Value), notice.Message.Length == 0 ? null : Cut(notice.Message, 300)));
    }

    private static string? Detail(P.EventNotice notice)
    {
        var parts = new List<string>();
        if (notice.ContainerName.Length > 0) parts.Add(notice.ContainerName);
        if (notice.Type is P.EventType.ContainerDied or P.EventType.ContainerStopped) parts.Add($"exit={notice.ExitCode}");
        if (notice.OomKilled) parts.Add("oom");
        if (notice.Type == P.EventType.ContainerHealthChanged) parts.Add($"{notice.PreviousHealth}->{notice.Health}");
        if (notice.Message.Length > 0) parts.Add(Cut(notice.Message, 300));
        return parts.Count == 0 ? null : Cut(string.Join(' ', parts), 1000);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
