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
    /// <paramref name="app"/> must have <c>GitSource</c>, <c>BuildConfig</c>, <c>ImageSource</c>, <c>ComposeSource</c>, ports, environment variables
    /// (with their <c>Secret</c>), volumes, non-deleted domains and networks (with <c>Network</c>) loaded.
    /// </summary>
    /// <exception cref="UnsupportedSourceException">The source kind has no usable configuration.</exception>
    public static DeploymentSnapshot Create(Application app)
    {
        var s = new DeploymentSnapshot
        {
            WorkloadId = app.Id, EnvironmentId = app.EnvironmentId, Slug = app.Slug, SourceKind = app.SourceKind,
            Runtime = Runtime(app.Runtime),
            Env = app.EnvironmentVariables.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new EnvEntry(
                e.Key, e.SecretId is null ? e.Value : null, e.SecretId, e.Secret?.CurrentVersion, e.IsBuildTime, e.IsRuntime)).ToList(),
            Ports = app.Ports.OrderBy(p => p.ContainerPort).Select(p => new PortEntry(p.ContainerPort, p.Protocol, p.PublishedPort, p.IsHttp)).ToList(),
            Volumes = app.Volumes.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => new VolumeEntry(v.Name, v.MountPath, v.HostPath, v.ReadOnly)).ToList(),
            Domains = app.Domains.Where(d => !d.IsDeleted).OrderBy(d => d.Hostname, StringComparer.Ordinal)
                .Select(d => new DomainEntry(d.Hostname, d.PathPrefix, d.HttpsEnabled, d.TargetPort)).ToList(),
            Networks = app.Networks.Where(n => n.Network is not null).OrderBy(n => n.Network.DockerName, StringComparer.Ordinal)
                .Select(n => new NetworkEntry(n.Network.DockerName, n.Network.IsInternal, n.Aliases)).ToList(),
        };

        switch (app.SourceKind)
        {
            case ApplicationSourceKind.DockerImage:
                var image = app.ImageSource ?? throw new UnsupportedSourceException("source.not_configured", "The application has no image source configured.");
                return s with { Image = new ImageSnapshot(image.Image, image.Tag, image.PullPolicy, image.RegistryId) };

            case ApplicationSourceKind.Compose:
                var compose = app.ComposeSource;
                if (compose?.InlineContent is not { Length: > 0 } content)
                    throw new UnsupportedSourceException("source.unsupported",
                        "Compose applications currently need the compose file pasted in (inline content); reading it from the repository is not supported yet.");
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

    private static RuntimeSnapshot Runtime(RuntimeConfig r) => new()
    {
        Strategy = r.DeploymentStrategy, RestartPolicy = r.RestartPolicy, CpuLimit = r.CpuLimit, CpuReservation = r.CpuReservation,
        MemoryLimitBytes = r.MemoryLimitBytes, MemoryReservationBytes = r.MemoryReservationBytes, PidsLimit = r.PidsLimit,
        HealthType = r.HealthCheck.Type, HealthPath = r.HealthCheck.Path, HealthPort = r.HealthCheck.Port,
        HealthIntervalSeconds = r.HealthCheck.IntervalSeconds, HealthTimeoutSeconds = r.HealthCheck.TimeoutSeconds,
        HealthRetries = r.HealthCheck.Retries, HealthStartPeriodSeconds = r.HealthCheck.StartPeriodSeconds,
    };
}
