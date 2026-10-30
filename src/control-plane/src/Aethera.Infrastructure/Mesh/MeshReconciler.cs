using System.Net;
using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Mesh;

/// <summary>What one reconcile did, for the job result and the tests.</summary>
public sealed record MeshReconcileResult(int Rounds, int Applied, int Failed, int Removed, int Skipped, bool HadTransientFailure);

/// <summary>The mesh columns of a server, written as one unit (never together with the columns the API owns: endpoint and listen port).</summary>
internal sealed record MeshServerState(MeshProvider Provider, string? Address, string? PublicKey, MeshStatus Status, string? Error, DateTimeOffset? HandshakeAt);

/// <summary>
/// Brings every server of an organization to the mesh the <c>mesh</c> setting asks for (ADR 0012): allocates addresses, sends each connected
/// server its configuration with all its peers, stores the public keys the agents report and runs another round when a key appeared so the
/// servers that were applied earlier learn it. The private keys never reach the control plane. A server that cannot be reached or refuses is
/// recorded on its own row (<see cref="Server.MeshStatus"/>, <see cref="Server.MeshError"/>) and never stops the others.
/// </summary>
public sealed class MeshReconciler(AetheraDbContext db, IServerTransportResolver transport, IClock clock, ILogger<MeshReconciler> logger)
{
    /// <summary>A key that appears in round one needs round two to reach the servers applied before it; a third is a safety margin.</summary>
    public const int MaxRounds = 3;

    public const string WireGuardInterface = "aethera0";
    public const string TailscaleInterface = "tailscale0";
    public const int KeepaliveSeconds = 25;
    public static readonly TimeSpan HandshakeFreshness = TimeSpan.FromMinutes(3);

    /// <param name="idempotencyKey">The agent idempotency key of a step (<c>JobContext.IdempotencyKey</c>); a round and a server make the step name.</param>
    /// <param name="log">Narration for the job log.</param>
    public async Task<MeshReconcileResult> ReconcileAsync(
        Guid organizationId, Func<string, string> idempotencyKey, Func<string, Task> log, CancellationToken ct)
    {
        var setting = await MeshSettingsStore.GetAsync(db, ct);
        var servers = await db.Servers.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.Transport == ServerTransport.Agent && s.Lifecycle == ServerLifecycle.Active)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(ct);
        var run = new Run(setting, servers, idempotencyKey, log, organizationId);

        switch (setting.Provider)
        {
            case MeshProvider.None: await TurnOffAsync(run, ct); break;
            case MeshProvider.Tailscale: await ReconcileTailscaleAsync(run, ct); break;
            default: await ReconcileWireGuardAsync(run, ct); break;
        }

