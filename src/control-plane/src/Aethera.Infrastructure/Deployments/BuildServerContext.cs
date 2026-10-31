using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Deployments;

/// <summary>What the plan builder needs to know about the server that builds (ADR 0012): names, registry, quota, build slots.</summary>
internal sealed record BuildServerContext(
    Guid BuildServerId, string BuildServerName, string RunServerName, string? RegistryPrefix, RegistryCredentials? Registry,
    ResourceLimitsSpec? BuilderLimits, Func<CancellationToken, Task<IAsyncDisposable>> AcquireBuildSlot);

/// <summary>
/// Resolves the build server of a deployment that builds from source: its registry (credentials decrypted in memory and registered for log
/// redaction, like a pull registry), its build quota when the agent there can enforce it, and its build slots. Also the registry of a rollback
/// to an image that came through a registry.
/// </summary>
internal static class BuildServerContextResolver
{
    /// <summary>The agent capability of the quota (BuildKit builder container, ADR 0012).</summary>
    public const string QuotaCapability = "build.quota";

    /// <summary>Docker Hub's names and the address its credentials are filed under.</summary>
    private const string DockerHubCredentialServer = "https://index.docker.io/v1/";

    private static readonly string[] DockerHubHosts = ["docker.io", "index.docker.io", "registry-1.docker.io", "registry.hub.docker.com"];

    /// <exception cref="DeploymentFailure">The build server is gone or inactive, or builds for another server without a usable registry.</exception>
    public static async Task<BuildServerContext> ResolveAsync(
        IServiceProvider sp, DeploymentSnapshot snapshot, Guid runServerId, JobContext context, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AetheraDbContext>();
        var buildServerId = snapshot.Build?.BuildServerId ?? runServerId;
        var servers = await db.Servers.AsNoTracking().Where(s => s.Id == buildServerId || s.Id == runServerId).ToListAsync(ct);
        var runServer = servers.FirstOrDefault(s => s.Id == runServerId);
        var buildServer = servers.FirstOrDefault(s => s.Id == buildServerId)
                          ?? throw new DeploymentFailure(DeploymentStep.Build, "build.server_missing", "The server this application builds on no longer exists. Choose another build server in the application's Build settings.");
        var elsewhere = buildServerId != runServerId;
        if (elsewhere && buildServer.Lifecycle != ServerLifecycle.Active)
            throw new DeploymentFailure(DeploymentStep.Build, FailureCodes.ServerUnavailable,
                $"The build server {buildServer.Name} is {buildServer.Lifecycle.ToString().ToLowerInvariant()}, not active.");

        string? prefix = null;
        RegistryCredentials? credentials = null;
        if (elsewhere && buildServer.BuildRegistryId is { } registryId)
        {
            var registry = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == registryId, ct);
            if (registry is not null)
            {
                var (host, server) = Describe(registry.Url);
                var ns = (registry.Namespace ?? registry.Username)?.Trim().Trim('/').ToLowerInvariant();
                if (string.IsNullOrEmpty(ns))
                    throw new DeploymentFailure(DeploymentStep.Build, FailureCodes.BuildRegistryMissing,
                        $"The registry {registry.Name} has no namespace, so the image has no path to be pushed to. Set its namespace (for ghcr.io: your user or organization) in Registries.");
                prefix = $"{host}/{ns}";
                var password = registry.PasswordSecretId is { } secretId ? await DeployJobHandler.ReadSecretAsync(sp, secretId, null, context, ct) : null;
                credentials = new RegistryCredentials(server, registry.Username ?? "", password is null ? null : new SecretValue(password));
            }
        }

