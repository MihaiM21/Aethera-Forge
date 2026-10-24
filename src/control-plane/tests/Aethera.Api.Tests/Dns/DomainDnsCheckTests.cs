using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;

namespace Aethera.Api.Tests.DnsProviders;

/// <summary>ADR 0010: <c>POST /domains/{id}/verify-dns</c> with and without a DNS provider, and with the Cloudflare proxy in the way.</summary>
[Collection(DnsCollection.Name)]
public sealed class DomainDnsCheckTests(DnsFixture fixture)
{
    private const string Token = "cf-token-dns-check-0123456789";
    private const string ServerIp = "203.0.113.10";

    private static string Zone() => $"z{Guid.NewGuid().ToString("N")[..8]}.example";

    private sealed record Setup(Tenant Tenant, string AppId, string Zone, string? ProviderId = null);

    private async Task<Setup> StartAsync(bool withProvider = true, params string[] zones)
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync(); // server with public IP 203.0.113.10
        var zone = Zone();
        string? providerId = null;
        if (withProvider)
        {
            fixture.Cloudflare.Tokens[Token] = new FakeToken();
            fixture.Cloudflare.WithZone(zone);
            foreach (var extra in zones) fixture.Cloudflare.WithZone(extra);
            var provider = await tenant.Admin.CreateAsync("/api/v1/dns-providers", new { name = Tenant.Unique("cf"), apiToken = Token });
            providerId = provider.Id();
            Assert.True((await (await tenant.Admin.PostAsync($"/api/v1/dns-providers/{provider.Id()}/verify", null)).ReadAsync())["valid"]!.GetValue<bool>());
            fixture.Cloudflare.Requests.Clear();
        }

