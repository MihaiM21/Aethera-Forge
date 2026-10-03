using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Aethera.Infrastructure.Jobs;

/// <summary>
/// Write side of the log pipeline (ADR 0004 section 6): sinks cut text into chunks, the ingestor stores them in batches
/// (<c>INSERT ... ON CONFLICT (stream_id, sequence) DO NOTHING</c>) and only <b>after the commit</b> publishes them on the
/// <see cref="ILiveBus"/>, so anything a client has seen is recoverable from Postgres.
/// </summary>
public sealed class LogIngestor : BackgroundService, ILogSinkFactory
{
    internal const int MaxChunkBytes = 32 * 1024;
    internal const int MaxPieceChars = 8 * 1024;

    private readonly IServiceProvider _services;
    private NpgsqlDataSource _dataSource => _services.GetRequiredService<NpgsqlDataSource>();
    private readonly ILiveBus _bus;
    private readonly IClock _clock;
    private readonly JobsOptions _options;
    private readonly AetheraMetrics _metrics;
    private readonly ILogger<LogIngestor> _logger;
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<StreamLogSink, byte> _dirty = new();

    public LogIngestor(
        IServiceProvider services, ILiveBus bus, IClock clock, IOptions<JobsOptions> options, AetheraMetrics metrics, ILogger<LogIngestor> logger)
    {
        _services = services;
        _bus = bus;
        _clock = clock;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    private abstract record Item;

    private sealed record ChunkItem(LogChunk Chunk) : Item;

    private sealed record Barrier(TaskCompletionSource Done) : Item;

    private sealed record EofItem(string StreamId, string Reason) : Item;

    public async Task<ILogSink> CreateAsync(string streamId, ISecretRedactor? redactor = null, LogSource source = LogSource.Job, CancellationToken cancellationToken = default)
    {
        long last = 0, bytes = 0;
        await using (var command = _dataSource.CreateCommand(
            "SELECT COALESCE(MAX(sequence), 0), COALESCE(SUM(octet_length(data)), 0) FROM log_chunks WHERE stream_id = $1"))
        {
            command.Parameters.AddWithValue(streamId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                last = reader.GetInt64(0);
                bytes = reader.GetInt64(1);
            }
        }

        return new StreamLogSink(this, streamId, redactor ?? new SecretRedactor(), source, last + 1, bytes, _options.LogMaxBytesPerStream, _clock);
    }

    /// <summary>Publishes the end of a stream (<c>succeeded</c>, <c>failed</c>, <c>cancelled</c>) after everything queued before it was stored.</summary>
    public void CompleteStream(string streamId, string reason) => _queue.Writer.TryWrite(new EofItem(streamId, reason));

    internal void Enqueue(LogChunk chunk) => _queue.Writer.TryWrite(new ChunkItem(chunk));

    internal Task EnqueueBarrierAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(new Barrier(done));
        return done.Task;
    }

    internal void MarkDirty(StreamLogSink sink) => _dirty[sink] = 0;

    internal void MarkClean(StreamLogSink sink) => _dirty.TryRemove(sink, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (AetheraHost.IsOpenApiGeneration) return; // build-time OpenAPI generation: nothing writes logs

        try { _ = _dataSource; }
        catch (InvalidOperationException)
        {
            return; // no database configured (OpenAPI generation): nothing can be written
        }

        var sealer = SealLoopAsync(stoppingToken);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                await SafeDrainAsync();
        }
        catch (OperationCanceledException)
        {
            // stopping: drain what is left below
        }

