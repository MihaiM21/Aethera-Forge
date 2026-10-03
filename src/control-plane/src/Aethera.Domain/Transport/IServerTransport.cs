namespace Aethera.Domain.Transport;

/// <summary>How the control plane reaches a server (ADR 0002).</summary>
public enum TransportKind
{
    /// <summary>The Go agent dials out over gRPC + mTLS.</summary>
    Agent = 0,

    /// <summary>SSH bootstrap / allowlisted Docker CLI fallback (WP2.3).</summary>
    Ssh = 1,
}

/// <summary>What a transport can do. A command whose capability the transport lacks fails fast with <c>transport.unsupported</c>.</summary>
[Flags]
public enum TransportCapabilities
{
    None = 0,
    ContainerOps = 1,
    ImageOps = 2,
    VolumeNetworkOps = 4,
    Compose = 8,
    Builds = 16,
    LogFollow = 32,
    PushEvents = 64,
    PushMetrics = 128,
    HealthProbe = 256,
    SelfUpdate = 512,

    /// <summary>Everything the agent transport supports.</summary>
    AllAgent = ContainerOps | ImageOps | VolumeNetworkOps | Compose | Builds | LogFollow | PushEvents | PushMetrics | HealthProbe | SelfUpdate,
}

/// <summary>Whether a transport can currently carry commands for a server, and why not.</summary>
public sealed record TransportStatus(TransportKind Kind, bool Available, string? Reason = null)
{
    public static TransportStatus Up(TransportKind kind) => new(kind, true);

    public static TransportStatus Down(TransportKind kind, string reason) => new(kind, false, reason);
}

/// <summary>
/// A typed command of the allowlist (domain mirror of the protocol's <c>Command.request</c> arms, ADR 0002). There is deliberately no
/// generic "run this shell string" command.
/// </summary>
/// <typeparam name="TResult">The typed success payload.</typeparam>
public interface IServerCommand<TResult>
{
    /// <summary>Stable dotted name for audit and logs, e.g. <c>container.start</c>.</summary>
    string Name { get; }

    /// <summary>The transport capability the command needs.</summary>
    TransportCapabilities Required { get; }

    /// <summary>Per-type deadline the job engine uses when the caller gives none (ADR 0002 "Timeouts").</summary>
    TimeSpan DefaultTimeout { get; }

    /// <summary>True for list/inspect style commands that change nothing on the host.</summary>
    bool ReadOnly { get; }
}

/// <summary>Convenience base for the command records.</summary>
public abstract record ServerCommand<TResult>(string Name, TransportCapabilities Required, TimeSpan DefaultTimeout, bool ReadOnly = false)
    : IServerCommand<TResult>;

/// <summary>Progress notification of a long-running command (build stage, image pull, compose service).</summary>
public sealed record CommandProgressInfo(string Step, string Message, int Percent, DateTimeOffset At);

/// <summary>Per-call settings of <see cref="IServerTransport.ExecuteAsync{TCommand, TResult}"/>.</summary>
public sealed class CommandOptions
{
    /// <summary>
    /// Stable across redelivery of one logical operation, <c>"&lt;job_id&gt;:&lt;step&gt;:&lt;retry_no&gt;"</c> (ADR 0002 "Idempotency").
    /// Redelivery after an unknown outcome reuses it; a deliberate retry after a reported failure uses a new one.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Absolute deadline. Null = now + the command's <see cref="IServerCommand{TResult}.DefaultTimeout"/>.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>The job the command belongs to (correlation, cancellation bookkeeping).</summary>
    public Guid? JobId { get; init; }

    /// <summary>Organization for the audit event. Null = taken from the server.</summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>The user on whose behalf the command runs (audit), if any.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>W3C traceparent forwarded to the agent.</summary>
    public string? TraceParent { get; init; }

    /// <summary>
    /// Persisted log stream (<c>build:&lt;id&gt;</c>, <c>deploy:&lt;id&gt;</c>) the agent's BUILD/DEPLOY chunks of this command are stored under.
    /// Null = <c>build:&lt;build id&gt;</c> for builds and <c>deploy:&lt;command id&gt;</c> otherwise.
    /// </summary>
    public string? LogStreamId { get; init; }

    /// <summary>Called for every <see cref="CommandProgressInfo"/> the agent reports.</summary>
    public Action<CommandProgressInfo>? OnProgress { get; init; }

    /// <summary>Called for every log entry of the command, after it was stored.</summary>
    public Action<LogEntry>? OnLog { get; init; }

    /// <summary>Time the agent gets to stop gracefully when the call is cancelled (ADR 0002 "Cancellation").</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(15);

    public static CommandOptions For(string idempotencyKey, Guid? jobId = null) => new() { IdempotencyKey = idempotencyKey, JobId = jobId };
}

public enum CommandStatus
{
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
    TimedOut = 4,
}

