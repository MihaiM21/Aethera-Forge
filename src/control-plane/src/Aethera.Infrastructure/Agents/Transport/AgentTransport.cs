using System.Runtime.CompilerServices;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Sessions;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Agents.Transport;

/// <summary>
/// The primary transport (ADR 0002 "AgentTransport"): maps <c>ExecuteAsync</c> onto the agent stream through the session registry and the
/// command dispatcher. Supports every capability.
/// </summary>
public sealed class AgentTransport(AgentSessionRegistry registry, AgentCommandDispatcher dispatcher, ILogger<AgentTransport> logger) : IServerTransport
{
    public TransportKind Kind => TransportKind.Agent;

    public TransportCapabilities Capabilities => TransportCapabilities.AllAgent;

    public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(registry.IsConnected(serverId)
            ? TransportStatus.Up(TransportKind.Agent)
            : TransportStatus.Down(TransportKind.Agent, "No agent session is connected for this server."));

    public Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        Guid serverId, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : IServerCommand<TResult> =>
        dispatcher.ExecuteAsync<TCommand, TResult>(serverId, command, options, cancellationToken);

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        Guid serverId, LogStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = registry.State(serverId);
        var streamId = "container:" + Guid.CreateVersion7().ToString("N");
        var broadcaster = state.LogSubscribers.GetOrAdd(streamId, _ => new Broadcaster<LogEntry>());
        using var subscription = broadcaster.Subscribe(4096);
        try
        {
            var start = new LogStreamStartCommand(streamId, request.Container, request.Follow, request.Since, request.Tail, request.IncludeStdout, request.IncludeStderr);
            var outcome = await dispatcher.ExecuteAsync<LogStreamStartCommand, LogStreamStarted>(
                serverId, start, CommandOptions.For($"logs:{streamId}"), cancellationToken);
            if (outcome.Succeeded && outcome.Result is { Started: true })
            {
                await foreach (var entry in subscription.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return entry;
                    if (entry.Eof) yield break;
                }
            }
            else
            {
                outcome.EnsureSucceeded();
            }
        }
        finally
        {
            state.LogSubscribers.TryRemove(streamId, out _);
            await StopQuietlyAsync(serverId, streamId);
        }
    }

    public async IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = registry.State(serverId).Events.Subscribe();
        await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken)) yield return item;
    }

    private async Task StopQuietlyAsync(Guid serverId, string streamId)
    {
        if (!registry.IsConnected(serverId)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await dispatcher.ExecuteAsync<LogStreamStopCommand, Unit>(serverId, new LogStreamStopCommand(streamId), CommandOptions.For($"logs-stop:{streamId}"), timeout.Token);
        }
        catch (Exception ex) when (ex is ServerTransportException or OperationCanceledException)
        {
            logger.LogDebug("Stopping log stream {StreamId} failed: {Error}", streamId, ex.GetType().Name);
        }
    }
}
