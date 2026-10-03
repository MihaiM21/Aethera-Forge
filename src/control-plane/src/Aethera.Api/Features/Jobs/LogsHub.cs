using System.Collections.Concurrent;
using System.Threading.Channels;
using Aethera.Infrastructure.Jobs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Jobs;

/// <summary>Server-to-client messages of <c>/hubs/logs</c> (ADR 0003 section 6).</summary>
public interface ILogsClient
{
    /// <summary>A batch of chunks in sequence order. Replayed and live lines use the same message.</summary>
    Task LogLines(string streamId, IReadOnlyList<LogLine> lines);

    /// <summary><paramref name="reason"/>: <c>succeeded</c>, <c>failed</c>, <c>cancelled</c>, or <c>slow_consumer</c> (resync from REST).</summary>
    Task LogStreamEnded(string streamId, string reason);
}

/// <summary>
/// Live log lines. <c>Subscribe(streamId, fromSequence)</c> replays the stored chunks from Postgres and then continues live,
/// de-duplicated by sequence (ADR 0004 section 6); <c>Unsubscribe(streamId)</c> stops. Failures are hub errors whose message is a problem
/// code: <c>log_stream.not_found</c> (unknown stream or not yours), <c>validation.invalid_parameter</c>, <c>log_stream.too_many</c>.
/// </summary>
public sealed class LogsHub(LogStreamRelay relay) : Hub<ILogsClient>
{
    /// <param name="streamId">e.g. <c>job:&lt;jobId&gt;</c></param>
    /// <param name="fromSequence">First sequence to send; null or negative = from the start.</param>
    public Task Subscribe(string streamId, long? fromSequence) =>
        relay.SubscribeAsync(Context.ConnectionId, HubIdentity.OrganizationOf(Context.User), streamId, fromSequence ?? 0, Context.ConnectionAborted);

    public Task Unsubscribe(string streamId)
    {
        relay.Unsubscribe(Context.ConnectionId, streamId);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        relay.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Bridges the <see cref="ILiveBus"/> (Redis or in-process) to <c>/hubs/logs</c> subscribers. Each stream with local subscribers has
/// one reference-counted bus subscription; each subscriber has its own pump that replays from Postgres, then follows the live messages,
/// skipping sequences it already sent. Postgres stays the source of truth: the pump re-reads it when it sees a sequence gap, before it
/// announces the end of a stream, and every <c>Aethera:Jobs:PollSeconds</c> as a safety net (which also keeps a subscription working when
/// Redis is down), so lost Redis messages cost latency, never lines.
/// </summary>
public sealed class LogStreamRelay
{
    private const int MaxStreamIdLength = 200;
    private const int MaxSubscriptionsPerConnection = 50;
    private const int ReplayPageSize = 100;
    private const int InboxCapacity = 2000;
    private const int MaxBatchBytes = 64 * 1024;
    private static readonly TimeSpan BusSubscribeTimeout = TimeSpan.FromSeconds(3);

    private readonly IHubContext<LogsHub, ILogsClient> _hub;
    private readonly ILiveBus _bus;
    private readonly LogReader _reader;
    private readonly List<ILogStreamAuthorizer> _authorizers;
    private readonly TimeSpan _catchUpInterval;
    private readonly ILogger<LogStreamRelay> _logger;
    private readonly ConcurrentDictionary<(string Connection, string Stream), Subscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, Feed> _feeds = new();
    private readonly SemaphoreSlim _feedGate = new(1, 1);

    public LogStreamRelay(
        IHubContext<LogsHub, ILogsClient> hub,
        ILiveBus bus,
        LogReader reader,
        IEnumerable<ILogStreamAuthorizer> authorizers,
        IOptions<JobsOptions> options,
        ILogger<LogStreamRelay> logger)
    {
        _hub = hub;
        _bus = bus;
        _reader = reader;
        _authorizers = authorizers.ToList();
        _catchUpInterval = options.Value.Poll;
        _logger = logger;
    }

    private sealed class Feed
    {
        public ConcurrentDictionary<Subscription, byte> Subscribers { get; } = new();

        public IAsyncDisposable? Bus { get; set; }
    }

    public async Task SubscribeAsync(string connectionId, Guid organizationId, string streamId, long fromSequence, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamId) || streamId.Length > MaxStreamIdLength) throw new HubException("validation.invalid_parameter");
        var authorizer = _authorizers.FirstOrDefault(a => a.Handles(streamId)) ?? throw new HubException("log_stream.not_found");
        if (!(await authorizer.AuthorizeAsync(streamId, organizationId, cancellationToken)).Allowed) throw new HubException("log_stream.not_found");

        Unsubscribe(connectionId, streamId);
        if (_subscriptions.Keys.Count(k => k.Connection == connectionId) >= MaxSubscriptionsPerConnection) throw new HubException("log_stream.too_many");

        var subscription = new Subscription(this, connectionId, streamId, Math.Max(0, fromSequence), authorizer, organizationId);
        _subscriptions[(connectionId, streamId)] = subscription;
        try
        {
            // Attach to the live feed first, then read Postgres: whatever was committed before the attach is in the replay, whatever
            // is published after it is queued in the inbox, and the overlap is removed by sequence.
            await AttachAsync(subscription);
            // The end of the stream can only be missed (it was published before we attached) if it happened before this check.
            var access = await authorizer.AuthorizeAsync(streamId, organizationId, cancellationToken);
            if (!access.Allowed) throw new HubException("log_stream.not_found");
            subscription.Start(access.EndedReason);
        }
        catch
        {
            await DetachAsync(subscription);
            throw;
        }
    }

