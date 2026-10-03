using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Protocol;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Persistence;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Ingest;

/// <summary>A stored discovery report and when the agent collected it.</summary>
public sealed record StoredDiscovery(DiscoveryInfo Info, DateTimeOffset StoredAt);

/// <summary>
/// Persists the agent's <c>DiscoveryReport</c> (spec section 55): the discovered facts go to the server's columns, the Docker axis is
/// updated, and the whole report is kept as a JSON document in <c>settings</c> under <c>agent.discovery.&lt;serverId&gt;</c> so the API can
/// show the last known inventory (flagged stale) while the agent is offline. No dedicated table exists; the key prefix <c>agent.</c> is
/// reserved for the gateway.
/// </summary>
public sealed class AgentDiscoveryStore(IServiceScopeFactory scopes, IClock clock, ServerStatusService status, ILogger<AgentDiscoveryStore> logger)
{
    public static string Key(Guid serverId) => $"agent.discovery.{serverId:D}";

    public async Task SaveAsync(Guid serverId, P.DiscoveryReport report, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var slim = report.Clone();
        foreach (var container in slim.Containers) container.InspectJson = ByteString.Empty; // raw inspect output stays on the host
        var json = JsonFormatter.Default.Format(slim);

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var key = Key(serverId);
            var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
            if (setting is null) db.Settings.Add(new InstanceSetting { Key = key, ValueJson = json, UpdatedAt = now });
            else { setting.ValueJson = json; setting.UpdatedAt = now; }
            await db.SaveChangesAsync(cancellationToken);
        }

        await status.ApplyAsync(serverId, (server, at) =>
        {
            AgentFacts.Apply(server, report, at);
            var before = server.DockerStatus;
            var docker = AgentFacts.ToDomain(report.Docker?.Status ?? P.DockerStatus.Unspecified);
            var transitions = new List<StatusTransition>();
            if (docker != DockerStatus.Unknown && server.SetDockerStatus(docker, at))
                transitions.Add(new("docker", ServerStatusMachine.Camel(before), ServerStatusMachine.Camel(docker), "discovery"));
            return transitions;
        }, cancellationToken);
    }

    public async Task<StoredDiscovery?> LoadAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var key = Key(serverId);
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null) return null;
        try
        {
            return new StoredDiscovery(JsonParser.Default.Parse<P.DiscoveryReport>(setting.ValueJson).ToDomain(), setting.UpdatedAt);
        }
        catch (InvalidProtocolBufferException ex)
        {
            logger.LogWarning(ex, "The stored discovery report of server {ServerId} could not be read", serverId);
            return null;
        }
    }
}
