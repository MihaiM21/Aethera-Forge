using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.HostCommands;
using Aethera.Domain;
using Aethera.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aethera.Api.Tests.Auth;

/// <summary>TOTP two-factor authentication end to end (ADR 0013): enrolment, two-step sign-in, recovery codes, the policy, the host reset.</summary>
public sealed class TwoFactorTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    private const string Owner = "owner@example.com";
    private ApiClient _owner = null!;

    public async Task InitializeAsync()
    {
        if (!factory.HasDatabase) return;
        await AuthDb.ResetAsync(factory);
        _owner = await factory.SetupOwnerAsync(Owner);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers -----------------------------------------------------------------------------------------------------------------

    private sealed record Enrolled(byte[] Secret, IReadOnlyList<string> RecoveryCodes);

    /// <summary>The code for "now" on the test clock; moves the clock to the next step first so it was never used.</summary>
    private string NextCode(byte[] secret)
    {
        factory.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));
        return Totp.Code(secret, Totp.StepAt(factory.Clock.UtcNow));
    }

    private async Task<Enrolled> EnrolAsync(ApiClient client)
    {
        var start = await client.ReadAsync(await client.PostAsync("/api/v1/auth/two-factor/enrolment", new { password = ApiClient.Password }), HttpStatusCode.OK);
        var secret = Base32.Decode(start.Str("secret"));
        Assert.StartsWith("otpauth://totp/Aethera:", start.Str("otpauthUri"));
        Assert.Contains("secret=" + start.Str("secret"), start.Str("otpauthUri"));

        var confirm = await client.ReadAsync(await client.PostAsync("/api/v1/auth/two-factor/enrolment/confirm", new { code = NextCode(secret) }), HttpStatusCode.OK);
        var codes = confirm["recoveryCodes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        return new Enrolled(secret, codes);
    }

    /// <summary>The password step; returns the challenge of the 401 auth.two_factor_required.</summary>
    private static async Task<string> PasswordStepAsync(ApiClient client, string email, bool rememberMe = false)
    {
        var response = await client.LoginAsync(email, rememberMe: rememberMe);
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 401, "auth.two_factor_required");
        Assert.Null(client.SessionCookie);
        Assert.Equal(300, body["expiresIn"]!.GetValue<int>());
        return body.Str("challenge");
    }

    private static async Task<HttpResponseMessage> CodeStepAsync(ApiClient client, string challenge, string? code = null, string? recoveryCode = null)
    {
        var response = await client.PostAsync("/api/v1/auth/login/two-factor", new { challenge, code, recoveryCode }, csrf: false);
        if (response.IsSuccessStatusCode) await client.LoadCsrfAsync();
        return response;
    }

    private async Task<ApiClient> SignInWithCodeAsync(string email, byte[] secret)
    {
        var client = ApiClient.Create(factory);
        var challenge = await PasswordStepAsync(client, email);
        await client.ReadAsync(await CodeStepAsync(client, challenge, NextCode(secret)), HttpStatusCode.OK);
        return client;
    }

    // ---- enrolment ---------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Enrolment_TurnsTwoFactorOn_StoresTheSecretEncrypted_AndAudits()
    {
        var before = await _owner.ReadAsync(await _owner.GetAsync("/api/v1/auth/two-factor"), HttpStatusCode.OK);
        Assert.False(before["enabled"]!.GetValue<bool>());

        var enrolled = await EnrolAsync(_owner);

        Assert.Equal(10, enrolled.RecoveryCodes.Count);
        var status = await _owner.ReadAsync(await _owner.GetAsync("/api/v1/auth/two-factor"), HttpStatusCode.OK);
        Assert.True(status["enabled"]!.GetValue<bool>());
        Assert.False(status["enrolmentPending"]!.GetValue<bool>());
        Assert.Equal(10, status["recoveryCodesRemaining"]!.GetValue<int>());
        var me = await _owner.ReadAsync(await _owner.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
        Assert.True(me["twoFactorEnabled"]!.GetValue<bool>());

        // The row holds ciphertext only: neither the raw secret nor its base32 form appear, and recovery codes are hashed.
        var ciphertext = await AuthDb.ScalarAsync<byte[]>(factory, "SELECT secret_ciphertext FROM user_two_factors");
        Assert.False(ciphertext.AsSpan().IndexOf(enrolled.Secret) >= 0);
        var row = await AuthDb.ScalarAsync<string>(factory, "SELECT row_to_json(t)::text FROM user_two_factors t");
        Assert.DoesNotContain(Base32.Encode(enrolled.Secret), row);
        var hashes = await AuthDb.ScalarAsync<string>(factory, "SELECT string_agg(encode(code_hash, 'escape'), ',') FROM user_recovery_codes");
        foreach (var code in enrolled.RecoveryCodes) Assert.DoesNotContain(code.Replace("-", ""), hashes);

        var actions = await AuthDb.AuditActionsAsync(factory);
        Assert.Contains("auth.two_factor.enrolment_started", actions);
        Assert.Contains("auth.two_factor.enabled", actions);
        var audit = string.Join(" ", await Task.WhenAll(actions.Distinct().Select(a => AuthDb.AuditMetadataAsync(factory, a))));
        Assert.DoesNotContain(Base32.Encode(enrolled.Secret), audit);
    }

    [RequiresDatabaseFact]
    public async Task Enrolment_NeedsTheCurrentPassword_AndAWrongOneCountsTowardsTheLockout()
    {
        var response = await _owner.PostAsync("/api/v1/auth/two-factor/enrolment", new { password = "not the password at all" });
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 422, "validation.failed");
        Assert.Equal("incorrect", body["errors"]![0].Str("code"));
        Assert.Equal(1, await AuthDb.ScalarAsync<int>(factory, "SELECT failed_login_count FROM users"));
    }

    [RequiresDatabaseFact]
    public async Task Enrolment_RefusesAWrongCode_AndLeavesTwoFactorOff()
    {
        var start = await _owner.ReadAsync(await _owner.PostAsync("/api/v1/auth/two-factor/enrolment", new { password = ApiClient.Password }), HttpStatusCode.OK);
        var secret = Base32.Decode(start.Str("secret"));
        var wrong = Totp.Code(secret, Totp.StepAt(factory.Clock.UtcNow) + 5);

        var response = await _owner.PostAsync("/api/v1/auth/two-factor/enrolment/confirm", new { code = wrong });
        response.AssertProblem(await response.ReadJsonAsync(), 422, "validation.failed");
        var status = await _owner.ReadAsync(await _owner.GetAsync("/api/v1/auth/two-factor"), HttpStatusCode.OK);
        Assert.False(status["enabled"]!.GetValue<bool>());
        Assert.True(status["enrolmentPending"]!.GetValue<bool>());

        // A pending enrolment does not change sign-in.
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync(Owner), HttpStatusCode.OK);
    }

    [RequiresDatabaseFact]
    public async Task ConfirmingEnrolment_SignsOutTheUsersOtherSessions()
    {
        var other = ApiClient.Create(factory);
        await other.ReadAsync(await other.LoginAsync(Owner), HttpStatusCode.OK);

        await EnrolAsync(_owner);

        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task StartingAgain_WhileEnabled_IsRefused()
    {
        await EnrolAsync(_owner);
        var response = await _owner.PostAsync("/api/v1/auth/two-factor/enrolment", new { password = ApiClient.Password });
        response.AssertProblem(await response.ReadJsonAsync(), 409, "auth.two_factor_already_enabled");
    }

    [RequiresDatabaseFact]
    public async Task ApiTokens_CannotManageTwoFactor_AndKeepWorkingWhenItIsOn()
    {
        var (_, token) = await _owner.CreateTokenAsync("*");
        var bearer = factory.WithBearer(token);

        var response = await bearer.PostAsync("/api/v1/auth/two-factor/enrolment", new { password = ApiClient.Password });
        response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.forbidden");

        await EnrolAsync(_owner);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/v1/projects")).StatusCode);
        var me = await bearer.ReadAsync(await bearer.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
        Assert.False(me["twoFactorEnrolmentRequired"]!.GetValue<bool>());
    }

    // ---- sign-in -----------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task SignIn_WithTwoFactor_NeedsTheCode_AfterThePassword()
    {
        var enrolled = await EnrolAsync(_owner);
        var client = ApiClient.Create(factory);

        var challenge = await PasswordStepAsync(client, Owner, rememberMe: true);
        var me = await client.ReadAsync(await CodeStepAsync(client, challenge, NextCode(enrolled.Secret)), HttpStatusCode.OK);

        Assert.Equal(Owner, me["user"].Str("email"));
        Assert.NotNull(client.SessionCookie);
        Assert.Contains("expires=", client.LastSetCookie!, StringComparison.OrdinalIgnoreCase); // remember me came through the challenge
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/projects")).StatusCode);
        Assert.Contains("\"twoFactor\": \"totp\"", await AuthDb.AuditMetadataAsync(factory, "auth.login.succeeded"));
    }

    [RequiresDatabaseFact]
    public async Task SignIn_RefusesAReusedCode()
    {
        var enrolled = await EnrolAsync(_owner);
        var code = NextCode(enrolled.Secret);
        var first = ApiClient.Create(factory);
        await first.ReadAsync(await CodeStepAsync(first, await PasswordStepAsync(first, Owner), code), HttpStatusCode.OK);

        var second = ApiClient.Create(factory);
        var response = await CodeStepAsync(second, await PasswordStepAsync(second, Owner), code);
        response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.two_factor_invalid_code");
        Assert.Null(second.SessionCookie);
    }

    [RequiresDatabaseFact]
    public async Task WrongCodes_LockTheAccount_AndAPasswordAloneDoesNotResetTheCount()
    {
        await EnrolAsync(_owner);
        var client = ApiClient.Create(factory);
        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++)
        {
            // A fresh challenge (correct password) each time: it must not clear the failures.
            last = await CodeStepAsync(client, await PasswordStepAsync(client, Owner), "000000");
        }

        last.AssertProblem(await last.ReadJsonAsync(), 423, "auth.locked_out");
        var locked = await client.LoginAsync(Owner);
        locked.AssertProblem(await locked.ReadJsonAsync(), 423, "auth.locked_out");
        Assert.Contains("invalid_two_factor_code", await AuthDb.AuditMetadataAsync(factory, "auth.login.failed"));
    }

    [RequiresDatabaseFact]
    public async Task ParallelWrongCodes_CannotOutrunTheLockout()
    {
        var enrolled = await EnrolAsync(_owner);
        var client = ApiClient.Create(factory);
        var challenge = await PasswordStepAsync(client, Owner);

        // Ten guesses at once with one challenge: each is checked and counted under the per-user lock, so only five are counted and the
        // others find the account locked without their code being tried.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            ApiClient.Create(factory).PostAsync("/api/v1/auth/login/two-factor", new { challenge, code = $"00000{i}" }, csrf: false)));

        Assert.Equal(5, await AuthDb.ScalarAsync<int>(factory, "SELECT failed_login_count FROM users WHERE normalized_email = @e", ("e", Owner)));
        // Four plain failures, the fifth locks (423), the other five arrive locked (423).
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.Locked));
        var correct = await CodeStepAsync(client, challenge, NextCode(enrolled.Secret));
        correct.AssertProblem(await correct.ReadJsonAsync(), 423, "auth.locked_out");
        Assert.Null(client.SessionCookie);
    }

    [RequiresDatabaseFact]
    public async Task WrongEnrolmentCodes_CountTowardsTheLockout()
    {
        await _owner.ReadAsync(await _owner.PostAsync("/api/v1/auth/two-factor/enrolment", new { password = ApiClient.Password }), HttpStatusCode.OK);
        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++) last = await _owner.PostAsync("/api/v1/auth/two-factor/enrolment/confirm", new { code = "000000" });

        last.AssertProblem(await last.ReadJsonAsync(), 423, "auth.locked_out");
        Assert.Contains("auth.two_factor.code_failed", await AuthDb.AuditActionsAsync(factory));
    }

    [RequiresDatabaseFact]
    public async Task RecoveryCode_SignsInOnce_AndIsAudited()
    {
        var enrolled = await EnrolAsync(_owner);
        var recovery = enrolled.RecoveryCodes[3];

        var client = ApiClient.Create(factory);
        await client.ReadAsync(await CodeStepAsync(client, await PasswordStepAsync(client, Owner), recoveryCode: recovery.ToUpperInvariant()), HttpStatusCode.OK);

        var again = ApiClient.Create(factory);
        var reused = await CodeStepAsync(again, await PasswordStepAsync(again, Owner), recoveryCode: recovery);
        reused.AssertProblem(await reused.ReadJsonAsync(), 401, "auth.two_factor_invalid_code");

        Assert.Contains("auth.two_factor.recovery_code_used", await AuthDb.AuditActionsAsync(factory));
        Assert.Contains("\"remaining\": 9", await AuthDb.AuditMetadataAsync(factory, "auth.two_factor.recovery_code_used"));
        Assert.DoesNotContain(recovery, await AuthDb.AuditMetadataAsync(factory, "auth.login.succeeded"));
    }

    [RequiresDatabaseFact]
    public async Task Challenge_Expires_AndDiesWithAPasswordChange()
    {
        var enrolled = await EnrolAsync(_owner);

        var client = ApiClient.Create(factory);
        var expired = await PasswordStepAsync(client, Owner);
        factory.Clock.Advance(TwoFactorChallenge.Lifetime + TimeSpan.FromSeconds(5));
        var late = await CodeStepAsync(client, expired, Totp.Code(enrolled.Secret, Totp.StepAt(factory.Clock.UtcNow)));
        late.AssertProblem(await late.ReadJsonAsync(), 401, "auth.two_factor_challenge_invalid");

        var pending = await PasswordStepAsync(client, Owner);
        await _owner.ReadAsync(await _owner.PostAsync("/api/v1/auth/password",
            new { currentPassword = ApiClient.Password, newPassword = "another long passphrase here" }), HttpStatusCode.NoContent);
        var stale = await CodeStepAsync(client, pending, NextCode(enrolled.Secret));
        stale.AssertProblem(await stale.ReadJsonAsync(), 401, "auth.two_factor_challenge_invalid");
    }

    [RequiresDatabaseFact]
    public async Task CodeStep_ValidatesTheBody()
    {
        var client = ApiClient.Create(factory);
        var neither = await client.PostAsync("/api/v1/auth/login/two-factor", new { challenge = "x" }, csrf: false);
        neither.AssertProblem(await neither.ReadJsonAsync(), 422, "validation.failed");
        var both = await client.PostAsync("/api/v1/auth/login/two-factor", new { challenge = "x", code = "123456", recoveryCode = "aaaa-bbbb-cccc-dddd" }, csrf: false);
        both.AssertProblem(await both.ReadJsonAsync(), 422, "validation.failed");
        var garbage = await client.PostAsync("/api/v1/auth/login/two-factor", new { challenge = "x", code = "123456" }, csrf: false);
        garbage.AssertProblem(await garbage.ReadJsonAsync(), 401, "auth.two_factor_challenge_invalid");
    }

    // ---- disable / recovery codes ------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Disable_NeedsPasswordAndCode_ThenSignInIsPasswordOnlyAgain()
    {
        var enrolled = await EnrolAsync(_owner);

        var noCode = await _owner.PostAsync("/api/v1/auth/two-factor/disable", new { password = ApiClient.Password, code = "000000" });
        noCode.AssertProblem(await noCode.ReadJsonAsync(), 422, "validation.failed");

        await _owner.ReadAsync(await _owner.PostAsync("/api/v1/auth/two-factor/disable",
            new { password = ApiClient.Password, code = NextCode(enrolled.Secret) }), HttpStatusCode.NoContent);

        Assert.Equal(0L, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM user_two_factors"));
        Assert.Equal(0L, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM user_recovery_codes"));
        Assert.Contains("auth.two_factor.disabled", await AuthDb.AuditActionsAsync(factory));
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync(Owner), HttpStatusCode.OK);
    }

    [RequiresDatabaseFact]
    public async Task Disable_AcceptsARecoveryCode()
    {
        var enrolled = await EnrolAsync(_owner);
        await _owner.ReadAsync(await _owner.PostAsync("/api/v1/auth/two-factor/disable",
            new { password = ApiClient.Password, recoveryCode = enrolled.RecoveryCodes[0] }), HttpStatusCode.NoContent);
        Assert.Contains("\"method\": \"recovery_code\"", await AuthDb.AuditMetadataAsync(factory, "auth.two_factor.disabled"));
    }

    [RequiresDatabaseFact]
    public async Task RegeneratingRecoveryCodes_ReplacesTheOldOnes()
    {
        var enrolled = await EnrolAsync(_owner);
        var fresh = await _owner.ReadAsync(await _owner.PostAsync("/api/v1/auth/two-factor/recovery-codes",
            new { password = ApiClient.Password, code = NextCode(enrolled.Secret) }), HttpStatusCode.OK);
        var codes = fresh["recoveryCodes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Equal(10, codes.Count);
        Assert.Empty(codes.Intersect(enrolled.RecoveryCodes));

        var client = ApiClient.Create(factory);
        var old = await CodeStepAsync(client, await PasswordStepAsync(client, Owner), recoveryCode: enrolled.RecoveryCodes[0]);
        old.AssertProblem(await old.ReadJsonAsync(), 401, "auth.two_factor_invalid_code");
        await client.ReadAsync(await CodeStepAsync(client, await PasswordStepAsync(client, Owner), recoveryCode: codes[0]), HttpStatusCode.OK);
        Assert.Contains("auth.two_factor.recovery_codes_regenerated", await AuthDb.AuditActionsAsync(factory));
    }

    // ---- organization policy -----------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Policy_NeedsTheOwnersOwnTwoFactor_AndTheOwnerRole()
    {
        var refused = await _owner.PutAsync("/api/v1/auth/two-factor/policy", new { required = true });
        refused.AssertProblem(await refused.ReadJsonAsync(), 409, "auth.two_factor_not_enabled");

        var (admin, _) = await _owner.CreateSignedInUserAsync(factory, "admin@example.com", OrganizationRole.Admin);
        var notOwner = await admin.PutAsync("/api/v1/auth/two-factor/policy", new { required = false });
        notOwner.AssertProblem(await notOwner.ReadJsonAsync(), 403, "auth.forbidden");

        await EnrolAsync(_owner);
        var status = await _owner.ReadAsync(await _owner.PutAsync("/api/v1/auth/two-factor/policy", new { required = true }), HttpStatusCode.OK);
        Assert.True(status["requiredByOrganization"]!.GetValue<bool>());
        Assert.Contains("\"required\": true", await AuthDb.AuditMetadataAsync(factory, "organization.two_factor_policy_changed"));
    }

    [RequiresDatabaseFact]
    public async Task Policy_RestrictsMembersWithoutTwoFactor_ToEnrolment_UntilTheyEnrol()
    {
        var (developer, developerId) = await _owner.CreateSignedInUserAsync(factory, "dev@example.com", OrganizationRole.Developer);
        var (_, token) = await developer.CreateTokenAsync("read");
        await EnrolAsync(_owner);
        await _owner.ReadAsync(await _owner.PutAsync("/api/v1/auth/two-factor/policy", new { required = true }), HttpStatusCode.OK);

        // The existing session is restricted at once: me, CSRF and two-factor work, the rest is 403 auth.two_factor_enrolment_required.
        var me = await developer.ReadAsync(await developer.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
        Assert.True(me["twoFactorEnrolmentRequired"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await developer.GetAsync("/api/v1/auth/csrf")).StatusCode);
        foreach (var path in new[] { "/api/v1/projects", "/api/v1/api-tokens", "/api/v1/users" })
        {
            var blocked = await developer.GetAsync(path);
            blocked.AssertProblem(await blocked.ReadJsonAsync(), 403, "auth.two_factor_enrolment_required");
        }

        var create = await developer.PostAsync("/api/v1/api-tokens", new { name = "sneaky", scopes = new[] { "read" } });
        create.AssertProblem(await create.ReadJsonAsync(), 403, "auth.two_factor_enrolment_required");

        // Live connections are covered too (the authorization handler, not just the /api/v1 filter).
        using (var negotiate = new HttpRequestMessage(HttpMethod.Post, "/hubs/jobs/negotiate?negotiateVersion=1"))
        {
            negotiate.Headers.Add("Cookie", $"aethera_session={developer.SessionCookie}");
            negotiate.Headers.Add("Origin", "http://localhost");
            var hub = await developer.Http.SendAsync(negotiate);
            hub.AssertProblem(await hub.ReadJsonAsync(), 403, "auth.two_factor_enrolment_required");
        }

        // API tokens are not affected.
        Assert.Equal(HttpStatusCode.OK, (await factory.WithBearer(token).GetAsync("/api/v1/projects")).StatusCode);

        // A fresh password sign-in gives the same restricted session; enrolling lifts the restriction for it.
        var again = ApiClient.Create(factory);
        await again.ReadAsync(await again.LoginAsync("dev@example.com"), HttpStatusCode.OK);
        var enrolled = await EnrolAsync(again);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/projects")).StatusCode);
        var after = await again.ReadAsync(await again.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
        Assert.False(after["twoFactorEnrolmentRequired"]!.GetValue<bool>());

        var members = await _owner.ReadAsync(await _owner.GetAsync("/api/v1/users"), HttpStatusCode.OK);
        var dev = members["items"]!.AsArray().First(u => u.Str("id") == developerId.ToString());
        Assert.True(dev!["twoFactorEnabled"]!.GetValue<bool>());
        Assert.NotEmpty(enrolled.RecoveryCodes);
    }

    // ---- host reset --------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task ResetTwoFactorCommand_ClearsIt_RevokesSessions_AndAuditsAsTheSystem()
    {
        await EnrolAsync(_owner);

        var error = new StringWriter();
        var exit = await ResetTwoFactorCommand.TryRunAsync(["reset-2fa", "OWNER@example.com"], error, () => factory.Services);

        Assert.Equal(0, exit);
        Assert.Contains("is off", error.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _owner.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(0L, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM user_two_factors"));
        Assert.Equal(0L, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM user_recovery_codes"));
        Assert.Equal("system", await AuthDb.ScalarAsync<string>(factory, "SELECT actor_type FROM audit_events WHERE action = 'users.two_factor_reset'"));

        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync(Owner), HttpStatusCode.OK);

        var again = await ResetTwoFactorCommand.TryRunAsync(["reset-2fa", Owner], TextWriter.Null, () => factory.Services);
        Assert.Equal(0, again);
        Assert.Equal(1, await ResetTwoFactorCommand.TryRunAsync(["reset-2fa", "ghost@example.com"], TextWriter.Null, () => factory.Services));
    }

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("a@b.c", "extra")]
    public async Task ResetTwoFactorCommand_BadUsage_Exits2(params string[] rest)
    {
        var error = new StringWriter();
        var exit = await ResetTwoFactorCommand.TryRunAsync(["reset-2fa", .. rest], error, () => throw new InvalidOperationException("must not be reached"));
        Assert.Equal(2, exit);
        Assert.Contains("usage", error.ToString());
        Assert.Null(await ResetTwoFactorCommand.TryRunAsync(["reset-password", "a@b.c"], TextWriter.Null));
    }
}
