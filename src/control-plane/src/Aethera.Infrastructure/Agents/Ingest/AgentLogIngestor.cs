using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Ingest;

/// <summary>
/// Ingests <c>LogChunk</c>s from agents (ADR 0002 "Log streaming and flow control"). Build/deploy/agent chunks are stored with
/// <c>INSERT ... ON CONFLICT (stream_id, sequence) DO NOTHING</c> (replay safe) and only then acknowledged with a <c>LogFlowControl</c>;
/// container follow chunks are fanned out to subscribers and acknowledged right after. A chunk for a stream the control plane did not ask
/// for is dropped and never acknowledged: a compromised agent cannot write into another job's log.
/// </summary>
public sealed class AgentLogIngestor(
    NpgsqlDataSource dataSource, ILiveBus bus, IClock clock, IOptions<AgentGatewayOptions> options, ILogger<AgentLogIngestor> logger)
{
    private readonly AgentGatewayOptions _options = options.Value;

    /// <summary>The persisted stream of an agent process's own logs; the process id comes from the agent, so it is reduced to a short safe token.</summary>
    public static string AgentStreamId(Guid serverId, string processId)
    {
        var safe = new string(processId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(64).ToArray());
        return $"agent:{serverId:D}:{(safe.Length == 0 ? "unknown" : safe)}";
    }

    public async Task HandleChunkAsync(AgentSession session, ServerAgentState state, P.LogChunk chunk, CancellationToken cancellationToken)
    {
        if (chunk.Sequence == 0 || chunk.Data.Length > Math.Max(1, _options.LogChunkMaxBytes) * 2)
        {
            logger.LogWarning("Server {ServerId}: dropped a malformed log chunk (sequence {Sequence}, {Bytes} bytes)", session.ServerId, chunk.Sequence, chunk.Data.Length);
            return;
        }

        switch (chunk.Source)
        {
            case P.LogSource.Build or P.LogSource.Deploy:
                if (!state.StreamAliases.TryGetValue(chunk.StreamId, out var persisted))
                {
                    logger.LogWarning("Server {ServerId}: dropped a log chunk for an unknown stream", session.ServerId);
                    return;
                }

                await StoreAsync(session, state, chunk, persisted, cancellationToken);
                break;
            case P.LogSource.Agent:
                await StoreAsync(session, state, chunk, AgentStreamId(session.ServerId, session.Hello.ProcessId), cancellationToken);
                break;
            case P.LogSource.Container:
                FanOut(session, state, chunk);
                break;
            default:
                logger.LogWarning("Server {ServerId}: dropped a log chunk with an unspecified source", session.ServerId);
                break;
        }
    }

    private async Task StoreAsync(AgentSession session, ServerAgentState state, P.LogChunk chunk, string persistedStream, CancellationToken cancellationToken)
    {
        var sequence = (long)chunk.Sequence + state.SequenceOffsets.GetValueOrDefault(chunk.StreamId);
        var text = Decode(chunk.Data.Span);
        if (chunk.DroppedBytes > 0) text = $"[aethera] {chunk.DroppedBytes} bytes were dropped by the agent\n{text}";
        text = state.Redact(text);
        var timestamp = chunk.Timestamp is { } ts && ts.Seconds > 0 ? ts.ToDateTimeOffset() : clock.UtcNow;

        // Size cap per stream: one marker where the log is cut, nothing after it (still acknowledged, so the agent does not stall).
        if (state.StreamBytes.GetValueOrDefault(persistedStream) >= _options.LogMaxBytesPerStream)
        {
            if (!state.TruncatedStreams.TryAdd(persistedStream, 0))
            {
                GrantCredit(session, chunk);
                return;
            }

            text = "... log truncated ...\n";
        }

        // Durable first, acknowledgement second.
        var inserted = await InsertAsync(persistedStream, sequence, timestamp, (short)chunk.Source, (short)(chunk.Stream == P.LogStream.Stderr ? 2 : 1), text, cancellationToken);
        if (inserted)
        {
            state.StreamBytes.AddOrUpdate(persistedStream, text.Length, (_, used) => used + text.Length);
            var line = new LogLine(sequence, timestamp, chunk.Stream == P.LogStream.Stderr ? LogStream.Stderr : LogStream.Stdout, (LogSource)(short)chunk.Source, text);
            await bus.PublishAsync(LiveChannels.Logs(persistedStream), new LogBusMessage(persistedStream, line, null).ToJson());
            foreach (var pending in state.Pending.Values)
                if (pending.StreamIds.Contains(chunk.StreamId)) SafeCallback(pending.Options.OnLog, new LogEntry(persistedStream, line.Sequence, timestamp, line.Source, line.Stream, text, (long)chunk.DroppedBytes));
        }

        if (chunk.Eof)
            await bus.PublishAsync(LiveChannels.Logs(persistedStream), new LogBusMessage(persistedStream, null, chunk.EofReason.Length == 0 ? "succeeded" : "failed").ToJson());
        GrantCredit(session, chunk);
    }

    private void FanOut(AgentSession session, ServerAgentState state, P.LogChunk chunk)
    {
        if (!state.LogSubscribers.TryGetValue(chunk.StreamId, out var subscribers) || subscribers.Count == 0)
        {
            // Nobody listens any more: pause the stream (window 0) once, the agent keeps its position.
            if (session.PausedStreams.Add(chunk.StreamId))
                session.Send(new P.ControlMessage { LogFlowControl = new P.LogFlowControl { StreamId = chunk.StreamId, AckedSequence = chunk.Sequence, WindowBytes = 0 } });
            return;
        }

        var text = state.Redact(Decode(chunk.Data.Span));
        var timestamp = chunk.Timestamp is { } ts && ts.Seconds > 0 ? ts.ToDateTimeOffset() : clock.UtcNow;
        subscribers.Publish(new LogEntry(chunk.StreamId, (long)chunk.Sequence, timestamp, LogSource.Container,
            chunk.Stream == P.LogStream.Stderr ? LogStream.Stderr : LogStream.Stdout, text, (long)chunk.DroppedBytes, chunk.Eof, chunk.Eof ? chunk.EofReason : null));
        GrantCredit(session, chunk);
    }

    /// <summary>Roughly every half window (and at the end of a stream) the agent is told how far we got and gets the window back.</summary>
    private void GrantCredit(AgentSession session, P.LogChunk chunk)
    {
        var window = (ulong)Math.Max(1, _options.LogInitialWindowBytes);
        session.UnackedLogBytes.TryGetValue(chunk.StreamId, out var unacked);
        unacked += chunk.Data.Length;
        if (chunk.Eof || (ulong)unacked * 2 >= window)
        {
            session.Send(new P.ControlMessage { LogFlowControl = new P.LogFlowControl { StreamId = chunk.StreamId, AckedSequence = chunk.Sequence, WindowBytes = window } });
            unacked = 0;
        }

        if (chunk.Eof) session.UnackedLogBytes.Remove(chunk.StreamId);
        else session.UnackedLogBytes[chunk.StreamId] = unacked;
    }

    /// <summary>
    /// The highest sequence of the agent's stream that is durably stored (0 = none), for the resume <c>LogFlowControl</c> after a reconnect.
    /// </summary>
    public async Task<long> LastDurableSequenceAsync(ServerAgentState state, string agentStreamId, CancellationToken cancellationToken)
    {
        var candidates = state.StreamAliases.TryGetValue(agentStreamId, out var alias)
            ? new[] { alias }
            : new[] { $"build:{agentStreamId}", $"deploy:{agentStreamId}" };
        var offset = state.SequenceOffsets.GetValueOrDefault(agentStreamId);
        long best = 0;
        foreach (var stream in candidates)
        {
            await using var command = dataSource.CreateCommand("SELECT COALESCE(max(sequence), 0) FROM log_chunks WHERE stream_id = $1");
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = stream });
            var max = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            best = Math.Max(best, max - offset);
        }

        return Math.Max(0, best);
    }

    /// <summary>The highest stored sequence of a persisted stream (for the sequence offset of a re-delivered command).</summary>
    public async Task<long> MaxStoredSequenceAsync(string persistedStreamId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT COALESCE(max(sequence), 0) FROM log_chunks WHERE stream_id = $1");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = persistedStreamId });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<bool> InsertAsync(string stream, long sequence, DateTimeOffset timestamp, short source, short osStream, string text, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var command = dataSource.CreateCommand(
                    "INSERT INTO log_chunks (stream_id, sequence, ts, source, stream, data) VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT (stream_id, sequence) DO NOTHING");
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = stream });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sequence });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = timestamp.UtcDateTime });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = source });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = osStream });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = text });
                return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            }
            catch (Exception ex) when (attempt < 4 && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Storing a log chunk failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>UTF-8 is not guaranteed; invalid sequences become U+FFFD and NUL (rejected by PostgreSQL text) is replaced too.</summary>
    internal static string Decode(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(data).Replace('\0', '�');

    private void SafeCallback(Action<LogEntry>? callback, LogEntry entry)
    {
        try { callback?.Invoke(entry); }
        catch (Exception ex) { logger.LogWarning(ex, "A log callback threw"); }
    }
}
