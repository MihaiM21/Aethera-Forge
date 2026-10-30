using System.Net;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Testing;

namespace Aethera.Api.Tests.Mesh;

/// <summary>The REST surface of the private network: settings, per-server state, validation, authorization, audit and the ports' bind scope.</summary>
[Collection(MeshCollection.Name)]
public sealed class MeshApiTests(MeshFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task The_mesh_is_off_by_default_and_the_defaults_are_the_documented_ones()
    {
        var t = await fixture.NewTenantAsync();
        await fixture.SetMeshAsync(t, "none");
        var body = await t.Admin.GetJsonAsync("/api/v1/system/mesh");

        Assert.Equal("none", body["provider"]!.GetValue<string>());
        Assert.Equal("10.210.0.0/16", body["cidr"]!.GetValue<string>());
        Assert.Equal(51820, body["listenPort"]!.GetValue<int>());
    }

    [RequiresDatabaseFact]
    public async Task Saving_validates_the_range_the_provider_and_the_port()
    {
        var t = await fixture.NewTenantAsync();
        async Task<List<(string Pointer, string Code)>> Errors(object body)
        {
            var response = await t.Admin.PutAsync("/api/v1/system/mesh", Json.Body(body));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return (await response.ReadAsync()).FieldErrors();
        }

        Assert.Contains(("/provider", "required"), await Errors(new { provider = "" }));
        Assert.Contains(("/provider", "invalid_enum"), await Errors(new { provider = "ipsec" }));
        Assert.Contains(("/cidr", "pattern"), await Errors(new { provider = "wireGuard", cidr = "8.8.0.0/16" }));      // not private
        Assert.Contains(("/cidr", "pattern"), await Errors(new { provider = "wireGuard", cidr = "10.0.0.0/8" }));      // too wide
        Assert.Contains(("/cidr", "pattern"), await Errors(new { provider = "wireGuard", cidr = "10.210.0.0/28" }));   // too narrow
        Assert.Contains(("/cidr", "pattern"), await Errors(new { provider = "wireGuard", cidr = "10.210.7.0/16" }));   // host bits set
        Assert.Contains(("/cidr", "pattern"), await Errors(new { provider = "wireGuard", cidr = "not-a-range" }));
        Assert.Contains(("/cidr", "pattern"), await Errors(new { provider = "wireGuard", cidr = "172.32.0.0/16" }));   // just outside 172.16/12
        Assert.Contains(("/listenPort", "range"), await Errors(new { provider = "wireGuard", listenPort = 0 }));
        Assert.Contains(("/listenPort", "range"), await Errors(new { provider = "wireGuard", listenPort = 70000 }));
    }

    [RequiresDatabaseFact]
    public async Task Only_administrators_change_the_mesh_and_every_change_is_audited()
    {
        var t = await fixture.NewTenantAsync();
        foreach (var client in new[] { t.Developer, t.Viewer })
        {
            await (await client.GetAsync("/api/v1/system/mesh")).AssertProblemAsync(403, "auth.forbidden");
            await (await client.PutAsync("/api/v1/system/mesh", Json.Body(new { provider = "wireGuard" }))).AssertProblemAsync(403, "auth.forbidden");
            await (await client.PostAsync("/api/v1/system/mesh/reconcile", new { })).AssertProblemAsync(403, "auth.forbidden");
        }

        var readOnlyToken = t.Token(OrganizationRole.Admin, "read");
        await (await readOnlyToken.PutAsync("/api/v1/system/mesh", Json.Body(new { provider = "wireGuard" }))).AssertProblemAsync(403, "auth.insufficient_scope");

        await fixture.SetMeshAsync(t, "wireGuard", "172.20.0.0/20", 41820);
        var events = await t.AuditAsync(action: "system.mesh_updated");
        Assert.Single(events);
        Assert.Contains("172.20.0.0/20", events[0].MetadataJson);
        await fixture.SetMeshAsync(t, "none");
    }

    [RequiresDatabaseFact]
    public async Task A_server_reports_its_mesh_state_and_an_endpoint_override_is_validated_stored_and_audited()
    {
        var t = await fixture.NewTenantAsync();
        var id = await fixture.ActiveServerAsync(t, publicIp: "203.0.113.20");

        var fresh = await t.Viewer.GetJsonAsync($"/api/v1/servers/{id}/mesh");
        Assert.Equal("disabled", fresh["status"]!.GetValue<string>());
        Assert.Null(fresh["address"]);
        Assert.Equal("203.0.113.20:51820", fresh["effectiveEndpoint"]!.GetValue<string>());
        Assert.Equal(51820, fresh["effectiveListenPort"]!.GetValue<int>());
        Assert.Empty(fresh["peers"]!.AsArray());

        async Task<List<(string Pointer, string Code)>> Errors(object body)
        {
            var response = await t.Admin.PutAsync($"/api/v1/servers/{id}/mesh", Json.Body(body));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return (await response.ReadAsync()).FieldErrors();
        }

        Assert.Contains(("/endpoint", "pattern"), await Errors(new { endpoint = "no-port" }));
        Assert.Contains(("/endpoint", "pattern"), await Errors(new { endpoint = "203.0.113.20:0" }));
        Assert.Contains(("/endpoint", "pattern"), await Errors(new { endpoint = "http://x:51820" }));
        Assert.Contains(("/endpoint", "pattern"), await Errors(new { endpoint = "2001:db8::1:51820" }));   // unbracketed IPv6
        Assert.Contains(("/endpoint", "pattern"), await Errors(new { endpoint = "0.0.0.0:51820" }));
        Assert.Contains(("/listenPort", "range"), await Errors(new { listenPort = 70000 }));

        await (await t.Developer.PutAsync($"/api/v1/servers/{id}/mesh", Json.Body(new { endpoint = "vpn.example.com:443" }))).AssertProblemAsync(403, "auth.forbidden");

        var updated = await t.Admin.PutOkAsync($"/api/v1/servers/{id}/mesh", new { endpoint = "vpn.example.com:443", listenPort = 51999 });
        Assert.Equal("vpn.example.com:443", updated["endpoint"]!.GetValue<string>());
        Assert.Equal("vpn.example.com:443", updated["effectiveEndpoint"]!.GetValue<string>());
        Assert.Equal(51999, updated["effectiveListenPort"]!.GetValue<int>());
        var stored = await fixture.ServerAsync(id);
        Assert.Equal("vpn.example.com:443", stored.MeshEndpoint);
        Assert.Equal(51999, stored.MeshListenPort);
        Assert.Single(await t.AuditAsync(id, "server.mesh_updated"));

        var v6 = await t.Admin.PutOkAsync($"/api/v1/servers/{id}/mesh", new { endpoint = "[2001:db8::1]:51820" });
        Assert.Equal("[2001:db8::1]:51820", v6["endpoint"]!.GetValue<string>());
        Assert.Equal(51820, v6["effectiveListenPort"]!.GetValue<int>()); // the port override was cleared with the body

        var cleared = await t.Admin.PutOkAsync($"/api/v1/servers/{id}/mesh", new { });
        Assert.Null(cleared["endpoint"]);
        Assert.Equal("203.0.113.20:51820", cleared["effectiveEndpoint"]!.GetValue<string>());

        // another organization's server is not found
        var other = await fixture.NewTenantAsync();
        await (await other.Admin.GetAsync($"/api/v1/servers/{id}/mesh")).AssertProblemAsync(404, "server.not_found");
    }

    [RequiresDatabaseFact]
    public async Task A_published_port_can_be_private_to_the_mesh_and_a_client_that_does_not_know_the_scope_never_changes_it()
    {
        var t = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await t.CreateStackAsync();
        var url = $"/api/v1/applications/{appId}";

        var created = await t.Developer.PatchOkAsync(url, new
        {
            runtime = new { ports = new object[] { new { containerPort = 5432, publishedPort = 15432, bindScope = "private" }, new { containerPort = 8080, publishedPort = 18080 }, new { containerPort = 9000 } } },
        });
        var ports = created["runtime"]!["ports"]!.AsArray();
        Assert.Equal(["private", "public", "public"], ports.Select(p => p!["bindScope"]!.GetValue<string>()));

        // resending the list without bindScope keeps what is there
        var resent = await t.Developer.PatchOkAsync(url, new
        {
            runtime = new { ports = new object[] { new { containerPort = 5432, publishedPort = 15432 }, new { containerPort = 8080, publishedPort = 18080, bindScope = "loopback" } } },
        });
        Assert.Equal(["private", "loopback"], resent["runtime"]!["ports"]!.AsArray().Select(p => p!["bindScope"]!.GetValue<string>()));

        // without a host port there is nothing to bind
        var response = await t.Developer.PatchAsync(url, new { runtime = new { ports = new object[] { new { containerPort = 5432, bindScope = "private" } } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(("/runtime/ports/0/bindScope", "required"), (await response.ReadAsync()).FieldErrors());

        var bad = await t.Developer.PatchAsync(url, new { runtime = new { ports = new object[] { new { containerPort = 5432, publishedPort = 15432, bindScope = "everywhere" } } } });
        Assert.Contains(("/runtime/ports/0/bindScope", "invalid_enum"), (await bad.ReadAsync()).FieldErrors());

        // dropping the host port drops the scope
        var internalOnly = await t.Developer.PatchOkAsync(url, new { runtime = new { ports = new object[] { new { containerPort = 5432 } } } });
        Assert.Equal("public", internalOnly["runtime"]!["ports"]![0]!["bindScope"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_service_port_can_be_private_to_the_mesh()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync(publicIp: "203.0.113.30");
        var (_, environmentId) = await t.CreateProjectWithEnvironmentAsync();
        var service = await t.Developer.CreateAsync("/api/v1/services", new
        {
            name = Tenant.Unique("db"), environmentId, serverId = server.Id(), templateKey = "postgres",
            runtime = new { ports = new object[] { new { containerPort = 5432, publishedPort = 15432, bindScope = "private" } } },
        });
        Assert.Equal("private", service["runtime"]!["ports"]![0]!["bindScope"]!.GetValue<string>());
        Assert.Equal(15432, service["runtime"]!["ports"]![0]!["publishedPort"]!.GetValue<int>());
    }

    [RequiresDatabaseFact]
    public async Task A_manual_reconcile_is_accepted_and_audited()
    {
        var t = await fixture.NewTenantAsync();
        await fixture.ActiveServerAsync(t);
        try
        {
            await fixture.SetMeshAsync(t, "wireGuard");
            var response = await t.Admin.PostAsync("/api/v1/system/mesh/reconcile", new { });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await fixture.ReconciledAsync(t);
            Assert.Single(await t.AuditAsync(action: "system.mesh_reconcile_requested"));
        }
        finally
        {
            await fixture.SetMeshAsync(t, "none");
        }
    }
}
