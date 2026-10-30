using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Mesh;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Tests.Mesh;

/// <summary>
/// A fake agent for the mesh: every <c>mesh.apply</c> answers with a public key derived from the address it was given (so two servers never share a
/// key), the interface up and a handshake with every peer unless <see cref="Handshake"/> says otherwise. It records nothing itself: the transport does.
/// </summary>
public sealed class FakeMeshAgents
{
    /// <summary>Addresses (with prefix) whose apply the fake agent refuses because it lacks the capability.</summary>
    public HashSet<string> Unsupported { get; } = [];

    /// <summary>The Tailscale address the fake agents report.</summary>
    private int _tailscale;

    /// <summary>The key of the one peer a <c>mesh.status</c> answer reports.</summary>
    public string StatusPeerKey { get; set; } = "";

    /// <summary>The last handshake the fake agent reports for a peer; null = never. Default: a minute ago.</summary>
    public Func<DateTimeOffset?> Handshake { get; set; } = () => DateTimeOffset.UtcNow.AddSeconds(-60);

    public static string KeyFor(string address) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(address.Split('/')[0])));

    public void Script(FakeServerTransport transport)
    {
        transport
            .On<MeshApplyCommand, MeshStateInfo>(Apply)
            .On<MeshStatusCommand, MeshStateInfo>(status => new MeshStateInfo(
                status.Provider, status.InterfaceName, true, status.Provider == MeshProviderKind.Tailscale ? "" : "status-key", ["10.211.0.99/24"], 51820,
                [new MeshPeerStateInfo(StatusPeerKey, "198.51.100.2:51820", Handshake(), 100, 200)], null))
            .On<MeshRemoveCommand, Unit>(_ => Unit.Value);
    }

    private MeshStateInfo Apply(MeshApplyCommand cmd)
    {
        if (cmd.Provider == MeshProviderKind.Tailscale)
            return new MeshStateInfo(MeshProviderKind.Tailscale, "tailscale0", true, "", [$"100.101.{Interlocked.Increment(ref _tailscale) % 250}.7/32"], 0, [], null);

        if (Unsupported.Contains(cmd.Address))
            throw new ServerTransportException(TransportErrors.Unsupported, "needs capability mesh.wireguard");
        var peers = cmd.Peers.Select(p => new MeshPeerStateInfo(p.PublicKey, p.Endpoint, Handshake(), 10, 20)).ToList();
        return new MeshStateInfo(MeshProviderKind.WireGuard, "aethera0", true, KeyFor(cmd.Address), [cmd.Address], cmd.ListenPort, peers, null);
    }
}

