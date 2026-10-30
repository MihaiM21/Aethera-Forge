using System.Net;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Servers;
using Aethera.Api.Http;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Mesh;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Mesh;

/// <summary>One server in the overview of the private network.</summary>
public sealed record MeshServerSummary(
    Guid Id, string Name, MeshProvider Provider, MeshStatus Status, string? Address, string? Error, DateTimeOffset? HandshakeAt, bool AgentConnected);

/// <summary>
/// The instance's private network (ADR 0012): the provider, the range and the WireGuard port, plus every server's state in it.
/// <c>provider</c> is <c>none</c> (default), <c>wireGuard</c> or <c>tailscale</c>.
/// </summary>
public sealed record MeshSettingsResponse(MeshProvider Provider, string Cidr, int ListenPort, IReadOnlyList<MeshServerSummary> Servers);

/// <summary>Replaces the setting. Absent <c>cidr</c> and <c>listenPort</c> mean the defaults (10.210.0.0/16, 51820).</summary>
public sealed record UpdateMeshSettingsRequest
{
    public string? Provider { get; init; }
    public string? Cidr { get; init; }
    public int? ListenPort { get; init; }
}

public sealed record MeshReconcileResponse(bool Queued);

/// <summary>A server's endpoint and port override. Null clears the override (<c>&lt;public ip&gt;:&lt;listen port&gt;</c> and the instance port).</summary>
public sealed record UpdateServerMeshRequest
{
    public string? Endpoint { get; init; }
    public int? ListenPort { get; init; }
}

/// <param name="ServerId">The peer's server, when it is one of the organization's servers.</param>
/// <param name="Endpoint">Where it was last seen (live) or where this server dials it (stored).</param>
/// <param name="LastHandshake">Null when there never was one (or the agent could not be asked).</param>
public sealed record MeshPeerResponse(
    Guid? ServerId, string? Name, string PublicKey, string? Address, string? Endpoint, DateTimeOffset? LastHandshake, long RxBytes, long TxBytes);

/// <summary>
/// A server in the mesh. <c>endpoint</c> and <c>listenPort</c> are the overrides (null = derived); <c>effectiveEndpoint</c> is what its peers dial
/// (null = they wait for it to dial in). <c>peers</c> are read from the agent when it is connected (<c>peersLive</c>).
/// </summary>
public sealed record ServerMeshResponse(
    Guid ServerId, MeshProvider InstanceProvider, MeshProvider Provider, MeshStatus Status, DateTimeOffset? StatusChangedAt, string? Error,
    string? Address, string? PublicKey, string? Endpoint, string? EffectiveEndpoint, int? ListenPort, int EffectiveListenPort, DateTimeOffset? HandshakeAt,
    bool AgentConnected, bool PeersLive, IReadOnlyList<MeshPeerResponse> Peers);

public static class MeshRules
{
    /// <summary>"host:port" or "[v6]:port"; the host is an IP address or a DNS name, the port 1-65535.</summary>
    public static bool IsValidEndpoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 260 || text != text.Trim()) return false;
        string host, port;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0) return false;
            host = text[1..close];
            port = text[(close + 2)..];
            if (!IPAddress.TryParse(host, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || text.IndexOf(':') != colon) return false; // an unbracketed IPv6 address is ambiguous
            host = text[..colon];
            port = text[(colon + 1)..];
            if (!ServerRules.IsValidHost(host)) return false;
        }

        return int.TryParse(port, out var number) && number is >= 1 and <= 65535
            && !(IPAddress.TryParse(host, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)));
    }
}

public sealed class UpdateMeshSettingsValidator : AbstractValidator<UpdateMeshSettingsRequest>
{
    public UpdateMeshSettingsValidator()
    {
        RuleFor(x => x.Provider).NotEmpty().MustBeEnum<UpdateMeshSettingsRequest, MeshProvider>();
        RuleFor(x => x.Cidr).Must(c => MeshCidr.TryParse(c, out _, out _)).WithErrorCode("pattern")
            .WithMessage(x => MeshCidr.TryParse(x.Cidr, out _, out var error) ? "" : error!).When(x => x.Cidr is not null);
        RuleFor(x => x.ListenPort).InclusiveBetween(1, 65535).When(x => x.ListenPort is not null);
    }
}

public sealed class UpdateServerMeshValidator : AbstractValidator<UpdateServerMeshRequest>
{
    public UpdateServerMeshValidator()
    {
        RuleFor(x => x.Endpoint).Must(MeshRules.IsValidEndpoint).WithErrorCode("pattern")
            .WithMessage("Must be host:port, for example 203.0.113.7:51820 or vpn.example.com:51820.").When(x => x.Endpoint is not null);
        RuleFor(x => x.ListenPort).InclusiveBetween(1, 65535).When(x => x.ListenPort is not null);
    }
}