    public void Unsubscribe(string connectionId, string streamId)
    {
        if (_subscriptions.TryRemove((connectionId, streamId), out var subscription)) subscription.Stop();
    }

    public void RemoveConnection(string connectionId)
    {
        foreach (var key in _subscriptions.Keys.Where(k => k.Connection == connectionId).ToList())
            Unsubscribe(connectionId, key.Stream);
    }

    private async Task AttachAsync(Subscription subscription)
    {
        await _feedGate.WaitAsync();
        try
        {
            var streamId = subscription.StreamId;
            var feed = _feeds.GetOrAdd(streamId, _ => new Feed());
            if (feed.Bus is null)
            {
                try
                {
                    feed.Bus = await _bus.SubscribeAsync(LiveChannels.Logs(streamId), json => OnMessage(streamId, json)).WaitAsync(BusSubscribeTimeout);
                }
                catch (Exception ex)
                {
                    // Redis is down: this subscription still works, it just follows Postgres on the catch-up interval.
                    _logger.LogWarning(ex, "Could not subscribe to the live log feed; following the stream by polling instead");
                }
            }

            feed.Subscribers[subscription] = 0;
        }
        finally
        {
            _feedGate.Release();
        }
    }

    private async Task DetachAsync(Subscription subscription)
    {
        _subscriptions.TryRemove(new KeyValuePair<(string, string), Subscription>((subscription.ConnectionId, subscription.StreamId), subscription));
        await _feedGate.WaitAsync();
        try
        {
            if (!_feeds.TryGetValue(subscription.StreamId, out var feed)) return;
            feed.Subscribers.TryRemove(subscription, out _);
            if (!feed.Subscribers.IsEmpty) return;
            _feeds.TryRemove(subscription.StreamId, out _);
            if (feed.Bus is not null) await feed.Bus.DisposeAsync();
        }
        finally
        {
            _feedGate.Release();
        }
    }

    // Called on the bus thread (a Redis thread): hand over without blocking.
    private void OnMessage(string streamId, string json)
    {
        if (LogBusMessage.Parse(json) is not { } message) return;
        if (!_feeds.TryGetValue(streamId, out var feed)) return;
        foreach (var subscriber in feed.Subscribers.Keys) subscriber.Post(message);
    }

