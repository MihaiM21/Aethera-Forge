namespace Aethera.Domain.Transport;

// Domain mirrors of the protocol's value types (proto/aethera/agent/v1). The engine only ever sees these; a mapper converts them to
// and from the generated Aethera.Agent.V1 types inside Aethera.Infrastructure.

/// <summary>
/// A secret that crosses the agent protocol. <see cref="ToString"/> never reveals the value, so interpolating a command into a log
/// line or exception cannot leak it (ADR 0002 "Secret-handling rules for code").
/// </summary>
public sealed record SecretValue
{
    public SecretValue(string value) => Value = value;

    // SECRET: never log
    public string Value { get; }

    public override string ToString() => "[secret]";

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Value = [secret]");
        return true;
    }
}

/// <summary>An environment variable whose value is either plain or secret.</summary>
public sealed record EnvVarSpec(string Name, string? Plain = null, SecretValue? Secret = null)
{
    public static EnvVarSpec OfPlain(string name, string value) => new(name, value);

    public static EnvVarSpec OfSecret(string name, string value) => new(name, null, new SecretValue(value));
}

public sealed record RegistryCredentials(string Server, string Username, SecretValue? Password = null, SecretValue? IdentityToken = null);

public enum PortProtocolKind { Tcp = 1, Udp = 2, Sctp = 3 }

public sealed record PortMappingSpec(int ContainerPort, int HostPort = 0, string? HostIp = null, PortProtocolKind Protocol = PortProtocolKind.Tcp);

public enum MountKind { Volume = 1, Bind = 2, Tmpfs = 3 }

public sealed record VolumeMountSpec(MountKind Type, string Source, string Target, bool ReadOnly = false, long TmpfsSizeBytes = 0);

public sealed record NetworkAttachmentSpec(string Network, IReadOnlyList<string>? Aliases = null, string? Ipv4Address = null);

public sealed record ResourceLimitsSpec(
    double CpuLimitCores = 0, double CpuReservationCores = 0, long MemoryLimitBytes = 0, long MemoryReservationBytes = 0,
    long MemorySwapLimitBytes = 0, long PidsLimit = 0, string? CpusetCpus = null);

public enum RestartPolicyKind { No = 1, Always = 2, OnFailure = 3, UnlessStopped = 4 }

public sealed record RestartPolicySpec(RestartPolicyKind Name, int MaximumRetryCount = 0);

public sealed record HealthcheckConfig(
    IReadOnlyList<string> Test, TimeSpan? Interval = null, TimeSpan? Timeout = null, int Retries = 0, TimeSpan? StartPeriod = null, TimeSpan? StartInterval = null);

public sealed record LogConfigSpec(string Driver, IReadOnlyDictionary<string, string>? Options = null);

