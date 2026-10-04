namespace Aethera.Domain.Transport;

// The command allowlist (ADR 0002 "Command allowlist"): one domain record per arm of the protocol's Command.request. Adding a command
// is a protocol change that needs an ADR amendment. Default timeouts are the ADR's per-type defaults.

// ---- containers ----------------------------------------------------------------------------------------------------------------------

public sealed record ContainerCreateCommand(ContainerSpec Spec, bool Start = true, bool PullIfMissing = false, RegistryCredentials? PullAuth = null, bool ReplaceExisting = false)
    : ServerCommand<ContainerCreated>("container.create", TransportCapabilities.ContainerOps, TimeSpan.FromMinutes(10));

public sealed record ContainerStartCommand(string Container)
    : ServerCommand<DockerContainer>("container.start", TransportCapabilities.ContainerOps, TimeSpan.FromSeconds(60));

public sealed record ContainerStopCommand(string Container, TimeSpan? Timeout = null)
    : ServerCommand<DockerContainer>("container.stop", TransportCapabilities.ContainerOps, TimeSpan.FromSeconds(30) + (Timeout ?? TimeSpan.FromSeconds(10)));

public sealed record ContainerRestartCommand(string Container, TimeSpan? Timeout = null)
    : ServerCommand<DockerContainer>("container.restart", TransportCapabilities.ContainerOps, TimeSpan.FromSeconds(30) + (Timeout ?? TimeSpan.FromSeconds(10)));

public sealed record ContainerRemoveCommand(string Container, bool Force = false, bool RemoveAnonymousVolumes = false)
    : ServerCommand<Unit>("container.remove", TransportCapabilities.ContainerOps, TimeSpan.FromSeconds(60));

public sealed record ContainerInspectCommand(string Container, bool IncludeRaw = false)
    : ServerCommand<DockerContainer>("container.inspect", TransportCapabilities.ContainerOps, TimeSpan.FromSeconds(30), ReadOnly: true);

public sealed record ContainerListCommand(bool All = true, IReadOnlyList<string>? LabelFilters = null, string? NameFilter = null)
    : ServerCommand<IReadOnlyList<DockerContainer>>("container.list", TransportCapabilities.ContainerOps, TimeSpan.FromSeconds(30), ReadOnly: true);

// ---- images --------------------------------------------------------------------------------------------------------------------------

public sealed record ImagePullCommand(string Reference, RegistryCredentials? Auth = null, string? Platform = null)
    : ServerCommand<ImagePulled>("image.pull", TransportCapabilities.ImageOps, TimeSpan.FromMinutes(10));

public sealed record ImageListCommand(bool IncludeIntermediate = false, IReadOnlyList<string>? LabelFilters = null, string? ReferenceFilter = null)
    : ServerCommand<IReadOnlyList<DockerImage>>("image.list", TransportCapabilities.ImageOps, TimeSpan.FromSeconds(30), ReadOnly: true);

public sealed record ImageRemoveCommand(string Image, bool Force = false)
    : ServerCommand<Unit>("image.remove", TransportCapabilities.ImageOps, TimeSpan.FromSeconds(60));

public sealed record ImagePruneCommand(bool AllUnused = false, TimeSpan? OlderThan = null, IReadOnlyList<string>? LabelFilters = null, IReadOnlyList<string>? Keep = null)
    : ServerCommand<PruneOutcome>("image.prune", TransportCapabilities.ImageOps, TimeSpan.FromMinutes(10));

public sealed record ImageInspectCommand(string Image)
    : ServerCommand<DockerImage>("image.inspect", TransportCapabilities.ImageOps, TimeSpan.FromSeconds(30), ReadOnly: true);

// ---- volumes -------------------------------------------------------------------------------------------------------------------------

public sealed record VolumeCreateCommand(string VolumeName, string? Driver = null, IReadOnlyDictionary<string, string>? DriverOptions = null, IReadOnlyDictionary<string, string>? Labels = null)
    : ServerCommand<DockerVolume>("volume.create", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromSeconds(30));

public sealed record VolumeListCommand(IReadOnlyList<string>? LabelFilters = null, bool IncludeSizes = false)
    : ServerCommand<IReadOnlyList<DockerVolume>>("volume.list", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromSeconds(60), ReadOnly: true);

public sealed record VolumeRemoveCommand(string VolumeName, bool Force = false)
    : ServerCommand<Unit>("volume.remove", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromSeconds(60));

/// <summary>Unlabelled volumes are never pruned unless <paramref name="IncludeUnlabelled"/> is set (data-loss guard; the API needs user confirmation).</summary>
public sealed record VolumePruneCommand(IReadOnlyList<string>? LabelFilters = null, bool IncludeUnlabelled = false)
    : ServerCommand<PruneOutcome>("volume.prune", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromMinutes(10));

// ---- networks ------------------------------------------------------------------------------------------------------------------------

public sealed record NetworkCreateCommand(
    string NetworkName, string? Driver = null, bool Internal = false, bool Attachable = false, bool Ipv6 = false, IReadOnlyList<IpamEntry>? Ipam = null,
    IReadOnlyDictionary<string, string>? Options = null, IReadOnlyDictionary<string, string>? Labels = null, bool IfNotExists = true)
    : ServerCommand<DockerNetwork>("network.create", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromSeconds(30));

public sealed record NetworkListCommand(IReadOnlyList<string>? LabelFilters = null)
    : ServerCommand<IReadOnlyList<DockerNetwork>>("network.list", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromSeconds(30), ReadOnly: true);

public sealed record NetworkRemoveCommand(string Network)
    : ServerCommand<Unit>("network.remove", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromSeconds(30));

