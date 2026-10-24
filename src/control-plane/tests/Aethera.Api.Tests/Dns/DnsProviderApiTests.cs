using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.DnsProviders;

[Collection(DnsCollection.Name)]
public sealed class DnsProviderApiTests(DnsFixture fixture)
{
    private const string Token = "cf-token-AbCdEf0123456789-secret";

    private static string Name() => Tenant.Unique("cf");

    private async Task<(Tenant Tenant, JsonNode Provider)> NewProviderAsync(string? token = null)
    {
        var tenant = await fixture.NewTenantAsync();
        return (tenant, await tenant.Admin.CreateAsync("/api/v1/dns-providers", new { name = Name(), apiToken = token ?? Token }));
    }

    [RequiresDatabaseFact]
    public async Task Create_StoresTheTokenAsAManagedSecret_AndNeverReturnsIt()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Admin.PostAsync("/api/v1/dns-providers", new { name = "  Cloudflare main ", apiToken = " " + Token + " " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Token, text);
        var provider = JsonNode.Parse(text)!;
        Assert.Equal($"/api/v1/dns-providers/{provider.Id()}", response.Headers.Location!.ToString());
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal("Cloudflare main", provider["name"]!.GetValue<string>());
        Assert.Equal("cloudflare", provider["kind"]!.GetValue<string>());
        Assert.True(provider["hasToken"]!.GetValue<bool>());
        Assert.Empty(provider["zones"]!.AsArray());
        Assert.Null(provider["lastVerifiedAt"]);
        Assert.Null(provider["lastError"]);
        Assert.Null(provider["apiToken"]);
        Assert.Null(provider["apiTokenSecretId"]);

        // Every read model and the audit trail are free of the token.
        foreach (var url in new[] { $"/api/v1/dns-providers/{provider.Id()}", "/api/v1/dns-providers" })
        {
            Assert.DoesNotContain(Token, await tenant.Viewer.GetStringAsync(url));
            Assert.DoesNotContain("secret", (await tenant.Viewer.GetStringAsync(url)).Replace("hasToken", ""), StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(await tenant.AuditAsync(), e => Assert.DoesNotContain(Token, e.MetadataJson));
        Assert.Contains(await tenant.AuditAsync(provider.Id()), e => e.Action == "dns_provider.created");

        // The token is a managed secret, listed masked, and the plaintext is stored encrypted.
        var secret = Assert.Single((await tenant.Owner.GetJsonAsync("/api/v1/secrets"))["items"]!.AsArray())!;
        Assert.StartsWith("dns/", secret["name"]!.GetValue<string>());
        Assert.Equal("********", secret["value"]!.GetValue<string>());
        Assert.True(secret["managed"]!.GetValue<bool>());
        Assert.Equal("dnsCredential", secret["purpose"]!.GetValue<string>());
        await (await tenant.Developer.DeleteAsync($"/api/v1/secrets/{secret.Id()}?confirm={secret["name"]!.GetValue<string>()}")).AssertProblemAsync(409, "secret.managed");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var version = await db.SecretVersions.AsNoTracking().FirstAsync(v => v.SecretId == Guid.Parse(secret.Id()));
        Assert.DoesNotContain(Token, System.Text.Encoding.UTF8.GetString(version.Ciphertext));
    }

    [RequiresDatabaseFact]
    public async Task Create_Validation()
    {
        var tenant = await fixture.NewTenantAsync();
        Assert.Contains(("/name", "required"), await (await tenant.Admin.PostAsync("/api/v1/dns-providers", new { apiToken = Token })).ValidationErrorsAsync());
        Assert.Contains("/apiToken", (await (await tenant.Admin.PostAsync("/api/v1/dns-providers", new { name = Name() })).ValidationErrorsAsync()).Select(e => e.Location));
        Assert.Contains("/apiToken", (await (await tenant.Admin.PostAsync("/api/v1/dns-providers", new { name = Name(), apiToken = "has space" })).ValidationErrorsAsync()).Select(e => e.Location));
        Assert.Contains("/apiToken", (await (await tenant.Admin.PostAsync("/api/v1/dns-providers", new { name = Name(), apiToken = new string('a', 501) })).ValidationErrorsAsync()).Select(e => e.Location));
        Assert.Contains("/name", (await (await tenant.Admin.PostAsync("/api/v1/dns-providers", new { name = new string('n', 201), apiToken = Token })).ValidationErrorsAsync()).Select(e => e.Location));
    }

    [RequiresDatabaseFact]
    public async Task Create_DuplicateNameIs409_AcrossOrganizationsItIsFine()
    {
        var (tenant, provider) = await NewProviderAsync();
        await (await tenant.Admin.PostAsync("/api/v1/dns-providers", new { name = provider["name"]!.GetValue<string>(), apiToken = Token })).AssertProblemAsync(409, "dns_provider.already_exists");

        var other = await fixture.NewTenantAsync();
        await other.Admin.CreateAsync("/api/v1/dns-providers", new { name = provider["name"]!.GetValue<string>(), apiToken = Token });
    }

    [RequiresDatabaseFact]
    public async Task ListAndGet_AreOrdered_Filterable_AndPaged()
    {
        var tenant = await fixture.NewTenantAsync();
        foreach (var name in new[] { "Charlie", "alpha", "Bravo" })
            await tenant.Admin.CreateAsync("/api/v1/dns-providers", new { name, apiToken = Token });

        var all = (await tenant.Viewer.GetJsonAsync("/api/v1/dns-providers"))["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal(all.Order(StringComparer.Ordinal).ToList(), all.OrderBy(n => n, StringComparer.Ordinal).ToList());

        var filtered = (await tenant.Viewer.GetJsonAsync("/api/v1/dns-providers?q=brav"))["items"]!.AsArray();
        Assert.Equal("Bravo", Assert.Single(filtered)!["name"]!.GetValue<string>());

        var first = await tenant.Viewer.GetJsonAsync("/api/v1/dns-providers?limit=2");
        Assert.Equal(2, first["items"]!.AsArray().Count);
        Assert.NotNull(first["nextCursor"]);

        var id = filtered[0]!.Id();
        var one = await tenant.Viewer.GetJsonAsync($"/api/v1/dns-providers/{id}");
        Assert.Equal("Bravo", one["name"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Update_RenamesAndRotatesTheToken_ResettingTheVerification()
    {
        var tenant = await fixture.NewTenantAsync();
        fixture.Cloudflare.Tokens[Token] = new FakeToken();
        fixture.Cloudflare.WithZone("example.com");
        var provider = await tenant.Admin.CreateAsync("/api/v1/dns-providers", new { name = Name(), apiToken = Token });
        var url = $"/api/v1/dns-providers/{provider.Id()}";
        var verified = await (await tenant.Admin.PostAsync(url + "/verify", null)).ReadAsync();
        Assert.True(verified["valid"]!.GetValue<bool>());
        Assert.NotEmpty((await tenant.Admin.GetJsonAsync(url))["zones"]!.AsArray());

        var newName = Name();
        var updated = await tenant.Admin.PatchOkAsync(url, new { name = newName, apiToken = "cf-rotated-token-0987654321" });
        Assert.Equal(newName, updated["name"]!.GetValue<string>());
        Assert.Empty(updated["zones"]!.AsArray());
        Assert.Null(updated["lastVerifiedAt"]);
        Assert.DoesNotContain("cf-rotated-token", updated.ToJsonString());
        var changed = (await tenant.AuditAsync(provider.Id(), "dns_provider.updated")).Single().MetadataJson;
        Assert.Contains("apiToken", changed);
        Assert.DoesNotContain("cf-rotated-token", changed);

        // The secret got a second version; the revealed value is the new token.
        var secret = Assert.Single((await tenant.Owner.GetJsonAsync("/api/v1/secrets"))["items"]!.AsArray())!;
        Assert.Equal("cf-rotated-token-0987654321", (await (await tenant.Admin.PostAsync($"/api/v1/secrets/{secret.Id()}/reveal", null)).ReadAsync())["value"]!.GetValue<string>());

        // A rename alone keeps the verification, and the token cannot be cleared.
        await tenant.Admin.PatchOkAsync(url, new { name = Name() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Admin.PatchAsync(url, new { apiToken = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tenant.Admin.PatchAsync(url, new { apiToken = "has space" })).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Update_RenameToAnExistingNameIs409_AndIfMatchIsHonoured()
    {
        var tenant = await fixture.NewTenantAsync();
        var a = await tenant.Admin.CreateAsync("/api/v1/dns-providers", new { name = Name(), apiToken = Token });
        var b = await tenant.Admin.CreateAsync("/api/v1/dns-providers", new { name = Name(), apiToken = Token });
        var url = $"/api/v1/dns-providers/{b.Id()}";
        await (await tenant.Admin.PatchAsync(url, new { name = a["name"]!.GetValue<string>() })).AssertProblemAsync(409, "dns_provider.already_exists");
        await (await tenant.Admin.PatchAsync(url, new { name = Name() }, ifMatch: "\"1\"")).AssertProblemAsync(412, "precondition.failed");
    }

    [RequiresDatabaseFact]
    public async Task Delete_NeedsTheNameAsConfirmation_AndRemovesTheSecret()
    {
        var (tenant, provider) = await NewProviderAsync();
        var url = $"/api/v1/dns-providers/{provider.Id()}";
        await (await tenant.Admin.DeleteAsync(url)).AssertProblemAsync(428, "confirmation.required");
        await (await tenant.Admin.DeleteAsync(url + "?confirm=wrong")).AssertProblemAsync(428, "confirmation.required");

        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync(url + "?confirm=" + Uri.EscapeDataString(provider["name"]!.GetValue<string>()))).StatusCode);
        await (await tenant.Admin.GetAsync(url)).AssertProblemAsync(404, "dns_provider.not_found");
        Assert.Empty((await tenant.Owner.GetJsonAsync("/api/v1/secrets"))["items"]!.AsArray());
        Assert.Contains(await tenant.AuditAsync(provider.Id()), e => e.Action == "dns_provider.deleted");

        // The name is free again.
        await tenant.Admin.CreateAsync("/api/v1/dns-providers", new { name = provider["name"]!.GetValue<string>(), apiToken = Token });
    }

    [RequiresDatabaseFact]
    public async Task Delete_OfAProviderUsedByADomain_Is409()
    {
        var (tenant, provider) = await NewProviderAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = $"in-use-{Guid.NewGuid():N}.example.com" });
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            (await db.Domains.FirstAsync(d => d.Id == Guid.Parse(domain.Id()))).DnsProviderId = Guid.Parse(provider.Id());
            await db.SaveChangesAsync();
        }

        var url = $"/api/v1/dns-providers/{provider.Id()}?confirm=" + Uri.EscapeDataString(provider["name"]!.GetValue<string>());
        await (await tenant.Admin.DeleteAsync(url)).AssertProblemAsync(409, "dns_provider.in_use");

        // Once the domain is gone the provider can go.
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/domains/{domain.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync(url)).StatusCode);
    }

    // ---- verify -------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Verify_StoresZonesAndTheTimestamp()
    {
        var (tenant, provider) = await NewProviderAsync();
        fixture.Cloudflare.Tokens[Token] = new FakeToken();
        for (var i = 0; i < 60; i++) fixture.Cloudflare.WithZone($"zone{i:00}.example");
        fixture.Cloudflare.WithZone("turnone.com");

        var response = await tenant.Admin.PostAsync($"/api/v1/dns-providers/{provider.Id()}/verify", null);
        var body = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body["valid"]!.GetValue<bool>());
        Assert.True(body["dnsAccess"]!.GetValue<bool>());
        Assert.Null(body["message"]);
        Assert.Equal(61, body["zones"]!.AsArray().Count);
        Assert.Contains("turnone.com", body["zones"]!.AsArray().Select(z => z!.GetValue<string>()));
        var stored = await tenant.Admin.GetJsonAsync($"/api/v1/dns-providers/{provider.Id()}");
        Assert.Equal(61, stored["zones"]!.AsArray().Count);
        Assert.NotNull(stored["lastVerifiedAt"]);
        Assert.Null(stored["lastError"]);
        Assert.All(fixture.Cloudflare.Requests, r => Assert.Equal(Token, r.Token));
        Assert.Equal(2, fixture.Cloudflare.UrlCount("/zones?per_page=50"));
        var audit = Assert.Single(await tenant.AuditAsync(provider.Id(), "dns_provider.verified"));
        Assert.DoesNotContain(Token, audit.MetadataJson);
    }

    [RequiresDatabaseFact]
    public async Task Verify_InvalidToken_RecordsTheErrorAndKeepsTheOldZones()
    {
        var (tenant, provider) = await NewProviderAsync();
        var url = $"/api/v1/dns-providers/{provider.Id()}/verify";
        fixture.Cloudflare.Tokens[Token] = new FakeToken();
        fixture.Cloudflare.WithZone("example.com");
        await tenant.Admin.PostAsync(url, null);

        fixture.Cloudflare.Tokens.Clear(); // revoked at Cloudflare
        var body = await (await tenant.Admin.PostAsync(url, null)).ReadAsync();

        Assert.False(body["valid"]!.GetValue<bool>());
        Assert.Contains("rejected", body["message"]!.GetValue<string>());
        Assert.DoesNotContain(Token, body.ToJsonString());
        var stored = await tenant.Admin.GetJsonAsync($"/api/v1/dns-providers/{provider.Id()}");
        Assert.Contains("rejected", stored["lastError"]!.GetValue<string>());
        Assert.Equal(["example.com"], stored["zones"]!.AsArray().Select(z => z!.GetValue<string>()));
    }

    [RequiresDatabaseFact]
    public async Task Verify_MissingDnsPermission_ExplainsIt()
    {
        var (tenant, provider) = await NewProviderAsync();
        fixture.Cloudflare.Tokens[Token] = new FakeToken(ReadDns: false);
        fixture.Cloudflare.WithZone("example.com");

        var body = await (await tenant.Admin.PostAsync($"/api/v1/dns-providers/{provider.Id()}/verify", null)).ReadAsync();

        Assert.True(body["valid"]!.GetValue<bool>());
        Assert.False(body["dnsAccess"]!.GetValue<bool>());
        Assert.Contains("Zone > DNS > Edit", body["message"]!.GetValue<string>());
        var stored = await tenant.Admin.GetJsonAsync($"/api/v1/dns-providers/{provider.Id()}");
        Assert.Contains("Zone > DNS > Edit", stored["lastError"]!.GetValue<string>());
        Assert.Equal(["example.com"], stored["zones"]!.AsArray().Select(z => z!.GetValue<string>()));
    }

    [RequiresDatabaseFact]
    public async Task Verify_CloudflareUnreachable_IsAResultNotAServerError()
    {
        var (tenant, provider) = await NewProviderAsync();
        fixture.Cloudflare.Failure = new HttpRequestException("network down");
        var response = await tenant.Admin.PostAsync($"/api/v1/dns-providers/{provider.Id()}/verify", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.ReadAsync())["valid"]!.GetValue<bool>());
    }

    // ---- roles, scopes, tenancy ---------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Roles_AdministratorsManage_EveryoneReads()
    {
        var (tenant, provider) = await NewProviderAsync();
        var url = $"/api/v1/dns-providers/{provider.Id()}";

        foreach (var client in new[] { tenant.Viewer, tenant.Developer })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/dns-providers")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
            await (await client.PostAsync("/api/v1/dns-providers", new { name = Name(), apiToken = Token })).AssertProblemAsync(403, "auth.forbidden");
            await (await client.PatchAsync(url, new { name = Name() })).AssertProblemAsync(403, "auth.forbidden");
            await (await client.DeleteAsync(url + "?confirm=x")).AssertProblemAsync(403, "auth.forbidden");
            await (await client.PostAsync(url + "/verify", null)).AssertProblemAsync(403, "auth.forbidden");
        }

        using var anonymous = fixture.Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/dns-providers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Admin.PostAsync(url + "/verify", null)).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Scopes_ATokenNeedsSecretsWriteToSetTheToken()
    {
        var (tenant, provider) = await NewProviderAsync();
        var url = $"/api/v1/dns-providers/{provider.Id()}";

        Assert.Equal(HttpStatusCode.OK, (await tenant.Token(OrganizationRole.Admin, "read").GetAsync(url)).StatusCode);
        await (await tenant.Token(OrganizationRole.Admin, "write").PostAsync("/api/v1/dns-providers", new { name = Name(), apiToken = Token })).AssertProblemAsync(403, "auth.insufficient_scope");
        await (await tenant.Token(OrganizationRole.Admin, "secrets:write").PostAsync("/api/v1/dns-providers", new { name = Name(), apiToken = Token })).AssertProblemAsync(403, "auth.insufficient_scope");
        await (await tenant.Token(OrganizationRole.Admin, "write").PatchAsync(url, new { name = Name() })).AssertProblemAsync(403, "auth.insufficient_scope");
        await (await tenant.Token(OrganizationRole.Admin, "write").DeleteAsync(url + "?confirm=x")).AssertProblemAsync(403, "auth.insufficient_scope");
        await (await tenant.Token(OrganizationRole.Admin, "read").PostAsync(url + "/verify", null)).AssertProblemAsync(403, "auth.insufficient_scope");

        var both = tenant.Token(OrganizationRole.Admin, "write", "secrets:write");
        Assert.Equal(HttpStatusCode.Created, (await both.PostAsync("/api/v1/dns-providers", new { name = Name(), apiToken = Token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Token(OrganizationRole.Admin, "write").PostAsync(url + "/verify", null)).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Tenancy_AnotherOrganizationSeesNothing()
    {
        var (tenant, provider) = await NewProviderAsync();
        var other = await fixture.NewTenantAsync();
        var url = $"/api/v1/dns-providers/{provider.Id()}";

        Assert.Empty((await other.Owner.GetJsonAsync("/api/v1/dns-providers"))["items"]!.AsArray());
        await (await other.Owner.GetAsync(url)).AssertProblemAsync(404, "dns_provider.not_found");
        await (await other.Owner.PatchAsync(url, new { name = Name() })).AssertProblemAsync(404, "dns_provider.not_found");
        await (await other.Owner.DeleteAsync(url + "?confirm=x")).AssertProblemAsync(404, "dns_provider.not_found");
        await (await other.Owner.PostAsync(url + "/verify", null)).AssertProblemAsync(404, "dns_provider.not_found");
        Assert.Single((await tenant.Owner.GetJsonAsync("/api/v1/dns-providers"))["items"]!.AsArray());
    }
}