internal static class MeshEndpoints
{
    private static readonly TimeSpan PeerReadTimeout = TimeSpan.FromSeconds(5);

    public static void Map(IEndpointRouteBuilder api)
    {
        var system = api.MapGroup("/system/mesh").WithTags("Network");
        system.MapGet("/", GetSettings).WithName("getMeshSettings")
            .WithSummary("The private network between servers")
            .RequireAdmin(Scopes.Read).Produces<MeshSettingsResponse>();
        system.MapPut("/", UpdateSettings).WithName("updateMeshSettings")
            .WithSummary("Turn the private network on or off and choose its range")
            .WithDescription("`wireGuard`: each agent creates the interface `aethera0` and every server gets an address from `cidr`. `tailscale`: the address of `tailscale0` is used; Aethera does not log in to Tailscale. `none` removes the interfaces Aethera created. Saving queues a reconcile of every server.")
            .Validate<UpdateMeshSettingsRequest>()
            .RequireAdmin(Scopes.Admin).Produces<MeshSettingsResponse>();
        system.MapPost("/reconcile", Reconcile).WithName("reconcileMesh")
            .WithSummary("Apply the private network to every server again")
            .RequireAdmin(Scopes.Admin).Produces<MeshReconcileResponse>(StatusCodes.Status202Accepted);

        var servers = api.MapGroup("/servers/{id:guid}/mesh").WithTags("Network");
        servers.MapGet("/", GetServer).WithName("getServerMesh")
            .WithSummary("A server's address, key, endpoint and peers in the private network")
            .RequireRead().Produces<ServerMeshResponse>();
        servers.MapPut("/", UpdateServer).WithName("updateServerMesh")
            .WithSummary("Set the endpoint and port peers use to reach this server")
            .Validate<UpdateServerMeshRequest>()
            .RequireAdmin(Scopes.ServersWrite).Produces<ServerMeshResponse>();
    }

    // ---- instance ---------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<MeshSettingsResponse>> GetSettings(AetheraDbContext db, ICurrentActor actor, CancellationToken ct) =>
        TypedResults.Ok(await BuildAsync(db, actor.Org(), await MeshSettingsStore.GetAsync(db, ct), ct));

    private static async Task<Ok<MeshSettingsResponse>> UpdateSettings(
        UpdateMeshSettingsRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, MeshReconcileScheduler scheduler, CancellationToken ct)
    {
        var provider = EnumText.Parse<MeshProvider>(request.Provider)!.Value;
        var previous = await MeshSettingsStore.GetAsync(db, ct);
        var setting = new MeshSetting(provider, request.Cidr is null ? MeshCidr.Default : MeshCidr.Parse(request.Cidr).ToString(), request.ListenPort ?? MeshSetting.DefaultListenPort);

        await MeshSettingsStore.SaveAsync(db, setting, actor.UserId, clock.UtcNow, ct);
        await audit.RecordAsync("system.mesh_updated", "system", null,
            new { provider = setting.Provider, setting.Cidr, setting.ListenPort, previousProvider = previous.Provider, previousCidr = previous.Cidr }, ct); // saves the setting
        await scheduler.EnqueueAllAsync("settings", ct);
        return TypedResults.Ok(await BuildAsync(db, actor.Org(), setting, ct));
    }

    private static async Task<Accepted<MeshReconcileResponse>> Reconcile(
        AetheraDbContext db, ICurrentActor actor, IAuditLog audit, MeshReconcileScheduler scheduler, CancellationToken ct)
    {
        var queued = await scheduler.EnqueueAllAsync("manual", ct) > 0;
        await audit.RecordAsync("system.mesh_reconcile_requested", "system", null, new { queued }, ct);
        return TypedResults.Accepted((string?)null, new MeshReconcileResponse(queued));
    }

    private static async Task<MeshSettingsResponse> BuildAsync(AetheraDbContext db, Guid org, MeshSetting setting, CancellationToken ct)
    {
        var servers = await db.ServersOf(org).AsNoTracking().Where(s => s.Transport == ServerTransport.Agent).OrderBy(s => s.Name).ToListAsync(ct);
        return new MeshSettingsResponse(setting.Provider, setting.Cidr, setting.ListenPort,
            servers.Select(s => new MeshServerSummary(s.Id, s.Name, s.MeshProvider, s.MeshStatus, s.MeshAddress, s.MeshError, s.MeshHandshakeAt,
                s.AgentStatus == AgentStatus.Connected)).ToList());
    }

