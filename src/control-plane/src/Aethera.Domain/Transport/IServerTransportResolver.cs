namespace Aethera.Domain.Transport;

/// <summary>The transport chosen for a call and whether it is a degraded one (recorded on the job: "ran over SSH fallback").</summary>
public sealed record ResolvedTransport(IServerTransport Transport, bool UsedFallback)
{
    public TransportKind Kind => Transport.Kind;
}

/// <summary>A command outcome together with how it was carried.</summary>
public sealed record ResolvedOutcome<TResult>(CommandOutcome<TResult> Outcome, TransportKind Transport, bool UsedFallback);

/// <summary>
/// Picks the transport per call (ADR 0002 "IServerTransport"): the agent when its session is connected, otherwise SSH when the server may
/// use it as a fallback, otherwise it fails with <c>server.agent_unavailable</c> (or <c>server.unreachable</c> when the machine itself does
/// not answer). A command whose capability the picked transport lacks fails fast with <c>transport.unsupported</c>.
/// </summary>
public interface IServerTransportResolver
{
    /// <exception cref="ServerTransportException">No transport can carry the command.</exception>
    Task<ResolvedTransport> ResolveAsync(Guid serverId, TransportCapabilities required, CancellationToken cancellationToken);

    /// <summary>Resolves a transport for the command and executes it.</summary>
    Task<ResolvedOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        Guid serverId, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : IServerCommand<TResult>;
}
