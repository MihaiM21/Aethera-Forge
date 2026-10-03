using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Resources;

/// <summary>
/// WP1.6 part 1: secrets that belong to another resource (registry password, server SSH key, git credential, generated service password) are
/// <em>managed</em>. They are listed but cannot be changed through <c>/secrets</c> and cannot be bound as environment variables; ordinary
/// secrets can be bound by a Developer only inside their scope, organization-wide ones only by an Administrator (ADR 0006).
/// </summary>
[Collection(ResourcesCollection.Name)]
public sealed class SecretBindingTests(ResourcesFixture fixture)
{
    // ---- the world ------------------------------------------------------------------------------------------------------------------

    /// <summary>One tenant with a secret of every purpose and scope, and two applications and two services to bind them to.</summary>
    private sealed class World
    {
        public required Tenant Tenant { get; init; }
        public required string ProjectId { get; init; }
        public required string EnvironmentId { get; init; }
        public required string AppId { get; init; }
        public required string OtherAppId { get; init; }
        public required string ServiceId { get; init; }
        public required string OtherServiceId { get; init; }
        public required Dictionary<string, string> Secrets { get; init; }
        public required string RegistryId { get; init; }
        public required string ServerWithKeyId { get; init; }
    }

    private async Task<World> BuildWorldAsync()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, projectId, appId) = await tenant.CreateStackAsync();
        var otherApp = await tenant.CreateApplicationAsync(envId, serverId);
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "cache", environmentId = envId, serverId, templateKey = "redis" });
        var otherService = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "cache2", environmentId = envId, serverId, templateKey = "redis" });

        var secrets = new Dictionary<string, string>
        {
            ["userOrg"] = (await tenant.CreateSecretAsync("U_ORG")).Id(),
            ["userProject"] = (await tenant.CreateSecretAsync("U_PROJECT", scope: new { projectId })).Id(),
            ["userEnvironment"] = (await tenant.CreateSecretAsync("U_ENV", scope: new { environmentId = envId })).Id(),
            ["userWorkload"] = (await tenant.CreateSecretAsync("U_APP", scope: new { workloadId = appId })).Id(),
            ["userOtherWorkload"] = (await tenant.CreateSecretAsync("U_OTHER", scope: new { workloadId = otherApp.Id() })).Id(),
        };

        // A registry password.
        var registry = await tenant.Admin.CreateAsync("/api/v1/registries", new { name = "ghcr", url = "ghcr.io", username = "bot", password = "registry-pass" });
        secrets["registry"] = await ManagedSecretOf(tenant, registry.Id());

        // An SSH credential: an organization secret that an Administrator attaches to a server.
        var sshSecret = await tenant.CreateSecretAsync("SSH_KEY", "ssh-private-key");
        var server = await tenant.Admin.CreateAsync("/api/v1/servers", new { name = "keyed", host = "keyed.example.com", sshCredentialSecretId = sshSecret.Id() });
        secrets["ssh"] = sshSecret.Id();

        // A git credential (there is no endpoint for it yet: the row is created directly).
        var gitSecret = await tenant.CreateSecretAsync("GIT_TOKEN", "git-token");
        await using (var scope = tenant.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var row = await db.Secrets.FirstAsync(s => s.Id == Guid.Parse(gitSecret.Id()));
            row.Purpose = SecretPurpose.GitCredential;
            db.GitCredentials.Add(new GitCredential { OrganizationId = tenant.Identity.OrganizationId, Name = "github", SecretId = row.Id });
            await db.SaveChangesAsync();
        }

        secrets["git"] = gitSecret.Id();

        // The passwords generated for the two services.
        secrets["serviceOwn"] = await GeneratedSecretOf(tenant, service.Id());
        secrets["serviceOther"] = await GeneratedSecretOf(tenant, otherService.Id());

        return new World
        {
            Tenant = tenant, ProjectId = projectId, EnvironmentId = envId, AppId = appId, OtherAppId = otherApp.Id(), ServiceId = service.Id(),
            OtherServiceId = otherService.Id(), Secrets = secrets, RegistryId = registry.Id(), ServerWithKeyId = server.Id(),
        };
    }

    private static async Task<string> ManagedSecretOf(Tenant tenant, string ownerId)
    {
        var items = (await tenant.Owner.GetJsonAsync("/api/v1/secrets?limit=200"))["items"]!.AsArray();
        return items.Single(s => s!["managedBy"]?["id"]?.GetValue<string>() == ownerId)!.Id();
    }

    private static async Task<string> GeneratedSecretOf(Tenant tenant, string serviceId) =>
        (await tenant.Owner.GetJsonAsync($"/api/v1/secrets?workloadId={serviceId}&q=REDIS"))["items"]!.AsArray().Single()!.Id();

    private static string EnvVars(World world, string workloadKind) =>
        workloadKind == "service" ? $"/api/v1/services/{world.ServiceId}/env-vars" : $"/api/v1/applications/{world.AppId}/env-vars";

    // ---- listing --------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task ManagedSecrets_AreListedAndFlagged_WithTheirOwner()
    {
        var world = await BuildWorldAsync();
        var items = (await world.Tenant.Viewer.GetJsonAsync("/api/v1/secrets?limit=200"))["items"]!.AsArray()
            .ToDictionary(s => s!.Id(), s => s!);

        void Expect(string key, string purpose, string? ownerType, string? ownerId, bool checkOwnerId = true)
        {
            var secret = items[world.Secrets[key]];
            Assert.Equal(purpose, secret["purpose"]!.GetValue<string>());
            Assert.Equal(ownerType is not null, secret["managed"]!.GetValue<bool>());
            Assert.Equal(ownerType, secret["managedBy"]?["type"]?.GetValue<string>());
            if (checkOwnerId) Assert.Equal(ownerId, secret["managedBy"]?["id"]?.GetValue<string>());
            else Assert.NotNull(secret["managedBy"]?["id"]);
            Assert.Equal("********", secret["value"]!.GetValue<string>());
            // The same through GET /secrets/{id}.
            var single = world.Tenant.Viewer.GetJsonAsync($"/api/v1/secrets/{secret.Id()}").GetAwaiter().GetResult();
            Assert.Equal(secret["managed"]!.GetValue<bool>(), single["managed"]!.GetValue<bool>());
            Assert.Equal(secret["managedBy"]?["id"]?.GetValue<string>(), single["managedBy"]?["id"]?.GetValue<string>());
        }

        Expect("userOrg", "user", null, null);
        Expect("userWorkload", "user", null, null);
        Expect("registry", "registryCredential", "registry", world.RegistryId);
        Expect("ssh", "sshCredential", "server", world.ServerWithKeyId);
        Expect("git", "gitCredential", "gitCredential", null, checkOwnerId: false);
        Expect("serviceOwn", "serviceGenerated", "service", world.ServiceId);
        Expect("serviceOther", "serviceGenerated", "service", world.OtherServiceId);
    }

    // ---- /secrets cannot touch managed secrets --------------------------------------------------------------------------------------

    [RequiresDatabaseTheory]
    [InlineData("registry", "/api/v1/registries/")]
    [InlineData("ssh", "/api/v1/servers/")]
    [InlineData("git", "/api/v1/git-credentials/")]
    [InlineData("serviceOwn", "/api/v1/services/")]
    public async Task ManagedSecrets_CannotBePatchedRotatedOrDeleted_ByAnyRole(string key, string ownerEndpoint)
    {
        var world = await BuildWorldAsync();
        var id = world.Secrets[key];
        var url = $"/api/v1/secrets/{id}";
        var before = await world.Tenant.Owner.GetJsonAsync(url);
        var name = before["name"]!.GetValue<string>();

        foreach (var role in new[] { OrganizationRole.Developer, OrganizationRole.Admin, OrganizationRole.Owner })
        {
            var client = world.Tenant.As(role);
            var patch = await client.PatchAsync(url, new { description = "tampered" });
            await patch.AssertProblemAsync(409, "secret.managed");
            Assert.Contains(ownerEndpoint, (await patch.ReadAsync())["detail"]!.GetValue<string>());
            await (await client.PostAsync(url + "/rotate", new { value = "attacker-value" })).AssertProblemAsync(409, "secret.managed");
            await (await client.DeleteAsync($"{url}?confirm={name}")).AssertProblemAsync(409, "secret.managed");
        }

        // Nothing changed: same version, same description, still there.
        var after = await world.Tenant.Owner.GetJsonAsync(url);
        Assert.Equal(before["currentVersion"]!.GetValue<int>(), after["currentVersion"]!.GetValue<int>());
        Assert.Equal(before["description"]?.GetValue<string>(), after["description"]?.GetValue<string>());
        Assert.Null(after["rotatedAt"]);
        Assert.Empty(await world.Tenant.AuditAsync(id, "secret.rotated"));
        Assert.Empty(await world.Tenant.AuditAsync(id, "secret.updated"));
        Assert.Empty(await world.Tenant.AuditAsync(id, "secret.deleted"));
    }

    [RequiresDatabaseFact]
    public async Task ManagedSecrets_AreChangedThroughTheirOwner()
    {
        var world = await BuildWorldAsync();
        var secretUrl = $"/api/v1/secrets/{world.Secrets["registry"]}";

        // The Administrator rotates the registry password through the registry: the secret gets a new version, /secrets could not.
        await world.Tenant.Admin.PatchOkAsync($"/api/v1/registries/{world.RegistryId}", new { password = "rotated" });
        var rotated = await world.Tenant.Owner.GetJsonAsync(secretUrl);
        Assert.Equal(2, rotated["currentVersion"]!.GetValue<int>());
        Assert.True(rotated["managed"]!.GetValue<bool>());
        Assert.Equal("rotated", (await (await world.Tenant.Admin.PostAsync(secretUrl + "/reveal", null)).ReadAsync())["value"]!.GetValue<string>());

        // Reveal stays Admin-only and is unaffected by the purpose.
        await (await world.Tenant.Developer.PostAsync(secretUrl + "/reveal", null)).AssertProblemAsync(403, "auth.forbidden");
    }

    [RequiresDatabaseFact]
    public async Task UserSecrets_StillBehaveAsBefore()
    {
        var world = await BuildWorldAsync();
        var id = world.Secrets["userProject"];
        var url = $"/api/v1/secrets/{id}";
        Assert.Equal(HttpStatusCode.OK, (await world.Tenant.Developer.PatchAsync(url, new { description = "fine" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await world.Tenant.Developer.PostAsync(url + "/rotate", new { value = "v2" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await world.Tenant.Developer.DeleteAsync(url + "?confirm=U_PROJECT")).StatusCode);
    }

    // ---- binding to environment variables: role x secret purpose x secret scope ------------------------------------------------------

    /// <summary>(secret, target, role, expected status, expected code). Target is "app" (the application) or "service" (the service that owns serviceOwn).</summary>
    public static TheoryData<string, string, OrganizationRole, int, string?> BindingTable()
    {
        var data = new TheoryData<string, string, OrganizationRole, int, string?>();
        foreach (var role in new[] { OrganizationRole.Developer, OrganizationRole.Admin, OrganizationRole.Owner })
        {
            var admin = role >= OrganizationRole.Admin;

            // User secrets: organization-wide needs an Administrator; narrower scopes any Developer, but only inside the scope.
            data.Add("userOrg", "app", role, admin ? 201 : 403, admin ? null : "secret.binding_forbidden");
            data.Add("userProject", "app", role, 201, null);
            data.Add("userEnvironment", "app", role, 201, null);
            data.Add("userWorkload", "app", role, 201, null);
            data.Add("userOtherWorkload", "app", role, 422, "validation.failed");

            // Managed secrets are never bindable, whatever the role...
            data.Add("registry", "app", role, 403, "secret.binding_forbidden");
            data.Add("ssh", "app", role, 403, "secret.binding_forbidden");
            data.Add("git", "app", role, 403, "secret.binding_forbidden");

            // ...except a generated service secret, which belongs to its own service only.
            data.Add("serviceOwn", "service", role, 201, null);
            data.Add("serviceOwn", "app", role, 403, "secret.binding_forbidden");
            data.Add("serviceOther", "service", role, 403, "secret.binding_forbidden");
            data.Add("serviceOther", "app", role, 403, "secret.binding_forbidden");
        }

        return data;
    }

    [RequiresDatabaseTheory]
    [MemberData(nameof(BindingTable))]
    public async Task Binding_Matrix_Create(string secret, string target, OrganizationRole role, int status, string? code)
    {
        var world = await BuildWorldAsync();
        var url = EnvVars(world, target);
        var response = await world.Tenant.As(role).PostAsync(url, new { key = "BOUND", secretId = world.Secrets[secret] });
        Assert.Equal(status, (int)response.StatusCode);
        if (code is not null) Assert.Equal(code, (await response.ReadAsync())["code"]!.GetValue<string>());

        var variables = (await world.Tenant.Viewer.GetJsonAsync(url))["items"]!.AsArray();
        Assert.Equal(status == 201, variables.Any(v => v!["key"]!.GetValue<string>() == "BOUND"));
    }

    [RequiresDatabaseTheory]
    [MemberData(nameof(BindingTable))]
    public async Task Binding_Matrix_Patch(string secret, string target, OrganizationRole role, int status, string? code)
    {
        var world = await BuildWorldAsync();
        var url = EnvVars(world, target);
        var plain = await world.Tenant.Developer.CreateAsync(url, new { key = "PLAIN_ONE", value = "x" });
        var response = await world.Tenant.As(role).PatchAsync($"{url}/{plain.Id()}", new { secretId = world.Secrets[secret] });
        Assert.Equal(status == 201 ? 200 : status, (int)response.StatusCode);
        if (code is not null) Assert.Equal(code, (await response.ReadAsync())["code"]!.GetValue<string>());

        var variable = await world.Tenant.Viewer.GetJsonAsync($"{url}/{plain.Id()}");
        Assert.Equal(status == 201, variable["isSecret"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Binding_Rejection_ExplainsItself_AndLeavesNoValueBehind()
    {
        var world = await BuildWorldAsync();
        var url = EnvVars(world, "app");

        var org = await world.Tenant.Developer.PostAsync(url, new { key = "A", secretId = world.Secrets["userOrg"] });
        Assert.Contains("Administrator", (await org.ReadAsync())["detail"]!.GetValue<string>());
        var managed = await world.Tenant.Admin.PostAsync(url, new { key = "B", secretId = world.Secrets["registry"] });
        var detail = (await managed.ReadAsync())["detail"]!.GetValue<string>();
        Assert.Contains("managed by another resource", detail);
        Assert.DoesNotContain("registry-pass", detail);
    }

    [RequiresDatabaseFact]
    public async Task Binding_ServiceGeneratedSecret_CanBeRestoredOnItsOwnService()
    {
        var world = await BuildWorldAsync();
        var url = EnvVars(world, "service");
        var variables = (await world.Tenant.Viewer.GetJsonAsync(url))["items"]!.AsArray();
        var password = variables.Single(v => v!["key"]!.GetValue<string>() == "REDIS_PASSWORD")!;
        Assert.Equal(world.Secrets["serviceOwn"], password["secretId"]!.GetValue<string>());

        // Deleting the variable and binding the generated secret again is the one managed binding that is allowed.
        Assert.Equal(HttpStatusCode.NoContent, (await world.Tenant.Developer.DeleteAsync($"{url}/{password.Id()}")).StatusCode);
        var again = await world.Tenant.Developer.CreateAsync(url, new { key = "REDIS_PASSWORD", secretId = world.Secrets["serviceOwn"] });
        Assert.True(again["isSecret"]!.GetValue<bool>());
    }

    // ---- API tokens -----------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Binding_WithAToken_NeedsTheSecretsWriteScope_OnTopOfTheRole()
    {
        var world = await BuildWorldAsync();
        var url = EnvVars(world, "app");
        var user = world.Secrets["userWorkload"];
        var org = world.Secrets["userOrg"];

        // 'write' alone does not cover secrets (Scopes.cs): even a plain Developer binding of a workload secret is refused.
        var noScope = await world.Tenant.Token(OrganizationRole.Developer, "read", "write").PostAsync(url, new { key = "K1", secretId = user });
        await noScope.AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal("secrets:write", (await noScope.ReadAsync())["requiredScope"]!.GetValue<string>());
        await (await world.Tenant.Token(OrganizationRole.Admin, "read", "write", "secrets:read").PostAsync(url, new { key = "K1", secretId = org }))
            .AssertProblemAsync(403, "auth.insufficient_scope");

        // Plain values need no secrets scope.
        Assert.Equal(HttpStatusCode.Created, (await world.Tenant.Token(OrganizationRole.Developer, "write").PostAsync(url, new { key = "PLAINV", value = "1" })).StatusCode);

        // With secrets:write: a Developer token binds a workload secret but still not an organization-wide one; an Admin token binds both.
        var developerToken = world.Tenant.Token(OrganizationRole.Developer, "write", "secrets:write");
        Assert.Equal(HttpStatusCode.Created, (await developerToken.PostAsync(url, new { key = "K2", secretId = user })).StatusCode);
        await (await developerToken.PostAsync(url, new { key = "K3", secretId = org })).AssertProblemAsync(403, "secret.binding_forbidden");
        var adminToken = world.Tenant.Token(OrganizationRole.Admin, "write", "secrets:write");
        Assert.Equal(HttpStatusCode.Created, (await adminToken.PostAsync(url, new { key = "K4", secretId = org })).StatusCode);
        await (await adminToken.PostAsync(url, new { key = "K5", secretId = world.Secrets["registry"] })).AssertProblemAsync(403, "secret.binding_forbidden");

        // The wildcard and admin scopes cover it as everywhere else; the role still limits the token.
        Assert.Equal(HttpStatusCode.Created, (await world.Tenant.Token(OrganizationRole.Developer, "*").PostAsync(url, new { key = "K6", secretId = user })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await world.Tenant.Token(OrganizationRole.Admin, "admin").PostAsync(url, new { key = "K7", secretId = org })).StatusCode);
        await (await world.Tenant.Token(OrganizationRole.Developer, "admin").PostAsync(url, new { key = "K8", secretId = org })).AssertProblemAsync(403, "secret.binding_forbidden");
    }

    [RequiresDatabaseFact]
    public async Task Binding_ByPatch_WithAToken_NeedsTheSecretsWriteScope()
    {
        var world = await BuildWorldAsync();
        var url = EnvVars(world, "app");
        var plain = await world.Tenant.Developer.CreateAsync(url, new { key = "P", value = "x" });
        var target = $"{url}/{plain.Id()}";

        await (await world.Tenant.Token(OrganizationRole.Developer, "write").PatchAsync(target, new { secretId = world.Secrets["userWorkload"] }))
            .AssertProblemAsync(403, "auth.insufficient_scope");
        // Changing something else on a plain variable needs no secrets scope.
        Assert.Equal(HttpStatusCode.OK, (await world.Tenant.Token(OrganizationRole.Developer, "write").PatchAsync(target, new { value = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await world.Tenant.Token(OrganizationRole.Developer, "write", "secrets:write").PatchAsync(target, new { secretId = world.Secrets["userWorkload"] })).StatusCode);
    }

    // ---- the SSH credential of a server ---------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task SshCredential_BecomesManaged_WhenAServerUsesIt_AndIsReleasedWhenNoServerDoes()
    {
        var tenant = await fixture.NewTenantAsync();
        var first = await tenant.CreateSecretAsync("KEY_ONE", "one");
        var second = await tenant.CreateSecretAsync("KEY_TWO", "two");
        var urlOf = (JsonNode s) => $"/api/v1/secrets/{s.Id()}";
        Assert.False((await tenant.Owner.GetJsonAsync(urlOf(first)))["managed"]!.GetValue<bool>());

        var server = await tenant.Admin.CreateAsync("/api/v1/servers", new { name = "a", host = "a.example.com", sshCredentialSecretId = first.Id() });
        var shared = await tenant.Admin.CreateAsync("/api/v1/servers", new { name = "b", host = "b.example.com", sshCredentialSecretId = first.Id() });
        var managed = await tenant.Owner.GetJsonAsync(urlOf(first));
        Assert.Equal("sshCredential", managed["purpose"]!.GetValue<string>());
        Assert.True(managed["managed"]!.GetValue<bool>());

        // Switching one server to another key keeps the first managed (the other server still uses it) and manages the second.
        await tenant.Admin.PatchOkAsync($"/api/v1/servers/{server.Id()}", new { sshCredentialSecretId = second.Id() });
        Assert.True((await tenant.Owner.GetJsonAsync(urlOf(first)))["managed"]!.GetValue<bool>());
        Assert.True((await tenant.Owner.GetJsonAsync(urlOf(second)))["managed"]!.GetValue<bool>());

        // Dropping the last reference (clearing it, or deleting the server) makes the secret an ordinary one again.
        await tenant.Admin.PatchOkAsync($"/api/v1/servers/{shared.Id()}", new { sshCredentialSecretId = (string?)null });
        Assert.False((await tenant.Owner.GetJsonAsync(urlOf(first)))["managed"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync($"/api/v1/servers/{server.Id()}?confirm=a")).StatusCode);
        var released = await tenant.Owner.GetJsonAsync(urlOf(second));
        Assert.Equal("user", released["purpose"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await tenant.Developer.PostAsync(urlOf(second) + "/rotate", new { value = "again" })).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task SshCredential_MustBeAnUnusedOrganizationSecret()
    {
        var world = await BuildWorldAsync();
        var admin = world.Tenant.Admin;
        object Server(string secret) => new { name = Tenant.Unique("s"), host = Tenant.Unique("h") + ".example.com", sshCredentialSecretId = secret };

        // Managed by something else, scoped to a project, or already bound to an environment variable: not a candidate.
        Assert.Contains(("/sshCredentialSecretId", "secret.managed"), await (await admin.PostAsync("/api/v1/servers", Server(world.Secrets["registry"]))).ValidationErrorsAsync());
        Assert.Contains(("/sshCredentialSecretId", "secret.managed"), await (await admin.PostAsync("/api/v1/servers", Server(world.Secrets["serviceOwn"]))).ValidationErrorsAsync());
        Assert.Contains(("/sshCredentialSecretId", "scope_mismatch"), await (await admin.PostAsync("/api/v1/servers", Server(world.Secrets["userProject"]))).ValidationErrorsAsync());

        var bound = await world.Tenant.CreateSecretAsync("BOUND_ORG");
        await admin.CreateAsync(EnvVars(world, "app"), new { key = "USES_IT", secretId = bound.Id() });
        Assert.Contains(("/sshCredentialSecretId", "secret.in_use"), await (await admin.PostAsync("/api/v1/servers", Server(bound.Id()))).ValidationErrorsAsync());
        Assert.False((await world.Tenant.Owner.GetJsonAsync($"/api/v1/secrets/{bound.Id()}"))["managed"]!.GetValue<bool>());
    }
}
