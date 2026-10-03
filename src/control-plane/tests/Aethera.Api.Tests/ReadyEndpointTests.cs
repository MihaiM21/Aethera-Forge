using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aethera.Api.Tests;

public sealed class ReadyEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed record Ready(string Status, ReadyChecks Checks);

    private sealed record ReadyChecks(string Database);

    [Fact]
    public async Task Ready_WithoutConnectionString_ReportsUnavailable()
    {
        using var client = factory
            .WithWebHostBuilder(b => b.UseEnvironment("Testing").UseSetting("ConnectionStrings:Aethera", ""))
            .CreateClient();

        var response = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Ready>();
        Assert.Equal("unavailable", body?.Status);
        Assert.Equal("unavailable", body?.Checks.Database);
    }

    [Fact]
    public async Task Ready_WithUnreachableDatabase_ReportsUnavailableWithoutLeakingDetails()
    {
        using var client = factory
            .WithWebHostBuilder(b => b.UseEnvironment("Testing").UseSetting(
                "ConnectionStrings:Aethera", "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=super-secret-pw;Timeout=1;Pooling=false"))
            .CreateClient();

        var response = await client.GetAsync("/ready");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("super-secret-pw", text);
    }

    [RequiresDatabaseFact]
    public async Task Ready_WithReachableDatabase_ReportsReady()
    {
        var connection = System.Environment.GetEnvironmentVariable(RequiresDatabaseFactAttribute.EnvironmentVariable)!;
        using var client = factory
            .WithWebHostBuilder(b => b.UseEnvironment("Testing").UseSetting("ConnectionStrings:Aethera", connection))
            .CreateClient();

        var response = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Ready>();
        Assert.Equal("ready", body?.Status);
        Assert.Equal("ok", body?.Checks.Database);
    }
}