        var limits = await QuotaAsync(sp, buildServer, snapshot.Build?.Engine, context, ct);
        var slots = buildServer.MaxConcurrentBuilds;
        var gate = sp.GetRequiredService<BuildSlotGate>();
        return new BuildServerContext(
            buildServer.Id, buildServer.Name, runServer?.Name ?? "the run server", prefix, credentials, limits,
            slotCt => gate.AcquireAsync(buildServer.Id, slots,
                waitCt => context.Log.WriteSystemAsync($"{buildServer.Name} is already building {slots} image{(slots == 1 ? "" : "s")} (its limit); waiting for a free slot.", waitCt),
                slotCt));
    }

    /// <summary>
    /// The build quota of the server as limits for the build command, or null. Only an agent that advertises <see cref="QuotaCapability"/> can
    /// enforce it; for any other the build runs as before and the log says so.
    /// </summary>
    private static async Task<ResourceLimitsSpec?> QuotaAsync(IServiceProvider sp, Server server, string? engine, JobContext context, CancellationToken ct)
    {
        if (server.BuildCpuLimit is null && server.BuildMemoryLimitBytes is null) return null;
        var quota = QuotaText(server.BuildCpuLimit, server.BuildMemoryLimitBytes);
        if (engine == BuildEngines.Nixpacks)
        {
            await context.Log.WriteSystemAsync($"{server.Name} has a build quota ({quota}), but Nixpacks builds are not limited by it yet.", ct);
            return null;
        }

        var session = sp.GetRequiredService<AgentSessionRegistry>().Session(server.Id);
        if (session is null || !session.Capabilities.Contains(QuotaCapability))
        {
            await context.Log.WriteSystemAsync(
                $"{server.Name} has a build quota ({quota}), but the agent there{(session is null ? "" : $" ({session.AgentVersion})")} does not enforce it: this build runs without the quota. Update the agent to apply it.", ct);
            return null;
        }

        await context.Log.WriteSystemAsync($"Build quota on {server.Name}: {quota}, enforced by the BuildKit builder container aethera-buildkit.", ct);
        return new ResourceLimitsSpec(CpuLimitCores: server.BuildCpuLimit ?? 0, MemoryLimitBytes: server.BuildMemoryLimitBytes ?? 0);
    }

    private static string QuotaText(double? cpu, long? memory)
    {
        var parts = new List<string>();
        if (cpu is { } c) parts.Add($"{c:0.##} CPU");
        if (memory is { } m) parts.Add($"{Placement.PlacementRules.Bytes(m)} memory");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// The registry a rollback to <paramref name="target"/> pulls from: the target's build server's registry when the reference is on its host,
    /// else any registry of the organization on that host; null (an anonymous pull) when none matches.
    /// </summary>
    public static async Task<RegistryCredentials?> RegistryForReferenceAsync(
        IServiceProvider sp, Deployment target, JobContext context, CancellationToken ct)
    {
        if (target.ImageRef is not { Length: > 0 } reference) return null;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var wanted = HostOf(reference);
        var candidates = new List<Registry>();
        if (target.BuildServerId is { } buildServerId
            && await db.Servers.AsNoTracking().Where(s => s.Id == buildServerId).Select(s => s.BuildRegistryId).FirstOrDefaultAsync(ct) is { } preferred
            && await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == preferred, ct) is { } own)
            candidates.Add(own);
        var organizationId = context.Job.OrganizationId;
        candidates.AddRange(await db.Registries.AsNoTracking().Where(r => r.OrganizationId == organizationId).ToListAsync(ct));

        var match = candidates.FirstOrDefault(r => string.Equals(Describe(r.Url).Host, wanted, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;
        var password = match.PasswordSecretId is { } secretId ? await DeployJobHandler.ReadSecretAsync(sp, secretId, null, context, ct) : null;
        return new RegistryCredentials(Describe(match.Url).Server, match.Username ?? "", password is null ? null : new SecretValue(password));
    }

    /// <summary>
    /// After a deployment built elsewhere was promoted: removes the build server's local copies of the images beyond the rollback retention
    /// (the same policy as on the run server). Only local copies: nothing is deleted from the registry. Best effort.
    /// </summary>
    internal static async Task CleanUpBuildServerAsync(AetheraDbContext db, IServiceProvider sp, Workload app, Deployment deployment)
    {
        if (deployment.BuildServerId is not { } buildServerId || buildServerId == deployment.ServerId) return;
        try
        {
            var builds = await db.Deployments.AsNoTracking()
                .Where(d => d.WorkloadId == app.Id && d.BuildServerId == buildServerId && d.ImageRef != null)
                .Select(d => new ImagePolicy.Candidate(d.ImageRef!, d.Id, d.CreatedAt, d.IsRollbackPoint)).ToListAsync();
            var stale = ImagePolicy.ImagesToRemove(builds, deployment.Id, DeployJobHandler.KeepRollbackPoints);
            if (stale.Count == 0) return;

            var resolver = sp.GetRequiredService<IServerTransportResolver>();
            foreach (var image in stale)
                await resolver.ExecuteAsync<ImageRemoveCommand, Unit>(
                    buildServerId, new ImageRemoveCommand(image, Force: false), CommandOptions.For($"cleanup-build:{deployment.Id}:{image}"), CancellationToken.None);
        }
        catch (Exception)
        {
            // Housekeeping: the maintenance prune catches up later.
        }
    }

    /// <summary>The registry host an image reference is on (the first path segment when it looks like a host, else Docker Hub).</summary>
    internal static string HostOf(string reference)
    {
        var slash = reference.IndexOf('/');
        if (slash > 0)
        {
            var first = reference[..slash];
            if (first.Contains('.') || first.Contains(':') || first == "localhost") return DockerHubHosts.Contains(first, StringComparer.OrdinalIgnoreCase) ? "docker.io" : first;
        }

        return "docker.io";
    }

    /// <summary>
    /// The host image references use for a registry URL, and the key its credentials are filed under. Docker Hub is special: references say
    /// <c>docker.io</c> and credentials are kept under <c>https://index.docker.io/v1/</c>.
    /// </summary>
    internal static (string Host, string Server) Describe(string url)
    {
        var authority = DeployJobHandler.RegistryHost(url);
        return DockerHubHosts.Contains(authority, StringComparer.OrdinalIgnoreCase) ? ("docker.io", DockerHubCredentialServer) : (authority, authority);
    }
}
