using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aethera.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aethera.Api.Tests.Auth;

public sealed class ApiTokenTests : IClassFixture<AuthApiFactory>, IAsyncLifetime
{
    private readonly AuthApiFactory _factory;
    private readonly WebApplicationFactory<Program> _app;
    private ApiClient _owner = null!;

    public ApiTokenTests(AuthApiFactory factory)
    {
        _factory = factory;
        _app = factory.WithEndpoints(TestApi.Map, TestApi.Services);
    }

    public async Task InitializeAsync()
    {
        if (!_factory.HasDatabase) return;
        await AuthDb.ResetAsync(_factory);
        _owner = await _factory.SetupOwnerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ApiClient Bearer(string token)
    {
        var client = ApiClient.Create(_app);
        client.Bearer = token;
        return client;
    }

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    // ---- create / use --------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Create_ReturnsThePlaintextOnce_AndStoresOnlyTheHash()
    {
        var response = await _owner.PostAsync("/api/v1/api-tokens", new { name = "ci", scopes = new[] { "read", "deploy" } });
        var body = await _owner.ReadAsync(response, HttpStatusCode.Created);
        var token = body.Str("token");
        var id = body.Str("id");

        Assert.Matches(new Regex("^aeth_[0-9A-Za-z]{40}$"), token);
        Assert.Equal(token[5..13], body.Str("prefix"));
        Assert.Equal($"/api/v1/api-tokens/{id}", response.Headers.Location?.ToString());
        Assert.Equal(["read", "deploy"], Strings(body["scopes"]));
        Assert.InRange((DateTimeOffset.Parse(body.Str("expiresAt")) - DateTimeOffset.UtcNow).TotalDays, 89.9, 90.1); // default

        var hash = await AuthDb.ScalarAsync<byte[]>(_factory, "SELECT secret_hash FROM api_tokens");
        Assert.Equal(AuthTestExtensions.Sha256(token[5..]), hash);
        Assert.Equal(token[5..13], await AuthDb.ScalarAsync<string>(_factory, "SELECT prefix FROM api_tokens"));

        // The plaintext is gone for good: not in GET, not in the list, not in the audit trail.
        var single = await (await _owner.GetAsync($"/api/v1/api-tokens/{id}")).Content.ReadAsStringAsync();
        var list = await (await _owner.GetAsync("/api/v1/api-tokens")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(token[5..], single + list);
        Assert.DoesNotContain("\"token\"", single + list);
        Assert.Contains("api_tokens.created", await AuthDb.AuditActionsAsync(_factory));
        Assert.DoesNotContain(token[5..], await AuthDb.AuditMetadataAsync(_factory, "api_tokens.created"));
    }

    [RequiresDatabaseFact]
    public async Task Token_AuthenticatesAsItsOwner_AndRecordsLastUse()
    {
        var (id, token) = await _owner.CreateTokenAsync("read");
        var bearer = Bearer(token);

        Assert.Null((await _owner.ReadAsync(await _owner.GetAsync($"/api/v1/api-tokens/{id}"), HttpStatusCode.OK))["lastUsedAt"]);

        var me = await bearer.ReadAsync(await bearer.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
        Assert.Equal("owner@example.com", me["user"].Str("email"));
        Assert.Equal("owner", me.Str("role"));

        var firstUse = await AuthDb.ScalarAsync<DateTime>(_factory, "SELECT last_used_at FROM api_tokens");
        var shown = await _owner.ReadAsync(await _owner.GetAsync($"/api/v1/api-tokens/{id}"), HttpStatusCode.OK);
        Assert.NotNull(shown["lastUsedAt"]);

        // Within a minute nothing is written; after it, the timestamp moves.
        _factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await bearer.GetAsync("/api/v1/auth/me");
        Assert.Equal(firstUse, await AuthDb.ScalarAsync<DateTime>(_factory, "SELECT last_used_at FROM api_tokens"));
        _factory.Clock.Advance(TimeSpan.FromSeconds(40));
        await bearer.GetAsync("/api/v1/auth/me");
        Assert.True(await AuthDb.ScalarAsync<DateTime>(_factory, "SELECT last_used_at FROM api_tokens") - firstUse >= TimeSpan.FromSeconds(60));
    }

    [RequiresDatabaseFact]
    public async Task TokenPrincipal_CarriesTheOwnersRole_TheTokenIdAndItsScopes()
    {
        var (id, token) = await _owner.CreateTokenAsync("read", "deploy");
        var bearer = Bearer(token);

        var who = await bearer.ReadAsync(await bearer.GetAsync("/api/v1/_test/whoami"), HttpStatusCode.OK);
        Assert.Equal(id.ToString(), who.Str("apiTokenId"));
        Assert.Equal("Owner", who.Str("role"));
        Assert.Equal(["deploy", "read"], Strings(who["scopes"]));
        Assert.Equal(await AuthDb.ScalarAsync<Guid>(_factory, "SELECT id FROM users"), Guid.Parse(who.Str("userId")));
    }

    [RequiresDatabaseFact]
    public async Task TokenScopes_AreEnforced()
    {
        var (_, token) = await _owner.CreateTokenAsync("read");
        var bearer = Bearer(token);

        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/v1/_test/scope/read")).StatusCode);
        var denied = await bearer.PostAsync("/api/v1/_test/scope/write", csrf: false);
        var body = await denied.ReadJsonAsync();
        denied.AssertProblem(body, 403, "auth.insufficient_scope");
        Assert.Equal("write", body.Str("requiredScope"));
    }

    [RequiresDatabaseFact]
    public async Task Me_Describes_BothKindsOfPrincipal()
    {
        var (_, token) = await _owner.CreateTokenAsync("read");
        var viaToken = await Bearer(token).ReadAsync(await Bearer(token).GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);
        var viaSession = await _owner.ReadAsync(await _owner.GetAsync("/api/v1/auth/me"), HttpStatusCode.OK);

        Assert.Equal(viaSession.ToJsonString(), viaToken.ToJsonString());
        Assert.Equal("owner", viaSession.Str("role"));
        Assert.Equal("acme-inc", viaSession["organization"].Str("slug"));
        Assert.Equal("owner@example.com", viaSession["user"].Str("email"));

        var unauthenticated = await ApiClient.Create(_app).GetAsync("/api/v1/auth/me");
        unauthenticated.AssertProblem(await unauthenticated.ReadJsonAsync(), 401, "auth.unauthenticated");
    }

    [RequiresDatabaseFact]
    public async Task AccessTokenQueryParameter_IsNotAcceptedOutsideHubs()
    {
        var (_, token) = await _owner.CreateTokenAsync("read");
        var anonymous = ApiClient.Create(_app);
        var response = await anonymous.GetAsync($"/api/v1/auth/me?access_token={token}");
        response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.unauthenticated");
    }

    // ---- revoke / expiry / rejection ------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Revoking_TakesEffectImmediately_AndIsIdempotent()
    {
        var (id, token) = await _owner.CreateTokenAsync("read");
        var bearer = Bearer(token);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/v1/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/api-tokens/{id}")).StatusCode);
        var revoked = await bearer.GetAsync("/api/v1/auth/me");
        revoked.AssertProblem(await revoked.ReadJsonAsync(), 401, "auth.token_revoked");

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/api-tokens/{id}")).StatusCode);
        Assert.Equal(1, (await AuthDb.AuditActionsAsync(_factory)).Count(a => a == "api_tokens.revoked"));

        // The row stays, marked revoked.
        var shown = await _owner.ReadAsync(await _owner.GetAsync($"/api/v1/api-tokens/{id}"), HttpStatusCode.OK);
        Assert.NotNull(shown["revokedAt"]);
    }

    [RequiresDatabaseFact]
    public async Task ExpiredToken_Is401TokenExpired()
    {
        var expiresAt = _factory.Clock.UtcNow.AddDays(1).ToString("O");
        var response = await _owner.PostAsync("/api/v1/api-tokens", new { name = "short", scopes = new[] { "read" }, expiresAt });
        var token = (await _owner.ReadAsync(response, HttpStatusCode.Created)).Str("token");
        var bearer = Bearer(token);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/v1/auth/me")).StatusCode);

        _factory.Clock.Advance(TimeSpan.FromDays(2));
        var expired = await bearer.GetAsync("/api/v1/auth/me");
        expired.AssertProblem(await expired.ReadJsonAsync(), 401, "auth.token_expired");
    }

    [RequiresDatabaseFact]
    public async Task UnrecognizedTokens_AreAllJustUnauthenticated()
    {
        var (id, token) = await _owner.CreateTokenAsync("read");
        await _owner.DeleteAsync($"/api/v1/api-tokens/{id}");

        var wrongSecretSamePrefix = token[..13] + new string('x', 32);
        var unknownPrefix = "aeth_" + new string('Z', 40);
        foreach (var presented in new[] { wrongSecretSamePrefix, unknownPrefix, "aeth_short", "Bearer", "not-a-token", token + "x" })
        {
            var response = await Bearer(presented).GetAsync("/api/v1/auth/me");
            // A revoked token presented with the wrong secret must not reveal that it exists (no auth.token_revoked).
            response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.unauthenticated");
        }
    }

    [RequiresDatabaseFact]
    public async Task Token_StopsWorking_WhenItsOwnerIsDeactivated()
    {
        var (dev, devId) = await _owner.CreateSignedInUserAsync(_factory, "dev@example.com", OrganizationRole.Developer);
        var (_, token) = await dev.CreateTokenAsync("read");
        Assert.Equal(HttpStatusCode.OK, (await Bearer(token).GetAsync("/api/v1/auth/me")).StatusCode);

        await _owner.PatchAsync($"/api/v1/users/{devId}", new { isActive = false });

        var response = await Bearer(token).GetAsync("/api/v1/auth/me");
        response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.unauthenticated");
    }

    // ---- expiry rules ----------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Expiry_IsBoundedToOneYear_AndNeverIsForAdminsOnly()
    {
        var now = _factory.Clock.UtcNow;
        var (developer, _) = await _owner.CreateSignedInUserAsync(_factory, "dev@example.com", OrganizationRole.Developer);
        var (admin, _) = await _owner.CreateSignedInUserAsync(_factory, "admin@example.com", OrganizationRole.Admin);

        async Task<HttpResponseMessage> Create(ApiClient client, object? expiresAt, bool specified = true) =>
            await client.PostAsync("/api/v1/api-tokens", specified
                ? new Dictionary<string, object?> { ["name"] = "t", ["scopes"] = new[] { "read" }, ["expiresAt"] = expiresAt }
                : new Dictionary<string, object?> { ["name"] = "t", ["scopes"] = new[] { "read" } });

        async Task AssertField(HttpResponseMessage response, string pointer, string code)
        {
            var body = await response.ReadJsonAsync();
            response.AssertProblem(body, 422, "validation.failed");
            Assert.Contains(body["errors"]!.AsArray(), e => e!.Str("pointer") == pointer && e.Str("code") == code);
        }

        // Within a year: fine. Beyond it, or in the past: refused.
        Assert.Equal(HttpStatusCode.Created, (await Create(developer, now.AddDays(364).ToString("O"))).StatusCode);
        await AssertField(await Create(developer, now.AddDays(366).ToString("O")), "/expiresAt", "too_far");
        await AssertField(await Create(admin, now.AddDays(400).ToString("O")), "/expiresAt", "too_far");
        await AssertField(await Create(developer, now.AddMinutes(-1).ToString("O")), "/expiresAt", "in_past");

        // "Never" (explicit null) is an admin privilege; leaving it out means the default.
        await AssertField(await Create(developer, null), "/expiresAt", "never_not_allowed");
        var never = await admin.ReadAsync(await Create(admin, null), HttpStatusCode.Created);
        Assert.Null(never["expiresAt"]);
        var ownerNever = await _owner.ReadAsync(await Create(_owner, null), HttpStatusCode.Created);
        Assert.Null(ownerNever["expiresAt"]);
        var defaulted = await developer.ReadAsync(await Create(developer, null, specified: false), HttpStatusCode.Created);
        Assert.InRange((DateTimeOffset.Parse(defaulted.Str("expiresAt")) - now).TotalDays, 89.9, 90.1);
    }

    [RequiresDatabaseFact]
    public async Task Create_ValidatesNameAndScopes()
    {
        async Task AssertField(object body, string pointer, string code)
        {
            var response = await _owner.PostAsync("/api/v1/api-tokens", body);
            var json = await response.ReadJsonAsync();
            response.AssertProblem(json, 422, "validation.failed");
            Assert.Contains(json["errors"]!.AsArray(), e => e!.Str("pointer") == pointer && e.Str("code") == code);
        }

        await AssertField(new { name = "", scopes = new[] { "read" } }, "/name", "required");
        await AssertField(new { name = "x", scopes = Array.Empty<string>() }, "/scopes", "required");
        await AssertField(new { name = "x", scopes = new[] { "read", "launch:rockets" } }, "/scopes/1", "invalid_scope");
    }

    // ---- scope ceiling by role -------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task ScopeCeiling_FollowsTheOwnersRole()
    {
        var all = new[] { "read", "write", "deploy", "secrets:read", "secrets:write", "servers:write", "admin", "*" };
        var allowed = new Dictionary<OrganizationRole, string[]>
        {
            [OrganizationRole.Viewer] = ["read"],
            [OrganizationRole.Developer] = ["read", "write", "deploy"],
            [OrganizationRole.Admin] = all,
            [OrganizationRole.Owner] = all,
        };

        foreach (var (role, permitted) in allowed)
        {
            var client = role == OrganizationRole.Owner
                ? _owner
                : (await _owner.CreateSignedInUserAsync(_factory, $"{role}@example.com".ToLowerInvariant(), role)).Client;

            foreach (var scope in all)
            {
                var response = await client.PostAsync("/api/v1/api-tokens", new { name = "t", scopes = new[] { scope } });
                if (permitted.Contains(scope))
                {
                    Assert.True(response.StatusCode == HttpStatusCode.Created, $"{role} should get '{scope}' but got {(int)response.StatusCode}");
                }
                else
                {
                    var body = await response.ReadJsonAsync();
                    response.AssertProblem(body, 422, "validation.failed");
                    Assert.Contains(body["errors"]!.AsArray(), e => e!.Str("pointer") == "/scopes/0" && e.Str("code") == "scope_not_allowed");
                }
            }
        }
    }

    [RequiresDatabaseFact]
    public async Task ATokenNeverActsWithMoreThanItsOwnersCurrentRole()
    {
        var (admin, adminId) = await _owner.CreateSignedInUserAsync(_factory, "admin@example.com", OrganizationRole.Admin);
        var (_, wildcard) = await admin.CreateTokenAsync("*");
        var (_, writer) = await admin.CreateTokenAsync("write");

        await _owner.PutAsync($"/api/v1/users/{adminId}/role", new { role = "developer" });
        var asDeveloper = await Bearer(wildcard).ReadAsync(await Bearer(wildcard).GetAsync("/api/v1/_test/whoami"), HttpStatusCode.OK);
        Assert.Equal(["deploy", "read", "write"], Strings(asDeveloper["scopes"]));

        await _owner.PutAsync($"/api/v1/users/{adminId}/role", new { role = "viewer" });
        var asViewer = await Bearer(wildcard).ReadAsync(await Bearer(wildcard).GetAsync("/api/v1/_test/whoami"), HttpStatusCode.OK);
        Assert.Equal(["read"], Strings(asViewer["scopes"]));
        var noScopes = await Bearer(writer).ReadAsync(await Bearer(writer).GetAsync("/api/v1/_test/whoami"), HttpStatusCode.OK);
        Assert.Empty(Strings(noScopes["scopes"]));
    }

    [RequiresDatabaseFact]
    public async Task ManagingTokensWithAToken_NeedsTheAdminScope()
    {
        var (_, readOnly) = await _owner.CreateTokenAsync("read");
        var denied = await Bearer(readOnly).PostAsync("/api/v1/api-tokens", new { name = "x", scopes = new[] { "read" } }, csrf: false);
        var body = await denied.ReadJsonAsync();
        denied.AssertProblem(body, 403, "auth.insufficient_scope");
        Assert.Equal("admin", body.Str("requiredScope"));
        Assert.Equal(HttpStatusCode.OK, (await Bearer(readOnly).GetAsync("/api/v1/api-tokens")).StatusCode); // listing needs only read

        var (_, adminToken) = await _owner.CreateTokenAsync("admin");
        Assert.Equal(HttpStatusCode.Created,
            (await Bearer(adminToken).PostAsync("/api/v1/api-tokens", new { name = "x", scopes = new[] { "read" } }, csrf: false)).StatusCode);
    }

    // ---- who may see and revoke whose tokens -----------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task UsersManageTheirOwnTokens_AdminsManageAll()
    {
        var (dev1, dev1Id) = await _owner.CreateSignedInUserAsync(_factory, "dev1@example.com", OrganizationRole.Developer);
        var (dev2, _) = await _owner.CreateSignedInUserAsync(_factory, "dev2@example.com", OrganizationRole.Developer);
        var (tokenId, token) = await dev1.CreateTokenAsync("read");
        await dev2.CreateTokenAsync("read");

        // Each developer sees only their own.
        var mine = await dev1.ReadAsync(await dev1.GetAsync("/api/v1/api-tokens"), HttpStatusCode.OK);
        Assert.Single(mine["items"]!.AsArray());
        Assert.Single((await dev2.ReadAsync(await dev2.GetAsync("/api/v1/api-tokens"), HttpStatusCode.OK))["items"]!.AsArray());

        // Someone else's token is "not found", for reading and for revoking.
        var get = await dev2.GetAsync($"/api/v1/api-tokens/{tokenId}");
        get.AssertProblem(await get.ReadJsonAsync(), 404, "api_token.not_found");
        var delete = await dev2.DeleteAsync($"/api/v1/api-tokens/{tokenId}");
        delete.AssertProblem(await delete.ReadJsonAsync(), 404, "api_token.not_found");
        Assert.Equal(HttpStatusCode.OK, (await Bearer(token).GetAsync("/api/v1/auth/me")).StatusCode);

        // Asking for another user's list is forbidden, not silently filtered.
        var other = await dev2.GetAsync($"/api/v1/api-tokens?userId={dev1Id}");
        other.AssertProblem(await other.ReadJsonAsync(), 403, "auth.forbidden");

        // An admin sees everyone's, can narrow by user, and can revoke.
        Assert.Equal(2, (await _owner.ReadAsync(await _owner.GetAsync("/api/v1/api-tokens"), HttpStatusCode.OK))["items"]!.AsArray().Count);
        var narrowed = await _owner.ReadAsync(await _owner.GetAsync($"/api/v1/api-tokens?userId={dev1Id}"), HttpStatusCode.OK);
        Assert.Equal(tokenId.ToString(), narrowed["items"]![0].Str("id"));
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync($"/api/v1/api-tokens/{tokenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/api-tokens/{tokenId}")).StatusCode);
        var afterRevoke = await Bearer(token).GetAsync("/api/v1/auth/me");
        afterRevoke.AssertProblem(await afterRevoke.ReadJsonAsync(), 401, "auth.token_revoked");
    }

    [RequiresDatabaseFact]
    public async Task List_PagesNewestFirst_AndFiltersByStatus()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add((await _owner.CreateTokenAsync("read")).Id);
        await _owner.DeleteAsync($"/api/v1/api-tokens/{ids[0]}");

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/api/v1/api-tokens?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await _owner.ReadAsync(await _owner.GetAsync(url), HttpStatusCode.OK);
            seen.AddRange(page["items"]!.AsArray().Select(i => i.Str("id")));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        } while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(ids.AsEnumerable().Reverse().Select(i => i.ToString()), seen); // newest first, no duplicates, none missing

