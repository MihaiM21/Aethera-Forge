using System.Text.Json;
using System.Text.Json.Serialization;
using Aethera.Domain;

namespace Aethera.Engine.Deployments;

/// <summary>
/// The configuration a deployment runs with, frozen when its job starts (ADR 0004). It holds no secret values, only secret id and
/// version references, so it is safe to persist and is exactly what a rollback re-applies.
/// </summary>
public sealed record DeploymentSnapshot
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public int Version { get; init; } = CurrentVersion;
    public required Guid WorkloadId { get; init; }
    public required Guid EnvironmentId { get; init; }
    public required string Slug { get; init; }
    public required ApplicationSourceKind SourceKind { get; init; }

    public GitSnapshot? Git { get; init; }
    public BuildSnapshot? Build { get; init; }
    public ImageSnapshot? Image { get; init; }
    public ComposeSnapshot? Compose { get; init; }

    public RuntimeSnapshot Runtime { get; init; } = new();
    public List<EnvEntry> Env { get; init; } = [];
    public List<PortEntry> Ports { get; init; } = [];
    public List<VolumeEntry> Volumes { get; init; } = [];
    public List<DomainEntry> Domains { get; init; } = [];
    public List<NetworkEntry> Networks { get; init; } = [];

    /// <summary>Extra container labels of the workload (administrators only), merged over the generated ones. Null in snapshots from before ADR 0010.</summary>
    public Dictionary<string, string>? CustomLabels { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DeploymentSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<DeploymentSnapshot>(json, Json) ?? throw new InvalidOperationException("The deployment snapshot is empty.");

    public static bool TryFromJson(string json, out DeploymentSnapshot? snapshot)
    {
        try
        {
            snapshot = JsonSerializer.Deserialize<DeploymentSnapshot>(json, Json);
            return snapshot is { Slug: not null };
        }
        catch (JsonException)
        {
            snapshot = null;
            return false;
        }
    }
}

public sealed record GitSnapshot(string Url, string Branch, string? CommitPin, Guid? CredentialId);

public sealed record BuildSnapshot(
    string Engine, string Context, string? DockerfilePath, string? InstallCommand, string? BuildCommand, string? StartCommand,
    string? OutputDirectory, bool CacheEnabled, string? TargetPlatform);

public sealed record ImageSnapshot(string Image, string Tag, ImagePullPolicy PullPolicy, Guid? RegistryId, IReadOnlyList<string>? Command = null);

/// <summary>
/// For compose applications. <see cref="Content"/> is the inline compose file (empty when the file comes from the repository: then
/// <see cref="FilePath"/> is its path inside the application's git source). <see cref="RoutedService"/> is the service of domains from before
/// ADR 0010 that name none (<see cref="DomainEntry.ComposeService"/>).
/// </summary>
public sealed record ComposeSnapshot(string Content, string? RoutedService, string? FilePath = null);

public sealed record RuntimeSnapshot
{
    public string Strategy { get; init; } = DeploymentStrategies.Recreate;
    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.UnlessStopped;
    public double? CpuLimit { get; init; }
    public double? CpuReservation { get; init; }
    public long? MemoryLimitBytes { get; init; }
    public long? MemoryReservationBytes { get; init; }
    public int? PidsLimit { get; init; }
    public HealthCheckType HealthType { get; init; } = HealthCheckType.None;
    public string? HealthPath { get; init; }
    public int? HealthPort { get; init; }
    public int HealthIntervalSeconds { get; init; } = 10;
    public int HealthTimeoutSeconds { get; init; } = 5;
    public int HealthRetries { get; init; } = 3;
    public int HealthStartPeriodSeconds { get; init; }

    public const int DefaultStopTimeoutSeconds = 30;
    public const int DatabaseStopTimeoutSeconds = 60;

    /// <summary>Seconds a container gets to stop gracefully (SIGTERM) before it is killed: 30 for applications, 60 for the databases of the service templates. Snapshots from before R1 read as 30.</summary>
    public int StopTimeoutSeconds { get; init; } = DefaultStopTimeoutSeconds;
}

/// <summary>An environment variable. Exactly one of <see cref="Value"/> and <see cref="SecretId"/> is set.</summary>
public sealed record EnvEntry(string Key, string? Value, Guid? SecretId, int? SecretVersion, bool Build, bool Runtime);

public sealed record PortEntry(int ContainerPort, PortProtocol Protocol, int? PublishedPort, bool IsHttp);

public sealed record VolumeEntry(string Name, string MountPath, string? HostPath, bool ReadOnly);

/// <summary>
/// A hostname routed to the workload. The later fields (ADR 0010) default so snapshots of earlier deployments still read. The basic auth
/// secret is a pinned id and version, like an environment variable's: the htpasswd lines never enter the snapshot.
/// </summary>
public sealed record DomainEntry(
    string Hostname, string PathPrefix, bool Https, int? TargetPort, string? ComposeService = null, string? RedirectTo = null,
    bool StripPathPrefix = false, Guid? BasicAuthSecretId = null, int? BasicAuthSecretVersion = null, Guid? DnsProviderId = null);

/// <summary>A managed network the workload joins: its Docker name and DNS aliases.</summary>
public sealed record NetworkEntry(string DockerName, bool Internal, IReadOnlyList<string> Aliases);
