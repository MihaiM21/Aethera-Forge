using System.Collections.Concurrent;
using System.Threading.Channels;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Jobs;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Sessions;

/// <summary>
/// A command the gateway has sent and is still waiting for. It belongs to the <b>server</b>, not to a session: when the stream drops the
/// command stays pending and is reconciled after the next <c>Hello</c> (ADR 0002 "Reconnect reconciliation").
/// </summary>
public sealed class PendingCommand
{
    public PendingCommand(P.Command proto, string name, CommandOptions options, DateTimeOffset deadline, ISecretRedactor redactor)
    {
        Proto = proto;
        CommandId = proto.CommandId;
        Name = name;
        Options = options;
        Deadline = deadline;
        Redactor = redactor;
    }

    public string CommandId { get; private set; }

    public string IdempotencyKey => Proto.IdempotencyKey;

    public string Name { get; }

    public CommandOptions Options { get; }

    public DateTimeOffset Deadline { get; }

    /// <summary>The wire command, kept for re-delivery. Holds secret values for as long as the command is pending, in memory only.</summary>
    public P.Command Proto { get; }

    /// <summary>Masks the command's own secret values in log data it produces (defense in depth, ADR 0002 "Log streaming" 6).</summary>
    public ISecretRedactor Redactor { get; }

    /// <summary>Completed by the first <c>CommandAck</c>.</summary>
    public TaskCompletionSource<P.CommandAck> Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed by the terminal <c>CommandResult</c>, or failed when a re-delivery is rejected.</summary>
    public TaskCompletionSource<P.CommandResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool AckReceived { get; set; }

    public bool CancelRequested { get; set; }

    public TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(15);

    public string CancelReason { get; set; } = "";

    public int Deliveries { get; set; } = 1;

    /// <summary>Agent stream ids this command produces chunks for (its command id, a build id).</summary>
    public HashSet<string> StreamIds { get; } = [];

    internal void Rebind(string newCommandId)
    {
        CommandId = newCommandId;
        Proto.CommandId = newCommandId;
        Deliveries++;
    }
}

/// <summary>Fan-out of items to any number of subscribers; a slow subscriber loses the oldest items instead of blocking the producer.</summary>
public sealed class Broadcaster<T>
{
    private readonly Lock _gate = new();
    private readonly List<Channel<T>> _subscribers = [];