        // Shutdown: seal everything pending and write it, so a graceful stop loses no log lines.
        foreach (var sink in _dirty.Keys) sink.Seal();
        while (_queue.Reader.TryPeek(out _))
            await SafeDrainAsync();
        try { await sealer; } catch (OperationCanceledException) { }
    }

    // An unexpected failure must not stop the host (the default for a crashed BackgroundService): log it and carry on.
    private async Task SafeDrainAsync()
    {
        try { await DrainAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "The log ingestor failed to process a batch"); }
    }

    private async Task SealLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, _options.LogFlushMilliseconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            foreach (var sink in _dirty.Keys) sink.Seal();
    }

    private async Task DrainAsync()
    {
        var batch = new List<Item>();
        var chunks = 0;
        while (chunks < _options.LogBatchSize && _queue.Reader.TryRead(out var item))
        {
            batch.Add(item);
            if (item is ChunkItem) chunks++;
            else if (item is Barrier or EofItem) break; // flush promptly: somebody waits for it
        }

        if (batch.Count == 0) return;
        var stored = await StoreAsync(batch.OfType<ChunkItem>().Select(i => i.Chunk).ToList());

        // Whoever waits (FlushAsync) is released as soon as the rows are committed: a slow bus must not hold up a job's completion.
        foreach (var item in batch)
            if (item is Barrier barrier) barrier.Done.TrySetResult();

        if (!stored) return;
        foreach (var item in batch)
        {
            switch (item)
            {
                case ChunkItem chunk:
                    await _bus.PublishAsync(LiveChannels.Logs(chunk.Chunk.StreamId),
                        new LogBusMessage(chunk.Chunk.StreamId, LogLine.From(chunk.Chunk), null).ToJson());
                    break;
                case EofItem eof:
                    await _bus.PublishAsync(LiveChannels.Logs(eof.StreamId), new LogBusMessage(eof.StreamId, null, eof.Reason).ToJson());
                    break;
            }
        }
    }

    private async Task<bool> StoreAsync(List<LogChunk> chunks)
    {
        if (chunks.Count == 0) return true;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var command = _dataSource.CreateCommand(
                    "INSERT INTO log_chunks (stream_id, sequence, ts, source, stream, data) "
                    + "SELECT * FROM unnest($1, $2, $3, $4, $5, $6) ON CONFLICT (stream_id, sequence) DO NOTHING");
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = chunks.Select(c => c.StreamId).ToArray() });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = chunks.Select(c => c.Sequence).ToArray() });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, Value = chunks.Select(c => c.Timestamp.UtcDateTime).ToArray() });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint, Value = chunks.Select(c => (short)c.Source).ToArray() });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint, Value = chunks.Select(c => (short)c.Stream).ToArray() });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = chunks.Select(c => c.Data).ToArray() });
                await command.ExecuteNonQueryAsync();
                _metrics.LogChunksWritten.Inc(chunks.Count);
                return true;
            }
            catch (Exception ex) when (attempt < 5)
            {
                _logger.LogWarning(ex, "Storing {Count} log chunks failed (attempt {Attempt}); retrying", chunks.Count, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
            catch (Exception ex)
            {
                _metrics.LogPublishFailures.Inc();
                _logger.LogError(ex, "Giving up storing {Count} log chunks", chunks.Count);
                return false;
            }
        }
    }
}

/// <summary>Cuts the lines written for one stream into chunks with contiguous sequence numbers; redacts secrets before anything leaves it.</summary>
internal sealed class StreamLogSink : ILogSink
{
    private const string TruncatedMarker = "... log truncated ...\n";

    private readonly LogIngestor _ingestor;
    private readonly ISecretRedactor _redactor;
    private readonly LogSource _defaultSource;
    private readonly long _maxBytes;
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly StringBuilder _buffer = new();
    private long _next;
    private long _bytes;
    private int _bufferBytes;
    private bool _truncated;
    private LogStream _bufferStream;
    private LogSource _bufferSource;
    private DateTimeOffset _bufferTimestamp;

    public StreamLogSink(LogIngestor ingestor, string streamId, ISecretRedactor redactor, LogSource source, long nextSequence, long existingBytes, long maxBytes, IClock clock)
    {
        _ingestor = ingestor;
        StreamId = streamId;
        _redactor = redactor;
        _defaultSource = source;
        _next = nextSequence;
        _bytes = existingBytes;
        _maxBytes = maxBytes;
        _clock = clock;
        _truncated = existingBytes >= maxBytes;
    }