/// <summary>The API with job workers, a <see cref="FakeServerTransport"/> and <see cref="FakeMeshAgents"/> (own database per fixture, so nothing else sees its jobs).</summary>
public sealed class MeshFixture : IDisposable
{
    public MeshFixture()
    {
        Base = new AetheraApiFactory();
        Transport = new FakeServerTransport();
        Agents = new FakeMeshAgents();
        Agents.Script(Transport);
        Factory = Jobs.JobsApiFixture.Configure(Base, builder => builder.UseSetting("Aethera:Mesh:StatusSeconds", "3600"))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IServerTransport>();
                services.AddSingleton<IServerTransport>(Transport);
            }));
    }

    public AetheraApiFactory Base { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeServerTransport Transport { get; }

    public FakeMeshAgents Agents { get; }

    public async Task<Tenant> NewTenantAsync()
    {
        NextRange();
        // The mesh setting is instance wide: servers of finished tests must not join this test's range.
        await WithDbAsync(db => db.Servers.Where(s => s.Lifecycle == ServerLifecycle.Active).ExecuteUpdateAsync(set => set.SetProperty(s => s.Lifecycle, ServerLifecycle.Disabled)));
        return new(Factory, await Factory.SeedIdentityAsync());
    }

    public void Dispose() => Base.Dispose();

    public async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    public async Task<Server> ServerAsync(string id) =>
        await WithDbAsync(db => db.Servers.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(id)));

    /// <summary>A server through the API, made <c>active</c> with a connected agent (what enrollment leaves behind).</summary>
    public async Task<string> ActiveServerAsync(Tenant tenant, string? publicIp = "203.0.113.10", bool connected = true)
    {
        var id = (await tenant.CreateServerAsync(publicIp: publicIp)).Id();
        await WithDbAsync(async db =>
        {
            var server = await db.Servers.FirstAsync(s => s.Id == Guid.Parse(id));
            server.Lifecycle = ServerLifecycle.Active;
            if (connected) server.SetAgentStatus(AgentStatus.Connected, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return 0;
        });
        return id;
    }

    private static int _rangeCounter;
    private int _range;

    /// <summary>The mesh range of the current test: a /24 of its own, because addresses are unique across the instance and every test has its own servers.</summary>
    public string Cidr => $"10.211.{_range}.0/24";

    public string Ip(int host) => $"10.211.{_range}.{host}";

    public void NextRange() => _range = Interlocked.Increment(ref _rangeCounter) % 250;

    public Task SetMeshAsync(Tenant tenant, string provider, string? cidr = null, int? listenPort = null) =>
        tenant.Admin.PutOkAsync("/api/v1/system/mesh", new { provider, cidr = cidr ?? (provider == "wireGuard" ? Cidr : null), listenPort });

    /// <summary>Waits until no <c>mesh.reconcile</c> job of the organization is queued or running, and at least <paramref name="atLeast"/> have finished.</summary>
    public async Task ReconciledAsync(Tenant tenant, int atLeast = 1)
    {
        await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(async db =>
        {
            var jobs = await db.Jobs.AsNoTracking().Where(j => j.Type == "mesh.reconcile" && j.OrganizationId == tenant.Identity.OrganizationId).ToListAsync();
            return jobs.Count(j => j.Status is JobStatus.Succeeded or JobStatus.Failed) >= atLeast && jobs.All(j => j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled);
        }), "the mesh reconcile to finish", timeoutMs: 20000);
        var failed = (await ReconcileJobsAsync(tenant)).FirstOrDefault(j => j.Status == JobStatus.Failed);
        if (failed is not null) throw new Xunit.Sdk.XunitException("The reconcile job failed: " + failed.ErrorJson);
    }

    public Task<List<Job>> ReconcileJobsAsync(Tenant tenant) =>
        WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.Type == "mesh.reconcile" && j.OrganizationId == tenant.Identity.OrganizationId).OrderBy(j => j.CreatedAt).ToListAsync());

    /// <summary>Every command of type <typeparamref name="T"/> sent to a server, oldest first.</summary>
    public List<RecordedCommand> Sent<T>(string serverId)
    {
        lock (Transport.Commands) return Transport.Commands.Where(c => c.ServerId == Guid.Parse(serverId) && c.Command is T).ToList();
    }

    /// <summary>The newest apply sent to a server.</summary>
    public MeshApplyCommand LastApply(string serverId) => (MeshApplyCommand)Sent<MeshApplyCommand>(serverId).Last().Command;
}

/// <summary>Mesh tests change the instance setting of their fixture; one collection keeps them in order.</summary>
[CollectionDefinition(Name)]
public sealed class MeshCollection : ICollectionFixture<MeshFixture>
{
    public const string Name = "mesh";
}

public static class MeshTestExtensions
{
    public static async Task<JsonNode> PutOkAsync(this HttpClient client, string url, object body)
    {
        var response = await client.PutAsync(url, Json.Body(body));
        var node = await response.ReadAsync();
        Assert.True(response.IsSuccessStatusCode, $"PUT {url} -> {(int)response.StatusCode}: {node.ToJsonString()}");
        return node;
    }

    public static List<(string Pointer, string Code)> FieldErrors(this JsonNode problem) =>
        problem["errors"]!.AsArray().Select(e => (e!["pointer"]!.GetValue<string>(), e["code"]!.GetValue<string>())).ToList();
}
