using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Microsoft.AspNetCore.Identity;

namespace Aethera.Api.Tests.Auth;

public sealed class LoginTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!factory.HasDatabase) return;
        await AuthDb.ResetAsync(factory);
        await factory.SetupOwnerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresDatabaseFact]
    public async Task Login_WithCorrectCredentials_SignsInAndAudits()
    {
        var client = ApiClient.Create(factory);
        var response = await client.LoginAsync("OWNER@example.com");
        var me = await client.ReadAsync(response, HttpStatusCode.OK);

        Assert.Equal("owner@example.com", me["user"].Str("email"));
        Assert.Equal("owner", me.Str("role"));
        Assert.NotNull(client.SessionCookie);
        Assert.NotNull(await AuthDb.ScalarAsync<DateTime?>(factory, "SELECT last_login_at FROM users"));
        Assert.Contains("auth.login.succeeded", await AuthDb.AuditActionsAsync(factory));

        var audit = await AuthDb.AuditMetadataAsync(factory, "auth.login.succeeded");
        Assert.DoesNotContain(ApiClient.Password, audit);
    }

    [RequiresDatabaseFact]
    public async Task UnknownEmailAndWrongPassword_AreIndistinguishable()
    {
        var client = ApiClient.Create(factory);
        var unknown = await client.LoginAsync("nobody@example.com", "whatever password 123");
        var wrong = await client.LoginAsync("owner@example.com", "whatever password 123");

        var unknownBody = await unknown.ReadJsonAsync();
        var wrongBody = await wrong.ReadJsonAsync();
        unknown.AssertProblem(unknownBody, 401, "auth.invalid_credentials");
        wrong.AssertProblem(wrongBody, 401, "auth.invalid_credentials");
        Assert.Equal(unknownBody.Str("title"), wrongBody.Str("title"));
        Assert.Equal(unknownBody.Str("detail"), wrongBody.Str("detail"));
        Assert.Equal(unknown.Headers.WwwAuthenticate.ToString(), wrong.Headers.WwwAuthenticate.ToString());
        Assert.Null(client.SessionCookie);
    }

    [RequiresDatabaseFact]
    public async Task FailedLogins_AreAudited_WithTheEmailAndNeverThePassword()
    {
        var client = ApiClient.Create(factory);
        const string guess = "a very secret wrong guess";
        await client.LoginAsync("owner@example.com", guess);
        await client.LoginAsync("ghost@example.com", guess);

        Assert.Equal(2, (await AuthDb.AuditActionsAsync(factory)).Count(a => a == "auth.login.failed"));
        var metadata = await AuthDb.AuditMetadataAsync(factory, "auth.login.failed");
        Assert.Contains("owner@example.com", metadata);
        Assert.Contains("ghost@example.com", metadata);
        Assert.DoesNotContain(guess, metadata);
    }

    [RequiresDatabaseFact]
    public async Task Login_ValidatesTheBody()
    {
        var client = ApiClient.Create(factory);
        var response = await client.PostAsync("/api/v1/auth/login", new { email = "", password = "" }, csrf: false);
        response.AssertProblem(await response.ReadJsonAsync(), 422, "validation.failed");
    }

    [RequiresDatabaseFact]
    public async Task RepeatedFailures_LockTheAccount_UntilTheLockoutExpires()
    {
        var client = ApiClient.Create(factory);
        for (var i = 1; i <= 4; i++)
        {
            var failed = await client.LoginAsync("owner@example.com", "wrong password " + i);
            failed.AssertProblem(await failed.ReadJsonAsync(), 401, "auth.invalid_credentials");
        }

        // The fifth wrong attempt trips the lockout and says so.
        var locking = await client.LoginAsync("owner@example.com", "wrong password 5");
        var body = await locking.ReadJsonAsync();
        locking.AssertProblem(body, 423, "auth.locked_out");
        var retryAfter = body["retryAfter"]!.GetValue<int>();
        Assert.InRange(retryAfter, 800, 900);
        Assert.Equal(retryAfter.ToString(), locking.Headers.GetValues("Retry-After").Single());

        // Even the correct password is refused while locked, and reveals nothing more.
        var correct = await client.LoginAsync("owner@example.com");
        correct.AssertProblem(await correct.ReadJsonAsync(), 423, "auth.locked_out");

        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        Assert.Equal(0, await AuthDb.ScalarAsync<int>(factory, "SELECT failed_login_count FROM users"));
    }

    [RequiresDatabaseFact]
    public async Task ParallelWrongPasswords_CannotDodgeTheLockout()
    {
        var attempts = Enumerable.Range(0, 12).Select(async i =>
        {
            var client = ApiClient.Create(factory);
            return (await client.LoginAsync("owner@example.com", "wrong password " + i)).StatusCode;
        });
        await Task.WhenAll(attempts);

        var count = await AuthDb.ScalarAsync<int>(factory, "SELECT failed_login_count FROM users");
        Assert.True(count >= 5, $"expected the failures to be counted, got {count}");
        Assert.NotNull(await AuthDb.ScalarAsync<DateTime?>(factory, "SELECT lockout_end_at FROM users"));
    }

    [RequiresDatabaseFact]
    public async Task DeactivatedUser_CannotLogIn_AndGetsTheSameAnswer()
    {
        await AuthDb.ExecuteAsync(factory, "UPDATE users SET is_active = false");
        var client = ApiClient.Create(factory);
        var response = await client.LoginAsync("owner@example.com");
        response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.invalid_credentials");
    }

    [RequiresDatabaseFact]
    public async Task LegacyPasswordHash_IsUpgradedOnLogin()
    {
        var legacy = new PasswordHasher<User>(new Microsoft.Extensions.Options.OptionsWrapper<PasswordHasherOptions>(
                new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }))
            .HashPassword(null!, ApiClient.Password);
        await AuthDb.ExecuteAsync(factory, "UPDATE users SET password_hash = @h", ("h", legacy));

        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);

        var upgraded = await AuthDb.ScalarAsync<string>(factory, "SELECT password_hash FROM users");
        Assert.NotEqual(legacy, upgraded);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(null!, upgraded, ApiClient.Password));

        // And the upgraded hash keeps working.
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
    }

    [RequiresDatabaseFact]
    public async Task SessionRow_HoldsOnlyTheHashOfTheCookie()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        var cookie = client.SessionCookie!;

        var stored = await AuthDb.ScalarAsync<byte[]>(factory, "SELECT secret_hash FROM user_sessions ORDER BY created_at DESC LIMIT 1");
        Assert.Equal(AuthTestExtensions.Sha256(cookie), stored);
        Assert.DoesNotContain(cookie, Encoding.ASCII.GetString(stored));
        Assert.Equal(44, cookie.Length); // one lifetime marker + 32 random bytes in base64url
        Assert.Equal('s', cookie[0]);
    }

    [RequiresDatabaseFact]
    public async Task SessionCookie_InTesting_IsHttpOnlyLaxAndNotSecure()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        var cookie = client.LastSetCookie!.ToLowerInvariant();

        Assert.StartsWith("aethera_session=", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("secure", cookie);
        Assert.DoesNotContain("domain=", cookie);
        // A browser-session cookie: no Expires / Max-Age.
        Assert.DoesNotContain("expires=", cookie);
        Assert.DoesNotContain("max-age=", cookie);
    }

    [RequiresDatabaseFact]
    public async Task RememberMe_SetsAPersistentCookie()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com", rememberMe: true), HttpStatusCode.OK);

        Assert.Contains("expires=", client.LastSetCookie!.ToLowerInvariant());
        Assert.Equal('p', client.SessionCookie![0]);
        var expires = await AuthDb.ScalarAsync<DateTime>(factory, "SELECT expires_at FROM user_sessions ORDER BY created_at DESC LIMIT 1");
        Assert.InRange((expires - DateTime.UtcNow).TotalDays, 6.9, 7.1);
    }

    [RequiresDatabaseFact]
    public async Task SigningInAgain_ReplacesTheSessionTheBrowserStillHeld()
    {
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        var first = client.SessionCookie!;

        // Log in again without discarding the cookie, as a browser would.
        var again = await client.PostAsync("/api/v1/auth/login", new { email = "owner@example.com", password = ApiClient.Password });
        await client.ReadAsync(again, HttpStatusCode.OK);
        Assert.NotEqual(first, client.SessionCookie);

        var stale = ApiClient.Create(factory);
        stale.SessionCookie = first;
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task ResponsesThatCarryCredentials_AreNotCacheable()
    {
        var client = ApiClient.Create(factory);
        var login = await client.LoginAsync("owner@example.com");
        Assert.Equal("no-store", login.Headers.CacheControl?.ToString());
        Assert.Equal("no-store", (await client.GetAsync("/api/v1/auth/csrf")).Headers.CacheControl?.ToString());
        var created = await client.PostAsync("/api/v1/api-tokens", new { name = "t", scopes = new[] { "read" } });
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
    }

    [RequiresDatabaseFact]
    public async Task EverySessionGetsItsOwnUnguessableSecret()
    {
        var secrets = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            var client = ApiClient.Create(factory);
            await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK);
            Assert.True(secrets.Add(client.SessionCookie!));
        }
    }
}

public sealed class LoginRateLimitTests(RateLimitedAuthApiFactory factory) : IClassFixture<RateLimitedAuthApiFactory>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!factory.HasDatabase) return;
        await AuthDb.ResetAsync(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresDatabaseFact]
    public async Task Login_IsRateLimitedPerIp_With429AndRetryAfter()
    {
        await factory.SetupOwnerAsync(); // setup is not limited
        var client = ApiClient.Create(factory);

        for (var i = 0; i < 3; i++)
        {
            var allowed = await client.LoginAsync("owner@example.com", "wrong password " + i);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var limited = await client.LoginAsync("owner@example.com");
        var body = await limited.ReadJsonAsync();
        limited.AssertProblem(body, 429, "rate_limited");
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, 60);

        // Everything else is untouched by the login limit.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/setup")).StatusCode);
    }
}