        var revoked = await _owner.ReadAsync(await _owner.GetAsync("/api/v1/api-tokens?status=revoked"), HttpStatusCode.OK);
        Assert.Equal([ids[0].ToString()], revoked["items"]!.AsArray().Select(i => i.Str("id")));
        Assert.Equal(4, (await _owner.ReadAsync(await _owner.GetAsync("/api/v1/api-tokens?status=active"), HttpStatusCode.OK))["items"]!.AsArray().Count);
    }

    [RequiresDatabaseFact]
    public async Task List_RejectsUnknownParametersAndBadValues()
    {
        var unknown = await _owner.GetAsync("/api/v1/api-tokens?colour=red");
        unknown.AssertProblem(await unknown.ReadJsonAsync(), 400, "validation.invalid_parameter");
        var limit = await _owner.GetAsync("/api/v1/api-tokens?limit=0");
        limit.AssertProblem(await limit.ReadJsonAsync(), 400, "validation.invalid_parameter");
        var status = await _owner.GetAsync("/api/v1/api-tokens?status=sleeping");
        status.AssertProblem(await status.ReadJsonAsync(), 400, "validation.invalid_parameter");
        var cursor = await _owner.GetAsync("/api/v1/api-tokens?cursor=nonsense");
        cursor.AssertProblem(await cursor.ReadJsonAsync(), 400, "pagination.invalid_cursor");
    }
}
