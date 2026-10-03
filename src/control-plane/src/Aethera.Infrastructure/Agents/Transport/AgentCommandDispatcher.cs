using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Protocol;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Jobs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Transport;

/// <summary>
/// Sends typed commands to a connected agent and turns acks and results back into typed outcomes (ADR 0002 "Command idempotency,
/// timeouts, cancellation"):
/// <list type="bullet">
/// <item>no <c>CommandAck</c> within 10 s fails the dispatch (<c>transport.ack_timeout</c>; the stream is probably dead, the job retries with the same key);</item>
/// <item>no result by <c>deadline + 30 s</c> fails the wait with <c>server.agent_unavailable</c> without assuming the command failed on the host;</item>
/// <item>cancellation sends <c>CancelCommand</c> and waits <c>grace + 10 s</c> for the <c>CANCELLED</c> result;</item>
/// <item>a stream drop leaves commands pending; after the next <c>Hello</c> they are reconciled and re-sent with the same idempotency key.</item>
/// </list>
/// </summary>
public sealed class AgentCommandDispatcher(
    AgentSessionRegistry registry, CommandMapper mapper, AgentLogIngestor logs, AgentAudit audit, IClock clock,
    IOptions<AgentGatewayOptions> options, ILogger<AgentCommandDispatcher> logger)
{
    private readonly AgentGatewayOptions _options = options.Value;

    public async Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        Guid serverId, TCommand command, CommandOptions commandOptions, CancellationToken cancellationToken)
        where TCommand : IServerCommand<TResult>
    {
        var entry = mapper.For(typeof(TCommand));
        var state = registry.State(serverId);
        if (state.Session is not { IsClosed: false } session) throw Unavailable("No agent session is connected for this server.");
        if (entry.Capability(command!) is { } capability && !session.Capabilities.Contains(capability))
            throw new ServerTransportException(TransportErrors.Unsupported, $"The agent on this server does not support '{capability}'. Update the agent.");

        var started = clock.UtcNow;
        var deadline = commandOptions.Deadline ?? started + command.DefaultTimeout;
        var commandId = Guid.CreateVersion7().ToString("D");
        var proto = mapper.ToProto(command!, commandId, commandOptions, deadline);

        var redactor = new SecretRedactor();
        foreach (var secret in CommandRedaction.CollectSecrets(proto)) redactor.Register(secret);
        var pending = new PendingCommand(proto, command.Name, commandOptions, deadline, redactor);
        RegisterStreams(state, pending, command, commandOptions, commandId);
        state.Pending[commandId] = pending;

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Dispatching {Command} to server {ServerId}: {Request}", command.Name, serverId, CommandRedaction.ToLogString(proto));

        var outcomeLabel = "failed";
        var errorCode = CommandErrorCode.None;
        var replayed = false;
        try
        {
            if (!session.Send(new P.ControlMessage { Command = proto })) throw Unavailable("The agent session closed while the command was being sent.");

            P.CommandResult result;
            try
            {
                await AwaitAckAsync(pending, cancellationToken);
                result = await AwaitResultAsync(pending, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcomeLabel = "cancelled";
                result = await CancelAsync(state, pending, cancellationToken) ?? throw new OperationCanceledException(cancellationToken);
            }

            var outcome = Convert<TCommand, TResult>(entry, command, pending, result);
            outcomeLabel = Label(outcome.Status);
            errorCode = outcome.ErrorCode;
            replayed = outcome.Replayed;
            return outcome;
        }
        catch (ServerTransportException ex)
        {
            outcomeLabel = ex.Code;
            throw;
        }
        finally
        {
            state.Pending.TryRemove(pending.CommandId, out _);
            if (!command.ReadOnly || _options.AuditReadOnlyCommands)
            {
                await audit.RecordAsync("agent.command", "server", serverId,
                    new
                    {
                        command = command.Name, commandId = pending.CommandId, idempotencyKey = commandOptions.IdempotencyKey, jobId = commandOptions.JobId,
                        outcome = outcomeLabel, errorCode = errorCode == CommandErrorCode.None ? null : errorCode.ToString(), replayed, deliveries = pending.Deliveries,
                        durationMs = (long)(clock.UtcNow - started).TotalMilliseconds,
                    },
                    AuditActorType.System, commandOptions.OrganizationId, commandOptions.ActorUserId);
            }
        }
    }

    // ---- waiting ---------------------------------------------------------------------------------------------------------------------

    private async Task AwaitAckAsync(PendingCommand pending, CancellationToken cancellationToken)
    {
        P.CommandAck ack;
        try
        {
            ack = await pending.Ack.Task.WaitAsync(_options.Ack, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new ServerTransportException(TransportErrors.AckTimeout, $"The agent did not acknowledge the command within {_options.Ack.TotalSeconds:0.#} s.");
        }

        switch (ack.Status)
        {
            case P.AckStatus.Accepted or P.AckStatus.DuplicateRunning or P.AckStatus.DuplicateCompleted:
                return;
            default:
                throw Rejection(ack);
        }
    }

    private async Task<P.CommandResult> AwaitResultAsync(PendingCommand pending, CancellationToken cancellationToken)
    {
        var remaining = pending.Deadline + _options.DeadlineGrace - clock.UtcNow;
        if (remaining < TimeSpan.FromMilliseconds(50)) remaining = TimeSpan.FromMilliseconds(50);
        try
        {
            return await pending.Result.Task.WaitAsync(remaining, cancellationToken);
        }
        catch (TimeoutException)
        {
            // The command is NOT assumed to have failed on the host; reconciliation on the next connect decides.
            throw Unavailable("No result arrived by the command deadline plus the grace period; the agent may be hung or gone.");
        }
    }

    private async Task<P.CommandResult?> CancelAsync(ServerAgentState state, PendingCommand pending, CancellationToken originalToken)
    {
        pending.CancelRequested = true;
        pending.CancelGrace = pending.Options.CancelGrace;
        pending.CancelReason = "cancelled by the control plane";
        state.Session?.Send(CancelMessage(pending));
        try
        {
            return await pending.Result.Task.WaitAsync(pending.CancelGrace + TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is TimeoutException or ServerTransportException)
        {
            return null; // the agent is gone for good: the command dies at its deadline; the job engine marks it orphaned
        }
    }

    private static P.ControlMessage CancelMessage(PendingCommand pending) => new()
    {
        CancelCommand = new P.CancelCommand { CommandId = pending.CommandId, Reason = pending.CancelReason, Grace = Duration.FromTimeSpan(pending.CancelGrace) },
    };

    // ---- inbound (called by the session handler) -------------------------------------------------------------------------------------

    public void OnAck(ServerAgentState state, P.CommandAck ack)
    {
        if (!state.Pending.TryGetValue(ack.CommandId, out var pending)) return;
        if (!pending.AckReceived)
        {
            pending.AckReceived = true;
            pending.Ack.TrySetResult(ack);
        }
        else if (ack.Status is not (P.AckStatus.Accepted or P.AckStatus.DuplicateRunning or P.AckStatus.DuplicateCompleted))
        {
            // A re-delivery after reconnect was refused (e.g. REJECTED_EXPIRED): nothing will answer it.
            pending.Result.TrySetException(Rejection(ack));
        }
    }

    public void OnProgress(ServerAgentState state, P.CommandProgress progress)
    {
        if (!state.Pending.TryGetValue(progress.CommandId, out var pending) || pending.Options.OnProgress is not { } callback) return;
        try
        {
            callback(new CommandProgressInfo(progress.Step, pending.Redactor.Redact(progress.Message), progress.Percent,
                progress.At is { Seconds: > 0 } at ? at.ToDateTimeOffset() : clock.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A progress callback threw");
        }
    }

    public void OnResult(ServerAgentState state, P.CommandResult result)
    {
        if (state.Pending.TryGetValue(result.CommandId, out var pending)) pending.Result.TrySetResult(result);
        else logger.LogDebug("Server {ServerId}: result for an unknown or finished command ignored", state.ServerId);
    }

    /// <summary>
    /// After a <c>Hello</c> (ADR 0002 "Reconnect reconciliation"): commands the agent still runs keep being awaited; the others are re-sent with
    /// the same idempotency key under a new <c>command_id</c> (the agent replays its cached result, or executes if it never saw them).
    /// </summary>
    public async Task ReconcileAsync(ServerAgentState state, AgentSession session, CancellationToken cancellationToken)
    {
        var running = new HashSet<string>(session.Hello.RunningCommandIds, StringComparer.Ordinal);
        foreach (var pending in state.Pending.Values.ToList())
        {
            if (pending.Result.Task.IsCompleted) continue;

            if (running.Contains(pending.CommandId))
            {
                if (pending.CancelRequested) session.Send(CancelMessage(pending));
                continue;
            }

            if (pending.CancelRequested)
            {
                // The job was cancelled while the agent was away and the agent has nothing running for it.
                pending.Result.TrySetResult(new P.CommandResult
                {
                    CommandId = pending.CommandId, Status = P.CommandStatus.Cancelled, ErrorCode = P.ErrorCode.Cancelled,
                    ErrorMessage = "Cancelled while the agent was disconnected.",
                });
                continue;
            }

            var oldId = pending.CommandId;
            var newId = Guid.CreateVersion7().ToString("D");
            state.Pending.TryRemove(oldId, out _);
            pending.Rebind(newId);
            state.Pending[newId] = pending;
            await RebindStreamAsync(state, pending, oldId, newId, cancellationToken);
            logger.LogInformation("Server {ServerId}: re-sending command {Command} after reconnect (delivery {Delivery})", state.ServerId, pending.Name, pending.Deliveries);
            session.Send(new P.ControlMessage { Command = pending.Proto });
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------

    private void RegisterStreams<TCommand>(ServerAgentState state, PendingCommand pending, TCommand command, CommandOptions commandOptions, string commandId)
    {
        pending.StreamIds.Add(commandId);
        if (command is BuildImageCommand build)
        {
            var persisted = commandOptions.LogStreamId ?? $"build:{build.Spec.BuildId}";
            pending.StreamIds.Add(build.Spec.BuildId);
            state.RegisterStream(build.Spec.BuildId, persisted);
            state.RegisterStream(commandId, persisted);
        }
        else
        {
            state.RegisterStream(commandId, commandOptions.LogStreamId ?? $"deploy:{commandId}");
        }
    }

    private async Task RebindStreamAsync(ServerAgentState state, PendingCommand pending, string oldId, string newId, CancellationToken cancellationToken)
    {
        pending.StreamIds.Add(newId);
        if (!state.StreamAliases.TryGetValue(oldId, out var persisted)) return;
        state.RegisterStream(newId, persisted);
        if (pending.Proto.RequestCase == P.Command.RequestOneofCase.Build) return; // build streams are keyed by the stable build id
        // A fresh execution restarts its stream at sequence 1: continue after what is already stored.
        state.SequenceOffsets[newId] = await logs.MaxStoredSequenceAsync(persisted, cancellationToken);
    }

    private CommandOutcome<TResult> Convert<TCommand, TResult>(CommandMapper.Entry entry, TCommand command, PendingCommand pending, P.CommandResult result)
        where TCommand : IServerCommand<TResult>
    {
        var status = result.Status switch
        {
            P.CommandStatus.Succeeded => CommandStatus.Succeeded,
            P.CommandStatus.Cancelled => CommandStatus.Cancelled,
            P.CommandStatus.TimedOut => CommandStatus.TimedOut,
            _ => CommandStatus.Failed,
        };
        var error = ProtoConvert.EnumOf<CommandErrorCode>((int)result.ErrorCode);
        var message = result.ErrorMessage.Length == 0 ? null : pending.Redactor.Redact(result.ErrorMessage);
        TResult? value = default;
        if (status == CommandStatus.Succeeded)
        {
            try
            {
                value = (TResult)entry.Read(result);
            }
            catch (UnexpectedResultException ex)
            {
                logger.LogWarning("Command {Command} returned an unexpected result: {Reason}", command.Name, ex.Message);
                status = CommandStatus.Failed;
                error = CommandErrorCode.Internal;
                message = "The agent returned an unexpected result.";
            }
        }

        return new CommandOutcome<TResult>(status, value, error, message, result.ExitCode, result.StartedAt.ToDomain(), result.FinishedAt.ToDomain(), result.Replayed);
    }

    private static ServerTransportException Rejection(P.CommandAck ack)
    {
        var error = ProtoConvert.EnumOf<CommandErrorCode>((int)ack.ErrorCode);
        var detail = ack.Message.Length == 0 ? "" : $" {ack.Message}";
        return ack.Status switch
        {
            P.AckStatus.RejectedUnsupported => new ServerTransportException(TransportErrors.Unsupported, $"The agent does not support this command.{detail}", error),
            P.AckStatus.RejectedBusy => new ServerTransportException(TransportErrors.AgentBusy, "The agent is at its concurrent command limit.", error),
            P.AckStatus.RejectedExpired => new ServerTransportException(TransportErrors.CommandRejected, "The command deadline had already passed when it reached the agent.", error),
            P.AckStatus.RejectedPolicy => new ServerTransportException(TransportErrors.CommandRejected, $"The agent's local policy refused the command.{detail}", CommandErrorCode.PolicyViolation),
            _ => new ServerTransportException(TransportErrors.CommandRejected, $"The agent refused the command.{detail}", error),
        };
    }

    private static ServerTransportException Unavailable(string reason) => new(TransportErrors.AgentUnavailable, reason);

    private static string Label(CommandStatus status) => status switch
    {
        CommandStatus.Succeeded => "succeeded",
        CommandStatus.Cancelled => "cancelled",
        CommandStatus.TimedOut => "timedOut",
        _ => "failed",
    };
}
