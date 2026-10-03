using System.Net;
using System.Net.WebSockets;
using Aethera.Api.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aethera.Api.Tests.Auth;

/// <summary>
/// <c>/hubs/*</c> with the real session and token handlers: a request authenticated by the session cookie must come from the application's
/// own origin (or an allowlisted one); bearer requests are exempt. Sibling subdomains share the cookie's site, so this is what keeps a page
/// of a deployed app from using an administrator's session.
/// </summary>
public sealed class HubOriginTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    private const string Negotiate = "/hubs/logs/negotiate?negotiateVersion=1";
    private ApiClient _owner = null!;

    public async Task InitializeAsync()
    {
        if (!factory.HasDatabase) return;
        await AuthDb.ResetAsync(factory);
        _owner = await factory.SetupOwnerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpRequestMessage NegotiateRequest(string? cookie, string? bearer, string? origin, string? host = null, params (string Name, string Value)[] headers) =>
        NegotiateRequest("aethera_session", cookie, bearer, origin, host, headers);

    private static HttpRequestMessage NegotiateRequest(string cookieName, string? cookie, string? bearer, string? origin, string? host, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Negotiate);
        if (cookie is not null) request.Headers.Add("Cookie", $"{cookieName}={cookie}");
        if (bearer is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> f, HttpRequestMessage request)
    {
        using var client = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        return await client.SendAsync(request);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.origin_not_allowed");
        Assert.DoesNotContain("connectionToken", await response.Content.ReadAsStringAsync());
    }

    [RequiresDatabaseFact]
    public async Task SameOriginCookie_IsAccepted()
    {
        var response = await SendAsync(factory, NegotiateRequest(_owner.SessionCookie, null, "http://localhost"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("connectionToken", await response.Content.ReadAsStringAsync());
    }

    [RequiresDatabaseTheory]
    [InlineData("https://app.example.com")]   // a sibling subdomain of the panel
    [InlineData("http://localhost:8080")]     // same host, other port
    [InlineData("https://localhost")]         // same host, other scheme
    [InlineData("http://evil.localhost")]
    [InlineData("null")]                      // sandboxed iframes, data: URLs
    [InlineData("garbage")]
    [InlineData("http://localhost/path")]     // not an origin
    public async Task CookieFromAnotherOrigin_IsRefused(string origin) =>
        await AssertRefusedAsync(await SendAsync(factory, NegotiateRequest(_owner.SessionCookie, null, origin)));

    [RequiresDatabaseFact]
    public async Task CookieWithoutOrigin_IsRefused() =>
        await AssertRefusedAsync(await SendAsync(factory, NegotiateRequest(_owner.SessionCookie, null, null)));

    [RequiresDatabaseFact]
    public async Task CookieWithoutOrigin_ButSecFetchSiteSameOrigin_IsAccepted()
    {
        // Browsers omit Origin on same-origin GETs (SSE / long polling) but always send the unforgeable Sec-Fetch-Site.
        var same = await SendAsync(factory, NegotiateRequest(_owner.SessionCookie, null, null, null, ("Sec-Fetch-Site", "same-origin")));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);

        // A sibling subdomain is "same-site", not "same-origin".
        await AssertRefusedAsync(await SendAsync(factory, NegotiateRequest(_owner.SessionCookie, null, null, null, ("Sec-Fetch-Site", "same-site"))));
        await AssertRefusedAsync(await SendAsync(factory, NegotiateRequest(_owner.SessionCookie, null, null, null, ("Sec-Fetch-Site", "cross-site"))));
    }

    [RequiresDatabaseFact]
    public async Task Bearer_WithoutOrigin_IsAccepted_AndWithAnyOrigin()
    {
        var (_, token) = await _owner.CreateTokenAsync("*");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(factory, NegotiateRequest(null, token, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(factory, NegotiateRequest(null, token, "https://app.example.com"))).StatusCode);
        // A bearer token wins over a cookie sent along: the request is token-authenticated, so the cookie plays no part.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(factory, NegotiateRequest(_owner.SessionCookie, token, null))).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task AccessTokenQueryParameter_IsExempt_LikeBearer()
    {
        var (_, token) = await _owner.CreateTokenAsync("*");
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Negotiate}&access_token={token}");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(factory, request)).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task AnAllowlistedOrigin_IsAccepted_OthersStillRefused()
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("AETHERA_CORS_ORIGINS", "https://ui.example.com/, http://localhost:3000"));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(configured, NegotiateRequest(_owner.SessionCookie, null, "https://ui.example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(configured, NegotiateRequest(_owner.SessionCookie, null, "HTTP://LOCALHOST:3000"))).StatusCode);
        await AssertRefusedAsync(await SendAsync(configured, NegotiateRequest(_owner.SessionCookie, null, "https://app.example.com")));
        await AssertRefusedAsync(await SendAsync(configured, NegotiateRequest(_owner.SessionCookie, null, "http://ui.example.com"))); // scheme matters
    }

    [RequiresDatabaseFact]
    public async Task Anonymous_IsStill401_NotAnOriginError()
    {
        var response = await SendAsync(factory, NegotiateRequest(null, null, "https://app.example.com"));

        response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.unauthenticated");
    }

    [RequiresDatabaseFact]
    public async Task TheOwnOrigin_IsTheOneSeenAfterForwardedHeaders()
    {
        // Behind Traefik: the browser talks https to aethera.example.com, the app sees plain http from the proxy.
        void Headers(HttpRequestMessage r) => r.Headers.Add("X-Forwarded-Proto", "https");

        using var trusted = FakeRemoteIpStartupFilter.From(factory, "10.1.2.3", "10.0.0.0/8");
        // (Over https, as the app sees it after X-Forwarded-Proto, the session cookie carries its __Host- name.)
        var request = NegotiateRequest("__Host-aethera_session", _owner.SessionCookie, null, "https://aethera.example.com", "aethera.example.com");
        Headers(request);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(trusted, request)).StatusCode);

        // The same request through a proxy we do not trust: X-Forwarded-Proto is ignored, so the origin is http://aethera.example.com.
        using var untrusted = FakeRemoteIpStartupFilter.From(factory, "198.51.100.9", "10.0.0.0/8");
        var spoofed = NegotiateRequest(_owner.SessionCookie, null, "https://aethera.example.com", "aethera.example.com");
        Headers(spoofed);
        await AssertRefusedAsync(await SendAsync(untrusted, spoofed));
    }

    [RequiresDatabaseFact]
    public async Task WebSocket_FromASiblingSubdomain_IsRefused_FromTheOwnOrigin_Accepted()
    {
        var negotiated = await SendAsync(factory, NegotiateRequest(_owner.SessionCookie, null, "http://localhost"));
        var token = (await negotiated.ReadJsonAsync()).Str("connectionToken");
        var server = factory.Server;

        async Task<WebSocket> ConnectAsync(string origin)
        {
            var client = server.CreateWebSocketClient();
            client.ConfigureRequest = r =>
            {
                r.Headers["Cookie"] = $"aethera_session={_owner.SessionCookie}";
                r.Headers["Origin"] = origin;
            };
            return await client.ConnectAsync(new Uri($"ws://localhost/hubs/logs?id={Uri.EscapeDataString(token)}"), CancellationToken.None);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("https://app.example.com"));

        using var socket = await ConnectAsync("http://localhost");
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [RequiresDatabaseFact]
    public async Task RestApi_IsNotAffectedByTheOriginRule()
    {
        // CSRF protects REST; the origin rule is for /hubs only.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("Cookie", $"aethera_session={_owner.SessionCookie}");
        request.Headers.Add("Origin", "https://app.example.com");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(factory, request)).StatusCode);
    }
}