/// <summary>Full desired state of one container. Privileged mode, host namespaces, devices and docker.sock mounts are not representable (ADR 0002).</summary>
public sealed record ContainerSpec(string Image, string Name)
{
    public string? Hostname { get; init; }
    public IReadOnlyList<string>? Entrypoint { get; init; }
    public IReadOnlyList<string>? Command { get; init; }
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    public IReadOnlyList<EnvVarSpec>? Env { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
    public IReadOnlyList<PortMappingSpec>? Ports { get; init; }
    public IReadOnlyList<VolumeMountSpec>? Mounts { get; init; }
    public IReadOnlyList<NetworkAttachmentSpec>? Networks { get; init; }
    public ResourceLimitsSpec? Resources { get; init; }
    public RestartPolicySpec? RestartPolicy { get; init; }
    public HealthcheckConfig? Healthcheck { get; init; }
    public LogConfigSpec? LogConfig { get; init; }
    public string? StopSignal { get; init; }
    public TimeSpan? StopTimeout { get; init; }
    public bool ReadOnlyRootFs { get; init; }
    public bool Init { get; init; }
    public IReadOnlyList<string>? CapDrop { get; init; }
    public IReadOnlyList<string>? ExtraHosts { get; init; }
}

public enum ContainerRunState { Unspecified = 0, Created = 1, Running = 2, Paused = 3, Restarting = 4, Removing = 5, Exited = 6, Dead = 7 }

public enum ContainerHealthState { Unspecified = 0, None = 1, Starting = 2, Healthy = 3, Unhealthy = 4 }

public sealed record ContainerNetworkInfo(string Network, string IpAddress, string MacAddress, IReadOnlyList<string> Aliases);

/// <summary>Observed state of one container.</summary>
public sealed record DockerContainer(
    string Id, string Name, string Image, string ImageId, ContainerRunState State, string Status, ContainerHealthState Health,
    DateTimeOffset? CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int ExitCode, bool OomKilled, int RestartCount,
    IReadOnlyDictionary<string, string> Labels, IReadOnlyList<PortMappingSpec> Ports, IReadOnlyList<VolumeMountSpec> Mounts,
    IReadOnlyList<ContainerNetworkInfo> Networks, string? InspectJson);

public sealed record DockerImage(
    string Id, IReadOnlyList<string> RepoTags, IReadOnlyList<string> RepoDigests, long SizeBytes, DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string, string> Labels, string Architecture, string Os, int ContainersUsing);

public sealed record DockerVolume(
    string Name, string Driver, string Mountpoint, IReadOnlyDictionary<string, string> Labels, DateTimeOffset? CreatedAt, long SizeBytes, int RefCount);

public sealed record IpamEntry(string Subnet, string Gateway);

public sealed record NetworkEndpointInfo(string ContainerId, string ContainerName, string Ipv4Address);

public sealed record DockerNetwork(
    string Id, string Name, string Driver, string Scope, bool Internal, bool Attachable, bool Ipv6, IReadOnlyList<IpamEntry> Ipam,
    IReadOnlyDictionary<string, string> Labels, DateTimeOffset? CreatedAt, IReadOnlyList<NetworkEndpointInfo> Endpoints);

// ---- results -------------------------------------------------------------------------------------------------------------------------

/// <summary>Result of commands that return nothing.</summary>
public sealed record Unit
{
    public static readonly Unit Value = new();
}

public sealed record ContainerCreated(string ContainerId, string Name, ContainerRunState State, IReadOnlyList<string> Warnings);

public sealed record ImagePulled(string ImageId, string Digest, long SizeBytes, IReadOnlyList<string> RepoDigests, bool AlreadyPresent);

public sealed record PruneOutcome(IReadOnlyList<string> Deleted, long SpaceReclaimedBytes);

public sealed record ComposeServiceInfo(
    string Service, string ContainerId, string ContainerName, string Image, ContainerRunState State, ContainerHealthState Health, int ExitCode,
    IReadOnlyList<PortMappingSpec> Ports);

public sealed record ComposeOutcome(string ProjectName, IReadOnlyList<ComposeServiceInfo> Services);

public sealed record LogStreamStarted(string StreamId, bool Started);

public sealed record HealthProbeOutcome(bool Healthy, int Attempts, int HttpStatus, TimeSpan Latency, string Detail, DateTimeOffset? CheckedAt);

public sealed record ProxyEnsured(string Provider, string ContainerId, string Version, bool Running, bool Changed, string ConfigSha256);

public sealed record SelfUpdateOutcome(string PreviousVersion, string NewVersion, bool Restarting);

// ---- discovery -----------------------------------------------------------------------------------------------------------------------

public sealed record HostFactsInfo(
    string Hostname, string OsName, string OsVersion, string KernelVersion, string Architecture, string CpuModel, int CpuCoresPhysical,
    int CpuCoresLogical, long MemoryTotalBytes, long SwapTotalBytes, string Virtualization, IReadOnlyList<string> IpAddresses, string Timezone,
    DateTimeOffset? BootTime);

public sealed record DockerDaemonInfo(
    DockerStatus Status, string Version, string ApiVersion, string StorageDriver, string CgroupVersion, string DockerRootDir, string ComposeVersion,
    string BuildxVersion, bool Rootless, bool SwarmActive, int ContainersRunning, int ContainersStopped, int ImageCount, string Error);

public sealed record DiskInfo(string MountPoint, string Device, string FsType, long TotalBytes, long UsedBytes, long InodesTotal, long InodesUsed);

public sealed record NetworkInterfaceInfo(string Name, string MacAddress, IReadOnlyList<string> Addresses, int Mtu, bool Up);

public sealed record ToolInfo(string Name, string Version, string Path);

/// <summary>Everything the agent knows about a machine (spec section 55).</summary>
public sealed record DiscoveryInfo(
    DateTimeOffset? CollectedAt, HostFactsInfo Host, DockerDaemonInfo Docker, IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<NetworkInterfaceInfo> Interfaces, IReadOnlyList<DockerContainer> Containers, IReadOnlyList<DockerNetwork> Networks,
    IReadOnlyList<DockerVolume> Volumes, IReadOnlyList<ToolInfo> Tools);

// ---- builds --------------------------------------------------------------------------------------------------------------------------

public enum BuildEngineKind { Unspecified = 0, Dockerfile = 1, Nixpacks = 2, Static = 3, Image = 4 }

public sealed record GitCredentialSpec(string CredentialsRef, string? HttpsUsername = null, SecretValue? HttpsToken = null, SecretValue? SshPrivateKey = null, string? KnownHosts = null);

public sealed record GitSourceSpec(string Url, string? Ref = null, string? Commit = null, int Depth = 0, bool Submodules = false, GitCredentialSpec? Credentials = null);

public sealed record BuildCacheSpec(bool Enabled, int Mode, IReadOnlyList<string>? CacheFrom = null, string? CacheTo = null, string? CacheKey = null, bool NoCache = false, long MaxSizeBytes = 0);

public sealed record BuildSecretSpec(string Id, SecretValue Value);

public sealed record BuildSpec(string BuildId, BuildEngineKind Engine, IReadOnlyList<string> ImageTags)
{
    public string? EngineName { get; init; }
    public GitSourceSpec? Git { get; init; }
    public string? ImageReference { get; init; }
    public RegistryCredentials? ImagePullAuth { get; init; }
    public string? ContextPath { get; init; }
    public string? DockerfilePath { get; init; }
    public string? TargetStage { get; init; }
    public IReadOnlyList<EnvVarSpec>? BuildArgs { get; init; }
    public IReadOnlyList<EnvVarSpec>? Env { get; init; }
    public IReadOnlyList<BuildSecretSpec>? Secrets { get; init; }
    public string? InstallCommand { get; init; }
    public string? BuildCommand { get; init; }
    public string? StartCommand { get; init; }
    public string? OutputDir { get; init; }
    public bool SpaFallback { get; init; }
    public BuildCacheSpec? Cache { get; init; }
    public IReadOnlyList<string>? TargetPlatforms { get; init; }
    public IReadOnlyDictionary<string, string>? ImageLabels { get; init; }
    public bool Push { get; init; }
    public RegistryCredentials? PushAuth { get; init; }
}

public sealed record BuildOutcome(
    string BuildId, string ImageId, string Digest, IReadOnlyList<string> Tags, IReadOnlyList<string> RepoDigests, long SizeBytes, TimeSpan Duration,
    string EngineUsed, string CommitSha, bool Pushed, string Platform, bool CacheHit);

public sealed record BuildCandidateInfo(
    BuildEngineKind Engine, string EngineName, double Confidence, string Reason, string DockerfilePath, string InstallCommand, string BuildCommand,
    string StartCommand, string OutputDir, string Language, IReadOnlyList<int> SuggestedPorts);

public sealed record BuildDetection(IReadOnlyList<BuildCandidateInfo> Candidates, string CommitSha);

/// <summary>A Compose project: file contents are data written by the agent to its managed directory, never interpolated into a shell.</summary>
public sealed record ComposeProjectSpec(string ProjectName, string ComposeFile)
{
    // SENSITIVE: may contain user-embedded secrets; log length/hash only (see CommandRedaction).
    public string? OverrideFile { get; init; }
    public IReadOnlyList<EnvVarSpec>? Env { get; init; }
    public IReadOnlyList<string>? Profiles { get; init; }
    public IReadOnlyList<RegistryCredentials>? RegistryAuths { get; init; }
}

public enum ComposePullPolicyKind { Missing = 1, Always = 2, Never = 3 }

public abstract record ProbeTarget;

public sealed record HttpProbeTarget(string Url, string? Method = null, IReadOnlyDictionary<string, string>? Headers = null, IReadOnlyList<int>? ExpectedStatus = null, string? BodyContains = null, bool FollowRedirects = false, bool InsecureSkipVerify = false) : ProbeTarget;

public sealed record TcpProbeTarget(string Host, int Port) : ProbeTarget;

public sealed record ContainerHealthProbeTarget(string Container) : ProbeTarget;