    // ---- per server ----------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<ServerMeshResponse>> GetServer(
        Guid id, AetheraDbContext db, ICurrentActor actor, IServerTransportResolver transport, CancellationToken ct) =>
        TypedResults.Ok(await BuildServerAsync(db, actor.Org(), id, transport, ct));

    private static async Task<Ok<ServerMeshResponse>> UpdateServer(
        Guid id, UpdateServerMeshRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IServerTransportResolver transport,
        MeshReconcileScheduler scheduler, CancellationToken ct)
    {
        var org = actor.Org();
        var server = await db.GetServerAsync(org, id, tracking: false, ct);
        var endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim();

        // Only these two columns: the reconcile owns the others and writes them with its own statements.
        await db.Servers.Where(s => s.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(s => s.MeshEndpoint, endpoint)
            .SetProperty(s => s.MeshListenPort, request.ListenPort), ct);
        await audit.RecordAsync("server.mesh_updated", "server", id, new { name = server.Name, endpoint, listenPort = request.ListenPort }, ct);
        if (endpoint != server.MeshEndpoint || request.ListenPort != server.MeshListenPort) await scheduler.EnqueueAsync(org, "endpoint", ct);
        return TypedResults.Ok(await BuildServerAsync(db, org, id, transport, ct));
    }

    private static async Task<ServerMeshResponse> BuildServerAsync(AetheraDbContext db, Guid org, Guid id, IServerTransportResolver transport, CancellationToken ct)
    {
        var server = await db.GetServerAsync(org, id, tracking: false, ct);
        var setting = await MeshSettingsStore.GetAsync(db, ct);
        var others = await db.ServersOf(org).AsNoTracking()
            .Where(s => s.Id != id && s.Transport == ServerTransport.Agent && s.MeshPublicKey != null && s.MeshAddress != null).OrderBy(s => s.Name).ToListAsync(ct);

        // Stored view of the peers; the agent adds handshakes and traffic when it can be asked.
        var peers = others.Select(o => new MeshPeerResponse(o.Id, o.Name, o.MeshPublicKey!, o.MeshAddress, MeshReconciler.EndpointOf(o, setting.ListenPort), null, 0, 0)).ToList();
        var live = false;
        if (server.AgentStatus == AgentStatus.Connected && server.MeshProvider == MeshProvider.WireGuard && server.MeshStatus != MeshStatus.Disabled)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PeerReadTimeout);
                var options = new CommandOptions { IdempotencyKey = $"mesh:peers:{id:N}:{Guid.NewGuid():N}", OrganizationId = org };
                var outcome = (await transport.ExecuteAsync<MeshStatusCommand, MeshStateInfo>(id, new MeshStatusCommand(MeshProviderKind.WireGuard), options, timeout.Token)).Outcome;
                if (outcome.Succeeded)
                {
                    live = true;
                    peers = Merge(peers, outcome.EnsureSucceeded().Peers);
                }
            }
            catch (Exception ex) when (ex is ServerTransportException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // The stored view is shown; peersLive says it is not live.
            }
        }

        return new ServerMeshResponse(id, setting.Provider, server.MeshProvider, server.MeshStatus, server.MeshStatusChangedAt, server.MeshError, server.MeshAddress,
            server.MeshPublicKey, server.MeshEndpoint, MeshReconciler.EndpointOf(server, setting.ListenPort), server.MeshListenPort, server.MeshListenPort ?? setting.ListenPort,
            server.MeshHandshakeAt, server.AgentStatus == AgentStatus.Connected, live, peers);
    }

    private static List<MeshPeerResponse> Merge(List<MeshPeerResponse> stored, IReadOnlyList<MeshPeerStateInfo> reported)
    {
        var byKey = reported.ToDictionary(p => p.PublicKey, StringComparer.Ordinal);
        var merged = stored.Select(p => byKey.TryGetValue(p.PublicKey, out var r)
            ? p with { Endpoint = r.Endpoint ?? p.Endpoint, LastHandshake = r.LastHandshake, RxBytes = r.RxBytes, TxBytes = r.TxBytes }
            : p).ToList();
        merged.AddRange(reported.Where(r => stored.All(p => p.PublicKey != r.PublicKey))
            .Select(r => new MeshPeerResponse(null, null, r.PublicKey, null, r.Endpoint, r.LastHandshake, r.RxBytes, r.TxBytes)));
        return merged;
    }
}
