using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Agents.Transport;

/// <summary>
/// Decides whether the SSH transport may stand in when the agent is down. The default (<see cref="DbSshFallbackPolicy"/>) allows it for a
/// server that has SSH credentials.
/// </summary>
public interface ISshFallbackPolicy
{
    ValueTask<bool> AllowsFallbackAsync(Guid serverId, CancellationToken cancellationToken);
}

/// <summary>Fallback is allowed when the server has an SSH credential secret (WP2.3 may refine this with an explicit per-server switch).</summary>
public sealed class DbSshFallbackPolicy(IServiceScopeFactory scopes) : ISshFallbackPolicy
{
    public async ValueTask<bool> AllowsFallbackAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        return await db.Servers.AsNoTracking().AnyAsync(s => s.Id == serverId && s.SshCredentialSecretId != null, cancellationToken);
    }
}

/// <summary>
/// <see cref="IServerTransportResolver"/> over every registered <see cref="IServerTransport"/>.
/// <para>
/// <b>WP2.3 seam:</b> the SSH transport is added by registering another <see cref="IServerTransport"/> with
/// <see cref="TransportKind.Ssh"/> in DI. Nothing here changes: transports are tried in <see cref="TransportKind"/> order (agent first),
/// the SSH transport only when <see cref="ISshFallbackPolicy"/> allows it, and a command is only sent to a transport whose
/// <see cref="IServerTransport.Capabilities"/> cover the command's requirement.
/// </para>
/// </summary>
public sealed class ServerTransportResolver(IEnumerable<IServerTransport> transports, ISshFallbackPolicy fallbackPolicy, IServiceScopeFactory scopes) : IServerTransportResolver
{
    private readonly IReadOnlyList<IServerTransport> _transports = transports.OrderBy(t => t.Kind).ToList();

    public async Task<ResolvedTransport> ResolveAsync(Guid serverId, TransportCapabilities required, CancellationToken cancellationToken)
    {
        var capabilityGap = false;
        foreach (var transport in _transports)
        {
            if (!(await transport.GetStatusAsync(serverId, cancellationToken)).Available) continue;
            if (transport.Kind != TransportKind.Agent && !await fallbackPolicy.AllowsFallbackAsync(serverId, cancellationToken)) continue;
            if ((transport.Capabilities & required) != required)
            {
                capabilityGap = true; // keep looking: a more capable transport may be available
                continue;
            }

            return new ResolvedTransport(transport, UsedFallback: transport.Kind != TransportKind.Agent);
        }

        if (capabilityGap)
            throw new ServerTransportException(TransportErrors.Unsupported,
                "The available transport cannot run this command. Enable the agent on this server or use a build server.");
        throw await UnavailableAsync(serverId, cancellationToken);
    }

    public async Task<ResolvedOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        Guid serverId, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : IServerCommand<TResult>
    {
        var resolved = await ResolveAsync(serverId, command.Required, cancellationToken);
        var outcome = await resolved.Transport.ExecuteAsync<TCommand, TResult>(serverId, command, options, cancellationToken);
        return new ResolvedOutcome<TResult>(outcome, resolved.Kind, resolved.UsedFallback);
    }

    /// <summary><c>server.unreachable</c> when the control plane's own probe says the machine does not answer, else <c>server.agent_unavailable</c>.</summary>
    private async Task<ServerTransportException> UnavailableAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var reachability = await db.Servers.AsNoTracking().Where(s => s.Id == serverId).Select(s => (ReachabilityStatus?)s.ReachabilityStatus).FirstOrDefaultAsync(cancellationToken);
        return reachability == ReachabilityStatus.Unreachable
            ? new ServerTransportException(TransportErrors.Unreachable, "The server does not answer and no agent session exists.")
            : new ServerTransportException(TransportErrors.AgentUnavailable, "No agent session is connected for this server.");
    }
}
