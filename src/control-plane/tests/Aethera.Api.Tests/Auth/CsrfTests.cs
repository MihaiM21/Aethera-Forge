using System.Net;
using System.Text.RegularExpressions;
using Aethera.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aethera.Api.Tests.Auth;

public sealed class CsrfTests : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    private readonly AuthApiFactory _factory;
    private readonly WebApplicationFactory<Program> _app;

    public CsrfTests(AuthApiFactory factory)
    {
        _factory = factory;
        _app = factory.WithEndpoints(TestApi.Map, TestApi.Services); // adds endpoints from "another feature" to the /api/v1 group
    }

    public async Task InitializeAsync()
    {
        if (_factory.HasDatabase) await AuthDb.ResetAsync(_factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ApiClient> OwnerAsync()
    {
        await _factory.SetupOwnerAsync();
        var client = ApiClient.Create(_app);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        return client;
    }

    [RequiresDatabaseFact]
    public async Task UnsafeRequest_WithTheSessionCookieButNoToken_Is403CsrfInvalid()
    {
        var client = await OwnerAsync();
        var (tokenId, _) = await client.CreateTokenAsync("read");

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, "/api/v1/auth/logout", new { }),
            (HttpMethod.Post, "/api/v1/api-tokens", new { name = "x", scopes = new[] { "read" } }),
            (HttpMethod.Delete, $"/api/v1/api-tokens/{tokenId}", null),
            (HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = ApiClient.Password, newPassword = "another long passphrase" }),
            (HttpMethod.Post, "/api/v1/_test/scope/write", new { }), // an endpoint of a different feature in the same group
        };
        foreach (var (method, path, body) in attempts)
        {
            var response = await client.SendAsync(method, path, body, csrf: false);
            response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.csrf_invalid");
        }

        // Nothing happened: still signed in, token still alive.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/api-tokens/{tokenId}")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task UnsafeRequest_WithTheToken_Works()
    {
        var client = await OwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/_test/scope/write")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/api-tokens", new { name = "x", scopes = new[] { "read" } })).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task UnsafeRequest_WithAWrongOrForeignToken_Is403()
    {
        var client = await OwnerAsync();
        var elsewhere = ApiClient.Create(_app);
        await elsewhere.LoginAsync("owner@example.com"); // a second session of the same user
        Assert.NotEqual(client.CsrfToken, elsewhere.CsrfToken);

        foreach (var wrong in new[] { "", "garbage", elsewhere.CsrfToken!, client.CsrfToken![..^1] + "x" })
        {
            client.CsrfToken = wrong;
            var response = await client.PostAsync("/api/v1/_test/scope/write");
            response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.csrf_invalid");
        }
    }

    [RequiresDatabaseFact]
    public async Task SafeMethods_NeedNoToken()
    {
        var client = await OwnerAsync();
        client.CsrfToken = null;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/api-tokens")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task BearerTokenRequests_AreExempt()
    {
        var owner = await OwnerAsync();
        var (_, token) = await owner.CreateTokenAsync("read", "write");

        var bearer = _app.WithBearer(token); // no cookie, no CSRF header
        Assert.Equal(HttpStatusCode.OK, (await bearer.PostAsync("/api/v1/_test/scope/write", csrf: false)).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task AnonymousEndpoints_NeedNoToken_EvenWithAStaleCookie()
    {
        var owner = await OwnerAsync();
        var stale = ApiClient.Create(_app);
        stale.SessionCookie = owner.SessionCookie; // a live cookie, no CSRF header
        var response = await stale.PostAsync("/api/v1/auth/login", new { email = "owner@example.com", password = ApiClient.Password }, csrf: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task AuthorizationFailures_WinOverCsrfFailures()
    {
        var owner = await OwnerAsync();
        var (viewer, _) = await owner.CreateSignedInUserAsync(_app, "viewer@example.com", OrganizationRole.Viewer);

        // Unauthenticated: 401, not 403.
        var anonymous = ApiClient.Create(_app);
        var unauthenticated = await anonymous.PostAsync("/api/v1/_test/scope/write", csrf: false);
        unauthenticated.AssertProblem(await unauthenticated.ReadJsonAsync(), 401, "auth.unauthenticated");

        // Authenticated but not allowed: 403 auth.forbidden, whether or not the token is sent.
        var forbidden = await viewer.SendAsync(HttpMethod.Post, "/api/v1/_test/role-and-scope", new { }, csrf: false);
        forbidden.AssertProblem(await forbidden.ReadJsonAsync(), 403, "auth.forbidden");
    }

    [RequiresDatabaseFact]
    public async Task CsrfToken_IsBoundToTheSession()
    {
        var owner = await OwnerAsync();
        var first = (await owner.ReadAsync(await owner.GetAsync("/api/v1/auth/csrf"), HttpStatusCode.OK)).Str("token");
        var again = (await owner.ReadAsync(await owner.GetAsync("/api/v1/auth/csrf"), HttpStatusCode.OK)).Str("token");
        Assert.Equal(first, again); // stable for the life of the session
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), first);

        var other = ApiClient.Create(_app);
        await other.LoginAsync("owner@example.com");
        Assert.NotEqual(first, other.CsrfToken);

        // After logout the old token is useless (the session is gone).
        await owner.PostAsync("/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v1/auth/csrf")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task CsrfEndpoint_IsForSessionsOnly()
    {
        var owner = await OwnerAsync();
        var (_, token) = await owner.CreateTokenAsync("*");

        var viaToken = _factory.WithBearer(token);
        var response = await viaToken.GetAsync("/api/v1/auth/csrf");
        response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.forbidden");

        var anonymous = ApiClient.Create(_app);
        var unauthenticated = await anonymous.GetAsync("/api/v1/auth/csrf");
        unauthenticated.AssertProblem(await unauthenticated.ReadJsonAsync(), 401, "auth.unauthenticated");
    }
}