    private sealed class Subscription(
        LogStreamRelay relay, string connectionId, string streamId, long from, ILogStreamAuthorizer authorizer, Guid organizationId)
    {
        private readonly Channel<LogBusMessage> _inbox = Channel.CreateBounded<LogBusMessage>(
            new BoundedChannelOptions(InboxCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly CancellationTokenSource _cts = new();
        private long _lastSent = from - 1;
        private volatile bool _slow;

        public string ConnectionId { get; } = connectionId;

        public string StreamId { get; } = streamId;

        public void Start(string? endedReason) => _ = Task.Run(() => RunAsync(endedReason), CancellationToken.None);

        public void Stop()
        {
            _cts.Cancel();
            _inbox.Writer.TryComplete();
            _ = relay.DetachAsync(this);
        }

        public void Post(LogBusMessage message)
        {
            if (_inbox.Writer.TryWrite(message)) return;
            // The client cannot keep up (ADR 0004: never slow the pipeline for a browser): drop it, it resyncs from REST.
            _slow = true;
            _cts.Cancel();
        }

        private async Task RunAsync(string? endedReason)
        {
            var ct = _cts.Token;
            try
            {
                await ReplayAsync(ct);
                if (endedReason is not null)
                {
                    await EndAsync(endedReason);
                    return;
                }

                var batch = new List<LogLine>();
                var bytes = 0;
                while (!ct.IsCancellationRequested)
                {
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        wait.CancelAfter(relay._catchUpInterval);
                        try
                        {
                            if (!await _inbox.Reader.WaitToReadAsync(wait.Token)) return;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Quiet for a while (or Redis is down): look at Postgres, which is the truth.
                            await ReplayAsync(ct);
                            var access = await authorizer.AuthorizeAsync(StreamId, organizationId, ct);
                            if (access.EndedReason is { } finished)
                            {
                                await ReplayAsync(ct);
                                await EndAsync(finished);
                                return;
                            }

                            continue;
                        }
                    }

                    while (_inbox.Reader.TryRead(out var message))
                    {
                        if (message.Eof is { } reason)
                        {
                            await FlushAsync(batch);
                            await ReplayAsync(ct); // a last chunk whose message was lost must not be skipped
                            await EndAsync(reason);
                            return;
                        }

                        if (message.Line is { } line && line.Sequence > _lastSent)
                        {
                            if (line.Sequence > _lastSent + 1)
                            {
                                // A gap: Redis is fire-and-forget. Whatever is missing is in Postgres.
                                await FlushAsync(batch);
                                bytes = 0;
                                await ReplayAsync(ct);
                            }

                            if (line.Sequence > _lastSent)
                            {
                                batch.Add(line);
                                bytes += line.Text.Length;
                                _lastSent = line.Sequence;
                            }
                        }

                        if (bytes >= MaxBatchBytes)
                        {
                            await FlushAsync(batch);
                            bytes = 0;
                        }
                    }

                    await FlushAsync(batch);
                }
            }
            catch (OperationCanceledException)
            {
                if (_slow) await TryEndAsync("slow_consumer");
            }
            catch (Exception ex)
            {
                relay._logger.LogWarning(ex, "Log subscription of connection {Connection} to {Stream} failed", ConnectionId, StreamId);
            }
            finally
            {
                relay._subscriptions.TryRemove(new KeyValuePair<(string, string), Subscription>((ConnectionId, StreamId), this));
                await relay.DetachAsync(this);
            }
        }

        private Task EndAsync(string reason) => relay._hub.Clients.Client(ConnectionId).LogStreamEnded(StreamId, reason);

        private async Task ReplayAsync(CancellationToken ct)
        {
            while (true)
            {
                var page = await relay._reader.ReadAsync(StreamId, _lastSent + 1, ReplayPageSize, ct);
                if (page.Count == 0) return;
                await relay._hub.Clients.Client(ConnectionId).LogLines(StreamId, page);
                _lastSent = page[^1].Sequence;
                if (page.Count < ReplayPageSize) return;
            }
        }

        private async Task FlushAsync(List<LogLine> batch)
        {
            if (batch.Count == 0) return;
            var lines = batch.ToArray();
            batch.Clear();
            await relay._hub.Clients.Client(ConnectionId).LogLines(StreamId, lines);
        }

        private async Task TryEndAsync(string reason)
        {
            try { await EndAsync(reason); }
            catch (Exception) { /* the connection is gone */ }
        }
    }
}
