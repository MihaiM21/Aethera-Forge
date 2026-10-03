using System.Net;
using System.Text.RegularExpressions;
using Aethera.Api.Http;
using Aethera.Domain;
using Microsoft.AspNetCore.Hosting;

namespace Aethera.Api.Tests;

[Collection(TestApiCollection.Name)]
public sealed class RequestIdTests(TestApiFixture fixture)
{
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? requestId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (requestId is not null) request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        return await client.SendAsync(request);
    }

    private HttpClient Client() => fixture.Factory.CreateClientAs(OrganizationRole.Viewer);

    [Fact]
    public async Task SafeInboundId_IsEchoed_AndIsTheTraceIdOfErrors()
    {
        var response = await GetAsync(Client(), "/api/v1/_test/not-found/" + Guid.NewGuid(), "client-req_42.a:b");
        Assert.Equal("client-req_42.a:b", response.Headers.GetValues("X-Request-Id").Single());
        Assert.Equal("client-req_42.a:b", (await response.ReadJsonAsync()).Str("traceId"));
    }

    [Fact]
    public async Task ActorSeesTheSameRequestId()
    {
        var response = await GetAsync(Client(), "/api/v1/_test/whoami", "abc-123");
        Assert.Equal("abc-123", (await response.ReadJsonAsync()).Str("requestId"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("semi;colon")]
    [InlineData("café")]
    public async Task UnsafeInboundId_IsReplacedByAGeneratedOne(string inbound)
    {
        var response = await GetAsync(Client(), "/health", inbound);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        Assert.NotEqual(inbound, id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task TooLongInboundId_IsReplaced()
    {
        var response = await GetAsync(Client(), "/health", new string('a', 101));
        Assert.Matches("^[0-9a-f]{32}$", response.Headers.GetValues("X-Request-Id").Single());
        var okLength = await GetAsync(Client(), "/health", new string('a', 100));
        Assert.Equal(new string('a', 100), okLength.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task EveryResponseCarriesAFreshIdWhenNoneIsSent()
    {
        var a = (await GetAsync(Client(), "/health", null)).Headers.GetValues("X-Request-Id").Single();
        var b = (await GetAsync(Client(), "/health", null)).Headers.GetValues("X-Request-Id").Single();
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task ServerErrors_StillCarryTheHeader()
    {
        var response = await GetAsync(Client(), "/api/v1/_test/boom", "boom-1");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("boom-1", response.Headers.GetValues("X-Request-Id").Single());
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData(null, false)]
    public void IsSafe(string? value, bool expected) => Assert.Equal(expected, RequestIdMiddleware.IsSafe(value));
}

[Collection(TestApiCollection.Name)]
public sealed class JsonConventionTests(TestApiFixture fixture)
{
    [Fact]
    public async Task Json_IsCamelCase_KeepsNulls_UsesEnumStrings_AndUtcZTimestamps()
    {
        var response = await fixture.Factory.CreateClientAs(OrganizationRole.Viewer).GetAsync("/api/v1/_test/json");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(
            "{\"status\":\"agentUnavailable\",\"note\":null,\"createdAt\":\"2026-10-03T14:07:31.482Z\",\"local\":\"2026-10-03T14:07:31.482Z\",\"id\":\"0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55\"}",
            text);
    }

    [Fact]
    public async Task DictionaryKeys_AreKeptVerbatim_PropertyNamesAreStillCamelCase()
    {
        var response = await fixture.Factory.CreateClientAs(OrganizationRole.Viewer).GetAsync("/api/v1/_test/json-dictionary");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal("{\"variables\":{\"NODE_ENV\":\"production\",\"X-Custom\":\"1\",\"PascalCase\":\"p\",\"camelCase\":\"c\"}}", text);
    }

    [Fact]
    public void DictionaryKeys_AreKeptVerbatim_InTheSharedOptions_AlsoWhenReading()
    {
        var options = JsonConventions.CreateOptions();
        var written = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, int> { ["NODE_ENV"] = 1, ["Port"] = 2 }, options);
        Assert.Equal("{\"NODE_ENV\":1,\"Port\":2}", written);

        var read = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(written, options)!;
        Assert.Equal(["NODE_ENV", "Port"], read.Keys);
    }

    [Fact]
    public void Converter_ReadsOffsets_AndNormalisesToUtc()
    {
        var options = JsonConventions.CreateOptions();
        var value = System.Text.Json.JsonSerializer.Deserialize<DateTimeOffset>("\"2026-10-03T16:07:31.482+02:00\"", options);
        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 7, 31, 482, TimeSpan.Zero), value);
        Assert.Equal("\"2026-10-03T14:07:31.482Z\"", System.Text.Json.JsonSerializer.Serialize(value, options));
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<DateTimeOffset>("\"yesterday\"", options));
    }
}

[Collection(TestApiCollection.Name)]
public sealed class CorsTests(TestApiFixture fixture)
{
    private static async Task<HttpResponseMessage> GetWithOriginAsync(HttpClient client, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Cors_IsOffByDefault()
    {
        var response = await GetWithOriginAsync(fixture.Factory.CreateClient(), "https://evil.example.com");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_AllowsOnlyTheConfiguredOrigins()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(b =>
            b.UseSetting("AETHERA_CORS_ORIGINS", "https://ui.example.com/, http://localhost:3000, *"));
        var client = factory.CreateClient();

        var allowed = await GetWithOriginAsync(client, "https://ui.example.com");
        Assert.Equal("https://ui.example.com", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        var other = await GetWithOriginAsync(client, "https://evil.example.com");
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/_test/validate");
        preflight.Headers.Add("Origin", "http://localhost:3000");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
        var pre = await client.SendAsync(preflight);
        Assert.Equal("http://localhost:3000", pre.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Theory]
    [InlineData("https://a.example.com,https://b.example.com/", new[] { "https://a.example.com", "https://b.example.com" })]
    [InlineData("*", new string[0])]
    [InlineData("not a url,ftp://x.example.com", new string[0])]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void ParseOrigins(string? value, string[] expected) => Assert.Equal(expected, CorsSetup.ParseOrigins(value));
}
