using System.Net;
using Aethera.Api.HostCommands;

namespace Aethera.Api.Tests.Auth;

/// <summary>The host command <c>reset-password</c>: argument handling (no database) and the reset itself (against the test database).</summary>
public sealed class ResetPasswordCommandTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!factory.HasDatabase) return;
        await AuthDb.ResetAsync(factory);
        await factory.SetupOwnerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Run(int? Exit, string Out, string Err);

    private async Task<Run> RunAsync(string stdin, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await ResetPasswordCommand.TryRunAsync(args, new StringReader(stdin), output, error, () => factory.Services);
        return new Run(exit, output.ToString().Trim(), error.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("serve")]
    [InlineData("--urls", "http://+:8080")]
    public async Task OtherArguments_AreNotThisCommand_SoTheWebHostStarts(params string[] args)
    {
        Assert.Null(await ResetPasswordCommand.TryRunAsync(args, TextReader.Null, TextWriter.Null, TextWriter.Null));
    }

    [Theory]
    [InlineData]
    [InlineData("--stdin")]
    [InlineData("a@b.c", "extra")]
    [InlineData("a@b.c", "--unknown")]
    public async Task BadUsage_Exits2_WithoutTouchingTheDatabase(params string[] rest)
    {
        // The services factory throws: usage errors must be reported before any database access.
        var error = new StringWriter();
        var exit = await ResetPasswordCommand.TryRunAsync(["reset-password", .. rest], TextReader.Null, TextWriter.Null, error,
            () => throw new InvalidOperationException("must not be reached"));
        Assert.Equal(2, exit);
        Assert.Contains("usage", error.ToString());
    }

    [Fact]
    public async Task EmptyStdin_Exits2()
    {
        var error = new StringWriter();
        var exit = await ResetPasswordCommand.TryRunAsync(["reset-password", "owner@example.com", "--stdin"], new StringReader(""), TextWriter.Null, error,
            () => throw new InvalidOperationException("must not be reached"));
        Assert.Equal(2, exit);
    }

    [RequiresDatabaseFact]
    public async Task GeneratedPassword_IsPrintedOnce_AndReplacesTheOldOne_ClearsLockout_RevokesSessions_AndAudits()
    {
        var session = ApiClient.Create(factory);
        await session.ReadAsync(await session.LoginAsync("owner@example.com"), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
        await AuthDb.ExecuteAsync(factory, "UPDATE users SET failed_login_count = 5, lockout_end_at = now() + interval '1 hour'");

        var run = await RunAsync("", "reset-password", "OWNER@example.com");

        Assert.Equal(0, run.Exit);
        Assert.True(run.Out.Length >= 12 && !run.Out.Contains(' '), "stdout carries just the generated password");
        Assert.DoesNotContain(run.Out, run.Err);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode); // sessions revoked

        var old = ApiClient.Create(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.LoginAsync("owner@example.com")).StatusCode);
        var fresh = ApiClient.Create(factory);
        await fresh.ReadAsync(await fresh.LoginAsync("owner@example.com", run.Out), HttpStatusCode.OK); // not locked out, new password works

        Assert.Contains("users.password_reset", await AuthDb.AuditActionsAsync(factory));
        Assert.Equal("system", await AuthDb.ScalarAsync<string>(factory, "SELECT actor_type FROM audit_events WHERE action = 'users.password_reset'"));
        var metadata = await AuthDb.AuditMetadataAsync(factory, "users.password_reset");
        Assert.DoesNotContain(run.Out, metadata);
        Assert.Contains("host", metadata);
    }

    [RequiresDatabaseFact]
    public async Task PasswordFromStdin_IsUsed_AndNotEchoed()
    {
        const string chosen = "chosen by the operator 42";
        var run = await RunAsync(chosen + "\n", "reset-password", "owner@example.com", "--stdin");

        Assert.Equal(0, run.Exit);
        Assert.Equal("", run.Out);
        Assert.DoesNotContain(chosen, run.Err);
        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com", chosen), HttpStatusCode.OK);
    }

    [RequiresDatabaseFact]
    public async Task WeakPassword_UnknownUser_AndSoftDeletedUser_ChangeNothing()
    {
        var weak = await RunAsync("short\n", "reset-password", "owner@example.com", "--stdin");
        Assert.Equal(1, weak.Exit);
        Assert.Contains("at least 12", weak.Err);

        var sameAsEmail = await RunAsync("owner@example.com\n", "reset-password", "owner@example.com", "--stdin");
        Assert.Equal(1, sameAsEmail.Exit);

        var unknown = await RunAsync("", "reset-password", "nobody@example.com");
        Assert.Equal(1, unknown.Exit);
        Assert.Contains("no user", unknown.Err);

        var client = ApiClient.Create(factory);
        await client.ReadAsync(await client.LoginAsync("owner@example.com"), HttpStatusCode.OK); // still the original password
        Assert.DoesNotContain("users.password_reset", await AuthDb.AuditActionsAsync(factory));

        await AuthDb.ExecuteAsync(factory, "UPDATE users SET deleted_at = now()");
        Assert.Equal(1, (await RunAsync("", "reset-password", "owner@example.com")).Exit);
    }
}
