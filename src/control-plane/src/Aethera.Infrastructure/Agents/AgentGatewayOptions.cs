namespace Aethera.Infrastructure.Agents;

/// <summary>
/// Settings of the agent gateway, bound from <c>Aethera:Agents</c> (ADR 0002). Durations are seconds (fractions allowed, which keeps
/// tests fast), except the certificate lifetimes, which are days.
/// </summary>
public sealed class AgentGatewayOptions
{
    public const string Section = "Aethera:Agents";

    /// <summary>Turns the dedicated gRPC listener (and everything that depends on a running agent stream) on or off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Port of the dedicated mTLS gRPC listener (ADR 0002: default 9443). It is separate from the HTTP API listener.</summary>
    public int GrpcPort { get; set; } = 9443;

    /// <summary>Address the gRPC listener binds to.</summary>
    public string GrpcBindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// <c>host:port</c> that agents dial and that the server certificate of the listener names (SAN). Defaults to <c>localhost:&lt;GrpcPort&gt;</c>,
    /// which is only right for a single-host setup: set it to the public hostname or IP in production.
    /// </summary>
    public string? PublicEndpoint { get; set; }

    /// <summary>URL of <c>install-agent.sh</c> used in the generated install command. Null = <c>&lt;API origin&gt;/install-agent.sh</c>.</summary>
    public string? InstallScriptUrl { get; set; }

    /// <summary>Common name of the internal CA certificate.</summary>
    public string CaName { get; set; } = "Aethera Internal CA";

    /// <summary>Validity of agent leaf certificates, days (ADR: 30).</summary>
    public double AgentCertificateDays { get; set; } = 30;

    /// <summary>How long before expiry the agent should renew, days (ADR: 10). Also when a <c>CertRotationHint</c> is sent.</summary>
    public double RenewBeforeDays { get; set; } = 10;

    /// <summary>Validity of the listener's own server certificate, days (ADR: 90, renewed in place).</summary>
    public double ServerCertificateDays { get; set; } = 90;

    /// <summary>Default join token lifetime, minutes (ADR: 1 hour). The maximum is 24 hours.</summary>
    public double JoinTokenDefaultMinutes { get; set; } = 60;

    /// <summary><c>Enroll</c> attempts allowed per source IP and window (ADR: 10 per minute).</summary>
    public int EnrollRateLimitPermits { get; set; } = 10;

    public double EnrollRateLimitWindowSeconds { get; set; } = 60;

    // ---- Welcome (server-driven agent configuration) ------------------------------------------------------------------------------

    public double HeartbeatSeconds { get; set; } = 15;

    public double MetricsSeconds { get; set; } = 10;

    /// <summary>How often the agent re-sends a full DiscoveryReport unprompted (0 = only at connect and on change).</summary>
    public double DiscoverySeconds { get; set; } = 3600;

    public int MaxConcurrentCommands { get; set; } = 8;

    public int LogChunkMaxBytes { get; set; } = 32 * 1024;

    public long LogInitialWindowBytes { get; set; } = 256 * 1024;

    /// <summary>Oldest agent version still accepted; older agents (and, above 0.0.0, unparseable ones such as "dev") get <c>Disconnect(UPGRADE_REQUIRED)</c>. The default accepts every agent.</summary>
    public string MinAgentVersion { get; set; } = "0.0.0";

    /// <summary>Oldest wire protocol revision accepted.</summary>
    public int MinProtocolVersion { get; set; } = 1;

    // ---- liveness and command handling --------------------------------------------------------------------------------------------

    /// <summary>The agent is unavailable after this many missed heartbeats (ADR: 3, i.e. 45 s with the default interval).</summary>
    public int HeartbeatMissLimit { get; set; } = 3;

    /// <summary>A <c>Ping</c> is sent after this long without any inbound message.</summary>
    public double PingSeconds { get; set; } = 60;

    /// <summary>The first message of a stream must arrive within this time.</summary>
    public double HelloTimeoutSeconds { get; set; } = 15;

    /// <summary>No <c>CommandAck</c> within this time fails the dispatch (ADR: 10 s).</summary>
    public double AckTimeoutSeconds { get; set; } = 10;

    /// <summary>The gateway gives up waiting for a result at <c>deadline + this</c> (ADR: 30 s).</summary>
    public double DeadlineGraceSeconds { get; set; } = 30;

    /// <summary>Every dispatched command writes an audit event. Set false to skip list/inspect/probe style commands (high volume).</summary>
    public bool AuditReadOnlyCommands { get; set; } = true;

    /// <summary>Heartbeats are persisted (<c>servers.last_heartbeat_at</c>) at most this often; status changes are always written at once.</summary>
    public double HeartbeatPersistSeconds { get; set; } = 30;

    // ---- reachability probe (the "server unavailable" axis) -------------------------------------------------------------------------

    public double ReachabilityProbeSeconds { get; set; } = 30;

    /// <summary>Consecutive failed probes before the server is reported unreachable (ADR: debounced, 2).</summary>
    public int ReachabilityFailureThreshold { get; set; } = 2;

    public double ReachabilityConnectTimeoutSeconds { get; set; } = 3;

    /// <summary>Retention and downsampling of <c>metric_samples</c>.</summary>
    public MetricsRetentionOptions Metrics { get; set; } = new();

    public TimeSpan AgentCertificateLifetime => TimeSpan.FromDays(AgentCertificateDays);

    public TimeSpan RenewBefore => TimeSpan.FromDays(RenewBeforeDays);

    public TimeSpan Heartbeat => TimeSpan.FromSeconds(Math.Max(0.02, HeartbeatSeconds));

    public TimeSpan Ack => TimeSpan.FromSeconds(Math.Max(0.02, AckTimeoutSeconds));

    public TimeSpan DeadlineGrace => TimeSpan.FromSeconds(Math.Max(0, DeadlineGraceSeconds));

    /// <summary>Time without any inbound message after which the stream is closed with <c>HEARTBEAT_TIMEOUT</c>.</summary>
    public TimeSpan HeartbeatTimeout => TimeSpan.FromTicks(Heartbeat.Ticks * Math.Max(1, HeartbeatMissLimit));

    /// <summary>The endpoint agents dial, never empty.</summary>
    public string EffectivePublicEndpoint => string.IsNullOrWhiteSpace(PublicEndpoint) ? $"localhost:{GrpcPort}" : PublicEndpoint.Trim();
}

/// <summary>
/// Metric retention (ADR 0004 appendix in <c>docs/architecture/0002-agent-communication.md</c>): raw samples (every 10 s) are kept
/// <see cref="RawHours"/>, then rolled up to 5-minute averages kept <see cref="FiveMinuteDays"/>, then to hourly averages kept
/// <see cref="HourlyDays"/>, then deleted. Rollup boundaries are aligned to the bucket size so a bucket is never split.
/// </summary>
public sealed class MetricsRetentionOptions
{
    public double RawHours { get; set; } = 24;

    public double FiveMinuteDays { get; set; } = 14;

    public double HourlyDays { get; set; } = 365;

    /// <summary>How often the roll-up/retention pass runs.</summary>
    public double IntervalSeconds { get; set; } = 300;

    /// <summary>Turns the background pass off (tests call the service directly).</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan Raw => TimeSpan.FromHours(RawHours);

    public TimeSpan FiveMinute => TimeSpan.FromDays(FiveMinuteDays);

    public TimeSpan Hourly => TimeSpan.FromDays(HourlyDays);
}
