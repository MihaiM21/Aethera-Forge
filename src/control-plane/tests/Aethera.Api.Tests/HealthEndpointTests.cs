using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aethera.Api.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed record Health(string Status);

    [Fact]
    public async Task Health_ReturnsOkStatus()
    {
        using var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing")).CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Health>();
        Assert.Equal("ok", body?.Status);
    }

    [Fact]
    public async Task InstallAgentScript_IsServedAnonymouslyAsAShellScriptWithUnixLineEndings()
    {
        using var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing")).CreateClient();

        var response = await client.GetAsync("/install-agent.sh");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/x-shellscript", response.Content.Headers.ContentType?.ToString());
        var script = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("#!/usr/bin/env bash", script);
        Assert.DoesNotContain('\r', script);
        Assert.Contains("--ca-sha256", script);
    }
}
