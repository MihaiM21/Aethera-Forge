using Aethera.Domain;
using Aethera.Engine.Deployments;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Previews;

/// <summary>What a preview deployment runs with: the frozen snapshot, and what the deployment log says was left out of it.</summary>
public sealed record PreviewSnapshotResult(DeploymentSnapshot Snapshot, IReadOnlyList<string> Notes);

/// <summary>
/// Freezes the configuration of a pull request preview (ADR 0012): the application's own configuration with the pull request's repository, branch
/// and head commit, the preview's variables over the regular ones, and its own names, volumes and route. It shares nothing with production that a
/// pull request could damage: no published host ports, no custom labels, no domains of the application, no read-write host paths, and the named
/// volumes are the preview's own (empty on the first deployment).
/// </summary>
public static class PreviewSnapshotFactory
{
    /// <summary>
    /// Loads the preview of <paramref name="deployment"/>, works out its host and builds the snapshot. Stores the host on the preview (saved by the caller
    /// together with the deployment).
    /// </summary>
    /// <exception cref="UnsupportedSourceException">The application or the server cannot host a preview (no git source, no domain base).</exception>
    public static async Task<PreviewSnapshotResult> CreateAsync(
        AetheraDbContext db, ProxyOptions proxyOptions, Deployment deployment, Workload workload, CancellationToken ct)
    {
        var preview = await db.Previews.FirstOrDefaultAsync(p => p.Id == deployment.PreviewId, ct)
                      ?? throw new UnsupportedSourceException("preview.not_found", "The preview of this deployment no longer exists.");
        if (workload is not Application app)
            throw new UnsupportedSourceException("preview.unsupported", "Only applications have previews.");

        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == preview.ServerId, ct)
                     ?? throw new UnsupportedSourceException("preview.server_missing", "The server of this preview no longer exists.");
        var baseDomain = DefaultDomainService.KnownBaseDomain(server)
                         ?? throw new UnsupportedSourceException("preview.no_domain",
                             $"The server {server.Name} has neither a wildcard domain nor a known public IP address, so a preview of a pull request has no host name. Set one in the server's settings.");

        var hostname = preview.Hostname ?? await FreeHostnameAsync(db, app, preview, baseDomain, ct)
                       ?? throw new UnsupportedSourceException("preview.no_domain", "No free host name was found for this preview; the wildcard domain is too long.");
        preview.Hostname = hostname;