public sealed record NetworkConnectCommand(string Network, string Container, IReadOnlyList<string>? Aliases = null, string? Ipv4Address = null)
    : ServerCommand<Unit>("network.connect", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromSeconds(30));

public sealed record NetworkDisconnectCommand(string Network, string Container, bool Force = false)
    : ServerCommand<Unit>("network.disconnect", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromSeconds(30));

public sealed record NetworkPruneCommand(IReadOnlyList<string>? LabelFilters = null, TimeSpan? OlderThan = null)
    : ServerCommand<PruneOutcome>("network.prune", TransportCapabilities.VolumeNetworkOps, TimeSpan.FromMinutes(10));

// ---- compose -------------------------------------------------------------------------------------------------------------------------

public sealed record ComposeUpCommand(
    ComposeProjectSpec Project, IReadOnlyList<string>? Services = null, ComposePullPolicyKind PullPolicy = ComposePullPolicyKind.Missing, bool Build = false,
    bool ForceRecreate = false, bool RemoveOrphans = false, bool Wait = true, TimeSpan? WaitTimeout = null)
    : ServerCommand<ComposeOutcome>("compose.up", TransportCapabilities.Compose, TimeSpan.FromMinutes(10));

/// <summary><paramref name="RemoveVolumes"/> is destructive: the API requires explicit user confirmation before it is set.</summary>
public sealed record ComposeDownCommand(ComposeProjectSpec Project, bool RemoveVolumes = false, bool RemoveOrphans = false, TimeSpan? Timeout = null)
    : ServerCommand<ComposeOutcome>("compose.down", TransportCapabilities.Compose, TimeSpan.FromMinutes(5));

public sealed record ComposePsCommand(ComposeProjectSpec Project, bool All = true)
    : ServerCommand<ComposeOutcome>("compose.ps", TransportCapabilities.Compose, TimeSpan.FromSeconds(60), ReadOnly: true);

public sealed record ComposePullCommand(ComposeProjectSpec Project, IReadOnlyList<string>? Services = null)
    : ServerCommand<Unit>("compose.pull", TransportCapabilities.Compose, TimeSpan.FromMinutes(10));

// ---- builds --------------------------------------------------------------------------------------------------------------------------

public sealed record BuildImageCommand(BuildSpec Spec)
    : ServerCommand<BuildOutcome>("build.run", TransportCapabilities.Builds, TimeSpan.FromMinutes(30));

public sealed record BuildDetectCommand(GitSourceSpec Git, string? ContextPath = null)
    : ServerCommand<BuildDetection>("build.detect", TransportCapabilities.Builds, TimeSpan.FromMinutes(5), ReadOnly: true);

// ---- logs ----------------------------------------------------------------------------------------------------------------------------

public sealed record LogStreamStartCommand(string StreamId, string Container, bool Follow = true, DateTimeOffset? Since = null, int Tail = 0, bool IncludeStdout = true, bool IncludeStderr = true)
    : ServerCommand<LogStreamStarted>("log.stream_start", TransportCapabilities.LogFollow, TimeSpan.FromSeconds(30));

public sealed record LogStreamStopCommand(string StreamId)
    : ServerCommand<Unit>("log.stream_stop", TransportCapabilities.LogFollow, TimeSpan.FromSeconds(30));

// ---- health / proxy / system ---------------------------------------------------------------------------------------------------------

public sealed record HealthProbeCommand(
    ProbeTarget Target, TimeSpan? Timeout = null, TimeSpan? Interval = null, int Retries = 1, TimeSpan? StartPeriod = null, int SuccessThreshold = 1)
    : ServerCommand<HealthProbeOutcome>("health.probe", TransportCapabilities.HealthProbe,
        (StartPeriod ?? TimeSpan.Zero) + (Retries < 1 ? 1 : Retries) * ((Timeout ?? TimeSpan.FromSeconds(5)) + (Interval ?? TimeSpan.FromSeconds(2))) + TimeSpan.FromSeconds(10),
        ReadOnly: true);

public sealed record ProxyEnsureCommand(
    string Provider, byte[] Config, string? Image = null, string? Version = null, IReadOnlyList<EnvVarSpec>? Env = null, string? Network = null,
    bool RestartOnChange = true)
    : ServerCommand<ProxyEnsured>("proxy.ensure", TransportCapabilities.ContainerOps, TimeSpan.FromMinutes(5));

public sealed record DiscoveryRefreshCommand()
    : ServerCommand<DiscoveryInfo>("discovery.refresh", TransportCapabilities.None, TimeSpan.FromSeconds(60), ReadOnly: true);

/// <summary>`docker system prune` with every scope opt-in: the agent rejects a command where no scope flag is set.</summary>
public sealed record SystemPruneCommand(
    bool StoppedContainers = false, bool DanglingImages = false, bool UnusedImages = false, bool UnusedNetworks = false, bool BuildCache = false,
    bool Volumes = false, TimeSpan? OlderThan = null, IReadOnlyList<string>? KeepImages = null)
    : ServerCommand<PruneOutcome>("system.prune", TransportCapabilities.ImageOps, TimeSpan.FromMinutes(10))
{
    public bool AnyScope => StoppedContainers || DanglingImages || UnusedImages || UnusedNetworks || BuildCache || Volumes;
}

public sealed record AgentSelfUpdateCommand(string Url, string Sha256, string TargetVersion, byte[]? Signature = null)
    : ServerCommand<SelfUpdateOutcome>("agent.self_update", TransportCapabilities.SelfUpdate, TimeSpan.FromMinutes(5));
