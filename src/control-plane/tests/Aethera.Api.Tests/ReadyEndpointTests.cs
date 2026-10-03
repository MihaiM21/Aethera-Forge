using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    private sealed class FakeCheck(string name, Func<ReadinessResult> result) : IReadinessCheck
    {
        public string Name => name;

        public Task<ReadinessResult> CheckAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.FromResult(result());
    }

    private async Task<(HttpStatusCode Status, JsonNode Body)> ReadyWithAsync(params IReadinessCheck[] checks)
    {
        using var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing").ConfigureTestServices(services =>
        {
            services.RemoveAll<IReadinessCheck>();
            foreach (var check in checks) services.AddSingleton(check);
        })).CreateClient();
        var response = await client.GetAsync("/ready");
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task Ready_SkippedChecks_NeverFailReadiness()
    {
        var (status, body) = await ReadyWithAsync(
            new FakeCheck("database", () => ReadinessResult.Ok()),
            new FakeCheck("redis", () => ReadinessResult.Skipped("Redis is not configured.")));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ready", (string?)body["status"]);
        Assert.Equal("ok", (string?)body["checks"]!["database"]);
        Assert.Equal("skipped", (string?)body["checks"]!["redis"]);
        Assert.Equal("Redis is not configured.", (string?)body["details"]!["redis"]);
        Assert.Null(body["details"]!["database"]);
    }

    [Fact]
    public async Task Ready_AnyUnavailableCheck_Is503_EvenNextToSkippedAndOkChecks()
    {
        var (status, body) = await ReadyWithAsync(
            new FakeCheck("a", () => ReadinessResult.Ok()),
            new FakeCheck("b", () => ReadinessResult.Skipped()),
            new FakeCheck("c", () => ReadinessResult.Unavailable("Cannot reach it.")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("unavailable", (string?)body["status"]);
        Assert.Equal("ok", (string?)body["checks"]!["a"]);
        Assert.Equal("skipped", (string?)body["checks"]!["b"]);
        Assert.Equal("unavailable", (string?)body["checks"]!["c"]);
    }

    [Fact]
    public async Task Ready_AThrowingCheck_IsUnavailable_AndTheExceptionIsNotReturned()
    {
        var (status, body) = await ReadyWithAsync(
            new FakeCheck("boom", () => throw new InvalidOperationException("Host=10.1.2.3;Password=hunter2")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("unavailable", (string?)body["checks"]!["boom"]);
        Assert.DoesNotContain("hunter2", body.ToJsonString());
        Assert.DoesNotContain("10.1.2.3", body.ToJsonString());
    }

    [Fact]
    public async Task Ready_WithoutChecks_IsReady()
    {
        var (status, body) = await ReadyWithAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ready", (string?)body["status"]);
    }
}
