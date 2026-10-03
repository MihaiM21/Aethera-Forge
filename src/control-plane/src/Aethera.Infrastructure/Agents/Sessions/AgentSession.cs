using System.Threading.Channels;
using Aethera.Agent.V1;
using Aethera.Infrastructure.Agents.Pki;

namespace Aethera.Infrastructure.Agents.Sessions;

/// <summary>
/// One live <c>AgentService.Connect</c> stream (ADR 0002 "Session lifecycle"). All control messages go through <see cref="Outbound"/>, which
/// a single pump drains into the gRPC response stream (a response writer is not thread safe). Any inbound message counts as proof of life.
/// </summary>
public sealed class AgentSession
{
    private readonly Channel<ControlMessage> _outbound = Channel.CreateUnbounded<ControlMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TimeProvider _time;
    private long _lastInbound;
    private int _closed;

    public AgentSession(Guid serverId, AgentIdentity identity, Hello hello, TimeProvider time)
    {
        ServerId = serverId;
        Identity = identity;
        Hello = hello;
        _time = time;
        SessionId = Guid.CreateVersion7().ToString("N");
        ConnectedAt = time.GetUtcNow();
        Capabilities = new HashSet<string>(hello.Capabilities, StringComparer.Ordinal);
        Touch();
    }

    public Guid ServerId { get; }

    public string SessionId { get; }

    public AgentIdentity Identity { get; }

    public Hello Hello { get; }

    public DateTimeOffset ConnectedAt { get; }

    public IReadOnlySet<string> Capabilities { get; }

    public string AgentVersion => Hello.AgentVersion;

    /// <summary>Round-trip time of the last <c>Ping</c>/<c>Pong</c>, if any.</summary>
    public TimeSpan? LastRtt { get; set; }

    /// <summary>Agent clock minus control-plane clock at the last heartbeat (diagnostics only).</summary>
    public TimeSpan? ClockSkew { get; set; }

    public ChannelReader<ControlMessage> Outbound => _outbound.Reader;

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>The goodbye this side sent, if the session was closed by the control plane.</summary>
    public Disconnect? SentDisconnect { get; private set; }

    /// <summary>Per log stream: bytes the agent has sent since the last <c>LogFlowControl</c> we granted.</summary>
    internal Dictionary<string, long> UnackedLogBytes { get; } = [];

    /// <summary>Container streams we already paused (window 0) because nobody listens, so the pause is sent once.</summary>
    internal HashSet<string> PausedStreams { get; } = [];

    internal DateTimeOffset LastCertHint { get; set; } = DateTimeOffset.MinValue;

    internal long LastHeartbeatSeq { get; set; }

    internal DateTimeOffset LastHeartbeatPersisted { get; set; } = DateTimeOffset.MinValue;

    internal DockerStatusSnapshot LastDocker { get; set; }

    /// <summary>Queues a message for the agent. False when the session is closed.</summary>
    public bool Send(ControlMessage message) => !IsClosed && _outbound.Writer.TryWrite(message);

    /// <summary>Queues a final <c>Disconnect</c> and stops accepting messages; the pump finishes after sending it.</summary>
    public void Close(Disconnect disconnect)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        SentDisconnect = disconnect;
        _outbound.Writer.TryWrite(new ControlMessage { Disconnect = disconnect });
        _outbound.Writer.TryComplete();
    }

    /// <summary>Ends the session without a goodbye (the stream is already gone).</summary>
    public void Abandon()
    {
        Interlocked.Exchange(ref _closed, 1);
        _outbound.Writer.TryComplete();
    }

    /// <summary>Records proof of life (any inbound message).</summary>
    public void Touch() => Interlocked.Exchange(ref _lastInbound, _time.GetTimestamp());

    public TimeSpan IdleFor => _time.GetElapsedTime(Interlocked.Read(ref _lastInbound));
}

/// <summary>Last Docker status the session persisted, so heartbeats only hit the database on a change.</summary>
internal readonly record struct DockerStatusSnapshot(Aethera.Domain.DockerStatus Status, bool Known);
