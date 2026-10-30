using System.Net;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Mesh;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Mesh;

/// <summary>The reconcile: addresses, keys, peers, rounds, failures, switching and turning off, with fake agents.</summary>
[Collection(MeshCollection.Name)]
public sealed class MeshReconcileTests(MeshFixture fixture)
{
    private async Task<(Tenant Tenant, string A, string B, string C)> ThreeServersAsync()
    {
        var t = await fixture.NewTenantAsync();
        var a = await fixture.ActiveServerAsync(t, "203.0.113.1");
        var b = await fixture.ActiveServerAsync(t, "203.0.113.2");
        var c = await fixture.ActiveServerAsync(t, publicIp: null);
        return (t, a, b, c);
    }

    [RequiresDatabaseFact]
    public async Task Turning_the_mesh_on_gives_every_server_a_unique_stable_address_and_connects_all_of_them()
    {
        var (t, a, b, c) = await ThreeServersAsync();
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard");
            await fixture.ReconciledAsync(t);

            var servers = new[] { await fixture.ServerAsync(a), await fixture.ServerAsync(b), await fixture.ServerAsync(c) };
            Assert.Equal([fixture.Ip(1), fixture.Ip(2), fixture.Ip(3)], servers.Select(s => s.MeshAddress));
            Assert.All(servers, s =>
            {
                Assert.Equal(MeshProvider.WireGuard, s.MeshProvider);
                Assert.Equal(MeshStatus.Up, s.MeshStatus);
                Assert.Equal(FakeMeshAgents.KeyFor(s.MeshAddress!), s.MeshPublicKey);
                Assert.Null(s.MeshError);
                Assert.NotNull(s.MeshHandshakeAt);
            });

            // The last apply of the first server lists the other two with their /32, their endpoints and the keepalive.
            var apply = fixture.LastApply(a);
            Assert.Equal(MeshProviderKind.WireGuard, apply.Provider);
            Assert.Equal("aethera0", apply.InterfaceName);
            Assert.Equal(fixture.Ip(1) + "/24", apply.Address);
            Assert.Equal(fixture.Cidr, apply.Cidr);
            Assert.Equal(51820, apply.ListenPort);
            Assert.Equal(2, apply.Peers.Count);
            var peerB = apply.Peers.Single(p => p.PublicKey == servers[1].MeshPublicKey);
            Assert.Equal([fixture.Ip(2) + "/32"], peerB.AllowedIps);
            Assert.Equal("203.0.113.2:51820", peerB.Endpoint);
            Assert.Equal(25, peerB.PersistentKeepaliveSeconds);
            Assert.Equal(b, peerB.Name);
            var peerC = apply.Peers.Single(p => p.PublicKey == servers[2].MeshPublicKey);
            Assert.Equal($"{servers[2].Host}:51820", peerC.Endpoint); // no public IP: the host name
            Assert.DoesNotContain(apply.Peers, p => p.PublicKey == servers[0].MeshPublicKey);

            // Rounds are bounded and every agent command carries its own idempotency key.
            var jobs = await fixture.ReconcileJobsAsync(t);
            Assert.All(jobs, j => Assert.Equal(JobStatus.Succeeded, j.Status));
            Assert.Equal(2, System.Text.Json.Nodes.JsonNode.Parse(jobs[0].ResultJson!)!["rounds"]!.GetValue<int>());
            var keys = fixture.Transport.Commands.Where(r => new[] { a, b, c }.Contains(r.ServerId.ToString()) && r.Command is MeshApplyCommand).Select(r => r.Options.IdempotencyKey).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.All(keys, k => Assert.Contains(":mesh-apply-", k));

            // A second reconcile changes nothing.
            var before = servers.Select(s => (s.MeshAddress, s.MeshPublicKey)).ToList();
            var response = await t.Admin.PostAsync("/api/v1/system/mesh/reconcile", new { });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await fixture.ReconciledAsync(t, atLeast: 2);
            var after = new[] { await fixture.ServerAsync(a), await fixture.ServerAsync(b), await fixture.ServerAsync(c) }.Select(s => (s.MeshAddress, s.MeshPublicKey)).ToList();
            Assert.Equal(before, after);

            // The per-server view: peers come with their handshake from the agent.
            fixture.Agents.StatusPeerKey = servers[1].MeshPublicKey!;
            var view = await t.Viewer.GetJsonAsync($"/api/v1/servers/{a}/mesh");
            Assert.Equal("up", view["status"]!.GetValue<string>());
            Assert.Equal(fixture.Ip(1), view["address"]!.GetValue<string>());
            Assert.Equal(servers[0].MeshPublicKey, view["publicKey"]!.GetValue<string>());
            Assert.True(view["peersLive"]!.GetValue<bool>());
            var peers = view["peers"]!.AsArray();
            Assert.Equal(2, peers.Count);
            var live = peers.Single(p => p!["serverId"]!.GetValue<string>() == b);
            Assert.NotNull(live!["lastHandshake"]);
            Assert.Equal(100, live["rxBytes"]!.GetValue<long>());
            Assert.Null(peers.Single(p => p!["serverId"]!.GetValue<string>() == c)!["lastHandshake"]);

            // Off: the interfaces are removed, statuses cleared, addresses and keys stay.
            await fixture.SetMeshAsync(t, "none");
            await GatewayFixture.EventuallyAsync(async () => (await fixture.ServerAsync(c)).MeshStatus == MeshStatus.Disabled, "the mesh to be turned off", 20000);
            foreach (var id in new[] { a, b, c })
            {
                var server = await fixture.ServerAsync(id);
                Assert.Equal(MeshStatus.Disabled, server.MeshStatus);
                Assert.Equal(MeshProvider.None, server.MeshProvider);
                Assert.NotNull(server.MeshAddress);
                Assert.NotNull(server.MeshPublicKey);
                Assert.Single(fixture.Sent<MeshRemoveCommand>(id));
            }
        }
        finally
        {
            await fixture.SetMeshAsync(t, "none");
        }
    }

    [RequiresDatabaseFact]
    public async Task A_server_that_cannot_run_wireguard_fails_alone_and_is_not_a_peer()
    {
        var (t, a, b, c) = await ThreeServersAsync();
        fixture.Agents.Unsupported.Add(fixture.Ip(2) + "/24");
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard");
            await fixture.ReconciledAsync(t);

            var failed = await fixture.ServerAsync(b);
            Assert.Equal(MeshStatus.Failed, failed.MeshStatus);
            Assert.Contains("cannot run the private network", failed.MeshError);
            Assert.Null(failed.MeshPublicKey);
            Assert.Equal(fixture.Ip(2), failed.MeshAddress); // still allocated
            foreach (var id in new[] { a, c })
            {
                var server = await fixture.ServerAsync(id);
                Assert.Equal(MeshStatus.Up, server.MeshStatus);
                Assert.Single(fixture.LastApply(id).Peers); // the other healthy one only
            }

            var view = await t.Viewer.GetJsonAsync($"/api/v1/servers/{b}/mesh");
            Assert.Equal("failed", view["status"]!.GetValue<string>());
            Assert.Contains("WireGuard", view["error"]!.GetValue<string>());
        }
        finally
        {
            fixture.Agents.Unsupported.Clear();
            await fixture.SetMeshAsync(t, "none");
        }
    }

    [RequiresDatabaseFact]
    public async Task A_peer_without_a_handshake_is_degraded_and_the_status_refresh_recovers_it()
    {
        var t = await fixture.NewTenantAsync();
        var a = await fixture.ActiveServerAsync(t, "203.0.113.1");
        var b = await fixture.ActiveServerAsync(t, "203.0.113.2");
        fixture.Agents.Handshake = () => null;
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard");
            await fixture.ReconciledAsync(t);
            var server = await fixture.ServerAsync(a);
            Assert.Equal(MeshStatus.Degraded, server.MeshStatus);
            Assert.Contains("UDP", server.MeshError);
            Assert.Null(server.MeshHandshakeAt);

            // The refresh reads the status of every connected server and judges the handshakes: older than three minutes is silent.
            var monitor = fixture.Factory.Services.GetRequiredService<MeshMonitor>();
            fixture.Agents.Handshake = () => DateTimeOffset.UtcNow.AddMinutes(-10);
            await monitor.RefreshStatusAsync(CancellationToken.None);
            Assert.Equal(MeshStatus.Degraded, (await fixture.ServerAsync(a)).MeshStatus);

            fixture.Agents.Handshake = () => DateTimeOffset.UtcNow.AddSeconds(-20);
            await monitor.RefreshStatusAsync(CancellationToken.None);
            foreach (var id in new[] { a, b })
            {
                var refreshed = await fixture.ServerAsync(id);
                Assert.Equal(MeshStatus.Up, refreshed.MeshStatus);
                Assert.Null(refreshed.MeshError);
                Assert.NotNull(refreshed.MeshHandshakeAt);
                Assert.True(refreshed.MeshStatusChangedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
            }
        }
        finally
        {
            fixture.Agents.Handshake = () => DateTimeOffset.UtcNow.AddSeconds(-60);
            await fixture.SetMeshAsync(t, "none");
        }
    }

    [RequiresDatabaseFact]
    public async Task Tailscale_stores_the_tailscale_address_removes_a_previous_wireguard_interface_and_reports_a_missing_tailscale0()
    {
        var t = await fixture.NewTenantAsync();
        var a = await fixture.ActiveServerAsync(t, "203.0.113.1");
        var b = await fixture.ActiveServerAsync(t, "203.0.113.2");
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard");
            await fixture.ReconciledAsync(t);
            Assert.Equal(fixture.Ip(1), (await fixture.ServerAsync(a)).MeshAddress);

            await fixture.SetMeshAsync(t, "tailscale");
            await GatewayFixture.EventuallyAsync(async () => (await fixture.ServerAsync(b)).MeshProvider == MeshProvider.Tailscale, "the switch to Tailscale", 20000);
            foreach (var id in new[] { a, b })
            {
                var server = await fixture.ServerAsync(id);
                Assert.StartsWith("100.101.", server.MeshAddress);
                Assert.Equal(MeshStatus.Up, server.MeshStatus);
                Assert.Null(server.MeshPublicKey); // WireGuard keys are of no use to a tailnet
                Assert.Single(fixture.Sent<MeshRemoveCommand>(id));
                Assert.Equal(MeshProviderKind.Tailscale, fixture.LastApply(id).Provider);
                Assert.Empty(fixture.LastApply(id).Peers);
            }

            fixture.Transport.Fail<MeshApplyCommand, MeshStateInfo>(CommandErrorCode.NotFound, "The interface tailscale0 was not found: is Tailscale installed and running on this server?");
            var jobs = (await fixture.ReconcileJobsAsync(t)).Count;
            await t.Admin.PostAsync("/api/v1/system/mesh/reconcile", new { });
            await fixture.ReconciledAsync(t, atLeast: jobs + 1);
            var failed = await fixture.ServerAsync(a);
            Assert.Equal(MeshStatus.Failed, failed.MeshStatus);
            Assert.Contains("tailscale0 was not found", failed.MeshError);
        }
        finally
        {
            fixture.Agents.Script(fixture.Transport);
            await fixture.SetMeshAsync(t, "none");
        }
    }

    [RequiresDatabaseFact]
    public async Task An_endpoint_override_reaches_the_peers()
    {
        var t = await fixture.NewTenantAsync();
        var a = await fixture.ActiveServerAsync(t, "203.0.113.1");
        var b = await fixture.ActiveServerAsync(t, "203.0.113.2");
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard", listenPort: 41820);
            await fixture.ReconciledAsync(t);
            Assert.Equal("203.0.113.2:41820", fixture.LastApply(a).Peers.Single().Endpoint);
            Assert.Equal(41820, fixture.LastApply(a).ListenPort);

            var jobs = (await fixture.ReconcileJobsAsync(t)).Count;
            await t.Admin.PutOkAsync($"/api/v1/servers/{b}/mesh", new { endpoint = "vpn.example.com:443", listenPort = 443 });
            await fixture.ReconciledAsync(t, atLeast: jobs + 1);
            Assert.Equal("vpn.example.com:443", fixture.LastApply(a).Peers.Single().Endpoint);
            Assert.Equal(443, fixture.LastApply(b).ListenPort);
            Assert.Equal(41820, fixture.LastApply(a).ListenPort);
        }
        finally
        {
            await fixture.SetMeshAsync(t, "none");
        }
    }

    [RequiresDatabaseFact]
    public async Task A_server_that_connects_later_is_reconciled_and_joins_its_peers()
    {
        var t = await fixture.NewTenantAsync();
        var a = await fixture.ActiveServerAsync(t, "203.0.113.1");
        var b = await fixture.ActiveServerAsync(t, "203.0.113.2", connected: false);
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard");
            await fixture.ReconciledAsync(t);
            var waiting = await fixture.ServerAsync(b);
            Assert.Equal(MeshStatus.Pending, waiting.MeshStatus);
            Assert.Equal(fixture.Ip(2), waiting.MeshAddress); // allocated while offline
            Assert.Null(waiting.MeshPublicKey);
            Assert.Empty(fixture.Sent<MeshApplyCommand>(b));
            Assert.Empty(fixture.LastApply(a).Peers);

            // the agent connects: the hook queues a reconcile (after a short debounce, once, however often it fires)
            await fixture.WithDbAsync(async db =>
            {
                var server = await db.Servers.FirstAsync(s => s.Id == Guid.Parse(b));
                server.SetAgentStatus(AgentStatus.Connected, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();
                return 0;
            });
            var jobs = (await fixture.ReconcileJobsAsync(t)).Count;
            var hooks = fixture.Factory.Services.GetServices<IAgentConnectedHook>().ToList();
            Assert.Contains(hooks, h => h is MeshConnectedHook);
            foreach (var hook in hooks) { hook.OnConnected(Guid.Parse(b)); hook.OnConnected(Guid.Parse(b)); }
            await fixture.ReconciledAsync(t, atLeast: jobs + 1);

            var joined = await fixture.ServerAsync(b);
            Assert.Equal(MeshStatus.Up, joined.MeshStatus);
            Assert.NotNull(joined.MeshPublicKey);
            Assert.Equal(joined.MeshPublicKey, fixture.LastApply(a).Peers.Single().PublicKey);
            Assert.Equal((await fixture.ServerAsync(a)).MeshPublicKey, fixture.LastApply(b).Peers.Single().PublicKey);
            Assert.True((await fixture.ReconcileJobsAsync(t)).Count - jobs <= 2, "the double connect must not queue a job per call");
        }
        finally
        {
            await fixture.SetMeshAsync(t, "none");
        }
    }

    [RequiresDatabaseFact]
    public async Task The_hook_ignores_servers_while_the_mesh_is_off()
    {
        var t = await fixture.NewTenantAsync();
        var a = await fixture.ActiveServerAsync(t);
        await fixture.SetMeshAsync(t, "none");
        var jobs = (await fixture.ReconcileJobsAsync(t)).Count;
        foreach (var hook in fixture.Factory.Services.GetServices<IAgentConnectedHook>()) hook.OnConnected(Guid.Parse(a));
        await Task.Delay(500);
        Assert.Equal(jobs, (await fixture.ReconcileJobsAsync(t)).Count);
    }

    [RequiresDatabaseFact]
    public async Task A_deleted_server_disappears_from_the_peers_of_the_others()
    {
        var (t, a, b, c) = await ThreeServersAsync();
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard");
            await fixture.ReconciledAsync(t);
            Assert.Equal(2, fixture.LastApply(a).Peers.Count);

            var monitor = fixture.Factory.Services.GetRequiredService<MeshMonitor>();
            var jobs = (await fixture.ReconcileJobsAsync(t)).Count;
            await monitor.TickAsync(CancellationToken.None);           // first look: records what the servers were given
            await fixture.ReconciledAsync(t, atLeast: jobs + 1);
            jobs = (await fixture.ReconcileJobsAsync(t)).Count;
            await monitor.TickAsync(CancellationToken.None);           // nothing changed: no job
            await Task.Delay(300);
            Assert.Equal(jobs, (await fixture.ReconcileJobsAsync(t)).Count);

            var name = (await fixture.ServerAsync(c)).Name;
            var deleted = await t.Admin.DeleteAsync($"/api/v1/servers/{c}?confirm={Uri.EscapeDataString(name)}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            await monitor.TickAsync(CancellationToken.None);           // drift: queues a reconcile
            await fixture.ReconciledAsync(t, atLeast: jobs + 1);
            Assert.Single(fixture.LastApply(a).Peers);
            Assert.Single(fixture.LastApply(b).Peers);
            Assert.Equal((await fixture.ServerAsync(b)).MeshPublicKey, fixture.LastApply(a).Peers.Single().PublicKey);
            Assert.Equal("drift", System.Text.Json.Nodes.JsonNode.Parse((await fixture.ReconcileJobsAsync(t)).Last().PayloadJson)!["reason"]!.GetValue<string>());
        }
        finally
        {
            await fixture.SetMeshAsync(t, "none");
        }
    }

    [RequiresDatabaseFact]
    public async Task Changing_the_range_gives_servers_addresses_in_the_new_one()
    {
        var t = await fixture.NewTenantAsync();
        var a = await fixture.ActiveServerAsync(t, "203.0.113.1");
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard");
            await fixture.ReconciledAsync(t);
            Assert.Equal(fixture.Ip(1), (await fixture.ServerAsync(a)).MeshAddress);

            await fixture.SetMeshAsync(t, "wireGuard", "192.168.40.0/24");
            await GatewayFixture.EventuallyAsync(async () => (await fixture.ServerAsync(a)).MeshAddress == "192.168.40.1", "the new range", 20000);
            Assert.Equal("192.168.40.1/24", fixture.LastApply(a).Address);
            Assert.Equal("192.168.40.0/24", fixture.LastApply(a).Cidr);
        }
        finally
        {
            await fixture.SetMeshAsync(t, "none");
        }
    }
}