        var https = await ProxyConfigurator.AcmeAvailableAsync(db, proxyOptions, ct);
        var commit = deployment.CommitSha ?? preview.HeadSha;
        return Create(app, preview, commit, hostname, https);
    }

    /// <summary>The first free host of the preview: <c>pr-&lt;n&gt;-&lt;label&gt;</c>, then the same with the start of the application's id (two applications may share a slug).</summary>
    private static async Task<string?> FreeHostnameAsync(AetheraDbContext db, Application app, PreviewEnvironment preview, string baseDomain, CancellationToken ct)
    {
        var automatic = app.Domains.Where(d => d.IsAutomatic).OrderBy(d => d.IsDeleted).Select(d => d.Hostname).FirstOrDefault();
        var candidates = new[]
        {
            PreviewNaming.Hostname(preview.Number, automatic, app.Slug, baseDomain),
            PreviewNaming.Hostname(preview.Number, null, $"{app.Slug}-{app.Id.ToString("N")[..6]}", baseDomain),
        };
        foreach (var candidate in candidates.Where(c => c is not null).Distinct())
        {
            if (!await db.Previews.AnyAsync(p => p.Id != preview.Id && p.Hostname == candidate && p.RemovedAt == null, ct)
                && !await db.Domains.AnyAsync(d => d.Hostname == candidate, ct))
                return candidate;
        }

        return null;
    }

    /// <summary>The snapshot of the preview. Pure over the loaded application (ports, volumes, domains, environment variables with their secrets, git source, build configuration).</summary>
    public static PreviewSnapshotResult Create(Application app, PreviewEnvironment preview, string? commitSha, string hostname, bool https)
    {
        var baseSnapshot = DeploymentSnapshotFactory.Create(app);
        if (baseSnapshot.Git is not { } git)
            throw new UnsupportedSourceException("preview.no_git_source", "A preview needs an application built from a git repository.");
        if (!DeploymentSnapshotFactory.IsSafeBranch(preview.SourceBranch))
            throw new UnsupportedSourceException("source.invalid_ref", "The branch of the pull request cannot be deployed.");

        var notes = new List<string>();
        var slug = PreviewEnvironment.ResourceSlug(app.Slug, preview.Number);
        if (preview.FromFork)
            notes.Add($"This pull request comes from a fork ({preview.SourceRepositoryUrl}); its code runs with the application's variables.");

        // Variables: the regular ones, then the preview-only ones over them.
        var regular = app.EnvironmentVariables.Where(e => !e.IsPreview).ToDictionary(e => e.Key);
        foreach (var variable in app.EnvironmentVariables.Where(e => e.IsPreview)) regular[variable.Key] = variable;
        var env = DeploymentSnapshotFactory.EnvEntries(regular.Values);

        // Volumes: the preview's own namespace. A host directory that could be written is left out: a pull request must not touch production data.
        var volumes = new List<VolumeEntry>();
        foreach (var v in baseSnapshot.Volumes)
        {
            if (v.HostPath is { Length: > 0 })
            {
                if (v.ReadOnly) volumes.Add(v);
                else notes.Add($"The host path {v.HostPath} (mounted at {v.MountPath}) is not mounted in previews; it is writable and shared with production.");
                continue;
            }

            volumes.Add(v with { Name = PreviewNaming.VolumeName(v.Name, app.Slug, app.Id) });
        }

        if (baseSnapshot.Ports.Any(p => p.PublishedPort is > 0))
            notes.Add("Published host ports are not used in previews; the preview is reached through its URL only.");
        if (baseSnapshot.CustomLabels is { Count: > 0 })
            notes.Add("Custom container labels of the application are not applied to previews.");

        // The environment network carries the preview under its own name; names the application took on other networks are not repeated.
        var environmentNetwork = DeploymentSnapshotFactory.EnvironmentNetwork(app.EnvironmentId);
        var networks = baseSnapshot.Networks
            .Select(n => n with { Aliases = n.DockerName == environmentNetwork ? [slug] : [] })
            .ToList();

        // Which service and port the application's own domain routes to, so the preview routes to the same place.
        var hint = app.Domains.Where(d => !d.IsDeleted && d.RedirectTo is null).OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Hostname, StringComparer.Ordinal).FirstOrDefault();
        var route = new DomainEntry(hostname, "/", https, hint?.TargetPort, hint?.ComposeService);

        var snapshot = baseSnapshot with
        {
            Slug = slug,
            Git = git with
            {
                Url = preview.SourceRepositoryUrl, Branch = preview.SourceBranch,
                CommitPin = DeploymentSnapshotFactory.IsCommitSha(commitSha) ? commitSha : null,
            },
            Env = env,
            Volumes = volumes,
            Ports = baseSnapshot.Ports.Select(p => p with { PublishedPort = null }).ToList(),
            Networks = networks,
            Domains = [route],
            CustomLabels = null,
            Preview = new PreviewSnapshot(preview.Id, preview.Number),
        };
        return new PreviewSnapshotResult(snapshot, notes);
    }

    /// <summary>
    /// The route of a compose preview once the services are known: the service the application's domain routes to, else the one that looks like a web
    /// server (the way the automatic domain is chosen).
    /// </summary>
    /// <exception cref="ComposeDiscoveryException">No service can be routed.</exception>
    public static DeploymentSnapshot CompleteComposeRoute(DeploymentSnapshot snapshot, IReadOnlyList<Aethera.Domain.Transport.ComposeServiceDefinition>? services)
    {
        if (snapshot.Compose is null || snapshot.Domains.Count != 1) return snapshot;
        if (snapshot.Domains[0].ComposeService is { Length: > 0 } existing && (services is null || services.Any(s => s.Name == existing))) return snapshot;

        var pick = ComposeRoutePicker.Pick(services)
                   ?? throw new ComposeDiscoveryException("preview.no_route", "No service of the compose file looks like a web server, so the preview has nothing to route to. Route a domain of the application to a service first.");
        return snapshot with { Domains = [snapshot.Domains[0] with { ComposeService = pick.Service, TargetPort = snapshot.Domains[0].TargetPort ?? pick.Port }] };
    }
}
