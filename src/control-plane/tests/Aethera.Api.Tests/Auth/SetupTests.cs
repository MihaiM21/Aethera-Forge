using System.Net;

namespace Aethera.Api.Tests.Auth;

public sealed class SetupTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (factory.HasDatabase) await AuthDb.ResetAsync(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object ValidSetup(string email = "owner@example.com") =>
        new { email, password = ApiClient.Password, displayName = "Mihai", organizationName = "Acme Inc" };

    [RequiresDatabaseFact]
    public async Task SetupStatus_IsRequired_UntilTheFirstUserExists()
    {
        var client = ApiClient.Create(factory);
        var before = await client.ReadAsync(await client.GetAsync("/api/v1/auth/setup"), HttpStatusCode.OK);
        Assert.True(before["setupRequired"]!.GetValue<bool>());

        await factory.SetupOwnerAsync();

        var after = await client.ReadAsync(await client.GetAsync("/api/v1/auth/setup"), HttpStatusCode.OK);
        Assert.False(after["setupRequired"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Setup_CreatesOrganizationOwnerAndSignsIn()
    {
        var client = ApiClient.Create(factory);
        var response = await client.PostAsync("/api/v1/auth/setup", ValidSetup(), csrf: false);
        var me = await client.ReadAsync(response, HttpStatusCode.Created);

        Assert.Equal("/api/v1/auth/me", response.Headers.Location?.ToString());
        Assert.Equal("owner@example.com", me["user"].Str("email"));
        Assert.Equal("Mihai", me["user"].Str("displayName"));
        Assert.Equal("Acme Inc", me["organization"].Str("name"));
        Assert.Equal("acme-inc", me["organization"].Str("slug"));
        Assert.Equal("owner", me.Str("role"));
        Assert.NotNull(client.SessionCookie);

        // The cookie signs the owner in.
        await client.LoadCsrfAsync();
        var current = await client.ReadAsync(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
        Assert.Equal(me["user"].Str("id"), current["user"].Str("id"));

        Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM organization_members WHERE role = 'owner'"));
        var hash = await AuthDb.ScalarAsync<string>(factory, "SELECT password_hash FROM users");
        Assert.DoesNotContain(ApiClient.Password, hash);
        Assert.Contains("auth.setup", await AuthDb.AuditActionsAsync(factory));
        var audit = await AuthDb.AuditMetadataAsync(factory, "auth.setup");
        Assert.DoesNotContain(ApiClient.Password, audit);
    }

    [RequiresDatabaseFact]
    public async Task Setup_AfterCompletion_Is409()
    {
        await factory.SetupOwnerAsync();

        var client = ApiClient.Create(factory);
        var response = await client.PostAsync("/api/v1/auth/setup", ValidSetup("second@example.com"), csrf: false);
        response.AssertProblem(await response.ReadJsonAsync(), 409, "auth.setup_completed");
        Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM users"));
    }

    [RequiresDatabaseFact]
    public async Task Setup_StaysClosed_EvenWhenEveryUserWasDeleted()
    {
        await factory.SetupOwnerAsync();
        await AuthDb.ExecuteAsync(factory, "UPDATE users SET deleted_at = now()");

        var client = ApiClient.Create(factory);
        Assert.False((await client.ReadAsync(await client.GetAsync("/api/v1/auth/setup"), HttpStatusCode.OK))["setupRequired"]!.GetValue<bool>());
        var response = await client.PostAsync("/api/v1/auth/setup", ValidSetup("again@example.com"), csrf: false);
        response.AssertProblem(await response.ReadJsonAsync(), 409, "auth.setup_completed");
    }

    [RequiresDatabaseFact]
    public async Task ParallelSetup_CreatesExactlyOneOwner()
    {
        var attempts = Enumerable.Range(0, 5).Select(async i =>
        {
            var client = ApiClient.Create(factory);
            var response = await client.PostAsync("/api/v1/auth/setup", ValidSetup($"owner{i}@example.com"), csrf: false);
            return response.StatusCode;
        });
        var statuses = await Task.WhenAll(attempts);

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM users"));
        Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM organizations"));
        Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM organization_members"));
    }

    [RequiresDatabaseTheory]
    [InlineData("not-an-email", "correct horse battery staple", "Acme", "/email")]
    [InlineData("a@example.com", "short", "Acme", "/password")]
    [InlineData("a@example.com", "correct horse battery staple", "!!!", "/organizationName")]
    [InlineData("a@example.com", "correct horse battery staple", "", "/organizationName")]
    public async Task Setup_RejectsInvalidInput_With422(string email, string password, string organization, string pointer)
    {
        var client = ApiClient.Create(factory);
        var response = await client.PostAsync("/api/v1/auth/setup",
            new { email, password, displayName = "Name", organizationName = organization }, csrf: false);
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 422, "validation.failed");
        Assert.Contains(body["errors"]!.AsArray(), e => e!.Str("pointer") == pointer);
        Assert.True(await client.ReadAsync(await client.GetAsync("/api/v1/auth/setup"), HttpStatusCode.OK) is { } status
                    && status["setupRequired"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Setup_RejectsAPasswordEqualToTheEmail()
    {
        var client = ApiClient.Create(factory);
        var email = "owner-with-a-long-address@example.com";
        var response = await client.PostAsync("/api/v1/auth/setup",
            new { email, password = email, displayName = "Name", organizationName = "Acme" }, csrf: false);
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 422, "validation.failed");
        Assert.Contains(body["errors"]!.AsArray(), e => e!.Str("pointer") == "/password" && e.Str("code") == "password.same_as_email");
    }

    [RequiresDatabaseFact]
    public async Task Setup_ThenLogin_WorksWithTheChosenPassword()
    {
        await factory.SetupOwnerAsync("Owner@Example.com");

        var client = ApiClient.Create(factory);
        var login = await client.LoginAsync("owner@example.com"); // case-insensitive email
        await client.ReadAsync(login, HttpStatusCode.OK);
    }
}
