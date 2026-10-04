using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Agents.Transport;
using Aethera.Infrastructure.Persistence;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Duration = Google.Protobuf.WellKnownTypes.Duration;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Sessions;

/// <summary>
/// The logic of one <c>AgentService.Connect</c> stream (ADR 0002 "The Connect stream"), independent of the gRPC host so it can be driven by
/// a scripted fake agent in tests:
/// Hello validation (identity match, versions), superseding an older stream, Welcome, then three loops: a reader that acknowledges and
/// routes messages, an ordered worker that does the database work (so a slow insert never delays an ack), and a watchdog that closes the
/// stream after three missed heartbeats. Any inbound message counts as proof of life.
/// </summary>
public sealed class AgentSessionHandler(
    AgentSessionRegistry registry, AgentCommandDispatcher dispatcher, ServerStatusService status, AgentMetricsIngestor metrics, AgentLogIngestor logs,
    AgentEventIngestor events, AgentDiscoveryStore discovery, IInternalCa ca, IServiceScopeFactory scopes, AgentAudit audit, IClock clock, TimeProvider time,
    IOptions<AgentGatewayOptions> options, ILogger<AgentSessionHandler> logger)
{
    private readonly AgentGatewayOptions _options = options.Value;

    public async Task RunAsync(
        AgentIdentity identity, IAsyncStreamReader<P.AgentMessage> requests, IServerStreamWriter<P.ControlMessage> responses, CancellationToken cancellationToken)
    {
        var hello = await ReadHelloAsync(requests, cancellationToken);
        if (hello is null)
        {
            await TryWriteAsync(responses, Goodbye(P.DisconnectReason.ProtocolViolation, "The first message of a stream must be Hello."), cancellationToken);
            return;
        }

        // The identity is the certificate's, never the message's.
        if (!Guid.TryParse(hello.ServerId, out var claimed) || claimed != identity.ServerId)
        {
            await TryWriteAsync(responses, Goodbye(P.DisconnectReason.ProtocolViolation, "Hello.server_id does not match the client certificate."), cancellationToken);
            await audit.RecordAsync("agent.protocol_violation", "server", identity.ServerId, new { reason = "hello_server_id_mismatch" }, AuditActorType.Agent);
            return;
        }

        if (hello.ProtocolVersion < (uint)_options.MinProtocolVersion || !AgentVersions.IsAtLeast(hello.AgentVersion, _options.MinAgentVersion))
        {
            await TryWriteAsync(responses, Goodbye(P.DisconnectReason.UpgradeRequired, $"Agent version {_options.MinAgentVersion} or newer is required."), cancellationToken);
            return;
        }

        if (!await ServerExistsAsync(identity.ServerId, cancellationToken))
        {
            await TryWriteAsync(responses, Goodbye(P.DisconnectReason.Revoked, "This server no longer exists. Re-enroll the agent."), cancellationToken);
            return;
        }

        var state = registry.State(identity.ServerId);
        var session = new AgentSession(identity.ServerId, identity, hello, time);
        state.Attach(session)?.Close(new P.Disconnect { Reason = P.DisconnectReason.Superseded, Message = "A newer connection from the same server replaced this one." });
        session.Send(new P.ControlMessage { Welcome = BuildWelcome(session) });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var queue = Channel.CreateBounded<P.AgentMessage>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        var pump = PumpAsync(session, responses, token);
        var worker = WorkerAsync(session, state, queue.Reader, token);
        var watchdog = WatchdogAsync(session, token);
        var connected = OnConnectedAsync(session, state, token);
        var reader = ReadLoopAsync(session, state, requests, queue.Writer, token);

        try
        {
            await Task.WhenAny(reader, pump);
        }
        finally
        {
            session.Abandon();
            queue.Writer.TryComplete();
            // A goodbye that is still queued (protocol violation found by the reader) must reach the agent before the pump is stopped.
            if (session.SentDisconnect is not null) await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            await linked.CancelAsync();
            await Task.WhenAll(Quiet(pump), Quiet(worker), Quiet(watchdog), Quiet(connected), Quiet(reader));
            await OnEndedAsync(session, state);
        }
    }

    // ---- start -----------------------------------------------------------------------------------------------------------------------

    private async Task<P.Hello?> ReadHelloAsync(IAsyncStreamReader<P.AgentMessage> requests, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.05, _options.HelloTimeoutSeconds)));
        try
        {
            if (!await requests.MoveNext(timeout.Token)) return null;
            return requests.Current.PayloadCase == P.AgentMessage.PayloadOneofCase.Hello ? requests.Current.Hello : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or RpcException or IOException)
        {
            return null;
        }
    }

    private async Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        return await db.Servers.AsNoTracking().AnyAsync(s => s.Id == serverId, cancellationToken);
    }

    private P.Welcome BuildWelcome(AgentSession session) => new()
    {
        ServerId = session.ServerId.ToString("D"), SessionId = session.SessionId,
        HeartbeatInterval = Duration.FromTimeSpan(_options.Heartbeat), MetricsInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_options.MetricsSeconds)),
        DiscoveryInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_options.DiscoverySeconds)), MaxConcurrentCommands = (uint)_options.MaxConcurrentCommands,
        LogChunkMaxBytes = (uint)_options.LogChunkMaxBytes, LogInitialWindowBytes = (ulong)_options.LogInitialWindowBytes, ConfigVersion = ConfigVersion(),
        ServerTime = Timestamp.FromDateTimeOffset(clock.UtcNow), MinAgentVersion = _options.MinAgentVersion,
    };

    /// <summary>A short hash of the server-driven settings, echoed in <c>Heartbeat.config_version</c>.</summary>
    private string ConfigVersion() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{_options.HeartbeatSeconds}|{_options.MetricsSeconds}|{_options.DiscoverySeconds}|{_options.MaxConcurrentCommands}|{_options.LogChunkMaxBytes}|{_options.LogInitialWindowBytes}")))[..12].ToLowerInvariant();

    // ---- loops -----------------------------------------------------------------------------------------------------------------------

    private static async Task PumpAsync(AgentSession session, IServerStreamWriter<P.ControlMessage> responses, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in session.Outbound.ReadAllAsync(cancellationToken)) await responses.WriteAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or RpcException or IOException or InvalidOperationException)
        {
            // The stream is gone; RunAsync notices through the other loops.
        }
    }

    private async Task ReadLoopAsync(
        AgentSession session, ServerAgentState state, IAsyncStreamReader<P.AgentMessage> requests, ChannelWriter<P.AgentMessage> queue, CancellationToken cancellationToken)
    {
        try
        {
            while (await requests.MoveNext(cancellationToken))
            {
                session.Touch();
                var message = requests.Current;
                switch (message.PayloadCase)
                {
                    case P.AgentMessage.PayloadOneofCase.CommandAck: dispatcher.OnAck(state, message.CommandAck); break;
                    case P.AgentMessage.PayloadOneofCase.CommandProgress: dispatcher.OnProgress(state, message.CommandProgress); break;
                    case P.AgentMessage.PayloadOneofCase.Pong: OnPong(session, message.Pong); break;
                    case P.AgentMessage.PayloadOneofCase.Hello:
                        session.Close(new P.Disconnect { Reason = P.DisconnectReason.ProtocolViolation, Message = "Hello may only be sent once per stream." });
                        return;
                    case P.AgentMessage.PayloadOneofCase.None: break;
                    default: await queue.WriteAsync(message, cancellationToken); break; // ordered: logs, then the result that follows them
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or RpcException or IOException or InvalidOperationException)
        {
            // Stream ended or broke: handled by RunAsync.
        }
    }

    private async Task WorkerAsync(AgentSession session, ServerAgentState state, ChannelReader<P.AgentMessage> queue, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in queue.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await HandleAsync(session, state, message, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Server {ServerId}: handling a {Payload} message failed", session.ServerId, message.PayloadCase);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task HandleAsync(AgentSession session, ServerAgentState state, P.AgentMessage message, CancellationToken cancellationToken)
    {
        switch (message.PayloadCase)
        {
            case P.AgentMessage.PayloadOneofCase.Heartbeat:
                await OnHeartbeatAsync(session, message.Heartbeat, cancellationToken);
                break;
            case P.AgentMessage.PayloadOneofCase.Metrics:
                await metrics.IngestAsync(state, message.Metrics, cancellationToken);
                break;
            case P.AgentMessage.PayloadOneofCase.Discovery:
                await discovery.SaveAsync(session.ServerId, message.Discovery, cancellationToken);
                break;
            case P.AgentMessage.PayloadOneofCase.CommandResult:
                // A DiscoveryRefresh answers with the fresh report: store it like a pushed one before the waiting job continues.
                if (message.CommandResult is { Status: P.CommandStatus.Succeeded, ResultCase: P.CommandResult.ResultOneofCase.Discovery } done)
                    await discovery.SaveAsync(session.ServerId, done.Discovery, cancellationToken);
                dispatcher.OnResult(state, message.CommandResult);
                break;
            case P.AgentMessage.PayloadOneofCase.LogChunk:
                await logs.HandleChunkAsync(session, state, message.LogChunk, cancellationToken);
                break;
            case P.AgentMessage.PayloadOneofCase.Event:
                await events.HandleAsync(state, message.Event, cancellationToken);
                break;
        }
    }

    private async Task OnHeartbeatAsync(AgentSession session, P.Heartbeat heartbeat, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (heartbeat.SentAt is { Seconds: > 0 } sent) session.ClockSkew = sent.ToDateTimeOffset() - now;
        if (session.LastHeartbeatSeq != 0 && heartbeat.Seq > (ulong)session.LastHeartbeatSeq + 1)
            logger.LogDebug("Server {ServerId}: heartbeat sequence gap ({Last} -> {Now})", session.ServerId, session.LastHeartbeatSeq, heartbeat.Seq);
        session.LastHeartbeatSeq = (long)heartbeat.Seq;

        var docker = AgentFacts.ToDomain(heartbeat.DockerStatus);
        var changed = !session.LastDocker.Known || session.LastDocker.Status != docker;
        if (!changed && now - session.LastHeartbeatPersisted < TimeSpan.FromSeconds(_options.HeartbeatPersistSeconds)) return;
        session.LastHeartbeatPersisted = now;
        session.LastDocker = new DockerStatusSnapshot(docker, true);
        await status.HeartbeatAsync(session.ServerId, docker, cancellationToken);
    }

    private void OnPong(AgentSession session, P.Pong pong)
    {
        if (pong.PingSentAt is { Seconds: > 0 } sent) session.LastRtt = clock.UtcNow - sent.ToDateTimeOffset();
    }

    private async Task WatchdogAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Max(10, _options.Heartbeat.TotalMilliseconds / 3));
        var ping = TimeSpan.FromSeconds(Math.Max(0.05, _options.PingSeconds));
        var pinged = false;
        try
        {
            using var timer = new PeriodicTimer(tick, time);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var idle = session.IdleFor;
                if (idle > _options.HeartbeatTimeout)
                {
                    logger.LogWarning("Server {ServerId}: no message for {Idle:0.#} s; closing the stream", session.ServerId, idle.TotalSeconds);
                    session.Close(new P.Disconnect { Reason = P.DisconnectReason.HeartbeatTimeout, Message = "Heartbeats stopped arriving." });
                    return;
                }

                if (idle > ping && !pinged)
                {
                    pinged = session.Send(new P.ControlMessage { Ping = new P.Ping { Nonce = Guid.NewGuid().ToString("N"), SentAt = Timestamp.FromDateTimeOffset(clock.UtcNow) } });
                }
                else if (idle < ping)
                {
                    pinged = false;
                }

                MaybeSendCertificateHint(session);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Tells the agent to renew now once its certificate is within the renewal window (and again hourly while it has not).</summary>
    private void MaybeSendCertificateHint(AgentSession session)
    {
        var now = clock.UtcNow;
        if (session.Identity.NotAfter - now > _options.RenewBefore || now - session.LastCertHint < TimeSpan.FromHours(1)) return;
        session.LastCertHint = now;
        session.Send(new P.ControlMessage
        {
            CertRotation = new P.CertRotationHint
            {
                CurrentNotAfter = Timestamp.FromDateTimeOffset(session.Identity.NotAfter),
                RenewBy = Timestamp.FromDateTimeOffset(session.Identity.NotAfter - TimeSpan.FromDays(1) is var by && by > now ? by : now.AddMinutes(5)),
                Reason = "The client certificate is close to expiry.",
            },
        });
    }

    // ---- connect / end ---------------------------------------------------------------------------------------------------------------

    private async Task OnConnectedAsync(AgentSession session, ServerAgentState state, CancellationToken cancellationToken)
    {
        try
        {
            var hello = session.Hello;
            await status.AgentConnectedAsync(session.ServerId, hello.AgentVersion, hello.Os, hello.Architecture, cancellationToken);
            await SyncCertificatesAsync(session, cancellationToken);
            await audit.RecordAsync("agent.connected", "server", session.ServerId,
                new { sessionId = session.SessionId, agentVersion = hello.AgentVersion, os = hello.Os, architecture = hello.Architecture, processId = hello.ProcessId, reconnectAttempt = hello.ReconnectAttempt },
                AuditActorType.Agent, cancellationToken: cancellationToken);
            state.Events.Publish(new ServerEvent(session.ServerId, ServerEventKind.AgentConnected, clock.UtcNow));

            // Resume log streams the agent still serves from our last durable sequence.
            foreach (var streamId in hello.ActiveLogStreamIds)
            {
                var acked = await logs.LastDurableSequenceAsync(session, state, streamId, cancellationToken);
                session.Send(new P.ControlMessage { LogFlowControl = new P.LogFlowControl { StreamId = streamId, AckedSequence = (ulong)acked, WindowBytes = (ulong)_options.LogInitialWindowBytes } });
            }

            await dispatcher.ReconcileAsync(state, session, cancellationToken);
            MaybeSendCertificateHint(session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Server {ServerId}: connect bookkeeping failed", session.ServerId);
        }
    }

    /// <summary>
    /// Records the certificate this stream authenticated with as the server's current one and revokes the ones it replaced (a renewed agent
    /// reconnects with the new certificate; the old one is no longer needed, ADR 0002 "Renewal").
    /// </summary>
    private async Task SyncCertificatesAsync(AgentSession session, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var certificates = await db.AgentCertificates.Where(c => c.ServerId == session.ServerId).ToListAsync(cancellationToken);
            var current = certificates.FirstOrDefault(c => string.Equals(c.Serial, session.Identity.Serial, StringComparison.OrdinalIgnoreCase));
            if (current is null) return;

            var now = clock.UtcNow;
            var superseded = certificates.Where(c => c.RevokedAt is null && c.Id != current.Id && c.NotBefore < current.NotBefore).ToList();
            foreach (var old in superseded) old.Revoke("superseded by renewal", now);
            ca.MarkRevoked(superseded.Select(c => c.Serial));

            var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == session.ServerId, cancellationToken);
            if (server is not null && !string.Equals(server.CertSerial, current.Serial, StringComparison.OrdinalIgnoreCase))
            {
                server.CertSerial = current.Serial;
                server.CertFingerprint = current.FingerprintSha256;
                server.CertExpiresAt = current.NotAfter;
            }

            try
            {
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // Retry on fresh rows.
            }
        }
    }

    private async Task OnEndedAsync(AgentSession session, ServerAgentState state)
    {
        // Only the current session owns the "agent unavailable" transition; a superseded one ends silently.
        if (!state.Detach(session)) return;
        var reason = session.SentDisconnect?.Reason switch
        {
            P.DisconnectReason.HeartbeatTimeout => "heartbeat timeout",
            P.DisconnectReason.Revoked => "revoked",
            P.DisconnectReason.ProtocolViolation => "protocol violation",
            P.DisconnectReason.ServerShutdown => "control plane shutdown",
            _ => "stream closed",
        };
        try
        {
            await status.AgentDisconnectedAsync(session.ServerId, reason);
            state.Events.Publish(new ServerEvent(session.ServerId, ServerEventKind.AgentDisconnected, clock.UtcNow, Message: reason));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Server {ServerId}: recording the disconnect failed", session.ServerId);
        }
    }

    private static P.ControlMessage Goodbye(P.DisconnectReason reason, string message) =>
        new() { Disconnect = new P.Disconnect { Reason = reason, Message = message } };

    private static async Task TryWriteAsync(IServerStreamWriter<P.ControlMessage> responses, P.ControlMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await responses.WriteAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or RpcException or IOException or InvalidOperationException)
        {
            // The agent is already gone.
        }
    }

    private static async Task Quiet(Task task)
    {
        try { await task; }
        catch (Exception) { /* each loop handles and logs its own failures */ }
    }
}
