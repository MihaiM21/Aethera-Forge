using System.Net;
using Aethera.Domain;

namespace Aethera.Api.Tests.Auth;

public sealed class UserTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    private ApiClient _owner = null!;

    public async Task InitializeAsync()
    {
        if (!factory.HasDatabase) return;
        await AuthDb.ResetAsync(factory);
        _owner = await factory.SetupOwnerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task AssertProblemAsync(HttpResponseMessage response, int status, string code) =>
        response.AssertProblem(await response.ReadJsonAsync(), status, code);

    // ---- role matrix -----------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task UserAdministration_NeedsTheAdminRole()
    {
        var (viewer, _) = await _owner.CreateSignedInUserAsync(factory, "viewer@example.com", OrganizationRole.Viewer);
        var (developer, _) = await _owner.CreateSignedInUserAsync(factory, "dev@example.com", OrganizationRole.Developer);
        var (admin, _) = await _owner.CreateSignedInUserAsync(factory, "admin@example.com", OrganizationRole.Admin);

        await AssertProblemAsync(await ApiClient.Create(factory).GetAsync("/api/v1/users"), 401, "auth.unauthenticated");
        await AssertProblemAsync(await viewer.GetAsync("/api/v1/users"), 403, "auth.forbidden");
        await AssertProblemAsync(await developer.GetAsync("/api/v1/users"), 403, "auth.forbidden");
        await AssertProblemAsync(await developer.PostAsync("/api/v1/users",
            new { email = "x@example.com", displayName = "X", password = ApiClient.Password, role = "viewer" }), 403, "auth.forbidden");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync("/api/v1/users")).StatusCode);

        // Anyone signed in may read their own identity and manage their own tokens.
        foreach (var client in new[] { viewer, developer, admin })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/api-tokens")).StatusCode);
        }
    }

    [RequiresDatabaseFact]
    public async Task UserAdministration_ByToken_NeedsTheAdminScopeToo()
    {
        var (_, readOnly) = await _owner.CreateTokenAsync("read");
        var (_, adminScoped) = await _owner.CreateTokenAsync("admin");

        var denied = await factory.WithBearer(readOnly).GetAsync("/api/v1/users");
        var body = await denied.ReadJsonAsync();
        denied.AssertProblem(body, 403, "auth.insufficient_scope");
        Assert.Equal("admin", body.Str("requiredScope"));
        Assert.Equal(HttpStatusCode.OK, (await factory.WithBearer(adminScoped).GetAsync("/api/v1/users")).StatusCode);

        // A viewer's token can never carry the admin scope, so it can never administer users.
        var (viewer, _) = await _owner.CreateSignedInUserAsync(factory, "viewer@example.com", OrganizationRole.Viewer);
        var attempt = await viewer.PostAsync("/api/v1/api-tokens", new { name = "x", scopes = new[] { "admin" } });
        await AssertProblemAsync(attempt, 422, "validation.failed");
    }

    // ---- create / read ---------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Create_MakesAUserWhoCanSignInWithTheInitialPassword()
    {
        var response = await _owner.PostAsync("/api/v1/users",
            new { email = "New.User@Example.com", displayName = "New User", password = ApiClient.Password, role = "developer" });
        var body = await _owner.ReadAsync(response, HttpStatusCode.Created);

        Assert.Equal($"/api/v1/users/{body.Str("id")}", response.Headers.Location?.ToString());
        Assert.Equal("New.User@Example.com", body.Str("email"));
        Assert.Equal("developer", body.Str("role"));
        Assert.True(body["isActive"]!.GetValue<bool>());
        Assert.DoesNotContain("password", body.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var login = ApiClient.Create(factory);
        var me = await login.ReadAsync(await login.LoginAsync("new.user@example.com"), HttpStatusCode.OK);
        Assert.Equal("developer", me.Str("role"));

        var shown = await _owner.ReadAsync(await _owner.GetAsync($"/api/v1/users/{body.Str("id")}"), HttpStatusCode.OK);
        Assert.Equal(body.Str("id"), shown.Str("id"));
        Assert.Contains("users.created", await AuthDb.AuditActionsAsync(factory));
        Assert.DoesNotContain(ApiClient.Password, await AuthDb.AuditMetadataAsync(factory, "users.created"));
    }

    [RequiresDatabaseFact]
    public async Task Create_RejectsDuplicatesAndWeakPasswords()
    {
        await _owner.CreateUserAsync("taken@example.com", OrganizationRole.Viewer);

        await AssertProblemAsync(await _owner.PostAsync("/api/v1/users",
            new { email = "TAKEN@example.com", displayName = "Dup", password = ApiClient.Password, role = "viewer" }), 409, "user.already_exists");

        var weak = await _owner.PostAsync("/api/v1/users",
            new { email = "weak@example.com", displayName = "Weak", password = "short", role = "viewer" });
        var body = await weak.ReadJsonAsync();
        weak.AssertProblem(body, 422, "validation.failed");
        Assert.Contains(body["errors"]!.AsArray(), e => e!.Str("pointer") == "/password" && e.Str("code") == "too_short");

        var tooLong = await _owner.PostAsync("/api/v1/users",
            new { email = "long@example.com", displayName = "Long", password = new string('x', 257), role = "viewer" });
        Assert.Contains((await tooLong.ReadJsonAsync())["errors"]!.AsArray(), e => e!.Str("code") == "too_long");

        var unknownRole = await _owner.PostAsync("/api/v1/users",
            new { email = "r@example.com", displayName = "R", password = ApiClient.Password, role = "superuser" });
        await AssertProblemAsync(unknownRole, 400, "request.malformed");
    }

    [RequiresDatabaseFact]
    public async Task OnlyAnOwnerCanGrantOwner()
    {
        var (admin, _) = await _owner.CreateSignedInUserAsync(factory, "admin@example.com", OrganizationRole.Admin);
        var viewerId = await _owner.CreateUserAsync("viewer@example.com", OrganizationRole.Viewer);

        await AssertProblemAsync(await admin.PostAsync("/api/v1/users",
            new { email = "o@example.com", displayName = "O", password = ApiClient.Password, role = "owner" }), 403, "auth.forbidden");
        await AssertProblemAsync(await admin.PutAsync($"/api/v1/users/{viewerId}/role", new { role = "owner" }), 403, "auth.forbidden");

        Assert.Equal(HttpStatusCode.Created, (await _owner.PostAsync("/api/v1/users",
            new { email = "o@example.com", displayName = "O", password = ApiClient.Password, role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsync($"/api/v1/users/{viewerId}/role", new { role = "owner" })).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task GetUnknownUser_Is404()
    {
        await AssertProblemAsync(await _owner.GetAsync($"/api/v1/users/{Guid.NewGuid()}"), 404, "user.not_found");
        await AssertProblemAsync(await _owner.DeleteAsync($"/api/v1/users/{Guid.NewGuid()}"), 404, "user.not_found");
    }

    // ---- update ----------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Patch_ChangesProfileFields_AndChecksEmailUniqueness()
    {
        var id = await _owner.CreateUserAsync("a@example.com", OrganizationRole.Viewer, "Alice");
        await _owner.CreateUserAsync("b@example.com", OrganizationRole.Viewer, "Bob");

        var updated = await _owner.ReadAsync(
            await _owner.PatchAsync($"/api/v1/users/{id}", new { displayName = "Alice Cooper", email = "alice@example.com" }), HttpStatusCode.OK);
        Assert.Equal("Alice Cooper", updated.Str("displayName"));
        Assert.Equal("alice@example.com", updated.Str("email"));

        await AssertProblemAsync(await _owner.PatchAsync($"/api/v1/users/{id}", new { email = "B@example.com" }), 409, "user.already_exists");
        await AssertProblemAsync(await _owner.PatchAsync($"/api/v1/users/{id}", new { displayName = "" }), 422, "validation.failed");
        Assert.Contains("users.updated", await AuthDb.AuditActionsAsync(factory));
    }

    [RequiresDatabaseFact]
    public async Task Patch_CanResetAPassword_WhichSignsTheUserOutEverywhere()
    {
        var (user, id) = await _owner.CreateSignedInUserAsync(factory, "dev@example.com", OrganizationRole.Developer);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/auth/me")).StatusCode);

        const string reset = "an administrator chose this one";
        await _owner.ReadAsync(await _owner.PatchAsync($"/api/v1/users/{id}", new { password = reset }), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/auth/me")).StatusCode);
        var again = ApiClient.Create(factory);
        await AssertProblemAsync(await again.LoginAsync("dev@example.com", ApiClient.Password), 401, "auth.invalid_credentials");
        await again.ReadAsync(await again.LoginAsync("dev@example.com", reset), HttpStatusCode.OK);
        Assert.Contains("users.password_reset", await AuthDb.AuditActionsAsync(factory));
        Assert.DoesNotContain(reset, await AuthDb.AuditMetadataAsync(factory, "users.password_reset") ?? "");

        var weak = await _owner.PatchAsync($"/api/v1/users/{id}", new { password = "short" });
        await AssertProblemAsync(weak, 422, "validation.failed");
    }

    [RequiresDatabaseFact]
    public async Task Deactivate_ThenReactivate()
    {
        var id = await _owner.CreateUserAsync("dev@example.com", OrganizationRole.Developer);

        var off = await _owner.ReadAsync(await _owner.PatchAsync($"/api/v1/users/{id}", new { isActive = false }), HttpStatusCode.OK);
        Assert.False(off["isActive"]!.GetValue<bool>());
        var blocked = await ApiClient.Create(factory).LoginAsync("dev@example.com");
        await AssertProblemAsync(blocked, 401, "auth.invalid_credentials");

        await _owner.ReadAsync(await _owner.PatchAsync($"/api/v1/users/{id}", new { isActive = true }), HttpStatusCode.OK);
        var back = ApiClient.Create(factory);
        await back.ReadAsync(await back.LoginAsync("dev@example.com"), HttpStatusCode.OK);
    }

    [RequiresDatabaseFact]
    public async Task AnAdmin_CannotTouchAnOwner()
    {
        var (admin, _) = await _owner.CreateSignedInUserAsync(factory, "admin@example.com", OrganizationRole.Admin);
        var ownerId = await AuthDb.ScalarAsync<Guid>(factory, "SELECT id FROM users WHERE normalized_email = 'owner@example.com'");

        await AssertProblemAsync(await admin.PatchAsync($"/api/v1/users/{ownerId}", new { displayName = "Hacked" }), 403, "auth.forbidden");
        await AssertProblemAsync(await admin.PatchAsync($"/api/v1/users/{ownerId}", new { password = "an admin picked this one" }), 403, "auth.forbidden");
        await AssertProblemAsync(await admin.PutAsync($"/api/v1/users/{ownerId}/role", new { role = "viewer" }), 403, "auth.forbidden");
        await AssertProblemAsync(await admin.DeleteAsync($"/api/v1/users/{ownerId}"), 403, "auth.forbidden");
    }

    // ---- roles / last owner ---------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task SetRole_ChangesTheRole_AndAuditsIt()
    {
        var id = await _owner.CreateUserAsync("dev@example.com", OrganizationRole.Viewer);
        var response = await _owner.ReadAsync(await _owner.PutAsync($"/api/v1/users/{id}/role", new { role = "admin" }), HttpStatusCode.OK);
        Assert.Equal("admin", response.Str("role"));

        Assert.Contains("users.role_changed", await AuthDb.AuditActionsAsync(factory));
        var metadata = await AuthDb.AuditMetadataAsync(factory, "users.role_changed");
        Assert.Contains("viewer", metadata);
        Assert.Contains("admin", metadata);

        var invalid = await _owner.PutAsync($"/api/v1/users/{id}/role", new { role = "emperor" });
        await AssertProblemAsync(invalid, 400, "request.malformed");
    }

    [RequiresDatabaseFact]
    public async Task TheLastOwner_CannotBeDemotedDeactivatedOrDeleted()
    {
        var ownerId = await AuthDb.ScalarAsync<Guid>(factory, "SELECT id FROM users");

        await AssertProblemAsync(await _owner.PutAsync($"/api/v1/users/{ownerId}/role", new { role = "admin" }), 409, "users.last_owner");
        await AssertProblemAsync(await _owner.PatchAsync($"/api/v1/users/{ownerId}", new { isActive = false }), 409, "users.last_owner");
        await AssertProblemAsync(await _owner.DeleteAsync($"/api/v1/users/{ownerId}"), 409, "users.last_owner");

        // Setting the same role again is harmless, and nothing changed.
        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsync($"/api/v1/users/{ownerId}/role", new { role = "owner" })).StatusCode);
        Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM organization_members WHERE role = 'owner'"));
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task WithASecondOwner_TheFirstCanLeave_ButThenTheSecondIsTheLast()
    {
        var firstId = await AuthDb.ScalarAsync<Guid>(factory, "SELECT id FROM users");
        var (second, secondId) = await _owner.CreateSignedInUserAsync(factory, "second@example.com", OrganizationRole.Owner);

        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsync($"/api/v1/users/{firstId}/role", new { role = "admin" })).StatusCode);

        // The second owner is now the only one.
        await AssertProblemAsync(await second.PutAsync($"/api/v1/users/{secondId}/role", new { role = "admin" }), 409, "users.last_owner");
        await AssertProblemAsync(await second.DeleteAsync($"/api/v1/users/{secondId}"), 409, "users.last_owner");
    }

    [RequiresDatabaseFact]
    public async Task ADeactivatedOwner_DoesNotCountAsAnOwner()
    {
        var firstId = await AuthDb.ScalarAsync<Guid>(factory, "SELECT id FROM users");
        var secondId = await _owner.CreateUserAsync("second@example.com", OrganizationRole.Owner);
        await _owner.ReadAsync(await _owner.PatchAsync($"/api/v1/users/{secondId}", new { isActive = false }), HttpStatusCode.OK);

        await AssertProblemAsync(await _owner.PutAsync($"/api/v1/users/{firstId}/role", new { role = "admin" }), 409, "users.last_owner");
    }

    [RequiresDatabaseFact]
    public async Task OwnersDemotingEachOtherAtTheSameTime_CannotLeaveTheOrganizationWithoutAnOwner()
    {
        var firstId = await AuthDb.ScalarAsync<Guid>(factory, "SELECT id FROM users WHERE normalized_email = 'owner@example.com'");
        var (second, secondId) = await _owner.CreateSignedInUserAsync(factory, "second@example.com", OrganizationRole.Owner);

        for (var round = 0; round < 3; round++)
        {
            await AuthDb.ExecuteAsync(factory, "UPDATE organization_members SET role = 'owner'");
            var results = await Task.WhenAll(
                _owner.PutAsync($"/api/v1/users/{secondId}/role", new { role = "viewer" }),
                second.PutAsync($"/api/v1/users/{firstId}/role", new { role = "viewer" }));

            var codes = results.Select(r => (int)r.StatusCode).Order().ToArray();
            Assert.Equal(200, codes[0]);
            Assert.Contains(codes[1], new[] { 403, 409 }); // 409 last owner, or 403 when the loser had already lost its own ownership
            Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM organization_members WHERE role = 'owner'"));
        }
    }

    // ---- delete ----------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Delete_RemovesTheAccount_EndsItsSessionsAndTokens_AndFreesTheEmail()
    {
        var (user, id) = await _owner.CreateSignedInUserAsync(factory, "gone@example.com", OrganizationRole.Developer);
        var (_, token) = await user.CreateTokenAsync("read");

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/users/{id}")).StatusCode);

        await AssertProblemAsync(await _owner.GetAsync($"/api/v1/users/{id}"), 404, "user.not_found");
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.WithBearer(token).GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(1, await AuthDb.ScalarAsync<long>(factory, "SELECT count(*) FROM users WHERE deleted_at IS NOT NULL")); // soft delete
        Assert.Contains("users.deleted", await AuthDb.AuditActionsAsync(factory));

        // The address can be used again by a new account.
        Assert.Equal(HttpStatusCode.Created, (await _owner.PostAsync("/api/v1/users",
            new { email = "gone@example.com", displayName = "Again", password = ApiClient.Password, role = "viewer" })).StatusCode);
    }

    // ---- listing ---------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task List_PagesSortsAndFilters()
    {
        await _owner.CreateUserAsync("carol@example.com", OrganizationRole.Viewer, "Carol");
        await _owner.CreateUserAsync("alice@example.com", OrganizationRole.Developer, "Alice");
        var bobId = await _owner.CreateUserAsync("bob@example.com", OrganizationRole.Viewer, "Robert Smith");
        await _owner.CreateUserAsync("dave@example.com", OrganizationRole.Admin, "Dave");
        await _owner.PatchAsync($"/api/v1/users/{bobId}", new { isActive = false });

        async Task<List<string>> Walk(string query, int limit)
        {
            var emails = new List<string>();
            string? cursor = null;
            do
            {
                var url = $"/api/v1/users?limit={limit}{query}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                var page = await _owner.ReadAsync(await _owner.GetAsync(url), HttpStatusCode.OK);
                emails.AddRange(page["items"]!.AsArray().Select(i => i.Str("email")));
                cursor = page["nextCursor"]?.GetValue<string>();
            } while (cursor is not null);

            return emails;
        }

        var all = new[] { "alice@example.com", "bob@example.com", "carol@example.com", "dave@example.com", "owner@example.com" };
        Assert.Equal(all, await Walk("", 2));
        Assert.Equal(all.Reverse(), await Walk("&sort=-email", 2));
        Assert.Equal(all.Length, (await Walk("&sort=createdAt", 3)).Distinct().Count());
        Assert.Equal(all.Length, (await Walk("&sort=-createdAt", 1)).Distinct().Count());
        Assert.Equal(["bob@example.com", "carol@example.com"], await Walk("&role=viewer", 1));
        Assert.Equal(["alice@example.com", "dave@example.com", "owner@example.com"], await Walk("&role=developer,admin,owner", 2));
        Assert.Equal(["bob@example.com"], await Walk("&isActive=false", 5));
        Assert.Equal(["bob@example.com"], await Walk("&q=ROBERT", 5)); // display name, case-insensitive
        Assert.Equal(["alice@example.com"], await Walk("&q=alic", 5));
    }

    [RequiresDatabaseFact]
    public async Task List_RejectsBadQueries()
    {
        await AssertProblemAsync(await _owner.GetAsync("/api/v1/users?bogus=1"), 400, "validation.invalid_parameter");
        await AssertProblemAsync(await _owner.GetAsync("/api/v1/users?sort=password"), 400, "validation.invalid_parameter");
        await AssertProblemAsync(await _owner.GetAsync("/api/v1/users?sort=email,createdAt"), 400, "validation.invalid_parameter");
        await AssertProblemAsync(await _owner.GetAsync("/api/v1/users?role=emperor"), 400, "validation.invalid_parameter");
        await AssertProblemAsync(await _owner.GetAsync("/api/v1/users?limit=500"), 400, "validation.invalid_parameter");
        await AssertProblemAsync(await _owner.GetAsync("/api/v1/users?cursor=nonsense"), 400, "pagination.invalid_cursor");

        // A cursor is only valid for the list that produced it.
        await _owner.CreateUserAsync("a@example.com", OrganizationRole.Viewer);
        var page = await _owner.ReadAsync(await _owner.GetAsync("/api/v1/users?limit=1"), HttpStatusCode.OK);
        var cursor = Uri.EscapeDataString(page["nextCursor"]!.GetValue<string>());
        await AssertProblemAsync(await _owner.GetAsync($"/api/v1/users?limit=1&role=viewer&cursor={cursor}"), 400, "pagination.invalid_cursor");
    }
}
