using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Jobs;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.SelfOps;

/// <summary>Payload of <c>server.agent_update</c>: the server and, optionally, a release version (default: the newest known).</summary>
public sealed class AgentUpdatePayload
{
    public Guid ServerId { get; set; }
    public string? Version { get; set; }
}

/// <summary>
/// <c>server.agent_update</c>: tells a connected agent to replace its own binary with the one from a GitHub release. The control plane only
/// names the release asset and its checksum; the agent downloads it over HTTPS, verifies the SHA-256, smoke-tests it, swaps it atomically and
/// rolls back on its own if the new binary does not reconnect (ADR 0002, "AgentSelfUpdate"). Agents that run in a container are updated by
/// pulling the new image instead (the updater does that for the local agent), because they have no writable binary to swap.
/// </summary>
public sealed class AgentUpdateJobHandler : IJobHandler
{
    public const string JobType = "server.agent_update";

    public string Type => JobType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.GetPayload<AgentUpdatePayload>();
        var checker = context.Services.GetRequiredService<ReleaseChecker>();
        var db = context.Services.GetRequiredService<AetheraDbContext>();

        var release = checker.Latest ?? await checker.CheckAsync(cancellationToken);
        if (release is null) throw JobFailedException.Permanent("agent_update.no_release", "No release is known", checker.LastError ?? "No release has been published, or release checks are disabled.", "resolve");
        if (payload.Version is { Length: > 0 } wanted && SemanticVersion.TryParse(wanted, out var w) && SemanticVersion.TryParse(release.Version, out var r) && w != r)
            throw JobFailedException.Permanent("agent_update.version_unavailable", "That version cannot be installed", $"Only the newest release ({release.Version}) can be installed from here.", "resolve");

        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == payload.ServerId, cancellationToken)
            ?? throw JobFailedException.Permanent("agent_update.server_not_found", "The server no longer exists", null, "resolve");
        if (server.AgentVersion is { } running && SemanticVersion.TryParse(running, out var current) && SemanticVersion.TryParse(release.Version, out var target) && current >= target)
        {
            await context.Log.WriteSystemAsync($"The agent already runs {running}; nothing to do.", cancellationToken);
            context.SetResult(new { previousVersion = running, newVersion = running, updated = false });
            return;
        }

        var asset = AssetName(server.Facts.Architecture)
            ?? throw JobFailedException.Permanent("agent_update.unsupported_architecture", "No agent binary for this CPU", $"The server reports '{server.Facts.Architecture ?? "unknown"}'; releases carry amd64 and arm64.", "resolve");
        var binary = release.Assets.FirstOrDefault(a => a.Name == asset)
            ?? throw JobFailedException.Permanent("agent_update.asset_missing", "The release has no agent binary", $"{release.Tag} has no asset named {asset}.", "resolve");
        var sha = await checker.GetChecksumAsync(release, asset, cancellationToken)
            ?? throw JobFailedException.Permanent("agent_update.checksum_missing", "The release has no checksum for the agent", $"SHA256SUMS of {release.Tag} does not list {asset}; the agent refuses to install without one.", "resolve");

        await context.Log.WriteSystemAsync($"Updating the agent to {release.Version} ({asset}, sha256 {sha[..12]}...).", cancellationToken);
        var resolver = context.Services.GetRequiredService<IServerTransportResolver>();
        ResolvedOutcome<SelfUpdateOutcome> resolved;
        try
        {
            resolved = await resolver.ExecuteAsync<AgentSelfUpdateCommand, SelfUpdateOutcome>(
                payload.ServerId, new AgentSelfUpdateCommand(binary.Url, sha, release.Version), ServerJobSupport.Options(context, "agent_update"), cancellationToken);
        }
        catch (ServerTransportException ex)
        {
            throw ServerJobSupport.Translate(ex, "agent_update");
        }

        if (!resolved.Outcome.Succeeded) throw ServerJobSupport.Translate(resolved.Outcome, "agent_update", "agent_update.failed");
        var result = resolved.Outcome.EnsureSucceeded();
        await context.Log.WriteSystemAsync($"The agent switched from {result.PreviousVersion} to {result.NewVersion}{(result.Restarting ? " and is restarting" : "")}.", cancellationToken);
        context.SetResult(new { previousVersion = result.PreviousVersion, newVersion = result.NewVersion, updated = true, restarting = result.Restarting });
    }

    /// <summary>The release asset for a CPU architecture as the agent or the OS reports it, or null.</summary>
    public static string? AssetName(string? architecture) => architecture?.Trim().ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" => "aethera-agent-linux-amd64",
        "arm64" or "aarch64" => "aethera-agent-linux-arm64",
        _ => null,
    };
}
