namespace Aethera.Infrastructure.Agents.Sessions;

/// <summary>
/// Called once an agent session is established and reconciled. Implementations must return at once: work that talks to the agent runs in
/// the background, so the session's own bookkeeping is never held up.
/// </summary>
public interface IAgentConnectedHook
{
    void OnConnected(Guid serverId);
}
