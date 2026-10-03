using System.Net;
using Aethera.Api.Http;
using Aethera.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests;

/// <summary>Plays the TCP peer: sets the connection's remote address before the application's pipeline runs.</summary>
internal sealed class FakeRemoteIpStartupFilter(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };

    public static WebApplicationFactory<Program> From(WebApplicationFactory<Program> factory, string peer, string? trustedProxies = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (trustedProxies is not null) builder.UseSetting(ForwardedHeadersSetup.TrustedProxiesKey, trustedProxies);
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter>(_ => new FakeRemoteIpStartupFilter(IPAddress.Parse(peer))));
        });
}

public sealed class ForwardedHeadersTests : IDisposable
{
    private readonly AetheraApiFactory _root = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ForwardedHeadersTests()
    {
        _factory = _root.WithEndpoints(api =>
            api.MapGet("/_test/where", (HttpContext http, Aethera.Domain.ICurrentActor actor) =>
                    new { ip = actor.IpAddress, scheme = http.Request.Scheme, host = http.Request.Host.Value })
                .WithName("testWhere").AllowAnonymous());
    }

    public void Dispose() => _root.Dispose();

    private async Task<(string? Ip, string Scheme, string Host)> WhereAsync(string peer, string? trusted, string? forwardedFor, string? proto)
    {
        using var factory = FakeRemoteIpStartupFilter.From(_factory, peer, trusted);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_test/where");
        if (forwardedFor is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        if (proto is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", proto);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example.com");
        var body = await (await client.SendAsync(request)).ReadJsonAsync();
        return (body["ip"]?.GetValue<string>(), body.Str("scheme"), body.Str("host"));
    }

    [Fact]
    public async Task ByDefault_OnlyLoopbackIsTrusted()
    {
        var trusted = await WhereAsync("127.0.0.1", null, "203.0.113.7", "https");
        Assert.Equal("203.0.113.7", trusted.Ip);
        Assert.Equal("https", trusted.Scheme);

        var v6 = await WhereAsync("::1", null, "203.0.113.7", "https");
        Assert.Equal("203.0.113.7", v6.Ip);

        var untrusted = await WhereAsync("192.0.2.50", null, "203.0.113.7", "https");
        Assert.Equal("192.0.2.50", untrusted.Ip);
        Assert.Equal("http", untrusted.Scheme);
    }

    [Fact]
    public async Task AnUntrustedProxysHeaders_AreIgnored()
    {
        var result = await WhereAsync("198.51.100.9", "10.0.0.0/8", "203.0.113.7", "https");

        Assert.Equal("198.51.100.9", result.Ip);
        Assert.Equal("http", result.Scheme);
    }

    [Theory]
    [InlineData("10.1.2.3", "10.0.0.0/8")]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("10.1.2.3", "192.168.0.1, 10.1.0.0/16")]
    [InlineData("fd00::5", "fd00::/8")]
    public async Task ATrustedProxysHeaders_AreHonoured(string peer, string trusted)
    {
        var result = await WhereAsync(peer, trusted, "203.0.113.7", "https");

        Assert.Equal("203.0.113.7", result.Ip);
        Assert.Equal("https", result.Scheme);
    }

    [Fact]
    public async Task OnlyForAndProto_AreApplied_NotHost()
    {
        var result = await WhereAsync("10.1.2.3", "10.0.0.0/8", "203.0.113.7", "https");

        Assert.Equal("localhost", result.Host); // X-Forwarded-Host was sent and is ignored
    }

    [Fact]
    public async Task ConfiguredProxies_ReplaceTheLoopbackDefault()
    {
        var result = await WhereAsync("127.0.0.1", "10.0.0.0/8", "203.0.113.7", "https");

        Assert.Equal("127.0.0.1", result.Ip);
    }

    [Fact]
    public async Task OfAChain_OnlyTheLastHopIsTrusted_ByDefault()
    {
        // A client-supplied "X-Forwarded-For: 1.1.1.1" that the proxy appended to: only the entry the trusted proxy added counts.
        var result = await WhereAsync("10.1.2.3", "10.0.0.0/8", "1.1.1.1, 203.0.113.7", "https");

        Assert.Equal("203.0.113.7", result.Ip);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/abc")]
    public void InvalidTrustedProxies_AreRejected(string value) =>
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.Parse(value));

    [Fact]
    public void Parse_IgnoresBlankEntries_AndDefaultsToLoopback()
    {
        Assert.Equal(2, ForwardedHeadersSetup.Parse(null).Count);
        Assert.Equal(2, ForwardedHeadersSetup.Parse(" , ").Count);
        Assert.Equal(2, ForwardedHeadersSetup.Parse("10.0.0.1, 172.16.0.0/12").Count);
    }
}

public sealed class AetheraHostTests
{
    [Theory]
    [InlineData("GetDocument.Insider", true)]
    [InlineData("Aethera.Api", false)]
    [InlineData("getdocument.insider", false)]
    [InlineData("testhost", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOpenApiGenerator_RecognisesTheBuildTimeGeneratorOnly(string? entryAssembly, bool expected) =>
        Assert.Equal(expected, AetheraHost.IsOpenApiGenerator(entryAssembly));

    [Fact]
    public void TheTestProcess_IsNotTheOpenApiGenerator() => Assert.False(AetheraHost.IsOpenApiGeneration);
}
