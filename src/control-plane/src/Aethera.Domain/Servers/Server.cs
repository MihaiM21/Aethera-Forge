namespace Aethera.Domain;

/// <summary>How the control plane reaches the server.</summary>
public enum ServerTransport
{
    /// <summary>The Go agent dials out over gRPC + mTLS (primary).</summary>
    Agent = 0,

    /// <summary>SSH (bootstrap install and fallback Docker CLI).</summary>
    Ssh = 1,
}

/// <summary>Placement-relevant capabilities of a server (a server may hold several).</summary>
public enum ServerRole
{
    Master = 0,
    Build = 1,
    Storage = 2,
    Ci = 3,
    Worker = 4,
}

/// <summary>Administrative lifecycle, independent from the observed health fields below.</summary>
public enum ServerLifecycle
{
    /// <summary>Created, waiting for the agent to enroll / SSH bootstrap.</summary>
    Pending = 0,
    Active = 1,
    Maintenance = 2,
    Disabled = 3,
}

// Observed status is split into independent axes (spec section 44, ADR 0002). The fifth axis, "control plane
// unavailable", can only be observed client-side and has no column; "application unavailable" lives on Workload.

/// <summary>Whether an agent session exists. <see cref="NotInstalled"/> = SSH-only server.</summary>
public enum AgentStatus
{
    Unknown = 0,
    NotInstalled = 1,
    Connected = 2,
    Unavailable = 3,
}

/// <summary>Mirrors the protocol's DockerStatus.</summary>
public enum DockerStatus
{
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    Unreachable = 3,
    NotInstalled = 4,
    PermissionDenied = 5,
}

/// <summary>Result of the control plane's own probe of the machine (independent of the agent).</summary>
public enum ReachabilityStatus
{
    Unknown = 0,
    Reachable = 1,
    Unreachable = 2,
}

/// <summary>Facts discovered by the agent / SSH probe (spec §55). All optional until first discovery.</summary>
public class ServerFacts
{
    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public string? Kernel { get; set; }
    public string? Architecture { get; set; }
    public string? CpuModel { get; set; }
    public int? CpuCores { get; set; }
    public long? MemoryBytes { get; set; }
    public long? DiskBytes { get; set; }
    public string? DockerVersion { get; set; }
    public DateTimeOffset? DiscoveredAt { get; set; }
}

public class Server : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }

    /// <summary>DNS name or IP address used to reach the server.</summary>
    public required string Host { get; set; }

    public int SshPort { get; set; } = 22;
    public string? SshUser { get; set; }

    /// <summary>Secret holding the SSH private key or password; never stored inline.</summary>
    public Guid? SshCredentialSecretId { get; set; }
    public Secret? SshCredentialSecret { get; set; }

    public ServerTransport Transport { get; set; } = ServerTransport.Agent;
    public List<ServerRole> Roles { get; set; } = [];
    public ServerLifecycle Lifecycle { get; set; } = ServerLifecycle.Pending;

    // Axis 1: server reachability (control-plane probe, TCP connect to ReachabilityProbePort or the SSH port).
    public ReachabilityStatus ReachabilityStatus { get; private set; } = ReachabilityStatus.Unknown;
    public DateTimeOffset? ReachabilityChangedAt { get; private set; }
    public DateTimeOffset? ReachabilityCheckedAt { get; set; }
    public int? ReachabilityProbePort { get; set; }

    // Axis 2: agent session.
    public AgentStatus AgentStatus { get; private set; } = AgentStatus.Unknown;
    public DateTimeOffset? AgentStatusChangedAt { get; private set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    // Axis 3: Docker daemon, as reported by the agent heartbeat (or SSH polling).
    public DockerStatus DockerStatus { get; private set; } = DockerStatus.Unknown;
    public DateTimeOffset? DockerStatusChangedAt { get; private set; }

    /// <summary>Cap on parallel builds on this server (ADR 0004); small VPSs keep the default of 1.</summary>
    public int MaxConcurrentBuilds { get; set; } = 1;

    /// <summary>SSH host key fingerprint pinned on first connect (TOFU).</summary>
    public string? SshHostKeyFingerprint { get; set; }

    /// <summary>Address the world sees; compared with domain DNS records for mismatch warnings.</summary>
    public string? PublicIp { get; set; }

    public string? AgentVersion { get; set; }
    public string? CertFingerprint { get; set; }
    public string? CertSerial { get; set; }
    public DateTimeOffset? CertExpiresAt { get; set; }

    public ServerFacts Facts { get; set; } = new();

    public bool HasRole(ServerRole role) => Roles.Contains(role);

    /// <summary>Each setter returns true when the value changed, so callers can append a <see cref="ResourceEvent"/>.</summary>
    public bool SetReachability(ReachabilityStatus status, DateTimeOffset now)
    {
        ReachabilityCheckedAt = now;
        if (ReachabilityStatus == status) return false;
        ReachabilityStatus = status;
        ReachabilityChangedAt = now;
        return true;
    }

    public bool SetAgentStatus(AgentStatus status, DateTimeOffset now)
    {
        if (AgentStatus == status) return false;
        AgentStatus = status;
        AgentStatusChangedAt = now;
        return true;
    }

    public bool SetDockerStatus(DockerStatus status, DateTimeOffset now)
    {
        if (DockerStatus == status) return false;
        DockerStatus = status;
        DockerStatusChangedAt = now;
        return true;
    }

    /// <summary>Any inbound agent message proves the agent session (and the machine) is alive.</summary>
    public void RecordHeartbeat(DateTimeOffset now, DockerStatus docker = DockerStatus.Unknown)
    {
        LastHeartbeatAt = now;
        SetAgentStatus(AgentStatus.Connected, now);
        SetReachability(ReachabilityStatus.Reachable, now);
        if (docker != DockerStatus.Unknown) SetDockerStatus(docker, now);
    }
}