    public string StreamId { get; }

    public ValueTask WriteAsync(LogStream stream, string line, CancellationToken cancellationToken = default)
    {
        Append(stream, _defaultSource, line);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteSystemAsync(string line, CancellationToken cancellationToken = default)
    {
        Append(LogStream.Stdout, _defaultSource, "[aethera] " + line);
        return ValueTask.CompletedTask;
    }

    private void Append(LogStream stream, LogSource source, string text)
    {
        text = _redactor.Redact(text ?? "");
        if (!text.EndsWith('\n')) text += "\n";

        lock (_gate)
        {
            if (_truncated) return;
            foreach (var piece in Pieces(text))
            {
                var size = Encoding.UTF8.GetByteCount(piece);
                if (_bytes + size > _maxBytes)
                {
                    Seal();
                    _truncated = true;
                    Emit(stream, source, TruncatedMarker);
                    return;
                }

                if (_buffer.Length > 0 && (stream != _bufferStream || source != _bufferSource)) Seal();
                if (_buffer.Length == 0)
                {
                    _bufferStream = stream;
                    _bufferSource = source;
                    _bufferTimestamp = _clock.UtcNow;
                }

                _buffer.Append(piece);
                _bufferBytes += size;
                _bytes += size;
                if (_bufferBytes >= LogIngestor.MaxChunkBytes) Seal();
            }

            if (_buffer.Length > 0) _ingestor.MarkDirty(this);
        }
    }

    // A single line may be huge; keep every piece small enough that a chunk stays below the 64 KiB of ADR 0004.
    private static IEnumerable<string> Pieces(string text)
    {
        if (text.Length <= LogIngestor.MaxPieceChars)
        {
            yield return text;
            yield break;
        }

        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(LogIngestor.MaxPieceChars, text.Length - offset);
            if (length == LogIngestor.MaxPieceChars && offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            yield return text.Substring(offset, length);
            offset += length;
        }
    }

    /// <summary>Turns the pending text into a chunk. Called under the lock by writers and by the ingestor's timer.</summary>
    internal void Seal()
    {
        lock (_gate)
        {
            if (_buffer.Length == 0) return;
            Emit(_bufferStream, _bufferSource, _buffer.ToString(), _bufferTimestamp);
            _buffer.Clear();
            _bufferBytes = 0;
            _ingestor.MarkClean(this);
        }
    }

    private void Emit(LogStream stream, LogSource source, string data, DateTimeOffset? timestamp = null) =>
        _ingestor.Enqueue(new LogChunk
        {
            StreamId = StreamId,
            Sequence = _next++,
            Timestamp = timestamp ?? _clock.UtcNow,
            Source = source,
            Stream = stream,
            Data = data,
        });

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Seal();
        await _ingestor.EnqueueBarrierAsync().WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try { await FlushAsync(); }
        catch (Exception) { /* best effort on disposal */ }
    }
}

/// <summary>Read side: stored chunks of a stream, for REST pages, downloads and the SignalR replay.</summary>
public sealed class LogReader(IServiceScopeFactory scopes)
{
    /// <summary>Chunks with <c>sequence &gt;= fromSequence</c> in order, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<LogLine>> ReadAsync(string streamId, long fromSequence, int limit, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var chunks = await db.LogChunks.AsNoTracking()
            .Where(c => c.StreamId == streamId && c.Sequence >= fromSequence)
            .OrderBy(c => c.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return chunks.Select(LogLine.From).ToList();
    }

    /// <summary>All stored chunks from <paramref name="fromSequence"/>, fetched in pages so a huge log is never held in memory.</summary>
    public async IAsyncEnumerable<LogLine> StreamAsync(
        string streamId, long fromSequence, int pageSize = 200, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var page = await ReadAsync(streamId, fromSequence, pageSize, cancellationToken);
            foreach (var line in page) yield return line;
            if (page.Count < pageSize) yield break;
            fromSequence = page[^1].Sequence + 1;
        }
    }
}
