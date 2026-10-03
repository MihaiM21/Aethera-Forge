using System.Net;
using Aethera.Api.Security;
using Aethera.Domain;

namespace Aethera.Api.Tests;

[Collection(TestApiCollection.Name)]
public sealed class AuthorizationTests(TestApiFixture fixture)
{
    private static readonly OrganizationRole[] Roles = [OrganizationRole.Viewer, OrganizationRole.Developer, OrganizationRole.Admin, OrganizationRole.Owner];

    /// <summary>Every (actual role, policy endpoint) pair; allowed exactly when actual &gt;= minimum.</summary>
    public static TheoryData<OrganizationRole, OrganizationRole> RoleMatrix()
    {
        var data = new TheoryData<OrganizationRole, OrganizationRole>();
        foreach (var actual in Roles)
            foreach (var minimum in Roles)
                data.Add(actual, minimum);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoleMatrix))]
    public async Task RolePolicies_FollowViewerDeveloperAdminOwnerOrder(OrganizationRole actual, OrganizationRole minimum)
    {
        var client = fixture.Factory.CreateClientAs(actual);
        var response = await client.GetAsync($"/api/v1/_test/role/{minimum.ToString().ToLowerInvariant()}");

        if (actual >= minimum)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        else
            response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.forbidden");
    }

    [Fact]
    public void PolicyNames_MapToTheRoles()
    {
        Assert.Equal(OrganizationRole.Viewer, AetheraPolicies.MinimumRole(AetheraPolicies.Viewer));
        Assert.Equal(OrganizationRole.Developer, AetheraPolicies.MinimumRole(AetheraPolicies.Developer));
        Assert.Equal(OrganizationRole.Admin, AetheraPolicies.MinimumRole(AetheraPolicies.Admin));
        Assert.Equal(OrganizationRole.Owner, AetheraPolicies.MinimumRole(AetheraPolicies.Owner));
        Assert.Throws<ArgumentOutOfRangeException>(() => AetheraPolicies.MinimumRole("Root"));
    }

    [Fact]
    public async Task Sessions_AreBoundByRoleOnly_ScopeRequirementsDoNotApply()
    {
        var session = fixture.Factory.CreateClientAs(OrganizationRole.Viewer); // scopes: null -> session
        Assert.Equal(HttpStatusCode.OK, (await session.PostAsync("/api/v1/_test/scope/write", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/_test/scope/secrets")).StatusCode);
    }

    [Fact]
    public async Task Tokens_WithoutTheScope_AreRefusedWithInsufficientScope()
    {
        var token = fixture.Factory.CreateClientAs(OrganizationRole.Owner, scopes: [Scopes.Read]);
        var response = await token.PostAsync("/api/v1/_test/scope/write", null);
        var body = await response.ReadJsonAsync();

        response.AssertProblem(body, 403, "auth.insufficient_scope");
        Assert.Equal("write", body.Str("requiredScope"));
    }

    [Fact]
    public async Task Tokens_WithNoScopesAtAll_AreRefused()
    {
        var token = fixture.Factory.CreateClientAs(OrganizationRole.Owner, scopes: []);
        var response = await token.GetAsync("/api/v1/_test/scope/read");
        response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.insufficient_scope");
    }

    [Theory]
    [InlineData("write")]
    [InlineData("admin")]
    [InlineData("*")]
    public async Task Tokens_WithTheScopeOrAWiderOne_Pass(string scope)
    {
        var token = fixture.Factory.CreateClientAs(OrganizationRole.Developer, scopes: [scope]);
        Assert.Equal(HttpStatusCode.OK, (await token.PostAsync("/api/v1/_test/scope/write", null)).StatusCode);
    }

    [Fact]
    public async Task WriteScope_CoversRead_ButNotSecrets()
    {
        var token = fixture.Factory.CreateClientAs(OrganizationRole.Developer, scopes: [Scopes.Write]);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/v1/_test/scope/read")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/v1/_test/scope/secrets")).StatusCode);

        var secrets = fixture.Factory.CreateClientAs(OrganizationRole.Developer, scopes: [Scopes.SecretsWrite]);
        Assert.Equal(HttpStatusCode.OK, (await secrets.GetAsync("/api/v1/_test/scope/secrets")).StatusCode);
    }

    [Fact]
    public async Task ScopeIsNotEnough_WhenTheRoleIsTooLow_AndTheRoleErrorWins()
    {
        // A Viewer's token can never write, whatever scopes it carries (effective permission = scopes AND role).
        var token = fixture.Factory.CreateClientAs(OrganizationRole.Viewer, scopes: [Scopes.Write, Scopes.Admin]);
        var response = await token.PostAsync("/api/v1/_test/role-and-scope", null);
        response.AssertProblem(await response.ReadJsonAsync(), 403, "auth.forbidden");

        var noScope = fixture.Factory.CreateClientAs(OrganizationRole.Viewer, scopes: []);
        var both = await noScope.PostAsync("/api/v1/_test/role-and-scope", null);
        both.AssertProblem(await both.ReadJsonAsync(), 403, "auth.forbidden");
    }

    [Fact]
    public async Task RoleAndScope_BothSatisfied_Passes()
    {
        var token = fixture.Factory.CreateClientAs(OrganizationRole.Developer, scopes: [Scopes.Write]);
        Assert.Equal(HttpStatusCode.OK, (await token.PostAsync("/api/v1/_test/role-and-scope", null)).StatusCode);

        var session = fixture.Factory.CreateClientAs(OrganizationRole.Developer);
        Assert.Equal(HttpStatusCode.OK, (await session.PostAsync("/api/v1/_test/role-and-scope", null)).StatusCode);
    }

    [Fact]
    public async Task Group_IsAuthenticatedByDefault_AndAllowAnonymousOptsOut()
    {
        var anonymous = fixture.Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/_test/authenticated")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/_test/scope/read")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/_test/anonymous")).StatusCode);
    }

    [Fact]
    public void Scopes_SatisfiesImplications()
    {
        HashSet<string> Set(params string[] s) => [.. s];
        Assert.True(Scopes.Satisfies(Set("read"), "read"));
        Assert.True(Scopes.Satisfies(Set("write"), "read"));
        Assert.False(Scopes.Satisfies(Set("read"), "write"));
        Assert.False(Scopes.Satisfies(Set("write"), "deploy"));
        Assert.False(Scopes.Satisfies(Set("write"), "servers:write"));
        Assert.True(Scopes.Satisfies(Set("secrets:write"), "secrets:read"));
        Assert.False(Scopes.Satisfies(Set("secrets:read"), "secrets:write"));
        Assert.True(Scopes.Satisfies(Set("admin"), "servers:write"));
        Assert.True(Scopes.Satisfies(Set("*"), "deploy"));
        Assert.Equal(["read", "write", "deploy", "secrets:read", "secrets:write", "servers:write", "admin"], Scopes.Known);
    }

    [Fact]
    public async Task CurrentActor_ReadsTheClaims_ForSessionsAndTokens()
    {
        var userId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var identity = new SeededIdentity(orgId, userId, OrganizationRole.Admin);

        var session = await (await fixture.Factory.CreateClientAs(OrganizationRole.Admin, identity: identity).GetAsync("/api/v1/_test/whoami")).ReadJsonAsync();
        Assert.True(session["isAuthenticated"]!.GetValue<bool>());
        Assert.Equal(userId.ToString(), session.Str("userId"));
        Assert.Equal(orgId.ToString(), session.Str("organizationId"));
        Assert.Equal("Admin", session.Str("role"));
        Assert.Null(session["apiTokenId"]);
        Assert.Empty(session["scopes"]!.AsArray());
        Assert.False(string.IsNullOrEmpty(session.Str("requestId")));

        var tokenId = Guid.NewGuid();
        var token = await (await fixture.Factory.CreateClientAs(OrganizationRole.Admin, [Scopes.Write, Scopes.Deploy], identity, tokenId)
            .GetAsync("/api/v1/_test/whoami")).ReadJsonAsync();
        Assert.Equal(tokenId.ToString(), token.Str("apiTokenId"));
        Assert.Equal(userId.ToString(), token.Str("userId"));
        Assert.Equal(["deploy", "write"], token["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public void Principal_UsesTheDocumentedClaimTypes()
    {
        var tokenId = Guid.NewGuid();
        var principal = AetheraPrincipal.Create("t", Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Owner, tokenId, ["read", "write"]);
        Assert.Equal("owner", principal.FindFirst("aethera:role")!.Value);
        Assert.Equal("token", principal.FindFirst("aethera:auth_method")!.Value);
        Assert.Equal(tokenId.ToString(), principal.FindFirst("aethera:token_id")!.Value);
        Assert.Equal(["read", "write"], principal.FindAll("aethera:scope").Select(c => c.Value));
        Assert.NotNull(principal.FindFirst("aethera:user_id"));
        Assert.NotNull(principal.FindFirst("aethera:org_id"));

        var session = AetheraPrincipal.Create("t", Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Viewer);
        Assert.Equal("session", session.FindFirst("aethera:auth_method")!.Value);
        Assert.Null(session.FindFirst("aethera:token_id"));
        Assert.Empty(session.FindAll("aethera:scope"));
    }
}