        return new Setup(tenant, appId, zone, providerId);
    }

    private static async Task<JsonNode> DomainAsync(Setup s, string hostname) =>
        // A wildcard host needs a DNS provider (DNS-01, ADR 0010).
        await s.Tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = s.AppId, hostname, dnsProviderId = hostname.StartsWith("*.") ? s.ProviderId : null });

    private static async Task<JsonNode> VerifyAsync(Setup s, JsonNode domain)
    {
        var response = await s.Tenant.Developer.PostAsync($"/api/v1/domains/{domain.Id()}/verify-dns", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync();
    }

    // ---- with a provider ----------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Provider_ProxiedRecordWhoseOriginIsTheServer_IsOk()
    {
        var s = await StartAsync();
        var host = $"app.{s.Zone}";
        fixture.Cloudflare.WithRecord(s.Zone, "A", host, ServerIp, proxied: true);
        fixture.Dns.Set(host, "104.21.5.9", "172.67.200.1"); // what the world sees: Cloudflare edge addresses
        var domain = await DomainAsync(s, host);

        var result = await VerifyAsync(s, domain);

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal("cloudflareApi", result["method"]!.GetValue<string>());
        Assert.Equal([ServerIp], result["resolvedIps"]!.AsArray().Select(i => i!.GetValue<string>()));
        Assert.Equal([ServerIp], result["expectedIps"]!.AsArray().Select(i => i!.GetValue<string>()));
        Assert.Contains("proxied", result["message"]!.GetValue<string>());
        // The record content is what is stored on the domain.
        var stored = await s.Tenant.Developer.GetJsonAsync($"/api/v1/domains/{domain.Id()}");
        Assert.Equal("ok", stored["dns"]!["status"]!.GetValue<string>());
        Assert.Equal([ServerIp], stored["dns"]!["resolvedIps"]!.AsArray().Select(i => i!.GetValue<string>()));
        Assert.All(fixture.Cloudflare.Requests, r => Assert.Equal(Token, r.Token));
    }

    [RequiresDatabaseFact]
    public async Task Provider_UnproxiedRecordToTheServer_IsOk()
    {
        var s = await StartAsync();
        var host = $"plain.{s.Zone}";
        fixture.Cloudflare.WithRecord(s.Zone, "A", host, ServerIp);
        var result = await VerifyAsync(s, await DomainAsync(s, host));
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal("The Cloudflare record points to the server.", result["message"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Provider_RecordPointingElsewhere_IsMismatch()
    {
        var s = await StartAsync();
        var host = $"elsewhere.{s.Zone}";
        fixture.Cloudflare.WithRecord(s.Zone, "A", host, "198.51.100.77", proxied: true);
        var result = await VerifyAsync(s, await DomainAsync(s, host));
        Assert.Equal("mismatch", result["status"]!.GetValue<string>());
        Assert.Equal("cloudflareApi", result["method"]!.GetValue<string>());
        Assert.Equal(["198.51.100.77"], result["resolvedIps"]!.AsArray().Select(i => i!.GetValue<string>()));
    }

    [RequiresDatabaseFact]
    public async Task Provider_NoRecord_IsMissing()
    {
        var s = await StartAsync();
        var result = await VerifyAsync(s, await DomainAsync(s, $"nothing.{s.Zone}"));
        Assert.Equal("missing", result["status"]!.GetValue<string>());
        Assert.Equal("cloudflareApi", result["method"]!.GetValue<string>());
        Assert.Empty(result["resolvedIps"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task Provider_WildcardRecordCoversTheHost()
    {
        var s = await StartAsync();
        fixture.Cloudflare.WithRecord(s.Zone, "A", $"*.{s.Zone}", ServerIp, proxied: true);
        var result = await VerifyAsync(s, await DomainAsync(s, $"deep.sub.{s.Zone}"));
        Assert.Equal("ok", result["status"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Provider_WildcardDomain_ChecksTheWildcardRecord()
    {
        var s = await StartAsync();
        fixture.Cloudflare.WithRecord(s.Zone, "A", $"*.{s.Zone}", ServerIp);
        var result = await VerifyAsync(s, await DomainAsync(s, $"*.{s.Zone}"));
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal("cloudflareApi", result["method"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Provider_CnameInTheSameZoneIsFollowed()
    {
        var s = await StartAsync();
        fixture.Cloudflare.WithRecord(s.Zone, "CNAME", $"www.{s.Zone}", $"origin.{s.Zone}.", proxied: true)
            .WithRecord(s.Zone, "A", $"origin.{s.Zone}", ServerIp);
        var result = await VerifyAsync(s, await DomainAsync(s, $"www.{s.Zone}"));
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal([ServerIp], result["resolvedIps"]!.AsArray().Select(i => i!.GetValue<string>()));
    }

    [RequiresDatabaseFact]
    public async Task Provider_CnameLoop_IsAnError()
    {
        var s = await StartAsync();
        fixture.Cloudflare.WithRecord(s.Zone, "CNAME", $"a.{s.Zone}", $"b.{s.Zone}")
            .WithRecord(s.Zone, "CNAME", $"b.{s.Zone}", $"a.{s.Zone}");
        var result = await VerifyAsync(s, await DomainAsync(s, $"a.{s.Zone}"));
        Assert.Equal("error", result["status"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Provider_CnameToAnotherZone_UsesTheResolverForTheTarget()
    {
        var s = await StartAsync();
        var target = $"target.{Zone()}";
        fixture.Cloudflare.WithRecord(s.Zone, "CNAME", $"c.{s.Zone}", target);
        fixture.Dns.Set(target, ServerIp);
        var result = await VerifyAsync(s, await DomainAsync(s, $"c.{s.Zone}"));
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Contains("CNAME", result["message"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Provider_LongestZoneAmongProviders_AndOtherOrganizationsProvidersAreIgnored()
    {
        var s = await StartAsync();
        // Another organization's provider covers the same zone but must not be used.
        var other = await fixture.NewTenantAsync(); // resets the fake: re-arm ours
        fixture.Cloudflare.Tokens[Token] = new FakeToken();
        fixture.Cloudflare.WithZone(s.Zone);
        fixture.Cloudflare.WithRecord(s.Zone, "A", $"x.{s.Zone}", ServerIp);
        var foreign = await other.Admin.CreateAsync("/api/v1/dns-providers", new { name = Tenant.Unique("cf"), apiToken = "foreign-token-0123456789" });
        fixture.Cloudflare.Tokens["foreign-token-0123456789"] = new FakeToken();
        await other.Admin.PostAsync($"/api/v1/dns-providers/{foreign.Id()}/verify", null);
        fixture.Cloudflare.Requests.Clear();

        var domain = await DomainAsync(s, $"x.{s.Zone}");
        fixture.Dns.Set($"x.{s.Zone}", ServerIp);
        var result = await VerifyAsync(s, domain);

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Cloudflare.Requests, r => r.Token == "foreign-token-0123456789");
    }

    [RequiresDatabaseFact]
    public async Task Provider_CloudflareFailing_FallsBackToTheResolverAndSaysSo()
    {
        var s = await StartAsync();
        var host = $"fallback.{s.Zone}";
        fixture.Dns.Set(host, ServerIp);
        fixture.Cloudflare.Failure = new HttpRequestException("network down");
        var result = await VerifyAsync(s, await DomainAsync(s, host));
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal("resolver", result["method"]!.GetValue<string>());
        Assert.Contains("Cloudflare lookup failed", result["message"]!.GetValue<string>());
        Assert.DoesNotContain(Token, result.ToJsonString());
    }

    [RequiresDatabaseFact]
    public async Task Provider_RevokedToken_FallsBackToTheProxiedClassification()
    {
        var s = await StartAsync();
        var host = $"revoked.{s.Zone}";
        fixture.Dns.Set(host, "104.21.5.9");
        fixture.Cloudflare.Tokens.Clear();
        var result = await VerifyAsync(s, await DomainAsync(s, host));
        Assert.Equal("proxied", result["status"]!.GetValue<string>());
        Assert.Equal("resolver", result["method"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Provider_NotCoveringTheZone_IsNotUsed()
    {
        var s = await StartAsync();
        var host = $"app.{Zone()}";
        fixture.Dns.Set(host, ServerIp);
        var result = await VerifyAsync(s, await DomainAsync(s, host));
        Assert.Equal("resolver", result["method"]!.GetValue<string>());
        Assert.Equal(0, fixture.Cloudflare.UrlCount("dns_records"));
    }

    // ---- without a provider -------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task NoProvider_AnswerInsideCloudflaresRanges_IsProxied_NotMismatch()
    {
        var s = await StartAsync(withProvider: false);
        var host = $"orange.{Zone()}";
        fixture.Dns.Set(host, "104.21.5.9", "172.67.200.1", "2606:4700:3030::6815:509");
        var domain = await DomainAsync(s, host);

        var result = await VerifyAsync(s, domain);

        Assert.Equal("proxied", result["status"]!.GetValue<string>());
        Assert.Equal("resolver", result["method"]!.GetValue<string>());
        Assert.Equal("proxied by Cloudflare: the origin cannot be verified without a DNS provider", result["message"]!.GetValue<string>());
        Assert.Equal(3, result["resolvedIps"]!.AsArray().Count);
        var stored = await s.Tenant.Developer.GetJsonAsync($"/api/v1/domains/{domain.Id()}");
        Assert.Equal("proxied", stored["dns"]!["status"]!.GetValue<string>());

        // The list filter understands the new status.
        var filtered = await s.Tenant.Developer.GetJsonAsync("/api/v1/domains?dnsStatus=proxied");
        Assert.Contains(filtered["items"]!.AsArray(), d => d!.Id() == domain.Id());
        Assert.Empty((await s.Tenant.Developer.GetJsonAsync("/api/v1/domains?dnsStatus=ok"))["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task NoProvider_MixedAnswer_IsStillAMismatch()
    {
        var s = await StartAsync(withProvider: false);
        var host = $"mixed.{Zone()}";
        fixture.Dns.Set(host, "104.21.5.9", "198.51.100.5");
        var result = await VerifyAsync(s, await DomainAsync(s, host));
        Assert.Equal("mismatch", result["status"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task NoProvider_ThePlainOutcomesAreUnchanged()
    {
        var s = await StartAsync(withProvider: false);
        var okHost = $"ok.{Zone()}";
        var otherHost = $"other.{Zone()}";
        fixture.Dns.Set(okHost, ServerIp);
        fixture.Dns.Set(otherHost, "198.51.100.5");

        var ok = await VerifyAsync(s, await DomainAsync(s, okHost));
        Assert.Equal("ok", ok["status"]!.GetValue<string>());
        Assert.Equal("resolver", ok["method"]!.GetValue<string>());
        Assert.Equal("mismatch", (await VerifyAsync(s, await DomainAsync(s, otherHost)))["status"]!.GetValue<string>());
        Assert.Equal("missing", (await VerifyAsync(s, await DomainAsync(s, $"none.{Zone()}")))["status"]!.GetValue<string>());
        Assert.Empty(fixture.Cloudflare.Requests);
    }

    [RequiresDatabaseFact]
    public async Task Verify_IsAudited_WithTheMethod()
    {
        var s = await StartAsync(withProvider: false);
        var host = $"audit.{Zone()}";
        fixture.Dns.Set(host, ServerIp);
        var domain = await DomainAsync(s, host);
        await VerifyAsync(s, domain);
        var audit = Assert.Single(await s.Tenant.AuditAsync(domain.Id(), "domain.dns_verified"));
        Assert.Contains("resolver", audit.MetadataJson);
    }
}