/// <summary>Stable machine-readable failure reason (mirrors the protocol's <c>ErrorCode</c>).</summary>
public enum CommandErrorCode
{
    None = 0,
    InvalidArgument = 1,
    NotFound = 2,
    AlreadyExists = 3,
    Conflict = 4,
    PermissionDenied = 5,
    PolicyViolation = 6,
    Unsupported = 7,
    DockerUnavailable = 8,
    RegistryAuthFailed = 9,
    ImagePullFailed = 10,
    GitFailed = 11,
    BuildFailed = 12,
    PortConflict = 13,
    OutOfDisk = 14,
    OutOfMemory = 15,
    HealthCheckFailed = 16,
    Timeout = 17,
    Cancelled = 18,
    ChecksumMismatch = 19,
    Internal = 20,
}

/// <summary>The terminal report of one command.</summary>
public sealed record CommandOutcome<TResult>(
    CommandStatus Status,
    TResult? Result,
    CommandErrorCode ErrorCode,
    string? ErrorMessage,
    int ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    bool Replayed)
{
    public bool Succeeded => Status == CommandStatus.Succeeded;

    /// <summary>The result, or a <see cref="ServerTransportException"/> describing the failure.</summary>
    public TResult EnsureSucceeded()
    {
        if (Status == CommandStatus.Succeeded && Result is not null) return Result;
        var (code, title) = Status switch
        {
            CommandStatus.Cancelled => (TransportErrors.CommandCancelled, "The command was cancelled."),
            CommandStatus.TimedOut => (TransportErrors.CommandTimedOut, "The command timed out on the server."),
            _ => (TransportErrors.CommandFailed, "The command failed on the server."),
        };
        throw new ServerTransportException(code, ErrorMessage is { Length: > 0 } ? $"{title} {ErrorMessage}" : title, this.ErrorCode);
    }
}

/// <summary>Problem codes the transport layer raises (stable; the API maps them to ProblemDetails, the job engine to failure codes).</summary>
public static class TransportErrors
{
    public const string AgentUnavailable = "server.agent_unavailable";
    public const string Unreachable = "server.unreachable";
    public const string Unsupported = "transport.unsupported";
    public const string AckTimeout = "transport.ack_timeout";
    public const string CommandRejected = "transport.command_rejected";
    public const string CommandFailed = "transport.command_failed";
    public const string CommandCancelled = "transport.command_cancelled";
    public const string CommandTimedOut = "transport.command_timed_out";
    public const string Superseded = "transport.superseded";
    public const string AgentBusy = "transport.agent_busy";
}

/// <summary>
/// A failure of the transport itself (as opposed to a command that ran and failed, which is a <see cref="CommandOutcome{TResult}"/>).
/// <see cref="Transient"/> failures are worth retrying on the next session.
/// </summary>
public sealed class ServerTransportException(string code, string message, CommandErrorCode agentError = CommandErrorCode.None, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;

    public CommandErrorCode AgentError { get; } = agentError;

    public bool Transient => Code is TransportErrors.AgentUnavailable or TransportErrors.Unreachable or TransportErrors.AckTimeout or TransportErrors.Superseded or TransportErrors.AgentBusy;
}

/// <summary>One persisted/streamed log chunk, as delivered to <see cref="IServerTransport.StreamLogsAsync"/> subscribers.</summary>
public sealed record LogEntry(
    string StreamId, long Sequence, DateTimeOffset Timestamp, LogSource Source, LogStream Stream, string Text, long DroppedBytes = 0, bool Eof = false, string? EofReason = null);

/// <summary>Request to follow a container's logs (the transport-neutral <c>LogStreamStart</c>).</summary>
public sealed record LogStreamRequest(string Container, bool Follow, DateTimeOffset? Since, int Tail, bool IncludeStdout = true, bool IncludeStderr = true);

public enum ServerEventKind
{
    ContainerStarted,
    ContainerStopped,
    ContainerDied,
    ContainerOom,
    ContainerRestarting,
    ContainerHealthChanged,
    ContainerRemoved,
    DockerDaemonUp,
    DockerDaemonDown,
    DiskPressure,
    AgentConnected,
    AgentDisconnected,
}

/// <summary>An edge-triggered happening on a server (agent event notices, session changes).</summary>
public sealed record ServerEvent(
    Guid ServerId,
    ServerEventKind Kind,
    DateTimeOffset OccurredAt,
    string? ContainerId = null,
    string? ContainerName = null,
    IReadOnlyDictionary<string, string>? Labels = null,
    string? Message = null);

/// <summary>
/// The control plane's only way to act on a server (ADR 0002). The deployment engine never talks gRPC or SSH directly, so it and its
/// tests never depend on generated protocol code or on a particular transport.
/// </summary>
public interface IServerTransport
{
    TransportKind Kind { get; }

    TransportCapabilities Capabilities { get; }

    ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>Typed command in, typed result out.</summary>
    /// <exception cref="ServerTransportException">The command could not be delivered or acknowledged (agent unavailable, rejected, ack timeout).</exception>
    Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        Guid serverId, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : IServerCommand<TResult>;

    IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, CancellationToken cancellationToken);
}
