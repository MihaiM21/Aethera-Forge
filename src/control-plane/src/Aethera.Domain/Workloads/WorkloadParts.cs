using System.Text.RegularExpressions;

namespace Aethera.Domain;

public class WorkloadPort : MutableEntity
{
    public Guid WorkloadId { get; set; }
    public Workload Workload { get; set; } = null!;
    public int ContainerPort { get; set; }
    public PortProtocol Protocol { get; set; } = PortProtocol.Tcp;

    /// <summary>Port published on the server (host); null = internal only.</summary>
    public int? PublishedPort { get; set; }

    /// <summary>True when the port speaks HTTP(S) and may be routed by the reverse proxy.</summary>
    public bool IsHttp { get; set; }
}

public partial class EnvironmentVariable : MutableEntity
{
    public Guid WorkloadId { get; set; }
    public Workload Workload { get; set; } = null!;
    public required string Key { get; set; }

    /// <summary>Plain value; null when <see cref="SecretId"/> is used.</summary>
    public string? Value { get; set; }
    public Guid? SecretId { get; set; }
    public Secret? Secret { get; set; }

    /// <summary>Passed to the build (build args / build env).</summary>
    public bool IsBuildTime { get; set; }

    /// <summary>Injected into the running container.</summary>
    public bool IsRuntime { get; set; } = true;

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string? key) => !string.IsNullOrEmpty(key) && KeyPattern().IsMatch(key);
}

public class Volume : MutableEntity
{
    public Guid WorkloadId { get; set; }
    public Workload Workload { get; set; } = null!;

    /// <summary>Docker volume name (named volume); ignored for bind mounts.</summary>
    public required string Name { get; set; }
    public required string MountPath { get; set; }

    /// <summary>Bind-mount source on the host; null for a named Docker volume.</summary>
    public string? HostPath { get; set; }
    public bool ReadOnly { get; set; }
    public bool BackupEnabled { get; set; }
}

/// <summary>A managed Docker network on one server, scoped to a project or a single environment.</summary>
public class Network : MutableEntity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Null = shared by every environment of the project.</summary>
    public Guid? EnvironmentId { get; set; }
    public ProjectEnvironment? Environment { get; set; }
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public required string Name { get; set; }

    /// <summary>Actual Docker network name (unique per server).</summary>
    public required string DockerName { get; set; }

    /// <summary>Internal networks have no external connectivity (databases).</summary>
    public bool IsInternal { get; set; }
}

/// <summary>Join between a workload and a managed network, with DNS aliases inside that network.</summary>
public class WorkloadNetwork
{
    public Guid WorkloadId { get; set; }
    public Workload Workload { get; set; } = null!;
    public Guid NetworkId { get; set; }
    public Network Network { get; set; } = null!;
    public List<string> Aliases { get; set; } = [];
}

public enum CertificateStatus
{
    None = 0,
    Pending = 1,
    Issued = 2,
    Failed = 3,
    Expired = 4,
}

public enum DnsStatus
{
    Unknown = 0,
    Ok = 1,
    Mismatch = 2,
    Missing = 3,
    Error = 4,
}

/// <summary>A hostname (+ optional path prefix) routed to a workload by the reverse proxy.</summary>
public class WorkloadDomain : SoftDeletableEntity
{
    public Guid WorkloadId { get; set; }
    public Workload Workload { get; set; } = null!;
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;

    /// <summary>Lower-cased, without trailing dot. Unique together with <see cref="PathPrefix"/> among non-deleted rows.</summary>
    public string Hostname { get; private set; } = null!;

    public string PathPrefix { get; set; } = "/";
    public bool HttpsEnabled { get; set; } = true;

    /// <summary>Container port to route to; null = the workload's first HTTP port.</summary>
    public int? TargetPort { get; set; }
    public bool IsPrimary { get; set; }

    public CertificateStatus CertificateStatus { get; set; } = CertificateStatus.None;
    public DateTimeOffset? CertificateExpiresAt { get; set; }
    public string? CertificateError { get; set; }

    public DnsStatus DnsStatus { get; set; } = DnsStatus.Unknown;
    public DateTimeOffset? DnsCheckedAt { get; set; }
    public List<string> DnsResolvedIps { get; set; } = [];
    public string? ProxyRouteName { get; set; }

    public void SetHostname(string hostname) => Hostname = NormalizeHostname(hostname);

    public static string NormalizeHostname(string hostname) => hostname.Trim().TrimEnd('.').ToLowerInvariant();
}

/// <summary>Image known to a server, tracked for cleanup policies (spec §23).</summary>
public class ImageRecord : MutableEntity
{
    public Guid WorkloadId { get; set; }
    public Workload Workload { get; set; } = null!;
    public Guid? DeploymentId { get; set; }
    public Deployment? Deployment { get; set; }
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public required string Repository { get; set; }
    public required string Tag { get; set; }
    public string? Digest { get; set; }
    public long? SizeBytes { get; set; }

    /// <summary>Creation time reported by Docker.</summary>
    public DateTimeOffset? ImageCreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Set when the image was pruned from the server (row kept for history).</summary>
    public DateTimeOffset? RemovedAt { get; set; }

    public string Reference => $"{Repository}:{Tag}";
}
