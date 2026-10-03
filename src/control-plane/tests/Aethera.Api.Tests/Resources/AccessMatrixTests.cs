using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

/// <summary>
/// The role and token-scope matrix of every endpoint of the resource API, driven by one table. Authorization runs before the handler, so the
/// requests use random ids: a denied call is a 403 with the expected code, an allowed one is anything else (404, 422, ...), never 401/403.
/// </summary>
[Collection(ResourcesCollection.Name)]
public sealed class AccessMatrixTests(ResourcesFixture fixture)
{
    private static readonly string Id = Guid.NewGuid().ToString();

    private const string Read = "read";
    private const string Write = "write";
    private const string SecretsRead = "secrets:read";
    private const string SecretsWrite = "secrets:write";
    private const string ServersWrite = "servers:write";
    private const string Admin = "admin";

    // method, path, minimum role, token scope
    private static readonly (string Method, string Path, OrganizationRole Role, string Scope)[] Endpoints =
    [
        ("GET", "/organizations", OrganizationRole.Viewer, Read),
        ("GET", "/organizations/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/organizations/{id}", OrganizationRole.Admin, Admin),

        ("GET", "/projects", OrganizationRole.Viewer, Read),
        ("POST", "/projects", OrganizationRole.Developer, Write),
        ("POST", "/projects/from-template", OrganizationRole.Developer, Write),
        ("GET", "/projects/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/projects/{id}", OrganizationRole.Developer, Write),
        ("DELETE", "/projects/{id}", OrganizationRole.Developer, Write),
        ("GET", "/projects/{id}/environments", OrganizationRole.Viewer, Read),
        ("POST", "/projects/{id}/environments", OrganizationRole.Developer, Write),
        ("GET", "/project-templates", OrganizationRole.Viewer, Read),
        ("GET", "/environments/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/environments/{id}", OrganizationRole.Developer, Write),
        ("DELETE", "/environments/{id}", OrganizationRole.Developer, Write),

        ("GET", "/servers", OrganizationRole.Viewer, Read),
        ("POST", "/servers", OrganizationRole.Admin, ServersWrite),
        ("GET", "/servers/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/servers/{id}", OrganizationRole.Admin, ServersWrite),
        ("DELETE", "/servers/{id}", OrganizationRole.Admin, ServersWrite),

        ("GET", "/applications", OrganizationRole.Viewer, Read),
        ("POST", "/applications", OrganizationRole.Developer, Write),
        ("GET", "/applications/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/applications/{id}", OrganizationRole.Developer, Write),
        ("DELETE", "/applications/{id}", OrganizationRole.Developer, Write),
        ("GET", "/applications/{id}/env-vars", OrganizationRole.Viewer, Read),
        ("POST", "/applications/{id}/env-vars", OrganizationRole.Developer, Write),
        ("GET", "/applications/{id}/env-vars/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/applications/{id}/env-vars/{id}", OrganizationRole.Developer, Write),
        ("DELETE", "/applications/{id}/env-vars/{id}", OrganizationRole.Developer, Write),
        ("POST", "/applications/{id}/env-vars/import", OrganizationRole.Developer, Write),
        ("GET", "/applications/{id}/env-vars/export", OrganizationRole.Viewer, Read),
        ("GET", "/applications/{id}/volumes", OrganizationRole.Viewer, Read),
        ("POST", "/applications/{id}/volumes", OrganizationRole.Developer, Write),
        ("GET", "/applications/{id}/domains", OrganizationRole.Viewer, Read),
        ("POST", "/applications/{id}/domains", OrganizationRole.Developer, Write),

        ("GET", "/services", OrganizationRole.Viewer, Read),
        ("POST", "/services", OrganizationRole.Developer, Write),
        ("GET", "/services/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/services/{id}", OrganizationRole.Developer, Write),
        ("DELETE", "/services/{id}", OrganizationRole.Developer, Write),
        ("GET", "/service-templates", OrganizationRole.Viewer, Read),
        ("GET", "/services/{id}/env-vars", OrganizationRole.Viewer, Read),
        ("POST", "/services/{id}/env-vars", OrganizationRole.Developer, Write),
        ("GET", "/services/{id}/env-vars/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/services/{id}/env-vars/{id}", OrganizationRole.Developer, Write),
        ("DELETE", "/services/{id}/env-vars/{id}", OrganizationRole.Developer, Write),
        ("POST", "/services/{id}/env-vars/import", OrganizationRole.Developer, Write),
        ("GET", "/services/{id}/env-vars/export", OrganizationRole.Viewer, Read),
        ("GET", "/services/{id}/volumes", OrganizationRole.Viewer, Read),
        ("POST", "/services/{id}/volumes", OrganizationRole.Developer, Write),
        ("GET", "/services/{id}/domains", OrganizationRole.Viewer, Read),
        ("POST", "/services/{id}/domains", OrganizationRole.Developer, Write),

        ("GET", "/secrets", OrganizationRole.Viewer, SecretsRead),
        ("POST", "/secrets", OrganizationRole.Developer, SecretsWrite),
        ("GET", "/secrets/{id}", OrganizationRole.Viewer, SecretsRead),
        ("PATCH", "/secrets/{id}", OrganizationRole.Developer, SecretsWrite),
        ("DELETE", "/secrets/{id}", OrganizationRole.Developer, SecretsWrite),
        ("POST", "/secrets/{id}/rotate", OrganizationRole.Developer, SecretsWrite),
        ("POST", "/secrets/{id}/reveal", OrganizationRole.Admin, SecretsWrite),

        ("GET", "/registries", OrganizationRole.Viewer, Read),
        ("POST", "/registries", OrganizationRole.Admin, Write),
        ("GET", "/registries/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/registries/{id}", OrganizationRole.Admin, Write),
        ("DELETE", "/registries/{id}", OrganizationRole.Admin, Write),
        ("POST", "/registries/{id}/test", OrganizationRole.Admin, Write),

        ("GET", "/volumes", OrganizationRole.Viewer, Read),
        ("POST", "/volumes", OrganizationRole.Developer, Write),
        ("GET", "/volumes/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/volumes/{id}", OrganizationRole.Developer, Write),
        ("DELETE", "/volumes/{id}", OrganizationRole.Developer, Write),

        ("GET", "/domains", OrganizationRole.Viewer, Read),
        ("POST", "/domains", OrganizationRole.Developer, Write),
        ("GET", "/domains/{id}", OrganizationRole.Viewer, Read),
        ("PATCH", "/domains/{id}", OrganizationRole.Developer, Write),
        ("DELETE", "/domains/{id}", OrganizationRole.Developer, Write),
        ("POST", "/domains/{id}/verify-dns", OrganizationRole.Developer, Write),
    ];

