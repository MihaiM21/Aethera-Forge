namespace Aethera.Domain;

/// <summary>Origin of a log stream; numeric values equal the protocol's <c>LogSource</c> (stored as smallint).</summary>
public enum LogSource : short
{
    Build = 1,
    Deploy = 2,
    Container = 3,
    Agent = 4,

    /// <summary>Control-plane narration for non-deployment jobs (<c>job:&lt;id&gt;</c> streams).</summary>
    Job = 5,
}

/// <summary>OS stream of a chunk; numeric values equal the protocol's <c>LogStream</c> (stored as smallint).</summary>
public enum LogStream : short
{
    Stdout = 1,
    Stderr = 2,
}

/// <summary>
/// One chunk (not one line) of a persisted log stream (ADR 0004). <see cref="StreamId"/> is e.g. <c>build:&lt;buildId&gt;</c>,
/// <c>deploy:&lt;deploymentId&gt;</c>, <c>job:&lt;jobId&gt;</c> or <c>agent:&lt;serverId&gt;</c>. Replays are made idempotent by
/// the (StreamId, Sequence) primary key and <c>INSERT ... ON CONFLICT DO NOTHING</c>.
/// </summary>
public class LogChunk
{
    public required string StreamId { get; set; }
    public long Sequence { get; set; }

    /// <summary>Time of the first line in the chunk.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public LogSource Source { get; set; } = LogSource.Deploy;
    public LogStream Stream { get; set; } = LogStream.Stdout;
    public required string Data { get; set; }
}
