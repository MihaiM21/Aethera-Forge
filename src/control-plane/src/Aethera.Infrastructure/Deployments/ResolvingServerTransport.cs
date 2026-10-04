using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// Presents <see cref="IServerTransportResolver"/> (agent first, SSH fallback) as one <see cref="IServerTransport"/> so the deployment engine,
/// which only knows that interface, picks the transport per command.
/// </summary>
public sealed class ResolvingServerTransport(IServerTransportResolver resolver) : IServerTransport
{
    public TransportKind Kind => TransportKind.Agent;

    public TransportCapabilities Capabilities => TransportCapabilities.AllAgent;

    public async ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await resolver.ResolveAsync(serverId, TransportCapabilities.ContainerOps, cancellationToken);
            return TransportStatus.Up(resolved.Kind);
        }
        catch (ServerTransportException ex)
        {
            return TransportStatus.Down(TransportKind.Agent, ex.Message);
        }
    }

    public async Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        Guid serverId, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : IServerCommand<TResult> =>
        (await resolver.ExecuteAsync<TCommand, TResult>(serverId, command, options, cancellationToken)).Outcome;

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        Guid serverId, LogStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(serverId, TransportCapabilities.LogFollow, cancellationToken);
        await foreach (var entry in resolved.Transport.StreamLogsAsync(serverId, request, cancellationToken)) yield return entry;
    }

    public async IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(
        Guid serverId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(serverId, TransportCapabilities.PushEvents, cancellationToken);
        await foreach (var e in resolved.Transport.SubscribeEventsAsync(serverId, cancellationToken)) yield return e;
    }
}
