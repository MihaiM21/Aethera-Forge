using System.Net;
using Aethera.Domain;

namespace Aethera.Api.Tests.Auth;

public sealed class SessionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    private ApiClient _owner = null!;

    public async Task InitializeAsync()
    {
        if (!factory.HasDatabase) return;
        await AuthDb.ResetAsync(factory);
        _owner = await factory.SetupOwnerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<HttpStatusCode> MeStatusAsync(ApiClient client) => (await client.GetAsync("/api/v1/auth/me")).StatusCode;

    [RequiresDatabaseFact]
    public async Task Session_SlidesWhileActive_AndExpiresWhenIdle()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromHours(11));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client));
        factory.Clock.Advance(TimeSpan.FromHours(11)); // 22 h after login, but only 11 h idle: the first request slid the expiry
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client));

        factory.Clock.Advance(TimeSpan.FromHours(13)); // idle for longer than 12 h
        var expired = await client.GetAsync("/api/v1/auth/me");
        expired.AssertProblem(await expired.ReadJsonAsync(), 401, "auth.unauthenticated");
        Assert.Null(expired.Headers.Location); // JSON, never a redirect to a login page
    }

    [RequiresDatabaseFact]
    public async Task Activity_IsWrittenAtMostOncePerMinute()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        var created = await AuthDb.ScalarAsync<DateTime>(factory, "SELECT last_seen_at FROM user_sessions WHERE revoked_at IS NULL ORDER BY created_at DESC LIMIT 1");

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await MeStatusAsync(client);
        Assert.Equal(created, await AuthDb.ScalarAsync<DateTime>(factory, "SELECT last_seen_at FROM user_sessions ORDER BY created_at DESC LIMIT 1"));

        factory.Clock.Advance(TimeSpan.FromSeconds(40)); // 70 s after the last write
        await MeStatusAsync(client);
        var touched = await AuthDb.ScalarAsync<DateTime>(factory, "SELECT last_seen_at FROM user_sessions ORDER BY created_at DESC LIMIT 1");
        Assert.True(touched - created >= TimeSpan.FromSeconds(60), $"last_seen_at moved by {touched - created}");
    }

    [RequiresDatabaseFact]
    public async Task RememberMeSession_SlidesForSevenDays_ButStopsAtThirtyDays()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com", rememberMe: true), HttpStatusCode.OK);
        var firstCookie = client.LastSetCookie;

        // Every 6 days is inside the 7-day idle window, and each visit slides it.
        for (var day = 6; day <= 24; day += 6)
        {
            factory.Clock.Advance(TimeSpan.FromDays(6));
            Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client));
        }

        // The persistent cookie was re-issued with a later expiry as the session slid.
        Assert.NotEqual(firstCookie, client.LastSetCookie);
        Assert.Contains("expires=", client.LastSetCookie!, StringComparison.OrdinalIgnoreCase);

        factory.Clock.Advance(TimeSpan.FromDays(6)); // day 30: the absolute limit, whatever the activity
        var expired = await client.GetAsync("/api/v1/auth/me");
        expired.AssertProblem(await expired.ReadJsonAsync(), 401, "auth.unauthenticated");
    }

    [RequiresDatabaseFact]
    public async Task TransientSession_AlsoStopsAtThirtyDays()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        for (var i = 0; i < 59; i++)
        {
            factory.Clock.Advance(TimeSpan.FromHours(11.9));
            Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client));
        }

        factory.Clock.Advance(TimeSpan.FromHours(11.9)); // 60 * 11.9 h = 29.75 days
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client));
        factory.Clock.Advance(TimeSpan.FromHours(11.9)); // past 30 days
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client));
    }

    [RequiresDatabaseFact]
    public async Task Logout_RevokesTheSession_ClearsTheCookie_AndIsAudited()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        var cookie = client.SessionCookie!;

        var logout = await client.PostAsync("/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Null(client.SessionCookie); // the response cleared it
        Assert.Contains("expires=Thu, 01 Jan 1970", client.LastSetCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth.logout", await AuthDb.AuditActionsAsync(factory));

        // A client that kept the old cookie is rejected: the server-side session is gone, not just the cookie.
        client.SessionCookie = cookie;
        var replay = await client.GetAsync("/api/v1/auth/me");
        replay.AssertProblem(await replay.ReadJsonAsync(), 401, "auth.unauthenticated");
        Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory,
            "SELECT count(*) FROM user_sessions WHERE revoked_at IS NOT NULL"));
    }

    [RequiresDatabaseFact]
    public async Task ChangingThePassword_RevokesOtherSessions_ButKeepsTheCurrentOne()
    {
        var current = ApiClient.Create(factory);
        var other = ApiClient.Create(factory);
        await current.ReadAsync(await current.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        await other.ReadAsync(await other.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        const string newPassword = "an entirely new passphrase 42";

        var changed = await current.PostAsync("/api/v1/auth/password", new { currentPassword = ApiClient.Password, newPassword });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(current));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(other));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(_owner)); // the setup session was another one

        var fresh = ApiClient.Create(factory);
        var oldPassword = await fresh.LoginAsync("owner@example.com", ApiClient.Password);
        oldPassword.AssertProblem(await oldPassword.ReadJsonAsync(), 401, "auth.invalid_credentials");
        await fresh.ReadAsync(await fresh.LoginAsync("owner@example.com", newPassword), HttpStatusCode.OK);

        Assert.Contains("auth.password.changed", await AuthDb.AuditActionsAsync(factory));
        var metadata = await AuthDb.AuditMetadataAsync(factory, "auth.password.changed");
        Assert.DoesNotContain(newPassword, metadata);
        Assert.DoesNotContain(ApiClient.Password, metadata);
    }

    [RequiresDatabaseFact]
    public async Task ChangingThePassword_WithTheWrongCurrentPassword_Is422_AndCountsTowardsLockout()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);

        for (var i = 0; i < 4; i++)
        {
            var wrong = await client.PostAsync("/api/v1/auth/password", new { currentPassword = "not the password " + i, newPassword = "an entirely new passphrase 42" });
            var body = await wrong.ReadJsonAsync();
            wrong.AssertProblem(body, 422, "validation.failed");
            Assert.Contains(body["errors"]!.AsArray(), e => e!.Str("pointer") == "/currentPassword" && e.Str("code") == "incorrect");
        }

        var locked = await client.PostAsync("/api/v1/auth/password", new { currentPassword = "not the password 5", newPassword = "an entirely new passphrase 42" });
        locked.AssertProblem(await locked.ReadJsonAsync(), 423, "auth.locked_out");
    }

    [RequiresDatabaseTheory]
    [InlineData("short", "/newPassword", "too_short")]
    [InlineData("owner@example.com", "/newPassword", "password.same_as_email")]
    public async Task ChangingThePassword_EnforcesThePolicy(string newPassword, string pointer, string code)
    {
        var response = await _owner.PostAsync("/api/v1/auth/password", new { currentPassword = ApiClient.Password, newPassword });
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 422, "validation.failed");
        Assert.Contains(body["errors"]!.AsArray(), e => e!.Str("pointer") == pointer && e.Str("code") == code);
    }

    [RequiresDatabaseFact]
    public async Task ChangingThePassword_NeedsABrowserSession_NotAnApiToken()
    {
        var (_, token) = await _owner.CreateTokenAsync("*");
        var viaToken = factory.WithBearer(token);
        var response = await viaToken.PostAsync("/api/v1/auth/password", new { currentPassword = ApiClient.Password, newPassword = "an entirely new passphrase 42" });
        response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.forbidden");
    }

    [RequiresDatabaseFact]
    public async Task DeactivatingAUser_EndsTheirSessionsImmediately()
    {
        var (developer, id) = await _owner.CreateSignedInUserAsync(factory, "dev@example.com", OrganizationRole.Developer);
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(developer));

        await _owner.ReadAsync(await _owner.PatchAsync($"/api/v1/users/{id}", new { isActive = false }), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(developer));
        var login = await ApiClient.Create(factory).LoginAsync("dev@example.com");
        login.AssertProblem(await login.ReadJsonAsync(), 401, "auth.invalid_credentials");
    }

    [RequiresDatabaseFact]
    public async Task RoleChanges_ApplyToLiveSessionsImmediately()
    {
        var (user, id) = await _owner.CreateSignedInUserAsync(factory, "dev@example.com", OrganizationRole.Developer);
        var denied = await user.GetAsync("/api/v1/users");
        denied.AssertProblem(await denied.ReadJsonAsync(), 403, "auth.forbidden");

        await _owner.ReadAsync(await _owner.PutAsync($"/api/v1/users/{id}/role", new { role = "admin" }), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/users")).StatusCode);
    }

    [RequiresDatabaseTheory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("sAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // right shape, no such session
    [InlineData("xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // wrong lifetime marker
    public async Task BadCookies_Are401Json(string cookie)
    {
        var client = ApiClient.Create(factory);
        client.SessionCookie = cookie;
        var response = await client.GetAsync("/api/v1/auth/me");
        response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.unauthenticated");
        Assert.Null(response.Headers.Location);
    }

    [RequiresDatabaseFact]
    public async Task ASessionWhoseUserWasDeleted_IsRejected()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        await AuthDb.ExecuteAsync(factory, "UPDATE users SET deleted_at = now()");
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client));
    }
}

/// <summary>Production: the cookie is the secure <c>__Host-</c> one whatever the request scheme says (TLS may be terminated by a proxy).</summary>
public sealed class ProductionCookieTests(ProductionAuthApiFactory factory) : IClassFixture<ProductionAuthApiFactory>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (factory.HasDatabase) await AuthDb.ResetAsync(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresDatabaseTheory]
    [InlineData("https://localhost")]
    [InlineData("http://localhost")] // behind a TLS-terminating proxy the app sees http
    public async Task SessionCookie_IsAHostPrefixedSecureHttpOnlyLaxCookie(string baseAddress)
    {
        var client = ApiClient.Create(factory, new Uri(baseAddress), cookieName: "__Host-aethera_session");
        var response = await client.PostAsync("/api/v1/auth/setup",
            new { email = "owner@example.com", password = ApiClient.Password, displayName = "Owner", organizationName = "Acme" }, csrf: false);
        await client.ReadAsync(response, HttpStatusCode.Created);

        var cookie = client.LastSetCookie!.ToLowerInvariant();
        Assert.StartsWith("__host-aethera_session=", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        // The development cookie is not accepted here.
        var other = ApiClient.Create(factory, new Uri(baseAddress), cookieName: "aethera_session");
        other.SessionCookie = client.SessionCookie;
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/auth/me")).StatusCode);
    }
}