    public static TheoryData<string, string, OrganizationRole, string> Table()
    {
        var data = new TheoryData<string, string, OrganizationRole, string>();
        foreach (var (method, path, role, scope) in Endpoints) data.Add(method, path, role, scope);
        return data;
    }

    [Fact]
    public void TheTableCoversEveryEndpointOfTheModule() => Assert.Equal(77, Endpoints.Length);

    private static HttpRequestMessage Request(string method, string path)
    {
        var url = "/api/v1" + path.Replace("{id}", Id);
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PATCH")
            request.Content = Json.Body(new { }, method == "PATCH" ? "application/merge-patch+json" : "application/json");
        return request;
    }

    private static async Task<(HttpStatusCode Status, string? Code, string? RequiredScope)> SendAsync(HttpClient client, string method, string path)
    {
        var response = await client.SendAsync(Request(method, path));
        if (response.IsSuccessStatusCode) return (response.StatusCode, null, null);
        var text = await response.Content.ReadAsStringAsync();
        var body = text.StartsWith('{') ? JsonNode.Parse(text) : null;
        return (response.StatusCode, body?["code"]?.GetValue<string>(), body?["requiredScope"]?.GetValue<string>());
    }

    private static bool Denied((HttpStatusCode Status, string? Code, string? RequiredScope) result) =>
        result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    [RequiresDatabaseTheory]
    [MemberData(nameof(Table))]
    public async Task Role_Matrix(string method, string path, OrganizationRole minimum, string scope)
    {
        _ = scope;
        var tenant = await fixture.NewTenantAsync();

        // Anonymous callers are never let in.
        using (var anonymous = fixture.Factory.CreateAnonymousClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, method, path)).Status);

        // Every role below the minimum is forbidden, every role from the minimum up is let through to the handler.
        foreach (var role in Enum.GetValues<OrganizationRole>())
        {
            var result = await SendAsync(tenant.As(role), method, path);
            if (role < minimum)
                Assert.True((HttpStatusCode.Forbidden, "auth.forbidden") == (result.Status, result.Code), $"{role} {method} {path} -> {result.Status} {result.Code}");
            else
                Assert.False(Denied(result), $"{role} {method} {path} -> {result.Status} {result.Code}");
        }
    }

    [RequiresDatabaseTheory]
    [MemberData(nameof(Table))]
    public async Task Scope_Matrix(string method, string path, OrganizationRole minimum, string scope)
    {
        var tenant = await fixture.NewTenantAsync();
        var role = OrganizationRole.Owner;

        // A token without the scope is refused with the scope named, whatever the role.
        var others = new[] { Read, Write, SecretsRead, SecretsWrite, ServersWrite, Admin, "deploy" }
            .Where(s => !Scopes_Satisfy(s, scope)).ToList();
        foreach (var other in others)
        {
            var result = await SendAsync(tenant.Token(role, other), method, path);
            Assert.True((HttpStatusCode.Forbidden, "auth.insufficient_scope", scope) == (result.Status, result.Code, result.RequiredScope),
                $"token[{other}] {method} {path} -> {result.Status} {result.Code} {result.RequiredScope}");
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(tenant.Token(role), method, path)).Status); // a token with no scopes

        // The scope itself, a broader one, the wildcard and admin pass.
        foreach (var granted in new[] { scope, "*", Admin }.Concat(scope == Read ? [Write] : []).Concat(scope == SecretsRead ? [SecretsWrite] : []))
        {
            var result = await SendAsync(tenant.Token(role, granted), method, path);
            Assert.False(Denied(result), $"token[{granted}] {method} {path} -> {result.Status} {result.Code}");
        }

        // The token never exceeds its owner's role.
        if (minimum > OrganizationRole.Viewer)
        {
            var result = await SendAsync(tenant.Token(minimum - 1, "*"), method, path);
            Assert.True((HttpStatusCode.Forbidden, "auth.forbidden") == (result.Status, result.Code), $"{method} {path} -> {result.Status} {result.Code}");
        }
    }

    private static bool Scopes_Satisfy(string granted, string required) =>
        Aethera.Api.Security.Scopes.Satisfies(new HashSet<string> { granted }, required);
}
