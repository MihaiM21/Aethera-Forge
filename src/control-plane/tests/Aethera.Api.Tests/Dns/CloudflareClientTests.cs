using System.Net;
using System.Net.Http;
using Aethera.Infrastructure.DnsProviders;

namespace Aethera.Api.Tests.DnsProviders;

public sealed class CloudflareClientTests
{
    private const string Token = "tok-active-0123456789";

    private static (FakeCloudflareHandler Handler, CloudflareClient Client) Setup(FakeToken? token = null)
    {
        var handler = new FakeCloudflareHandler();
        handler.Tokens[Token] = token ?? new FakeToken();
        return (handler, handler.CreateClient());
    }

    [Fact]
    public async Task VerifyToken_Active_SendsTheBearerToken()
    {
        var (handler, client) = Setup();
        Assert.True(await client.VerifyTokenAsync(Token, default));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/client/v4/user/tokens/verify", request.Url);
        Assert.Equal(Token, request.Token);
    }

    [Fact]
    public async Task VerifyToken_InvalidToken_IsFalse_NotAnException()
    {
        var (_, client) = Setup();
        Assert.False(await client.VerifyTokenAsync("wrong", default));
    }

    [Fact]
    public async Task ListZones_FollowsAllPages()
    {
        var (handler, client) = Setup();
        for (var i = 0; i < 120; i++) handler.WithZone($"zone{i:000}.example");

        var zones = await client.ListZonesAsync(Token, null, default);

        Assert.Equal(120, zones.Count);
        Assert.Equal(120, zones.Select(z => z.Name).Distinct().Count());
        Assert.Equal(3, handler.UrlCount("/zones?"));
        Assert.All(handler.Requests, r => Assert.Contains("per_page=50", r.Url));
    }

    [Fact]
    public async Task ListZones_ByName_AsksCloudflareToFilter()
    {
        var (handler, client) = Setup();
        handler.WithZone("one.example").WithZone("two.example");
        var zone = Assert.Single(await client.ListZonesAsync(Token, "two.example", default));
        Assert.Equal("two.example", zone.Name);
        Assert.Contains("name=two.example", handler.Requests[0].Url);
    }

    [Fact]
    public async Task ListDnsRecords_ReadsProxiedAndContent_AndSkipsOtherTypes()
    {
        var (handler, client) = Setup();
        handler.WithZone("example.com")
            .WithRecord("example.com", "A", "app.example.com", "203.0.113.10", proxied: true)
            .WithRecord("example.com", "TXT", "app.example.com", "v=spf1 -all")
            .WithRecord("example.com", "A", "other.example.com", "198.51.100.1");

        var records = await client.ListDnsRecordsAsync(Token, "zone-example.com", "app.example.com", default);

        var record = Assert.Single(records);
        Assert.Equal(new CloudflareDnsRecord("A", "app.example.com", "203.0.113.10", true), record);
    }

    [Fact]
    public async Task InvalidToken_IsReportedWithoutTheToken()
    {
        var (_, client) = Setup();
        var ex = await Assert.ThrowsAsync<CloudflareException>(() => client.ListZonesAsync("tok-secret-value-xyz", null, default));
        Assert.Equal(CloudflareFailure.InvalidToken, ex.Failure);
        Assert.DoesNotContain("tok-secret-value-xyz", ex.Message);
    }

    [Fact]
    public async Task MissingPermission_IsPermissionDenied()
    {
        var (handler, client) = Setup(new FakeToken(ReadDns: false));
        handler.WithZone("example.com");
        var ex = await Assert.ThrowsAsync<CloudflareException>(() => client.ListDnsRecordsAsync(Token, "zone-example.com", null, default));
        Assert.Equal(CloudflareFailure.PermissionDenied, ex.Failure);
    }

