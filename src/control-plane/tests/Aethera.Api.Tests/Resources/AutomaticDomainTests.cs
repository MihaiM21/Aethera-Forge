using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Resources;

/// <summary>Automatic domains (ADR 0011): every new application gets <c>&lt;slug&gt;-&lt;hex&gt;.&lt;server wildcard or ip.sslip.io&gt;</c>, like Coolify.</summary>
[Collection(ResourcesCollection.Name)]
public sealed class AutomaticDomainTests(ResourcesFixture fixture)
{
    private static async Task<JsonNode> SingleDomainAsync(Tenant t, string appId) =>
        Assert.Single((await t.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/domains"))["items"]!.AsArray())!;

    private static Task<JsonNode> AppAsync(Tenant t, string envId, string serverId, bool? autoDomain = true) =>
        t.CreateApplicationAsync(envId, serverId, extra: new { autoDomain });

    [RequiresDatabaseFact]
    public async Task A_new_application_gets_an_sslip_domain_from_the_servers_public_ip()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync(publicIp: "203.0.113.10");
        Assert.Equal("203-0-113-10.sslip.io", server["automaticDomainBase"]!.GetValue<string>());
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var app = await AppAsync(t, env, server.Id());

        var domain = await SingleDomainAsync(t, app.Id());
        var slug = app["slug"]!.GetValue<string>();
        Assert.Matches(new Regex($"^{Regex.Escape(slug)}-[0-9a-f]{{6}}\\.203-0-113-10\\.sslip\\.io$"), domain["hostname"]!.GetValue<string>());
        Assert.True(domain["isAutomatic"]!.GetValue<bool>());
        Assert.True(domain["isPrimary"]!.GetValue<bool>());
        Assert.Null(domain["targetPort"]?.GetValue<int?>()); // settled from the image at deployment time
        Assert.False(domain["httpsEnabled"]!.GetValue<bool>()); // no ACME email in the test host
    }

    [RequiresDatabaseFact]
    public async Task The_servers_wildcard_domain_wins_and_is_stored_without_its_star()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync(publicIp: "203.0.113.11");
        var patched = await t.Admin.PatchOkAsync($"/api/v1/servers/{server.Id()}", new { wildcardDomain = "*.Apps.Example.com" });
        Assert.Equal("apps.example.com", patched["wildcardDomain"]!.GetValue<string>());
        Assert.Equal("apps.example.com", patched["automaticDomainBase"]!.GetValue<string>());
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();

        var domain = await SingleDomainAsync(t, (await AppAsync(t, env, server.Id())).Id());
        Assert.EndsWith(".apps.example.com", domain["hostname"]!.GetValue<string>());

        var invalid = await t.Admin.PatchAsync($"/api/v1/servers/{server.Id()}", new { wildcardDomain = "localhost" });
        Assert.Contains(("/wildcardDomain", "pattern"), await invalid.ValidationErrorsAsync());
        var cleared = await t.Admin.PatchOkAsync($"/api/v1/servers/{server.Id()}", new { wildcardDomain = (string?)null });
        Assert.Equal("203-0-113-11.sslip.io", cleared["automaticDomainBase"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task No_automatic_domain_when_asked_not_to_or_when_the_servers_address_is_unknown()
    {
        var t = await fixture.NewTenantAsync();
        var withIp = await t.CreateServerAsync(publicIp: "203.0.113.12");
        var withoutIp = await t.CreateServerAsync(); // a DNS host name and no public IP: nothing to build an sslip.io name from
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();

        var optedOut = await AppAsync(t, env, withIp.Id(), autoDomain: false);
        Assert.Empty((await t.Viewer.GetJsonAsync($"/api/v1/applications/{optedOut.Id()}/domains"))["items"]!.AsArray());

        var unknown = await AppAsync(t, env, withoutIp.Id());
        Assert.Empty((await t.Viewer.GetJsonAsync($"/api/v1/applications/{unknown.Id()}/domains"))["items"]!.AsArray());
        // Not assigned yet: the first deployment tries again once the server has an address.
        Assert.False(await WithDbAsync(db => db.Workloads.Where(w => w.Id == Guid.Parse(unknown.Id())).Select(w => w.AutoDomainAssigned).SingleAsync()));
        Assert.True(await WithDbAsync(db => db.Workloads.Where(w => w.Id == Guid.Parse(optedOut.Id())).Select(w => w.AutoDomainAssigned).SingleAsync()));
    }

    [RequiresDatabaseFact]
    public async Task A_renamed_automatic_domain_becomes_the_users_own()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync(publicIp: "203.0.113.13");
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var domain = await SingleDomainAsync(t, (await AppAsync(t, env, server.Id())).Id());

        var renamed = await t.Developer.PatchOkAsync($"/api/v1/domains/{domain.Id()}", new { hostname = $"shop-{Guid.NewGuid():N}"[..20] + ".example.com" });
        Assert.False(renamed["isAutomatic"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task A_service_with_a_web_interface_gets_one_and_a_database_does_not()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync(publicIp: "203.0.113.14");
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();

        var grafana = await t.Developer.CreateAsync("/api/v1/services", new { name = Tenant.Unique("g"), environmentId = env, serverId = server.Id(), templateKey = "grafana" });
        var postgres = await t.Developer.CreateAsync("/api/v1/services", new { name = Tenant.Unique("pg"), environmentId = env, serverId = server.Id(), templateKey = "postgres" });

        Assert.Single((await t.Viewer.GetJsonAsync($"/api/v1/services/{grafana.Id()}/domains"))["items"]!.AsArray());
        Assert.Empty((await t.Viewer.GetJsonAsync($"/api/v1/services/{postgres.Id()}/domains"))["items"]!.AsArray());
    }

    private async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }
}

/// <summary>Instance-wide proxy settings change what every HTTPS domain looks like: their own database.</summary>
public sealed class ProxySettingsFixture : IDisposable
{
    public ProxySettingsFixture()
    {
        Base = new AetheraApiFactory();
        Factory = Base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<Aethera.Api.Features.Resources.DomainNames.IDnsResolver>(new FakeDnsResolver())));
    }

    public AetheraApiFactory Base { get; }

    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; }

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class ProxySettingsCollection : ICollectionFixture<ProxySettingsFixture>
{
    public const string Name = "proxy-settings";
}

[Collection(ProxySettingsCollection.Name)]
public sealed class ProxySettingsTests(ProxySettingsFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task The_dashboard_domain_and_acme_email_are_saved_and_make_new_domains_https()
    {
        var t = await fixture.NewTenantAsync();
        var before = await t.Admin.GetJsonAsync("/api/v1/system/proxy-settings");
        Assert.Equal("environment", before["source"]!.GetValue<string>());
        Assert.Null(before["dashboardDomain"]?.GetValue<string>());

        var response = await t.Admin.PutAsync("/api/v1/system/proxy-settings",
            Json.Body(new { acmeEmail = "ops@example.com", dashboardDomain = "  Aethera.Example.COM ", dashboardHttps = true }));
        var saved = await response.ReadAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = saved["settings"]!;
        Assert.Equal("settings", settings["source"]!.GetValue<string>());
        Assert.Equal("aethera.example.com", settings["dashboardDomain"]!.GetValue<string>());
        Assert.Equal("https://aethera.example.com", settings["dashboardUrl"]!.GetValue<string>());
        Assert.Equal("ops@example.com", settings["acmeEmail"]!.GetValue<string>());
        Assert.False(saved["proxyApplied"]!.GetValue<bool>()); // no local server in the test host

        // With an ACME email the next automatic domain is HTTPS, and HTTPS domains carry no warning.
        var server = await t.CreateServerAsync(publicIp: "203.0.113.20");
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var app = await t.CreateApplicationAsync(env, server.Id(), extra: new { autoDomain = true });
        var domain = Assert.Single((await t.Viewer.GetJsonAsync($"/api/v1/applications/{app.Id()}/domains"))["items"]!.AsArray())!;
        Assert.True(domain["httpsEnabled"]!.GetValue<bool>());
        Assert.Empty(domain["warnings"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task The_dashboard_domain_cannot_be_an_applications_and_only_administrators_change_it()
    {
        var t = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await t.CreateStackAsync();
        var taken = $"taken-{Guid.NewGuid():N}"[..14] + ".example.com";
        await t.Developer.CreateAsync($"/api/v1/applications/{appId}/domains", new { hostname = taken });

        var conflict = await t.Admin.PutAsync("/api/v1/system/proxy-settings", Json.Body(new { dashboardDomain = taken }));
        await conflict.AssertProblemAsync(409, "proxy.domain_in_use");
        var invalid = await t.Admin.PutAsync("/api/v1/system/proxy-settings", Json.Body(new { dashboardDomain = "*.example.com", acmeEmail = "not an email" }));
        var errors = await invalid.ValidationErrorsAsync();
        Assert.Contains(("/dashboardDomain", "pattern"), errors);
        Assert.Contains(("/acmeEmail", "pattern"), errors);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.PutAsync("/api/v1/system/proxy-settings", Json.Body(new { dashboardDomain = "x.example.com" }))).StatusCode);
    }
}