        return new MeshReconcileResult(run.Rounds, run.Applied, run.Failed, run.Removed, run.Skipped, run.Transient);
    }

    // ---- off -------------------------------------------------------------------------------------------------------------------------

    private async Task TurnOffAsync(Run run, CancellationToken ct)
    {
        foreach (var server in run.Servers.Where(s => s.MeshProvider != MeshProvider.None))
        {
            if (!Connected(server))
            {
                run.Skipped++;
                continue; // the server cleans up when it connects (see MeshConnectedHook)
            }

            if (server.MeshProvider == MeshProvider.WireGuard)
            {
                var removed = await TryAsync(run, server, new MeshRemoveCommand(WireGuardInterface), $"mesh-remove-{server.Id:N}", ct);
                if (removed is null) continue;
                if (!removed.Succeeded)
                {
                    await FailAsync(run, server, $"The private network could not be removed: {removed.ErrorMessage}", ct);
                    continue;
                }

                run.Removed++;
                await run.Log($"{server.Name}: the WireGuard interface was removed (the key stays on the server).");
            }

            await WriteAsync(server.Id, new MeshServerState(MeshProvider.None, server.MeshAddress, server.MeshPublicKey, MeshStatus.Disabled, null, null), ct);
        }
    }

    // ---- tailscale -------------------------------------------------------------------------------------------------------------------

    private async Task ReconcileTailscaleAsync(Run run, CancellationToken ct)
    {
        run.Rounds = 1;
        foreach (var server in run.Servers)
        {
            if (!Connected(server))
            {
                run.Skipped++;
                if (server.MeshProvider != MeshProvider.Tailscale)
                    await WriteAsync(server.Id, new MeshServerState(MeshProvider.Tailscale, null, null, MeshStatus.Pending, null, null), ct);
                continue;
            }

            if (server.MeshProvider == MeshProvider.WireGuard)
                await TryAsync(run, server, new MeshRemoveCommand(WireGuardInterface), $"mesh-remove-{server.Id:N}", ct); // best effort cleanup

            var applied = await TryAsync(run, server,
                new MeshApplyCommand(MeshProviderKind.Tailscale, "", "", 0, [], TailscaleInterface), $"mesh-apply-{server.Id:N}", ct);
            if (applied is null) continue;
            if (!applied.Succeeded)
            {
                await FailAsync(run, server, ReasonOf(applied), ct, MeshProvider.Tailscale);
                continue;
            }

            var state = applied.EnsureSucceeded();
            var address = state.Addresses.Select(StripPrefix).FirstOrDefault(a => a is not null);
            if (address is null)
            {
                await FailAsync(run, server, state.Error ?? "tailscale0 has no IPv4 address.", ct, MeshProvider.Tailscale);
                continue;
            }

            if (await db.Servers.IgnoreQueryFilters().AnyAsync(s => s.Id != server.Id && s.MeshAddress == address, ct))
            {
                await FailAsync(run, server, $"The Tailscale address {address} is already recorded for another server.", ct, MeshProvider.Tailscale);
                continue;
            }

            run.Applied++;
            await run.Log($"{server.Name}: Tailscale address {address}.");
            await WriteAsync(server.Id, new MeshServerState(MeshProvider.Tailscale, address, null, state.Up ? MeshStatus.Up : MeshStatus.Failed,
                state.Up ? null : state.Error ?? "tailscale0 is down.", null), ct);
        }
    }

    // ---- wireguard -------------------------------------------------------------------------------------------------------------------

    private async Task ReconcileWireGuardAsync(Run run, CancellationToken ct)
    {
        var cidr = MeshCidr.Parse(run.Setting.Cidr);
        var view = await AllocateAsync(run, cidr, ct);
        var applied = new Dictionary<Guid, string>();

        for (var round = 1; round <= MaxRounds; round++)
        {
            run.Rounds = round;
            var keyAppeared = false;
            foreach (var member in view.Where(m => m.Address is not null && !run.FailedIds.Contains(m.Server.Id)))
            {
                if (!Connected(member.Server))
                {
                    if (round == 1)
                    {
                        run.Skipped++;
                        if (member.Key is null && member.Server.MeshStatus != MeshStatus.Pending)
                            await WriteAsync(member.Server.Id, StateOf(member) with { Status = MeshStatus.Pending, Error = null }, ct);
                    }

                    continue;
                }

                var command = BuildApply(run.Setting, cidr, member, view);
                var fingerprint = Fingerprint(command);
                if (applied.TryGetValue(member.Server.Id, out var previous) && previous == fingerprint) continue; // nothing new for this server

                var outcome = await TryAsync(run, member.Server, command, $"mesh-apply-{member.Server.Id:N}-r{round}", ct);
                if (outcome is null) continue;
                if (!outcome.Succeeded)
                {
                    await FailAsync(run, member.Server, ReasonOf(outcome), ct, MeshProvider.WireGuard, member.Address, member.Key);
                    continue;
                }

                var state = outcome.EnsureSucceeded();
                applied[member.Server.Id] = fingerprint;
                run.Applied++;
                if (!string.IsNullOrEmpty(state.PublicKey) && state.PublicKey != member.Key)
                {
                    member.Key = state.PublicKey;
                    keyAppeared = true;
                    await run.Log($"{member.Server.Name}: new public key.");
                }

                var (status, handshake, error) = Evaluate(state, command.Peers.Count, clock.UtcNow);
                member.Status = status;
                await run.Log($"{member.Server.Name}: {member.Address}, {command.Peers.Count} peer(s), {status}.");
                await WriteAsync(member.Server.Id, new MeshServerState(MeshProvider.WireGuard, member.Address, member.Key, status, error, handshake), ct);
            }

            if (!keyAppeared) break;
        }
    }

    /// <summary>Assigns every eligible server an address of the range: keeps a valid unique one, allocates otherwise. Writes the result.</summary>
    private async Task<List<Member>> AllocateAsync(Run run, MeshCidr cidr, CancellationToken ct)
    {
        var members = run.Servers.Select(s => new Member(s) { Key = s.MeshPublicKey }).ToList();
        // A mesh address is unique across the whole instance (unique index, and a deleted server keeps its address): what servers outside this
        // run hold (other organizations, servers that are not active, deleted ones) is taken.
        var runIds = run.Servers.Select(s => s.Id).ToList();
        var claimed = new HashSet<uint>();
        var held = await db.Servers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.MeshAddress != null && !runIds.Contains(s.Id)).Select(s => s.MeshAddress!).ToListAsync(ct);
        foreach (var text in held)
            if (MeshCidr.TryParseAddress(text, out var heldAddress)) claimed.Add(heldAddress);
        foreach (var m in members)
        {
            if (MeshCidr.TryParseAddress(m.Server.MeshAddress, out var address) && cidr.IsHost(address) && claimed.Add(address))
                m.Address = MeshCidr.Format(address);
        }

        foreach (var m in members.Where(m => m.Address is null))
        {
            if (cidr.Allocate(claimed) is not { } free)
            {
                await FailAsync(run, m.Server, $"The mesh range {cidr} has no free address left. Choose a larger range in Settings > Network.", ct,
                    MeshProvider.WireGuard, null, m.Key);
                continue;
            }

            claimed.Add(free);
            m.Address = MeshCidr.Format(free);
            await run.Log($"{m.Server.Name}: allocated {m.Address}.");
        }

        // Addresses are written first, so the UI shows them even for servers that are offline.
        foreach (var m in members.Where(m => m.Address is not null && (m.Address != m.Server.MeshAddress || m.Server.MeshProvider != MeshProvider.WireGuard)))
        {
            var status = m.Server.MeshProvider == MeshProvider.WireGuard ? m.Server.MeshStatus : MeshStatus.Pending;
            await WriteAsync(m.Server.Id, new MeshServerState(MeshProvider.WireGuard, m.Address, m.Key, status, null, m.Server.MeshProvider == MeshProvider.WireGuard ? m.Server.MeshHandshakeAt : null), ct);
        }

        foreach (var m in members.Where(m => m.Address is not null)) run.Addresses[m.Server.Id] = m.Address!;
        return members;
    }

    private static MeshApplyCommand BuildApply(MeshSetting setting, MeshCidr cidr, Member self, IEnumerable<Member> everyone)
    {
        var peers = everyone
            .Where(m => m.Server.Id != self.Server.Id && m.Key is not null && m.Address is not null)
            .Select(m => new MeshPeerSpec(m.Key!, EndpointOf(m.Server, setting.ListenPort), [$"{m.Address}/32"], KeepaliveSeconds, m.Server.Id.ToString()))
            .OrderBy(p => p.PublicKey, StringComparer.Ordinal)
            .ToList();
        return new MeshApplyCommand(MeshProviderKind.WireGuard, $"{self.Address}/{cidr.Prefix}", cidr.ToString(), self.Server.MeshListenPort ?? setting.ListenPort, peers);
    }

    /// <summary>
    /// Where peers dial a server: the endpoint set on it, else <c>&lt;public ip&gt;:&lt;listen port&gt;</c> (the host when no public IP is known).
    /// Null for a server only reachable by name <c>localhost</c> or a loopback address (the control plane's own machine): its peers wait for it to dial in.
    /// </summary>
    public static string? EndpointOf(Server server, int defaultPort)
    {
        if (!string.IsNullOrWhiteSpace(server.MeshEndpoint)) return server.MeshEndpoint.Trim();
        var host = (server.PublicIp ?? server.Host).Trim();
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
        var port = server.MeshListenPort ?? defaultPort;
        if (!IPAddress.TryParse(host, out var ip)) return $"{host}:{port}";
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return null;
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }

    // ---- status ----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Up</c> when the interface is up and every peer had a handshake within <see cref="HandshakeFreshness"/> (also with no peers), <c>Degraded</c>
    /// when some did not, <c>Failed</c> when the interface is down.
    /// </summary>
    public static (MeshStatus Status, DateTimeOffset? HandshakeAt, string? Error) Evaluate(MeshStateInfo state, int expectedPeers, DateTimeOffset now)
    {
        var latest = state.Peers.Where(p => p.LastHandshake is not null).Select(p => p.LastHandshake).DefaultIfEmpty(null).Max();
        if (state.Provider == MeshProviderKind.Tailscale)
            return state.Up ? (MeshStatus.Up, null, null) : (MeshStatus.Failed, null, state.Error ?? "tailscale0 is down.");
        if (!state.Up) return (MeshStatus.Failed, latest, state.Error ?? "The mesh interface is down.");

        var fresh = state.Peers.Count(p => p.LastHandshake is { } at && now - at <= HandshakeFreshness);
        if (fresh >= expectedPeers) return (MeshStatus.Up, latest, null);
        return (MeshStatus.Degraded, latest,
            $"{expectedPeers - fresh} of {expectedPeers} peer(s) had no handshake in the last {(int)HandshakeFreshness.TotalMinutes} minutes. " +
            "Check that the UDP port is open between the servers (see the networking guide).");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------

    private static bool Connected(Server server) => server.AgentStatus == AgentStatus.Connected;

    private static string? StripPrefix(string address)
    {
        var slash = address.IndexOf('/');
        var text = slash < 0 ? address : address[..slash];
        return MeshCidr.TryParseAddress(text, out _) ? text : null;
    }

    private static string Fingerprint(MeshApplyCommand c)
    {
        var sb = new StringBuilder().Append(c.Address).Append('|').Append(c.Cidr).Append('|').Append(c.ListenPort);
        foreach (var p in c.Peers) sb.Append('|').Append(p.PublicKey).Append('@').Append(p.Endpoint).Append('=').Append(string.Join(',', p.AllowedIps));
        return sb.ToString();
    }

    private static MeshServerState StateOf(Member m) =>
        new(MeshProvider.WireGuard, m.Address, m.Key, m.Server.MeshStatus, m.Server.MeshError, m.Server.MeshHandshakeAt);

    private static string ReasonOf<T>(CommandOutcome<T> outcome) => outcome.ErrorCode == CommandErrorCode.Unsupported
        ? "This server cannot run the private network: it needs the WireGuard kernel module and the agent needs CAP_NET_ADMIN (or Tailscale). " + outcome.ErrorMessage
        : outcome.ErrorMessage ?? "The agent refused the mesh configuration.";

    /// <summary>
    /// Sends a command. Null when it could not be delivered: an agent that is gone is a transient failure (the next connect reconciles), a
    /// capability the agent lacks is recorded as the server's failure.
    /// </summary>
    private async Task<CommandOutcome<TResult>?> TryAsync<TCommand, TResult>(Run run, Server server, TCommand command, string step, CancellationToken ct)
        where TCommand : IServerCommand<TResult>
    {
        var options = new CommandOptions { IdempotencyKey = run.IdempotencyKey(step), OrganizationId = run.OrganizationId };
        try
        {
            return (await transport.ExecuteAsync<TCommand, TResult>(server.Id, command, options, ct)).Outcome;
        }
        catch (ServerTransportException ex) when (ex.Code == TransportErrors.Unsupported)
        {
            await FailAsync(run, server, "This server's agent cannot run the private network. Update the agent (Servers > Update agent) and make sure the host has the WireGuard kernel module and the agent has CAP_NET_ADMIN (see the networking guide).", ct);
            return null;
        }
        catch (ServerTransportException ex) when (!ex.Transient)
        {
            // The agent refused the command (policy, validation): a deterministic failure of this server.
            await FailAsync(run, server, ex.Message, ct);
            return null;
        }
        catch (ServerTransportException ex)
        {
            run.Transient = true;
            run.Skipped++;
            logger.LogWarning("Mesh: server {ServerId} could not be reached: {Error}", server.Id, ex.Message);
            await run.Log($"{server.Name}: not reachable ({ex.Message}).");
            return null;
        }
    }

    // Overload for commands whose result type the compiler cannot infer from the argument alone.
    private Task<CommandOutcome<Unit>?> TryAsync(Run run, Server server, MeshRemoveCommand command, string step, CancellationToken ct) =>
        TryAsync<MeshRemoveCommand, Unit>(run, server, command, step, ct);

    private Task<CommandOutcome<MeshStateInfo>?> TryAsync(Run run, Server server, MeshApplyCommand command, string step, CancellationToken ct) =>
        TryAsync<MeshApplyCommand, MeshStateInfo>(run, server, command, step, ct);

    private async Task FailAsync(
        Run run, Server server, string error, CancellationToken ct, MeshProvider provider = MeshProvider.WireGuard, string? address = null, string? key = null)
    {
        run.Failed++;
        run.FailedIds.Add(server.Id);
        await run.Log($"{server.Name}: failed: {error}");
        await WriteAsync(server.Id, new MeshServerState(provider, address ?? run.Addresses.GetValueOrDefault(server.Id) ?? server.MeshAddress, key ?? server.MeshPublicKey, MeshStatus.Failed, error, server.MeshHandshakeAt), ct);
    }

    /// <summary>Writes the mesh columns of a server in one statement; the status timestamp moves only when the status changes.</summary>
    internal async Task WriteAsync(Guid serverId, MeshServerState s, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var error = s.Error is { Length: > 500 } e ? e[..500] : s.Error;
        await db.Servers.Where(x => x.Id == serverId).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.MeshProvider, s.Provider)
            .SetProperty(x => x.MeshAddress, s.Address)
            .SetProperty(x => x.MeshPublicKey, s.PublicKey)
            .SetProperty(x => x.MeshStatusChangedAt, x => x.MeshStatus != s.Status ? now : x.MeshStatusChangedAt)
            .SetProperty(x => x.MeshStatus, s.Status)
            .SetProperty(x => x.MeshError, error)
            .SetProperty(x => x.MeshHandshakeAt, s.HandshakeAt), ct);
    }

    private sealed class Member(Server server)
    {
        public Server Server { get; } = server;
        public string? Address { get; set; }
        public string? Key { get; set; }
        public MeshStatus Status { get; set; }
    }

    private sealed class Run(MeshSetting setting, List<Server> servers, Func<string, string> idempotencyKey, Func<string, Task> log, Guid organizationId)
    {
        public MeshSetting Setting { get; } = setting;
        public List<Server> Servers { get; } = servers;
        public Func<string, string> IdempotencyKey { get; } = idempotencyKey;
        public Func<string, Task> Log { get; } = log;
        public Guid OrganizationId { get; } = organizationId;
        public int Rounds { get; set; }
        public int Applied { get; set; }
        public int Failed { get; set; }
        public HashSet<Guid> FailedIds { get; } = [];
        public Dictionary<Guid, string> Addresses { get; } = [];
        public int Removed { get; set; }
        public int Skipped { get; set; }
        public bool Transient { get; set; }
    }
}
