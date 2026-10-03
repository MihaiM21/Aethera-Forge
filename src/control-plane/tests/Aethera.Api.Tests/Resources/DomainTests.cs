using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

[Collection(ResourcesCollection.Name)]
public sealed class DomainTests(ResourcesFixture fixture)
{
    private static string Host(string label = "app") => $"{label}-{Guid.NewGuid().ToString("N")[..8]}.example.com";

    [RequiresDatabaseFact]
    public async Task Create_NormalizesHostnameAndPath_AndTheFirstDomainIsPrimary()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, _, _, appId) = await tenant.CreateStackAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var response = await tenant.Developer.PostAsync("/api/v1/domains", new
        {
            workloadId = appId, hostname = $"  Bücher-{suffix}.Example.COM. ", pathPrefix = "/Shop/", httpsEnabled = false, targetPort = 8080,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var domain = await response.ReadAsync();
        Assert.Equal($"/api/v1/domains/{domain.Id()}", response.Headers.Location!.ToString());
        Assert.Equal(PunyOf($"bücher-{suffix}").ToLowerInvariant() + ".example.com", domain["hostname"]!.GetValue<string>());
        Assert.StartsWith("xn--", domain["hostname"]!.GetValue<string>());
        Assert.Equal("/Shop", domain["pathPrefix"]!.GetValue<string>());
        Assert.False(domain["httpsEnabled"]!.GetValue<bool>());
        Assert.Equal(8080, domain["targetPort"]!.GetValue<int>());
        Assert.True(domain["isPrimary"]!.GetValue<bool>());
        Assert.Equal(appId, domain["workloadId"]!.GetValue<string>());
        Assert.Equal(serverId, domain["serverId"]!.GetValue<string>());
        Assert.Equal("none", domain["certificate"]!["status"]!.GetValue<string>());
        Assert.Equal("unknown", domain["dns"]!["status"]!.GetValue<string>());
        Assert.Null(domain["dns"]!["checkedAt"]);
        Assert.Empty(domain["dns"]!["resolvedIps"]!.AsArray());

        var second = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host("second") });
        Assert.False(second["isPrimary"]!.GetValue<bool>());
        Assert.Equal("/", second["pathPrefix"]!.GetValue<string>());
        Assert.True(second["httpsEnabled"]!.GetValue<bool>());
        Assert.Contains(await tenant.AuditAsync(domain.Id()), e => e.Action == "domain.created");
    }

    private static string PunyOf(string name) => new System.Globalization.IdnMapping().GetAscii(name).Split('.')[0];

    [RequiresDatabaseFact]
    public async Task Create_WildcardAtTheLeftmostLabel()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = $"*.Wild-{Guid.NewGuid().ToString("N")[..6]}.example.com" });
        Assert.StartsWith("*.wild-", domain["hostname"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Create_RejectsInvalidHostnames_WithDomainInvalidHost()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();

        foreach (var bad in new[]
        {
            "-bad.example.com", "bad-.example.com", "under_score.example.com", "a..b.example.com", "exa mple.com", "http://example.com", "example.com:8080",
            "example.com/path", "192.168.1.10", "foo.*.example.com", "*example.com", "*.com", "*", new string('a', 64) + ".example.com", "example.123",
        })
        {
            var errors = await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = bad })).ValidationErrorsAsync();
            Assert.True(errors.Contains(("/hostname", "domain.invalid_host")), $"'{bad}' was accepted");
        }

        Assert.Contains(("/hostname", "required"), await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId })).ValidationErrorsAsync());
        Assert.Contains(("/pathPrefix", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), pathPrefix = "no-slash" })).ValidationErrorsAsync());
        Assert.Contains(("/targetPort", "range"), await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), targetPort = 0 })).ValidationErrorsAsync());
        Assert.Contains(("/workloadId", "not_found"), await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = Guid.NewGuid(), hostname = Host() })).ValidationErrorsAsync());
        Assert.Contains(("/hostname", "domain.invalid_host"), await (await tenant.Developer.PatchAsync($"/api/v1/domains/{(await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host() })).Id()}", new { hostname = "bad host" })).ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task Duplicates_AreConflicts_AmongActiveDomainsOnly()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var (_, _, _, otherAppId) = await other.CreateStackAsync();
        var host = Host("dup");

        var first = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = host });
        await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = host.ToUpperInvariant() })).AssertProblemAsync(409, "domain.already_exists");
        await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = host + "." })).AssertProblemAsync(409, "domain.already_exists");
        await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = host, pathPrefix = "/" })).AssertProblemAsync(409, "domain.already_exists");
        // Hostnames are unique across the whole installation, not just the organization.
        await (await other.Developer.PostAsync("/api/v1/domains", new { workloadId = otherAppId, hostname = host })).AssertProblemAsync(409, "domain.already_exists");

        // A different path prefix is a different route.
        var api = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = host, pathPrefix = "/api" });
        await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = host, pathPrefix = "/api/" })).AssertProblemAsync(409, "domain.already_exists");
        await (await tenant.Developer.PatchAsync($"/api/v1/domains/{api.Id()}", new { pathPrefix = "/" })).AssertProblemAsync(409, "domain.already_exists");

        // After a delete the name is free again, for anyone.
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/domains/{first.Id()}")).StatusCode);
        await other.Developer.CreateAsync("/api/v1/domains", new { workloadId = otherAppId, hostname = host });
    }

    [RequiresDatabaseFact]
    public async Task Patch_ChangesRoutingFlags_FlipsPrimary_AndResetsStatusOnANewName()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var one = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host("one") });
        var two = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host("two") });
        Assert.True(one["isPrimary"]!.GetValue<bool>());
        Assert.False(two["isPrimary"]!.GetValue<bool>());

        var flipped = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{two.Id()}", new { isPrimary = true, targetPort = 9000, httpsEnabled = false });
        Assert.True(flipped["isPrimary"]!.GetValue<bool>());
        Assert.Equal(9000, flipped["targetPort"]!.GetValue<int>());
        Assert.False((await tenant.Viewer.GetJsonAsync($"/api/v1/domains/{one.Id()}"))["isPrimary"]!.GetValue<bool>());
        var cleared = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{two.Id()}", new { targetPort = (int?)null });
        Assert.Null(cleared["targetPort"]);

        // Verify, then rename: the check result belongs to the old name.
        tenant.Dns(fixture, one["hostname"]!.GetValue<string>(), "203.0.113.10");
        await tenant.Developer.PostAsync($"/api/v1/domains/{one.Id()}/verify-dns", null);
        Assert.Equal("ok", (await tenant.Viewer.GetJsonAsync($"/api/v1/domains/{one.Id()}"))["dns"]!["status"]!.GetValue<string>());
        var renamed = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{one.Id()}", new { hostname = Host("renamed").ToUpperInvariant() });
        Assert.StartsWith("renamed-", renamed["hostname"]!.GetValue<string>());
        Assert.Equal("unknown", renamed["dns"]!["status"]!.GetValue<string>());
        Assert.Null(renamed["dns"]!["checkedAt"]);
        Assert.Empty(renamed["dns"]!["resolvedIps"]!.AsArray());

        Assert.Contains(("/hostname", "required"), await (await tenant.Developer.PatchAsync($"/api/v1/domains/{one.Id()}", new { hostname = (string?)null })).ValidationErrorsAsync());
        await (await tenant.Developer.PatchAsync($"/api/v1/domains/{one.Id()}", new { hostname = two["hostname"] })).AssertProblemAsync(409, "domain.already_exists");
    }

    [RequiresDatabaseFact]
    public async Task VerifyDns_ComparesResolvedAddressesWithTheServersPublicIp()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync(); // the server's public IP is 203.0.113.10
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host("verify") });
        var hostname = domain["hostname"]!.GetValue<string>();
        var url = $"/api/v1/domains/{domain.Id()}/verify-dns";

        // Nothing resolves yet.
        var missing = await (await tenant.Developer.PostAsync(url, null)).ReadAsync();
        Assert.Equal("missing", missing["status"]!.GetValue<string>());
        Assert.Equal(["203.0.113.10"], missing["expectedIps"]!.AsArray().Select(i => i!.GetValue<string>()));
        Assert.Empty(missing["resolvedIps"]!.AsArray());

        // Resolves to somebody else.
        tenant.Dns(fixture, hostname, "198.51.100.7", "198.51.100.8");
        var mismatch = await (await tenant.Developer.PostAsync(url, null)).ReadAsync();
        Assert.Equal("mismatch", mismatch["status"]!.GetValue<string>());
        Assert.Equal(["198.51.100.7", "198.51.100.8"], mismatch["resolvedIps"]!.AsArray().Select(i => i!.GetValue<string>()));

        // Resolves to the server (among others): ok. The result is stored on the domain.
        tenant.Dns(fixture, hostname, "198.51.100.7", "203.0.113.10", "2001:db8::1");
        var response = await tenant.Developer.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ok = await response.ReadAsync();
        Assert.Equal("ok", ok["status"]!.GetValue<string>());
        Assert.Equal(domain.Id(), ok["domainId"]!.GetValue<string>());
        Assert.Equal(hostname, ok["hostname"]!.GetValue<string>());
        Assert.Equal(["198.51.100.7", "2001:db8::1", "203.0.113.10"], ok["resolvedIps"]!.AsArray().Select(i => i!.GetValue<string>()).Order(StringComparer.Ordinal));
        var checkedAt = ok["checkedAt"]!.GetValue<DateTimeOffset>();
        Assert.True(DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromMinutes(1));

        var stored = (await tenant.Viewer.GetJsonAsync($"/api/v1/domains/{domain.Id()}"))["dns"]!;
        Assert.Equal("ok", stored["status"]!.GetValue<string>());
        Assert.Equal(3, stored["resolvedIps"]!.AsArray().Count);
        Assert.Equal(checkedAt, stored["checkedAt"]!.GetValue<DateTimeOffset>());
        Assert.Contains(await tenant.AuditAsync(domain.Id()), e => e.Action == "domain.dns_verified" && e.MetadataJson.Contains("ok"));

        // A failing lookup is an error status, not a failed request; the previous good result is replaced by the error.
        fixture.Dns.Failure = new TimeoutException("resolver down");
        try
        {
            var failed = await (await tenant.Developer.PostAsync(url, null)).ReadAsync();
            Assert.Equal("error", failed["status"]!.GetValue<string>());
            Assert.DoesNotContain("resolver down", failed.ToJsonString());
        }
        finally
        {
            fixture.Dns.Failure = null;
        }

        Assert.Equal("error", (await tenant.Viewer.GetJsonAsync($"/api/v1/domains/{domain.Id()}"))["dns"]!["status"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task VerifyDns_UsesTheServerHostWhenItIsAnIp_AndReportsUnknownWithoutAnyAddress()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, envId, _, _) = await tenant.CreateStackAsync();

        var ipHost = await tenant.Admin.CreateAsync("/api/v1/servers", new { name = "by-ip", host = "192.0.2.55" });
        var app = await tenant.CreateApplicationAsync(envId, ipHost.Id());
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = app.Id(), hostname = Host("byip") });
        tenant.Dns(fixture, domain["hostname"]!.GetValue<string>(), "192.0.2.55");
        var ok = await (await tenant.Developer.PostAsync($"/api/v1/domains/{domain.Id()}/verify-dns", null)).ReadAsync();
        Assert.Equal("ok", ok["status"]!.GetValue<string>());
        Assert.Equal(["192.0.2.55"], ok["expectedIps"]!.AsArray().Select(i => i!.GetValue<string>()));

        var named = await tenant.Admin.CreateAsync("/api/v1/servers", new { name = "by-name", host = "box.example.net" });
        var app2 = await tenant.CreateApplicationAsync(envId, named.Id());
        var domain2 = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = app2.Id(), hostname = Host("byname") });
        tenant.Dns(fixture, domain2["hostname"]!.GetValue<string>(), "192.0.2.9");
        var unknown = await (await tenant.Developer.PostAsync($"/api/v1/domains/{domain2.Id()}/verify-dns", null)).ReadAsync();
        Assert.Equal("unknown", unknown["status"]!.GetValue<string>());
        Assert.Empty(unknown["expectedIps"]!.AsArray());
        Assert.Contains("publicIp", unknown["message"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task VerifyDns_ProbesAWildcardsCoveredName()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = $"*.wild-{suffix}.example.com" });
        tenant.Dns(fixture, $"aethera-dns-check.wild-{suffix}.example.com", "203.0.113.10");
        Assert.Equal("ok", (await (await tenant.Developer.PostAsync($"/api/v1/domains/{domain.Id()}/verify-dns", null)).ReadAsync())["status"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task NestedRoutes_ListAndCreateUnderApplicationsAndServices()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "grafana", environmentId = envId, serverId, templateKey = "grafana" });

        var onApp = await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/domains", new { hostname = Host("nested") });
        Assert.Equal(appId, onApp["workloadId"]!.GetValue<string>());
        var onService = await tenant.Developer.CreateAsync($"/api/v1/services/{service.Id()}/domains", new { hostname = Host("grafana") });
        Assert.Equal(service.Id(), onService["workloadId"]!.GetValue<string>());

        Assert.Equal([onApp.Id()], (await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/domains"))["items"]!.AsArray().Select(d => d!.Id()));
        Assert.Equal([onService.Id()], (await tenant.Viewer.GetJsonAsync($"/api/v1/services/{service.Id()}/domains"))["items"]!.AsArray().Select(d => d!.Id()));
        Assert.Equal(2, (await tenant.Viewer.GetJsonAsync("/api/v1/domains"))["items"]!.AsArray().Count);
        Assert.Equal([onApp.Id()], (await tenant.Viewer.GetJsonAsync($"/api/v1/domains?workloadId={appId}"))["items"]!.AsArray().Select(d => d!.Id()));
        Assert.Equal([onService.Id()], (await tenant.Viewer.GetJsonAsync("/api/v1/domains?q=GRAFANA"))["items"]!.AsArray().Select(d => d!.Id()));
        Assert.Equal(2, (await tenant.Viewer.GetJsonAsync("/api/v1/domains?dnsStatus=unknown"))["items"]!.AsArray().Count);
        Assert.Empty((await tenant.Viewer.GetJsonAsync("/api/v1/domains?dnsStatus=ok"))["items"]!.AsArray());

        await (await tenant.Viewer.GetAsync($"/api/v1/services/{appId}/domains")).AssertProblemAsync(404, "service.not_found");
        var mismatch = await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/domains", new { hostname = Host(), workloadId = service.Id() });
        Assert.Contains(("/workloadId", "mismatch"), await mismatch.ValidationErrorsAsync());

        // Deleting the application takes its domains with it.
        var slug = (await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}"))["slug"]!.GetValue<string>();
        await tenant.Developer.DeleteAsync($"/api/v1/applications/{appId}?confirm={slug}");
        await (await tenant.Viewer.GetAsync($"/api/v1/domains/{onApp.Id()}")).AssertProblemAsync(404, "domain.not_found");
        Assert.Single((await tenant.Viewer.GetJsonAsync("/api/v1/domains"))["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task MovingTheWorkloadToAnotherServer_MovesItsDomains()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host("move") });
        var other = await tenant.CreateServerAsync(publicIp: "198.51.100.1");

        await tenant.Developer.PatchOkAsync($"/api/v1/applications/{appId}", new { serverId = other.Id() });
        var moved = await tenant.Viewer.GetJsonAsync($"/api/v1/domains/{domain.Id()}");
        Assert.Equal(other.Id(), moved["serverId"]!.GetValue<string>());

        // ...and DNS is now compared with the new server's address.
        tenant.Dns(fixture, moved["hostname"]!.GetValue<string>(), "198.51.100.1");
        Assert.Equal("ok", (await (await tenant.Developer.PostAsync($"/api/v1/domains/{domain.Id()}/verify-dns", null)).ReadAsync())["status"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task List_PaginatesAcrossPages()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var hosts = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var host = Host($"p{i}");
            hosts.Add(host);
            await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = host });
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await tenant.Viewer.GetJsonAsync("/api/v1/domains?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            seen.AddRange(page["items"]!.AsArray().Select(d => d!["hostname"]!.GetValue<string>()));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(hosts.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        Assert.Equal(5, seen.Distinct().Count());
    }

    [RequiresDatabaseFact]
    public async Task Roles_Scopes_AndTenantIsolation()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await a.CreateStackAsync();
        var domain = await a.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host("iso") });
        var url = $"/api/v1/domains/{domain.Id()}";

        Assert.Equal(HttpStatusCode.OK, (await a.Viewer.GetAsync(url)).StatusCode);
        await (await a.Viewer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = Host() })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.PatchAsync(url, new { httpsEnabled = false })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.DeleteAsync(url)).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.PostAsync(url + "/verify-dns", null)).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Token(OrganizationRole.Owner, "read").PostAsync(url + "/verify-dns", null)).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.OK, (await a.Token(OrganizationRole.Developer, "write").PostAsync(url + "/verify-dns", null)).StatusCode);

        await (await b.Owner.GetAsync(url)).AssertProblemAsync(404, "domain.not_found");
        await (await b.Owner.PatchAsync(url, new { httpsEnabled = false })).AssertProblemAsync(404, "domain.not_found");
        await (await b.Owner.DeleteAsync(url)).AssertProblemAsync(404, "domain.not_found");
        await (await b.Owner.PostAsync(url + "/verify-dns", null)).AssertProblemAsync(404, "domain.not_found");
        await (await b.Owner.GetAsync($"/api/v1/applications/{appId}/domains")).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.PostAsync($"/api/v1/applications/{appId}/domains", new { hostname = Host() })).AssertProblemAsync(404, "application.not_found");
        Assert.Empty((await b.Owner.GetJsonAsync("/api/v1/domains"))["items"]!.AsArray());
        Assert.Contains(("/workloadId", "not_found"), await (await b.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = Host() })).ValidationErrorsAsync());
    }
}

internal static class DomainTestExtensions
{
    /// <summary>Teaches the fake resolver (shared by the fixture) what <paramref name="hostname"/> resolves to.</summary>
    public static void Dns(this Tenant tenant, ResourcesFixture fixture, string hostname, params string[] addresses) => fixture.Dns.Set(hostname, addresses);
}
