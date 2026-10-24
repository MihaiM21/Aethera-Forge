using Aethera.Domain;
using Aethera.Engine.Deployments;

namespace Aethera.Infrastructure.Deployments;

/// <summary>The source of a workload cannot be deployed (yet); carries the failure code stored on the deployment.</summary>
public sealed class UnsupportedSourceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Freezes the configuration of an application into a <see cref="DeploymentSnapshot"/>. Pure over the loaded entity graph.</summary>
public static class DeploymentSnapshotFactory
{
    /// <summary>
    /// <paramref name="workload"/> must have its ports, environment variables (with their <c>Secret</c>), volumes, non-deleted domains and
    /// networks (with <c>Network</c>) loaded, and for an <see cref="Application"/> its <c>GitSource</c>, <c>BuildConfig</c>, <c>ImageSource</c>
    /// and <c>ComposeSource</c>. A <see cref="Service"/> is an image: its snapshot is the template image plus the common parts.
    /// </summary>
    /// <exception cref="UnsupportedSourceException">The source kind has no usable configuration.</exception>
    public static DeploymentSnapshot Create(Workload workload) => workload switch
    {
        Application app => Create(app),
        Service service => Create(service),
        _ => throw new UnsupportedSourceException("source.unsupported", "This kind of workload cannot be deployed."),
    };

    /// <summary>The environment network every workload joins by default, so an application reaches a service of its environment by its slug.</summary>
    public static string EnvironmentNetwork(Guid environmentId) => DeploymentPlanBuilder.EnvironmentNetwork(environmentId);

    public static DeploymentSnapshot Create(Service service)
    {
        var (repository, tag) = SplitImage(service.Image);
        return Common(service, ApplicationSourceKind.DockerImage) with { Image = new ImageSnapshot(repository, tag, ImagePullPolicy.IfNotPresent, null, CommandOf(service)) };
    }

    /// <summary>The <c>command</c> array of the service config (set from the template, e.g. MinIO needs <c>server /data</c>), if any.</summary>
    private static IReadOnlyList<string>? CommandOf(Service service)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(service.ConfigJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !doc.RootElement.TryGetProperty("command", out var c) || c.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var list = c.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String).Select(x => x.GetString()!).ToList();
            return list.Count > 0 ? list : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static (string Repository, string Tag) SplitImage(string reference)
    {
        var at = reference.IndexOf('@');
        if (at > 0) return (reference[..at], reference[at..]);
        var colon = reference.LastIndexOf(':');
        return colon > reference.LastIndexOf('/') ? (reference[..colon], reference[(colon + 1)..]) : (reference, "latest");
    }

