using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aethera.Domain;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Aethera.Infrastructure.Jobs;

/// <summary>
/// Publish/subscribe between API instances (ADR 0004 section 6): Redis pub/sub when <c>ConnectionStrings:Redis</c> is set, an
/// in-process broadcaster otherwise. It is fire-and-forget by design: Postgres is the source of truth and readers resync from it.
/// </summary>
public interface ILiveBus
{
    /// <summary>True when messages cross process boundaries (Redis). False: this node only.</summary>
    bool IsDistributed { get; }

    /// <summary>Best effort; failures are logged, never thrown.</summary>
    Task PublishAsync(string channel, string message);

    /// <summary>Calls <paramref name="handler"/> for every message on <paramref name="channel"/> until the result is disposed.</summary>
    Task<IAsyncDisposable> SubscribeAsync(string channel, Action<string> handler);
}

public static class LiveChannels
{
    public const string JobEvents = "jobs:events";

    public static string Logs(string streamId) => "logs:" + streamId;
}

/// <summary>Single-node fallback used when Redis is not configured.</summary>
public sealed class InProcessLiveBus : ILiveBus
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<string>>> _channels = new();

    public bool IsDistributed => false;

    public Task PublishAsync(string channel, string message)
    {
        if (_channels.TryGetValue(channel, out var handlers))
        {
            foreach (var handler in handlers.Values)
            {
                try { handler(message); }
                catch { /* a faulty subscriber must not break the publisher */ }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(string channel, Action<string> handler)
    {
        var id = Guid.NewGuid();
        _channels.GetOrAdd(channel, _ => new()).TryAdd(id, handler);
        return Task.FromResult<IAsyncDisposable>(new Subscription(() =>
        {
            if (_channels.TryGetValue(channel, out var handlers) && handlers.TryRemove(id, out _) && handlers.IsEmpty)
                _channels.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Action<string>>>(channel, handlers));
        }));
    }

    private sealed class Subscription(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Redis pub/sub (StackExchange.Redis). Connects lazily and reconnects on its own; an unreachable Redis never blocks start-up.</summary>
public sealed class RedisLiveBus : ILiveBus, IAsyncDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection;
    private readonly ILogger<RedisLiveBus> _logger;
    private long _dropped;

    public RedisLiveBus(string connectionString, ILogger<RedisLiveBus> logger)
    {
        _logger = logger;
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ClientName = "aethera";
        _connection = new Lazy<Task<IConnectionMultiplexer>>(
            async () => await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsDistributed => true;

    /// <summary>Round-trips a PING. Used by the readiness check.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        var connection = await _connection.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!connection.IsConnected) return false;
        await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task PublishAsync(string channel, string message)
    {
        try
        {
            var connection = await _connection.Value.ConfigureAwait(false);
            if (!connection.IsConnected)
            {
                // Fail fast: waiting for a timeout per message would back the whole pipeline up. Subscribers resync from Postgres.
                _dropped++;
                if (_dropped == 1 || _dropped % 1000 == 0)
                    _logger.LogWarning("Redis is not connected; live messages are dropped (and subscribers poll Postgres instead). {Dropped} dropped so far", _dropped);
                return;
            }

            await connection.GetSubscriber().PublishAsync(RedisChannel.Literal(channel), message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publishing to Redis channel {Channel} failed; subscribers resync from Postgres", ChannelKind(channel));
        }
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string channel, Action<string> handler)
    {
        var connection = await _connection.Value.ConfigureAwait(false);
        var subscriber = connection.GetSubscriber();
        var redisChannel = RedisChannel.Literal(channel);
        void OnMessage(RedisChannel _, RedisValue value)
        {
            if (!value.IsNull) handler(value.ToString());
        }

        await subscriber.SubscribeAsync(redisChannel, OnMessage).ConfigureAwait(false);
        return new RedisSubscription(subscriber, redisChannel, OnMessage);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
            await _connection.Value.Result.DisposeAsync().ConfigureAwait(false);
    }

    // Channel names carry stream ids; log only the kind.
    private static string ChannelKind(string channel) => channel[..Math.Max(0, channel.IndexOf(':'))];

    private sealed class RedisSubscription(ISubscriber subscriber, RedisChannel channel, Action<RedisChannel, RedisValue> handler) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await subscriber.UnsubscribeAsync(channel, handler).ConfigureAwait(false); }
            catch { /* connection already gone */ }
        }
    }
}

/// <summary>One line (chunk) of a log stream as sent to clients (<c>LogLines</c> entries and REST pages).</summary>
public sealed record LogLine(long Sequence, DateTimeOffset Timestamp, LogStream Stream, LogSource Source, string Text)
{
    public static LogLine From(LogChunk chunk) => new(chunk.Sequence, chunk.Timestamp, chunk.Stream, chunk.Source, chunk.Data);
}

/// <summary>
/// Message on <c>logs:{streamId}</c>: a persisted chunk (<see cref="Line"/>) or the end of the stream (<see cref="Eof"/> is the reason:
/// <c>succeeded</c>, <c>failed</c>, <c>cancelled</c>).
/// </summary>
public sealed record LogBusMessage(string StreamId, LogLine? Line, string? Eof)
{
    public static readonly JsonSerializerOptions Json = CreateOptions();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static LogBusMessage? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<LogBusMessage>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>Message on <c>jobs:events</c>: a status change (<see cref="Kind"/> <c>updated</c>) or transient progress.</summary>
public sealed record JobBusEvent(
    string Kind, Guid JobId, string Type, JobStatus Status, string? ResourceType, Guid? ResourceId, Guid? CreatedBy, int? Percent = null, string? Message = null)
{
    public static readonly JsonSerializerOptions Json = LogBusMessage.Json;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static JobBusEvent? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<JobBusEvent>(json, Json); }
        catch (JsonException) { return null; }
    }
}
