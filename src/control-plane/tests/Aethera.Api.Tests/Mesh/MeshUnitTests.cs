using Aethera.Api.Features.Mesh;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Mesh;

namespace Aethera.Api.Tests.Mesh;

/// <summary>Pure logic of the mesh: ranges and allocation, status rules, endpoints, the stored setting. No database.</summary>
public sealed class MeshUnitTests
{
    [Theory]
    [InlineData("10.210.0.0/16", true)]
    [InlineData("10.0.0.0/16", true)]
    [InlineData("172.16.0.0/16", true)]
    [InlineData("172.31.255.0/24", true)]
    [InlineData("192.168.40.0/24", true)]
    [InlineData("192.168.0.0/16", true)]
    [InlineData("10.0.0.0/8", false)]          // too wide
    [InlineData("10.210.0.0/25", false)]       // too narrow
    [InlineData("10.210.0.5/16", false)]       // host bits
    [InlineData("172.32.0.0/16", false)]       // outside 172.16.0.0/12
    [InlineData("100.64.0.0/16", false)]       // CGNAT: Tailscale's range
    [InlineData("8.8.0.0/16", false)]
    [InlineData("0.0.0.0/0", false)]
    [InlineData("fd00::/16", false)]
    [InlineData("10.210.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_range_must_be_a_private_IPv4_network_between_slash_16_and_slash_24(string? text, bool valid)
    {
        Assert.Equal(valid, MeshCidr.TryParse(text, out var cidr, out var error));
        if (valid)
        {
            Assert.Null(error);
            Assert.Equal(text, cidr.ToString());
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Fact]
    public void Allocation_gives_the_lowest_free_host_address_and_never_the_network_or_broadcast_address()
    {
        var cidr = MeshCidr.Parse("10.210.0.0/24");
        Assert.Equal(254, cidr.HostCapacity);
        Assert.Equal("10.210.0.1", MeshCidr.Format(cidr.Allocate([])!.Value));

        var used = new List<uint>();
        for (var i = 0; i < 254; i++)
        {
            var next = cidr.Allocate(used);
            Assert.NotNull(next);
            Assert.True(cidr.IsHost(next!.Value));
            Assert.DoesNotContain(next.Value, used);
            used.Add(next.Value);
        }

        Assert.Equal("10.210.0.1", MeshCidr.Format(used.Min()));
        Assert.Equal("10.210.0.254", MeshCidr.Format(used.Max()));
        Assert.Null(cidr.Allocate(used)); // full
        Assert.Equal("10.210.0.7", MeshCidr.Format(cidr.Allocate(used.Where(u => u != MeshCidr.Parse("10.210.0.0/24").Network + 7))!.Value)); // a freed address is reused

        Assert.Equal(MeshCidr.Parse("10.210.0.0/16").Network + 65535, MeshCidr.Parse("10.210.0.0/16").Broadcast);
        Assert.Equal(65534, MeshCidr.Parse("10.210.0.0/16").HostCapacity);
        // addresses of another range are not "taken" here
        Assert.Equal("10.210.0.1", MeshCidr.Format(cidr.Allocate([MeshCidr.Parse("192.168.0.0/24").Network + 1])!.Value));
    }

    private static MeshStateInfo State(bool up, params DateTimeOffset?[] handshakes) => new(
        MeshProviderKind.WireGuard, "aethera0", up, "key", ["10.210.0.1/16"], 51820,
        handshakes.Select((h, i) => new MeshPeerStateInfo($"peer{i}", null, h, 0, 0)).ToList(), up ? null : "the mesh interface is down");

    [Fact]
    public void Status_is_up_when_every_peer_shook_hands_in_the_last_three_minutes_degraded_otherwise_failed_when_down()
    {
        var now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");

        Assert.Equal(MeshStatus.Up, MeshReconciler.Evaluate(State(true), 0, now).Status);                                   // alone in the mesh
        Assert.Equal(MeshStatus.Up, MeshReconciler.Evaluate(State(true, now.AddSeconds(-30), now.AddMinutes(-2.9)), 2, now).Status);
        var degraded = MeshReconciler.Evaluate(State(true, now.AddSeconds(-30), now.AddMinutes(-3.1)), 2, now);
        Assert.Equal(MeshStatus.Degraded, degraded.Status);
        Assert.Contains("1 of 2", degraded.Error);
        Assert.Equal(now.AddSeconds(-30), degraded.HandshakeAt);                                                             // the latest one
        Assert.Equal(MeshStatus.Degraded, MeshReconciler.Evaluate(State(true, (DateTimeOffset?)null), 1, now).Status);        // never
        Assert.Equal(MeshStatus.Degraded, MeshReconciler.Evaluate(State(true), 1, now).Status);                              // the agent lost the peer
        var down = MeshReconciler.Evaluate(State(false, now), 1, now);
        Assert.Equal(MeshStatus.Failed, down.Status);
        Assert.Contains("down", down.Error);

        var tailscale = new MeshStateInfo(MeshProviderKind.Tailscale, "tailscale0", true, "", ["100.1.2.3/32"], 0, [], null);
        Assert.Equal(MeshStatus.Up, MeshReconciler.Evaluate(tailscale, 5, now).Status);
        Assert.Equal(MeshStatus.Failed, MeshReconciler.Evaluate(tailscale with { Up = false, Error = "tailscale0 is down" }, 5, now).Status);
    }

    private static Server ServerWith(string host, string? publicIp = null, string? endpoint = null, int? port = null) =>
        new() { Name = "s", Host = host, PublicIp = publicIp, MeshEndpoint = endpoint, MeshListenPort = port };

    [Fact]
    public void Peers_dial_the_override_else_the_public_ip_else_the_host_and_never_loopback()
    {
        Assert.Equal("vpn.example.com:443", MeshReconciler.EndpointOf(ServerWith("a.example.com", "203.0.113.1", "vpn.example.com:443", 9999), 51820));
        Assert.Equal("203.0.113.1:51820", MeshReconciler.EndpointOf(ServerWith("a.example.com", "203.0.113.1"), 51820));
        Assert.Equal("203.0.113.1:4000", MeshReconciler.EndpointOf(ServerWith("a.example.com", "203.0.113.1", port: 4000), 51820));
        Assert.Equal("a.example.com:51820", MeshReconciler.EndpointOf(ServerWith("a.example.com"), 51820));
        Assert.Equal("[2001:db8::1]:51820", MeshReconciler.EndpointOf(ServerWith("x", "2001:db8::1"), 51820));
        Assert.Null(MeshReconciler.EndpointOf(ServerWith("localhost"), 51820));          // the control plane's own machine: its peers wait for it
        Assert.Null(MeshReconciler.EndpointOf(ServerWith("127.0.0.1"), 51820));
        Assert.Null(MeshReconciler.EndpointOf(ServerWith("a.example.com", "::1"), 51820));
        Assert.Equal("1.2.3.4:51820", MeshReconciler.EndpointOf(ServerWith("localhost", "1.2.3.4"), 51820));
    }

    [Fact]
    public void The_stored_setting_falls_back_field_by_field()
    {
        Assert.Equal(MeshSetting.Default, MeshSettingsStore.Parse("null"));
        Assert.Equal(MeshSetting.Default, MeshSettingsStore.Parse("not json"));
        Assert.Equal(new MeshSetting(MeshProvider.WireGuard, "172.20.0.0/20", 41820), MeshSettingsStore.Parse("""{"provider":"wireGuard","cidr":"172.20.0.0/20","listenPort":41820}"""));
        Assert.Equal(new MeshSetting(MeshProvider.Tailscale, "10.210.0.0/16", 51820), MeshSettingsStore.Parse("""{"provider":"tailscale","cidr":"8.8.8.0/24","listenPort":0}"""));
        Assert.Equal(MeshProvider.None, MeshSettingsStore.Parse("""{"provider":"99","cidr":"10.210.0.0/16","listenPort":1}""").Provider);
        Assert.False(MeshSetting.Default.Enabled);
        Assert.True(new MeshSetting(MeshProvider.Tailscale, "10.210.0.0/16", 1).Enabled);
    }

    [Theory]
    [InlineData("203.0.113.7:51820", true)]
    [InlineData("vpn.example.com:443", true)]
    [InlineData("[2001:db8::1]:51820", true)]
    [InlineData("203.0.113.7", false)]
    [InlineData("203.0.113.7:0", false)]
    [InlineData("203.0.113.7:65536", false)]
    [InlineData("203.0.113.7:abc", false)]
    [InlineData(":51820", false)]
    [InlineData("2001:db8::1:51820", false)]
    [InlineData("[2001:db8::1]", false)]
    [InlineData("[203.0.113.7]:51820", false)]
    [InlineData("0.0.0.0:51820", false)]
    [InlineData("http://vpn.example.com:443", false)]
    [InlineData(" vpn.example.com:443", false)]
    [InlineData("", false)]
    public void An_endpoint_is_a_host_and_a_port(string text, bool valid) => Assert.Equal(valid, MeshRules.IsValidEndpoint(text));

    [Fact]
    public void The_job_type_of_the_module_is_the_one_the_handler_runs()
    {
        Assert.Equal(MeshModule.ReconcileJobType, new MeshReconcileJobHandler().Type);
        Assert.Equal("mesh.reconcile", MeshModule.ReconcileJobType);
    }
}
