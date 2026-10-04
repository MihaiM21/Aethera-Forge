using Aethera.Infrastructure.Ssh.Docker;

namespace Aethera.Infrastructure.Ssh;

/// <summary>Where the agent binary for one CPU architecture comes from: a URL with its SHA-256, or a local path (development).</summary>
public sealed class AgentBinarySource
{
    /// <summary><c>https</c> URL the server downloads the binary from (it needs <c>curl</c> or <c>wget</c>).</summary>
    public string? Url { get; set; }

    /// <summary>Lower-case hex SHA-256 of the binary. Required for <see cref="Url"/>; optional for <see cref="LocalPath"/> (then only logged).</summary>
    public string? Sha256 { get; set; }

    /// <summary>A file on the control plane host that is uploaded over SSH (development, air-gapped installs).</summary>
    public string? LocalPath { get; set; }
}

/// <summary>Agent binary configuration for the SSH bootstrap, bound from <c>Aethera:Ssh:Agent</c>.</summary>
public sealed class SshAgentOptions
{
    /// <summary>Binaries by architecture: keys <c>linux-amd64</c> and <c>linux-arm64</c> (<c>uname -m</c> x86_64 / aarch64).</summary>
    public Dictionary<string, AgentBinarySource> Binaries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Informational version the install reports; the running agent reports its own on connect.</summary>
    public string? Version { get; set; }
}

/// <summary>
/// Settings of the SSH transport, bound from <c>Aethera:Ssh</c> (ADR 0002 "SshTransport"). Durations are seconds (fractions allowed so
/// tests stay fast).
/// </summary>
public sealed class SshOptions
{
    public const string Section = "Aethera:Ssh";

    /// <summary>Turns the background polling service on or off. Off under the <c>Testing</c> environment unless set explicitly.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time allowed to connect and authenticate.</summary>
    public double ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>SSH keep-alive interval of the multiplexed connection (detects dead sessions).</summary>
    public double KeepAliveSeconds { get; set; } = 15;

    /// <summary>Commands running at once on one server's connection (each uses its own SSH channel; sshd's default <c>MaxSessions</c> is 10).</summary>
    public int MaxConcurrentChannelsPerServer { get; set; } = 4;

    /// <summary>A connection that was not used for this long is closed.</summary>
    public double IdleDisconnectSeconds { get; set; } = 300;

    /// <summary>After a failed connect the server is considered down for this long (avoids stacking up timeouts).</summary>
    public double ConnectFailureBackoffSeconds { get; set; } = 10;

    /// <summary>How long a loaded server/credential snapshot is reused (changes through the API invalidate it at once).</summary>
    public double AccessCacheSeconds { get; set; } = 5;

    /// <summary>Metrics polling cadence (ADR 0002: 30 s).</summary>
    public double MetricsPollSeconds { get; set; } = 30;

    /// <summary>Total time one poll of one server may take.</summary>
    public double MetricsPollTimeoutSeconds { get; set; } = 25;

    /// <summary>Servers polled at once.</summary>
    public int MetricsPollParallelism { get; set; } = 4;

    /// <summary>Directory below which Compose projects are stored on the server.</summary>
    public string ProjectsDirectory { get; set; } = "/var/lib/aethera/projects";

    /// <summary>Host directories bind mounts may use (the agent's default: only <c>/var/lib/aethera</c>).</summary>
    public List<string> AllowedBindPrefixes { get; set; } = ["/var/lib/aethera"];

    /// <summary>Allowed private-key/password auth only; set to permit <c>http://</c> agent downloads (the SHA-256 is still verified).</summary>
    public bool AllowInsecureAgentDownload { get; set; }

    /// <summary>Seconds the bootstrap waits for the freshly installed agent's session.</summary>
    public double BootstrapSessionWaitSeconds { get; set; } = 120;

    /// <summary>Lifetime of the join token the bootstrap issues, minutes.</summary>
    public double BootstrapJoinTokenMinutes { get; set; } = 15;

    public SshAgentOptions Agent { get; set; } = new();

    public SshDockerPolicy ToDockerPolicy() => new() { AllowedBindPrefixes = AllowedBindPrefixes, ProjectsDirectory = ProjectsDirectory };
}