    private static DeploymentSnapshot Common(Workload w, ApplicationSourceKind kind)
    {
        var networks = w.Networks.Where(n => n.Network is not null).OrderBy(n => n.Network.DockerName, StringComparer.Ordinal)
            .Select(n => new NetworkEntry(n.Network.DockerName, n.Network.IsInternal, n.Aliases)).ToList();
        // Every workload joins the environment network: custom networks come in addition to it, never instead (a database template and an
        // application of the environment reach each other by name only there).
        var environmentNetwork = EnvironmentNetwork(w.EnvironmentId);
        if (networks.All(n => n.DockerName != environmentNetwork)) networks.Insert(0, new NetworkEntry(environmentNetwork, false, [w.Slug]));

        // The Docker volumes of the installation (its database, backups, certificates) are never mounted into a workload, by anyone: the API
        // refuses such names, and a row that got in some other way stops the deployment here.
        foreach (var volume in w.Volumes.Where(v => v.HostPath is not { Length: > 0 }))
        {
            if (VolumeNames.Classify(volume.Name, w.Slug, w.Id) == VolumeNameKind.Reserved)
                throw new UnsupportedSourceException("volume.name_reserved",
                    $"The volume '{volume.Name}' is a volume of the Aethera installation itself and cannot be mounted into a workload.");
        }

        return new DeploymentSnapshot
        {
            WorkloadId = w.Id, EnvironmentId = w.EnvironmentId, Slug = w.Slug, SourceKind = kind,
            Runtime = Runtime(w.Runtime) with { StopTimeoutSeconds = StopTimeoutSeconds(w) },
            Env = w.EnvironmentVariables.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new EnvEntry(
                e.Key, e.SecretId is null ? e.Value : null, e.SecretId, e.Secret?.CurrentVersion, e.IsBuildTime, e.IsRuntime)).ToList(),
            Ports = w.Ports.OrderBy(p => p.ContainerPort).Select(p => new PortEntry(p.ContainerPort, p.Protocol, p.PublishedPort, p.IsHttp)).ToList(),
            Volumes = w.Volumes.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => new VolumeEntry(v.Name, v.MountPath, v.HostPath, v.ReadOnly)).ToList(),
            Domains = w.Domains.Where(d => !d.IsDeleted).OrderBy(d => d.Hostname, StringComparer.Ordinal)
                .Select(d => new DomainEntry(d.Hostname, d.PathPrefix, d.HttpsEnabled, d.TargetPort, d.ComposeService, d.RedirectTo, d.StripPathPrefix,
                    d.BasicAuthSecretId, null, d.DnsProviderId)).ToList(),
            Networks = networks,
            CustomLabels = w.CustomLabels.Count > 0 ? new Dictionary<string, string>(w.CustomLabels) : null,
        };
    }

    public static DeploymentSnapshot Create(Application app)
    {
        var s = Common(app, app.SourceKind);

        switch (app.SourceKind)
        {
            case ApplicationSourceKind.DockerImage:
                var image = app.ImageSource ?? throw new UnsupportedSourceException("source.not_configured", "The application has no image source configured.");
                return s with { Image = new ImageSnapshot(image.Image, image.Tag, image.PullPolicy, image.RegistryId) };

            case ApplicationSourceKind.Compose:
                var compose = app.ComposeSource;
                if (compose?.FilePath is { Length: > 0 } filePath)
                {
                    // The file is read from the repository by the agent (ADR 0010): the application's git source says where, which branch or commit.
                    var composeGit = app.GitSource ?? throw new UnsupportedSourceException("source.not_configured",
                        "A compose file in the repository needs the application's git source (repository, branch).");
                    return s with
                    {
                        Git = new GitSnapshot(composeGit.RepositoryUrl, composeGit.Branch, composeGit.CommitPin, composeGit.GitCredentialId),
                        Compose = new ComposeSnapshot("", null, filePath),
                    };
                }

                if (compose?.InlineContent is not { Length: > 0 } content)
                    throw new UnsupportedSourceException("source.not_configured", "The compose application has neither a file in the repository nor inline content.");
                return s with { Compose = new ComposeSnapshot(content, null) };

            default:
                var git = app.GitSource ?? throw new UnsupportedSourceException("source.not_configured", "The application has no git source configured.");
                var build = app.BuildConfig;
                var engine = build?.Engine ?? app.SourceKind switch
                {
                    ApplicationSourceKind.Nixpacks => BuildEngines.Nixpacks,
                    ApplicationSourceKind.Static => BuildEngines.Static,
                    _ => BuildEngines.Dockerfile,
                };
                if (build?.DockerfileInline is { Length: > 0 })
                    throw new UnsupportedSourceException("source.unsupported", "Inline Dockerfiles are not supported yet; commit the Dockerfile to the repository.");
                return s with
                {
                    Git = new GitSnapshot(git.RepositoryUrl, git.Branch, git.CommitPin, git.GitCredentialId),
                    Build = new BuildSnapshot(
                        engine, build?.Context ?? ".", build?.DockerfilePath, build?.InstallCommand, build?.BuildCommand, build?.StartCommand,
                        build?.OutputDirectory, build?.CacheEnabled ?? true, build?.TargetPlatform),
                };
        }
    }

    /// <summary>The service templates of databases: they flush on SIGTERM, so they get 60 seconds before the kill instead of 30.</summary>
    private static readonly HashSet<string> DatabaseTemplates = new(["postgres", "mysql", "mariadb", "redis", "mongodb", "influxdb"], StringComparer.OrdinalIgnoreCase);

    private static int StopTimeoutSeconds(Workload w) =>
        w is Service service && DatabaseTemplates.Contains(service.TemplateKey) ? RuntimeSnapshot.DatabaseStopTimeoutSeconds : RuntimeSnapshot.DefaultStopTimeoutSeconds;

    /// <summary>A branch name that is safe to hand to git (the agent validates it again).</summary>
    private static bool IsSafeBranch(string branch) =>
        branch.Length is > 0 and <= 255 && branch[0] != '-' && branch[^1] != '/' && !branch.Contains("..", StringComparison.Ordinal)
        && !branch.Contains("//", StringComparison.Ordinal) && !branch.EndsWith(".lock", StringComparison.Ordinal)
        && branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-' or '+' or '@');

    private static bool IsCommitSha(string? sha) =>
        sha is { Length: >= 7 and <= 64 } && sha.All(char.IsAsciiHexDigit);

    /// <summary>
    /// A webhook push deploys the branch that was pushed, at the commit that was pushed (the application's branch setting is only the default and
    /// the filter's reference): a filter such as <c>release/*</c> matches several branches. Other triggers deploy the configured source.
    /// </summary>
    /// <exception cref="UnsupportedSourceException">The pushed reference is not a usable branch name.</exception>
    public static DeploymentSnapshot ApplyTrigger(DeploymentSnapshot snapshot, DeploymentTrigger trigger, string? deploymentRef, string? commitSha)
    {
        if (trigger != DeploymentTrigger.Webhook || snapshot.Git is not { } git || deploymentRef is not { Length: > 0 }) return snapshot;
        const string heads = "refs/heads/";
        var branch = deploymentRef.StartsWith(heads, StringComparison.Ordinal) ? deploymentRef[heads.Length..] : deploymentRef;
        if (!IsSafeBranch(branch))
            throw new UnsupportedSourceException("source.invalid_ref", "The pushed branch name cannot be deployed.");
        return snapshot with { Git = git with { Branch = branch, CommitPin = IsCommitSha(commitSha) ? commitSha : git.CommitPin } };
    }

    private static RuntimeSnapshot Runtime(RuntimeConfig r) => new()
    {
        Strategy = r.DeploymentStrategy, RestartPolicy = r.RestartPolicy, CpuLimit = r.CpuLimit, CpuReservation = r.CpuReservation,
        MemoryLimitBytes = r.MemoryLimitBytes, MemoryReservationBytes = r.MemoryReservationBytes, PidsLimit = r.PidsLimit,
        HealthType = r.HealthCheck.Type, HealthPath = r.HealthCheck.Path, HealthPort = r.HealthCheck.Port,
        HealthIntervalSeconds = r.HealthCheck.IntervalSeconds, HealthTimeoutSeconds = r.HealthCheck.TimeoutSeconds,
        HealthRetries = r.HealthCheck.Retries, HealthStartPeriodSeconds = r.HealthCheck.StartPeriodSeconds,
    };
}
