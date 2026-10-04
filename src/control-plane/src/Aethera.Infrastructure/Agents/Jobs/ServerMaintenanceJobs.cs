using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Agents.Jobs;

/// <summary>Payload of <c>server.prune</c>: ids and flags only.</summary>
public sealed class ServerPrunePayload
{
    public Guid ServerId { get; set; }
    public bool StoppedContainers { get; set; }
    public bool DanglingImages { get; set; }
    public bool UnusedImages { get; set; }
    public bool UnusedNetworks { get; set; }
    public bool BuildCache { get; set; }

    /// <summary>Destructive: the API only sets it after the user confirmed by name (ADR 0002 "SystemPrune").</summary>
    public bool Volumes { get; set; }

    public int? OlderThanHours { get; set; }
}

/// <summary>Payload of <c>server.discovery_refresh</c>.</summary>
public sealed class ServerRefreshPayload
{
    public Guid ServerId { get; set; }
}

internal static class ServerJobSupport
{
    /// <summary>Maps a transport failure to a job failure: unavailable agents are retried, everything else is deterministic.</summary>
    public static JobFailedException Translate(ServerTransportException ex, string step) =>
        ex.Transient
            ? JobFailedException.Transient(ex.Code, "The server could not be reached", ex.Message, step, ex)
            : JobFailedException.Permanent(ex.Code, "The agent could not run the command", ex.Message, step, ex);

    public static JobFailedException Translate<T>(CommandOutcome<T> outcome, string step, string code)
    {
        var mapped = outcome.ErrorCode switch
        {
            CommandErrorCode.OutOfDisk => "server.out_of_disk",
            CommandErrorCode.DockerUnavailable => "docker.unavailable",
            _ => code,
        };
        var detail = outcome.ErrorMessage ?? outcome.Status.ToString();
        return outcome.ErrorCode == CommandErrorCode.DockerUnavailable
            ? JobFailedException.Transient(mapped, "The command failed on the server", detail, step)
            : JobFailedException.Permanent(mapped, "The command failed on the server", detail, step);
    }

    public static CommandOptions Options(JobContext context, string step) => new()
    {
        IdempotencyKey = context.IdempotencyKey(step), JobId = context.JobId, OrganizationId = context.Job.OrganizationId, ActorUserId = context.Job.CreatedBy,
    };
}

/// <summary>
/// <c>server.prune</c> (ADR 0004 maintenance, lock key <c>server:&lt;id&gt;:maintenance</c>): a <c>docker system prune</c> with explicit
/// scopes through the agent. Images that are rollback points of deployments on the server are always passed as <c>keep</c>, so pruning can
/// never remove the image a rollback needs.
/// </summary>
public sealed class ServerPruneJobHandler : IJobHandler
{
    public const string JobType = "server.prune";

    public string Type => JobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.GetPayload<ServerPrunePayload>();
        var command = new SystemPruneCommand(
            payload.StoppedContainers, payload.DanglingImages, payload.UnusedImages, payload.UnusedNetworks, payload.BuildCache, payload.Volumes,
            payload.OlderThanHours is { } hours and > 0 ? TimeSpan.FromHours(hours) : null, await RollbackImagesAsync(context, payload.ServerId, cancellationToken));
        if (!command.AnyScope) throw JobFailedException.Permanent("server.prune_no_scope", "Nothing to prune", "At least one prune scope must be selected.", "prune");

        await context.Log.WriteSystemAsync("Pruning the server with the selected scopes.", cancellationToken);
        var resolver = context.Services.GetRequiredService<IServerTransportResolver>();
        ResolvedOutcome<PruneOutcome> resolved;
        try
        {
            resolved = await resolver.ExecuteAsync<SystemPruneCommand, PruneOutcome>(payload.ServerId, command, ServerJobSupport.Options(context, "prune"), cancellationToken);
        }
        catch (ServerTransportException ex)
        {
            throw ServerJobSupport.Translate(ex, "prune");
        }

        if (!resolved.Outcome.Succeeded) throw ServerJobSupport.Translate(resolved.Outcome, "prune", "server.prune_failed");
        var result = resolved.Outcome.EnsureSucceeded();
        await context.Log.WriteSystemAsync($"Removed {result.Deleted.Count} object(s), reclaimed {result.SpaceReclaimedBytes} bytes.", cancellationToken);
        context.SetResult(new { deleted = result.Deleted.Count, spaceReclaimedBytes = result.SpaceReclaimedBytes, transport = resolved.Transport.ToString().ToLowerInvariant(), fallback = resolved.UsedFallback });
    }

    private static async Task<IReadOnlyList<string>> RollbackImagesAsync(JobContext context, Guid serverId, CancellationToken cancellationToken)
    {
        var db = context.Services.GetRequiredService<AetheraDbContext>();
        return await db.Deployments.AsNoTracking()
            .Where(d => d.ServerId == serverId && d.IsRollbackPoint && d.ImageRef != null)
            .Select(d => d.ImageRef!).Distinct().ToListAsync(cancellationToken);
    }
}

/// <summary><c>server.discovery_refresh</c>: asks the agent to re-run discovery (spec section 55); the fresh report is stored like any other.</summary>
public sealed class ServerDiscoveryRefreshJobHandler : IJobHandler
{
    public const string JobType = "server.discovery_refresh";

    public string Type => JobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.GetPayload<ServerRefreshPayload>();
        var resolver = context.Services.GetRequiredService<IServerTransportResolver>();
        ResolvedOutcome<DiscoveryInfo> resolved;
        try
        {
            resolved = await resolver.ExecuteAsync<DiscoveryRefreshCommand, DiscoveryInfo>(payload.ServerId, new DiscoveryRefreshCommand(), ServerJobSupport.Options(context, "discovery"), cancellationToken);
        }
        catch (ServerTransportException ex)
        {
            throw ServerJobSupport.Translate(ex, "discovery");
        }

        if (!resolved.Outcome.Succeeded) throw ServerJobSupport.Translate(resolved.Outcome, "discovery", "server.discovery_failed");
        var info = resolved.Outcome.EnsureSucceeded();
        await context.Log.WriteSystemAsync($"Discovery refreshed: {info.Containers.Count} container(s), {info.Volumes.Count} volume(s), {info.Networks.Count} network(s).", cancellationToken);
        context.SetResult(new { containers = info.Containers.Count, volumes = info.Volumes.Count, networks = info.Networks.Count });
    }
}
