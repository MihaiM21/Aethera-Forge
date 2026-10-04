using Aethera.Domain.Transport;

namespace Aethera.Engine.Deployments;

/// <summary>
/// Everything the engine needs to run one deployment, already resolved by the caller (secrets decrypted, domains and ports
/// computed). The engine itself never reads the database or the secret store.
/// </summary>
public sealed class DeploymentPlan
{
    public required Guid ServerId { get; init; }

    /// <summary>Source build. Null = the image in <see cref="ImageReference"/> is pulled instead (image sources, redeploys, rollbacks).</summary>
    public BuildSpec? Build { get; init; }

    /// <summary>Image to pull when <see cref="Build"/> is null.</summary>
    public string? ImageReference { get; init; }

    public RegistryCredentials? PullAuth { get; init; }

    /// <summary>Container to run; <see cref="ContainerSpec.Image"/> is replaced with the built or pulled image.</summary>
    public required ContainerSpec Container { get; init; }

    /// <summary>Probe that must succeed before the deployment is considered live. Null = none.</summary>
    public ProbeTarget? Health { get; init; }

    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int HealthRetries { get; init; } = 15;
    public TimeSpan HealthStartPeriod { get; init; } = TimeSpan.Zero;

    /// <summary>Container of the currently live deployment, replaced by this one.</summary>
    public string? PreviousContainer { get; init; }

    /// <summary>Networks created (idempotently) before the container starts.</summary>
    public IReadOnlyList<string> Networks { get; init; } = [];

    public Guid? JobId { get; init; }
    public Guid? OrganizationId { get; init; }
    public Action<LogEntry>? OnLog { get; init; }
}
