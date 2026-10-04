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

/// <summary>For compose applications. <see cref="Content"/> is the compose file read from the repository or inline at start time.</summary>
public sealed record ComposeSnapshot(string Content, string? RoutedService);

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
}

/// <summary>An environment variable. Exactly one of <see cref="Value"/> and <see cref="SecretId"/> is set.</summary>
public sealed record EnvEntry(string Key, string? Value, Guid? SecretId, int? SecretVersion, bool Build, bool Runtime);

public sealed record PortEntry(int ContainerPort, PortProtocol Protocol, int? PublishedPort, bool IsHttp);

public sealed record VolumeEntry(string Name, string MountPath, string? HostPath, bool ReadOnly);

public sealed record DomainEntry(string Hostname, string PathPrefix, bool Https, int? TargetPort);

/// <summary>A managed network the workload joins: its Docker name and DNS aliases.</summary>
public sealed record NetworkEntry(string DockerName, bool Internal, IReadOnlyList<string> Aliases);
