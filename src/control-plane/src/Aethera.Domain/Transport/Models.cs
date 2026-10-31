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

/// <param name="OomScoreAdj">-1000..1000, 0 = Docker's default (workload priority, ADR 0012).</param>
public sealed record ResourceLimitsSpec(
    double CpuLimitCores = 0, double CpuReservationCores = 0, long MemoryLimitBytes = 0, long MemoryReservationBytes = 0,
    long MemorySwapLimitBytes = 0, long PidsLimit = 0, string? CpusetCpus = null, int OomScoreAdj = 0);

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

/// <param name="ExposedPorts">The image's EXPOSE entries, e.g. <c>8080/tcp</c>; empty when it exposes nothing (or the transport cannot tell).</param>
/// <param name="HasHealthcheck">The image defines a HEALTHCHECK (other than NONE).</param>
public sealed record DockerImage(
    string Id, IReadOnlyList<string> RepoTags, IReadOnlyList<string> RepoDigests, long SizeBytes, DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string, string> Labels, string Architecture, string Os, int ContainersUsing,
    IReadOnlyList<string>? ExposedPorts = null, bool HasHealthcheck = false);

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

/// <param name="CommitSha">The commit that was checked out when the project came from Git.</param>
public sealed record ComposeOutcome(string ProjectName, IReadOnlyList<ComposeServiceInfo> Services, string? CommitSha = null);

/// <summary>One service of a resolved compose file (<see cref="ComposeInspectCommand"/>).</summary>
/// <param name="NetworkMode">The service's <c>network_mode</c> as written (<c>service:db</c>, <c>host</c>, ...); null = the project's networks.</param>
public sealed record ComposeServiceDefinition(string Name, string Image, bool HasBuild, IReadOnlyList<int> Ports, string? NetworkMode = null);

/// <param name="Violations">Agent policy findings that would make <c>compose.up</c> fail; empty = valid.</param>
public sealed record ComposeInspection(IReadOnlyList<ComposeServiceDefinition> Services, string CommitSha, string ComposeSha256, IReadOnlyList<string> Violations);

/// <summary>A program that ran to completion inside a container; <see cref="ExitCode"/> decides success (ADR 0010).</summary>
public sealed record ContainerExecOutcome(int ExitCode, string OutputTail, bool OutputTruncated, TimeSpan Duration);

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

    /// <summary>
    /// The server's build quota (ADR 0012): the build runs in the agent's BuildKit builder container with these limits. Null = the daemon's
    /// builder. Only for agents that advertise <c>build.quota</c>.
    /// </summary>
    public ResourceLimitsSpec? BuilderLimits { get; init; }
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

    /// <summary>Repository the project lives in; when set, <see cref="ComposeFile"/> is empty and the file comes from <see cref="ComposePath"/>.</summary>
    public GitSourceSpec? Git { get; init; }

    /// <summary>Compose file path inside the repository; null = <c>docker-compose.yml</c>, then <c>compose.yaml</c>.</summary>
    public string? ComposePath { get; init; }

    /// <summary>Build arguments for every <c>build:</c> service.</summary>
    public IReadOnlyList<EnvVarSpec>? BuildArgs { get; init; }
}

public enum ComposePullPolicyKind { Missing = 1, Always = 2, Never = 3 }

public abstract record ProbeTarget;

public sealed record HttpProbeTarget(string Url, string? Method = null, IReadOnlyDictionary<string, string>? Headers = null, IReadOnlyList<int>? ExpectedStatus = null, string? BodyContains = null, bool FollowRedirects = false, bool InsecureSkipVerify = false) : ProbeTarget;

public sealed record TcpProbeTarget(string Host, int Port) : ProbeTarget;

public sealed record ContainerHealthProbeTarget(string Container) : ProbeTarget;

// ---- backups (ADR 0010) --------------------------------------------------------------------------------------------------------------

/// <summary>Where a backup stream comes from.</summary>
public abstract record BackupSourceSpec;

/// <summary>Runs <paramref name="Argv"/> inside the running container; its stdout is the backup stream.</summary>
public sealed record ExecDumpSource(string Container, IReadOnlyList<string> Argv, IReadOnlyList<EnvVarSpec>? Env = null, string? User = null) : BackupSourceSpec;

/// <summary>A gzipped tar of a named volume, made by a helper container that mounts it read-only.</summary>
public sealed record VolumeArchiveSource(string Volume, string? HelperImage = null) : BackupSourceSpec;

/// <summary>Where a restored stream goes.</summary>
public abstract record BackupSinkSpec;

/// <summary>Runs <paramref name="Argv"/> inside the running container with the decrypted stream on stdin.</summary>
public sealed record ExecRestoreSink(string Container, IReadOnlyList<string> Argv, IReadOnlyList<EnvVarSpec>? Env = null, string? User = null) : BackupSinkSpec;

/// <summary>Unpacks a <see cref="VolumeArchiveSource"/> archive into a named volume (the workload is stopped first).</summary>
public sealed record VolumeRestoreSink(string Volume, string? HelperImage = null, bool ClearFirst = false) : BackupSinkSpec;

/// <summary>A stored backup object.</summary>
public abstract record BackupLocationSpec;

/// <summary>An object in an S3-compatible store (S3, R2, B2, MinIO).</summary>
public sealed record S3LocationSpec(
    string Endpoint, string? Region, string Bucket, string Key, string AccessKeyId, SecretValue SecretAccessKey, bool ForcePathStyle = false) : BackupLocationSpec;

/// <summary>A file in the agent's own backup directory; <paramref name="Path"/> is relative.</summary>
public sealed record LocalBackupLocationSpec(string Path) : BackupLocationSpec;

/// <summary>The 32-byte stream key (base64) and its id (format AEB1, ADR 0010).</summary>
public sealed record BackupEncryptionSpec(SecretValue Key, string KeyId);

public sealed record BackupRunOutcome(string BackupId, long RawSizeBytes, long StoredSizeBytes, string StoredSha256, TimeSpan Duration);

public sealed record BackupRestoreOutcome(long RestoredBytes, TimeSpan Duration);

// ---- mesh (ADR 0012) ------------------------------------------------------------------------------------------------------------------

public enum MeshProviderKind { WireGuard = 1, Tailscale = 2 }

/// <param name="Endpoint">"host:port"; null = the peer dials in.</param>
/// <param name="AllowedIps">The peer's mesh address as a /32.</param>
/// <param name="Name">The peer's server id, for logs.</param>
public sealed record MeshPeerSpec(string PublicKey, string? Endpoint, IReadOnlyList<string> AllowedIps, int PersistentKeepaliveSeconds = 25, string? Name = null);

public sealed record MeshPeerStateInfo(string PublicKey, string? Endpoint, DateTimeOffset? LastHandshake, long RxBytes, long TxBytes);

/// <summary>What the agent reports about its mesh interface. <see cref="PublicKey"/> is empty for Tailscale.</summary>
public sealed record MeshStateInfo(
    MeshProviderKind Provider, string InterfaceName, bool Up, string PublicKey, IReadOnlyList<string> Addresses, int ListenPort,
    IReadOnlyList<MeshPeerStateInfo> Peers, string? Error);
