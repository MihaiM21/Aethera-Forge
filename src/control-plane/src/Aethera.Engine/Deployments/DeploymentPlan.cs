using Aethera.Domain;
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

    /// <summary>
    /// The server that runs <see cref="Build"/> when it is not <see cref="ServerId"/> (ADR 0012): the image is pushed to the registry reference in
    /// the build's tag and pulled by the run server. Null = the run server builds.
    /// </summary>
    public Guid? BuildServerId { get; init; }

    /// <summary>Names of the build and run servers, for the deployment log of a build elsewhere.</summary>
    public string? BuildServerName { get; init; }

    public string? RunServerName { get; init; }

    /// <summary>
    /// Waits for a free build slot on the build server (its <c>MaxConcurrentBuilds</c>) and returns the slot, released when disposed. The runner
    /// holds it for the Build step only. Null = no limit is enforced (tests, plans without a build).
    /// </summary>
    public Func<CancellationToken, Task<IAsyncDisposable>>? AcquireBuildSlot { get; init; }

    /// <summary>Image to pull when <see cref="Build"/> is null.</summary>
    public string? ImageReference { get; init; }

    public RegistryCredentials? PullAuth { get; init; }

    /// <summary>When an image source is pulled (<see cref="ImagePullPolicy"/>). The default pulls every time.</summary>
    public ImagePullPolicy PullPolicy { get; init; } = ImagePullPolicy.Always;

    /// <summary>Grace period of the old container before it is killed (SIGTERM, then SIGKILL) when a deployment replaces it.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(RuntimeSnapshot.DefaultStopTimeoutSeconds);

    /// <summary>The image only exists on the server (rollbacks): it is checked, not pulled, and a missing image fails with <c>image.missing</c>.</summary>
    public bool LocalImage { get; init; }

    /// <summary>Set for compose applications; they bypass the build and image steps and use the compose strategy.</summary>
    public ComposeProjectSpec? Compose { get; init; }

    public TimeSpan ComposeWaitTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Container to run; <see cref="ContainerSpec.Image"/> is replaced with the built or pulled image.</summary>
    public ContainerSpec Container { get; init; } = new("", "");

    /// <summary>Probe that must succeed before the deployment is considered live. Null = none.</summary>
    public ProbeTarget? Health { get; init; }

    /// <summary>Builds the probe for a container name; used by strategies that start the new container under a fresh name. Wins over <see cref="Health"/>.</summary>
    public Func<string, ProbeTarget?>? HealthFor { get; init; }

    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int HealthRetries { get; init; } = 15;
    public TimeSpan HealthStartPeriod { get; init; } = TimeSpan.Zero;

    /// <summary>Consecutive successful probes required (the liveness check of a workload without a health check wants a few in a row).</summary>
    public int HealthSuccessThreshold { get; init; } = 1;

    /// <summary>
    /// Rebuilds the container part of the plan once the image is known (its EXPOSE ports, its HEALTHCHECK). The runner inspects the image after
    /// the Image step and runs the strategy with the result. Null = nothing depends on the image (compose, or a plan built with the facts).
    /// </summary>
    public Func<ImageFacts, DeploymentPlan>? WithImage { get; init; }

    /// <summary>The container port taken from the image (or the build engine's default) because the workload declares none; set by the builder.</summary>
    public int? DetectedPort { get; init; }

    /// <summary>Set by the runner from the plan it ran: the port it detected, for the caller to store on the workload.</summary>
    public int? RanWithDetectedPort { get; set; }

    /// <summary>Set by a strategy that brought the previous container back after the new one failed: the old deployment is still the live one.</summary>
    public bool PreviousRestored { get; set; }

    /// <summary>Writes a line of the deployment's own log (a container's output on failure). Redacted by the sink.</summary>
    public Func<LogStream, string, CancellationToken, ValueTask>? WriteLog { get; init; }

    /// <summary>Writes control-plane narration to the deployment's log.</summary>
    public Func<string, CancellationToken, ValueTask>? Narrate { get; init; }

    /// <summary>Container of the currently live deployment, replaced by this one.</summary>
    public string? PreviousContainer { get; init; }

    /// <summary>Networks created (idempotently) before the container starts.</summary>
    public IReadOnlyList<string> Networks { get; init; } = [];

    /// <summary>Networks from <see cref="Networks"/> without outside connectivity (databases).</summary>
    public IReadOnlySet<string> InternalNetworks { get; init; } = new HashSet<string>();

    /// <summary>Filled in by the runner when the build step succeeded (size, digest, cache use, platform).</summary>
    public BuildOutcome? BuildResult { get; set; }

    public Guid? JobId { get; init; }
    public Guid? OrganizationId { get; init; }
    public Action<LogEntry>? OnLog { get; init; }
}

/// <summary>What the image that is about to run says about itself (<c>image.inspect</c>).</summary>
/// <param name="ExposedTcpPorts">The TCP ports of its EXPOSE instructions, ascending.</param>
public sealed record ImageFacts(IReadOnlyList<int> ExposedTcpPorts, bool HasHealthcheck)
{
    /// <summary>Ports a web server usually listens on, in order of preference when an image exposes several.</summary>
    private static readonly int[] WebPorts = [80, 8080, 3000, 8000, 5000, 4000, 8888];

    public static readonly ImageFacts Unknown = new([], false);

    public static ImageFacts From(DockerImage image)
    {
        var ports = new SortedSet<int>();
        foreach (var entry in image.ExposedPorts ?? [])
        {
            var parts = entry.Split('/');
            if (parts.Length > 1 && !parts[1].Equals("tcp", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
                ports.Add(port);
        }

        return new ImageFacts([.. ports], image.HasHealthcheck);
    }

    /// <summary>The port to route to: the only one, else the most web-like one, else the lowest.</summary>
    public int? PreferredPort => ExposedTcpPorts.Count switch
    {
        0 => null,
        1 => ExposedTcpPorts[0],
        _ => WebPorts.Cast<int?>().FirstOrDefault(p => ExposedTcpPorts.Contains(p!.Value)) ?? ExposedTcpPorts[0],
    };
}