    public int Count
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    public Subscription Subscribe(int capacity = 1024)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate) _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    public void Publish(T item)
    {
        Channel<T>[] snapshot;
        lock (_gate) snapshot = [.. _subscribers];
        foreach (var channel in snapshot) channel.Writer.TryWrite(item);
    }

    public sealed class Subscription(Broadcaster<T> owner, Channel<T> channel) : IDisposable
    {
        public ChannelReader<T> Reader => channel.Reader;

        public void Dispose()
        {
            lock (owner._gate) owner._subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}

/// <summary>Everything the gateway remembers about one server's agent, across sessions.</summary>
public sealed class ServerAgentState(Guid serverId)
{
    private const int MaxRememberedEvents = 4096;
    private const int MaxStreamAliases = 4096;

    private readonly Lock _gate = new();
    private readonly Queue<string> _eventOrder = new();
    private readonly HashSet<string> _eventIds = [];
    private readonly Queue<string> _aliasOrder = new();

    public Guid ServerId { get; } = serverId;

    private AgentSession? _session;

    public AgentSession? Session
    {
        get { lock (_gate) return _session; }
    }

    /// <summary>Commands sent and not yet answered, by their current <c>command_id</c>.</summary>
    public ConcurrentDictionary<string, PendingCommand> Pending { get; } = new();

    public Broadcaster<ServerEvent> Events { get; } = new();

    /// <summary>Subscribers of container log streams, by the stream id chosen in <c>LogStreamStart</c>.</summary>
    public ConcurrentDictionary<string, Broadcaster<LogEntry>> LogSubscribers { get; } = new();

    /// <summary>Agent stream id (<c>command_id</c>, build id) to the persisted stream id (<c>build:...</c>, <c>deploy:...</c>) the chunks are stored under.</summary>
    public ConcurrentDictionary<string, string> StreamAliases { get; } = new();

    /// <summary>Added to the agent's chunk sequence when a re-delivered command restarted a stream that already has stored chunks.</summary>
    public ConcurrentDictionary<string, long> SequenceOffsets { get; } = new();

    /// <summary>Workload ids of this server (metrics/event label validation), refreshed at most once a minute.</summary>
    internal HashSet<Guid>? WorkloadIds { get; set; }

    internal DateTimeOffset WorkloadIdsLoadedAt { get; set; }

    /// <summary>Makes <paramref name="session"/> current and returns the one it replaced (the caller sends it <c>SUPERSEDED</c>).</summary>
    public AgentSession? Attach(AgentSession session)
    {
        lock (_gate)
        {
            var previous = _session;
            _session = session;
            return previous;
        }
    }

    /// <summary>Clears the session if it is still the current one. False when a newer session already replaced it.</summary>
    public bool Detach(AgentSession session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session)) return false;
            _session = null;
            return true;
        }
    }

    public void RegisterStream(string agentStreamId, string persistedStreamId)
    {
        if (StreamAliases.TryAdd(agentStreamId, persistedStreamId)) lock (_gate) _aliasOrder.Enqueue(agentStreamId);
        lock (_gate)
            while (_aliasOrder.Count > MaxStreamAliases)
            {
                var old = _aliasOrder.Dequeue();
                StreamAliases.TryRemove(old, out _);
                SequenceOffsets.TryRemove(old, out _);
            }
    }

    /// <summary>True the first time an <c>event_id</c> is seen (the agent re-sends its buffered events after a reconnect).</summary>
    public bool FirstSightingOf(string eventId)
    {
        if (eventId.Length == 0) return true;
        lock (_gate)
        {
            if (!_eventIds.Add(eventId)) return false;
            _eventOrder.Enqueue(eventId);
            while (_eventOrder.Count > MaxRememberedEvents) _eventIds.Remove(_eventOrder.Dequeue());
            return true;
        }
    }

    /// <summary>Masks every secret of every pending command in <paramref name="text"/>.</summary>
    public string Redact(string text)
    {
        foreach (var pending in Pending.Values) text = pending.Redactor.Redact(text);
        return text;
    }

    public PendingCommand? FindPending(string commandId) => Pending.TryGetValue(commandId, out var pending) ? pending : null;
}

/// <summary>
/// <c>server_id -&gt; state</c>, in memory (ADR 0002: one API instance in the MVP; a Redis-backed registry is the path to several). The
/// "named seam" for scaling out: everything that needs a session goes through this class.
/// </summary>
public sealed class AgentSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, ServerAgentState> _states = new();

    public ServerAgentState State(Guid serverId) => _states.GetOrAdd(serverId, id => new ServerAgentState(id));

    public AgentSession? Session(Guid serverId) => _states.TryGetValue(serverId, out var state) ? state.Session : null;

    public bool IsConnected(Guid serverId) => Session(serverId) is { IsClosed: false };

    public IReadOnlyList<Guid> ConnectedServerIds => _states.Where(p => p.Value.Session is { IsClosed: false }).Select(p => p.Key).ToList();

    /// <summary>Closes the live stream of a server (revocation, deletion, reset) with the given reason. True when there was one.</summary>
    public bool Disconnect(Guid serverId, P.DisconnectReason reason, string message, TimeSpan? reconnectAfter = null)
    {
        if (Session(serverId) is not { } session) return false;
        var disconnect = new P.Disconnect { Reason = reason, Message = message };
        if (reconnectAfter is { } after) disconnect.ReconnectAfter = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(after);
        session.Close(disconnect);
        return true;
    }
}