    [Fact]
    public async Task NetworkFailure_IsUnreachable()
    {
        var (handler, client) = Setup();
        handler.Failure = new HttpRequestException("dns failure for api.cloudflare.com with " + Token);
        var ex = await Assert.ThrowsAsync<CloudflareException>(() => client.ListZonesAsync(Token, null, default));
        Assert.Equal(CloudflareFailure.Unreachable, ex.Failure);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task NonJsonErrorPage_IsAFailureWithTheStatus()
    {
        var (handler, client) = Setup();
        handler.RawStatus = HttpStatusCode.BadGateway;
        var ex = await Assert.ThrowsAsync<CloudflareException>(() => client.ListZonesAsync(Token, null, default));
        Assert.Equal(CloudflareFailure.Other, ex.Failure);
        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public async Task Timeout_IsUnreachable()
    {
        var (handler, client) = Setup();
        handler.Failure = new TaskCanceledException("timed out");
        var ex = await Assert.ThrowsAsync<CloudflareException>(() => client.ListZonesAsync(Token, null, default));
        Assert.Equal(CloudflareFailure.Unreachable, ex.Failure);
    }

    // ---- inspector ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inspect_ValidToken_ListsSortedZones_AndProbesDnsAccess()
    {
        var (handler, client) = Setup();
        handler.WithZone("zeta.example").WithZone("alpha.example");

        var report = await CloudflareInspector.InspectAsync(client, Token, default);

        Assert.True(report.Valid);
        Assert.True(report.DnsAccess);
        Assert.Null(report.Message);
        Assert.Equal(["alpha.example", "zeta.example"], report.Zones);
        Assert.Equal(1, handler.UrlCount("/dns_records"));
    }

    [Fact]
    public async Task Inspect_InvalidToken()
    {
        var (_, client) = Setup();
        var report = await CloudflareInspector.InspectAsync(client, "nope", default);
        Assert.False(report.Valid);
        Assert.Empty(report.Zones);
        Assert.Contains("rejected", report.Message);
    }

    [Fact]
    public async Task Inspect_MissingDnsPermission_ExplainsZoneDnsEdit()
    {
        var (handler, client) = Setup(new FakeToken(ReadDns: false));
        handler.WithZone("example.com");
        var report = await CloudflareInspector.InspectAsync(client, Token, default);
        Assert.True(report.Valid);
        Assert.False(report.DnsAccess);
        Assert.Equal(["example.com"], report.Zones);
        Assert.Contains("Zone > DNS > Edit", report.Message);
    }

    [Fact]
    public async Task Inspect_AccountOwnedToken_IsValidWhenItListsZones()
    {
        // The user endpoint rejects account-owned tokens; listing zones is the real proof.
        var (handler, client) = Setup(new FakeToken(UserVerify: false));
        handler.WithZone("example.com");
        var report = await CloudflareInspector.InspectAsync(client, Token, default);
        Assert.True(report.Valid);
        Assert.True(report.DnsAccess);
    }

    [Fact]
    public async Task Inspect_TokenWithoutZones_SaysSo()
    {
        var (_, client) = Setup();
        var report = await CloudflareInspector.InspectAsync(client, Token, default);
        Assert.True(report.Valid);
        Assert.False(report.DnsAccess);
        Assert.Contains("no zones", report.Message);
    }

    [Fact]
    public async Task Inspect_CannotListZones_IsNotValid()
    {
        var (handler, client) = Setup(new FakeToken(ListZones: false));
        handler.WithZone("example.com");
        var report = await CloudflareInspector.InspectAsync(client, Token, default);
        Assert.False(report.Valid);
        Assert.Contains("Zone > Zone > Read", report.Message);
    }

    [Fact]
    public async Task Inspect_Unreachable_ReportsTheFailure()
    {
        var (handler, client) = Setup();
        handler.Failure = new HttpRequestException("boom");
        var report = await CloudflareInspector.InspectAsync(client, Token, default);
        Assert.False(report.Valid);
        Assert.Equal("Cloudflare could not be reached.", report.Message);
    }

    // ---- record lookup ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FindRecords_PrefersTheExactName()
    {
        var (handler, client) = Setup();
        handler.WithZone("example.com")
            .WithRecord("example.com", "A", "*.example.com", "198.51.100.9")
            .WithRecord("example.com", "A", "app.example.com", "203.0.113.10", proxied: true);

        var found = await CloudflareInspector.FindRecordsAsync(client, Token, "example.com", "app.example.com", default);

        Assert.Equal("app.example.com", found.MatchedName);
        Assert.Equal("203.0.113.10", Assert.Single(found.Records).Content);
        Assert.True(found.Records[0].Proxied);
    }

    [Fact]
    public async Task FindRecords_FallsBackToTheWildcard()
    {
        var (handler, client) = Setup();
        handler.WithZone("example.com").WithRecord("example.com", "A", "*.example.com", "203.0.113.10");

        var found = await CloudflareInspector.FindRecordsAsync(client, Token, "example.com", "deep.app.example.com", default);

        Assert.Equal("*.example.com", found.MatchedName);
        Assert.Single(found.Records);
    }

    [Fact]
    public async Task FindRecords_NearestWildcardWins()
    {
        var (handler, client) = Setup();
        handler.WithZone("example.com")
            .WithRecord("example.com", "A", "*.example.com", "198.51.100.1")
            .WithRecord("example.com", "A", "*.app.example.com", "203.0.113.10");

        var found = await CloudflareInspector.FindRecordsAsync(client, Token, "example.com", "x.app.example.com", default);

        Assert.Equal("*.app.example.com", found.MatchedName);
    }

    [Fact]
    public async Task FindRecords_None()
    {
        var (handler, client) = Setup();
        handler.WithZone("example.com");
        var found = await CloudflareInspector.FindRecordsAsync(client, Token, "example.com", "app.example.com", default);
        Assert.Empty(found.Records);
        Assert.Null(found.MatchedName);
    }

    [Fact]
    public async Task FindRecords_ZoneNotVisibleToTheToken_Throws()
    {
        var (_, client) = Setup();
        await Assert.ThrowsAsync<CloudflareException>(() => CloudflareInspector.FindRecordsAsync(client, Token, "gone.example", "app.gone.example", default));
    }
}
