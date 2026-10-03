using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

[Collection(ResourcesCollection.Name)]
public sealed class OrganizationTests(ResourcesFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task List_ReturnsOnlyTheCallersOrganization()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var list = await a.Viewer.GetJsonAsync("/api/v1/organizations");
        var item = Assert.Single(list["items"]!.AsArray())!;
        Assert.Equal(a.Identity.OrganizationId.ToString(), item.Id());
        Assert.NotEqual(b.Identity.OrganizationId.ToString(), item.Id());
        Assert.Null(list["nextCursor"]);
    }

    [RequiresDatabaseFact]
    public async Task Get_Patch_AsAdmin()
    {
        var tenant = await fixture.NewTenantAsync();
        var id = tenant.Identity.OrganizationId;
        var get = await tenant.Viewer.GetAsync($"/api/v1/organizations/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var etag = get.Headers.ETag!.Tag;

        var newSlug = Tenant.Unique("acme");
        var patched = await tenant.Admin.PatchAsync($"/api/v1/organizations/{id}", new { name = "Acme Inc", slug = newSlug }, etag);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var body = await patched.ReadAsync();
        Assert.Equal("Acme Inc", body["name"]!.GetValue<string>());
        Assert.Equal(newSlug, body["slug"]!.GetValue<string>());
        Assert.Contains(await tenant.AuditAsync(id.ToString()), e => e.Action == "organization.updated");

        (await tenant.Admin.PatchAsync($"/api/v1/organizations/{id}", new { name = "Stale" }, etag)).EnsureProblem(412);
    }

    [RequiresDatabaseFact]
    public async Task Patch_Validation_Duplicates_AndNulls()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var id = tenant.Identity.OrganizationId;

        var invalid = await tenant.Admin.PatchAsync($"/api/v1/organizations/{id}", new { name = "", slug = "BAD SLUG" });
        var errors = await invalid.ValidationErrorsAsync();
        Assert.Contains(("/name", "required"), errors);
        Assert.Contains(("/slug", "pattern"), errors);

        var nulls = await tenant.Admin.PatchAsync($"/api/v1/organizations/{id}", new { name = (string?)null });
        Assert.Contains(("/name", "required"), await nulls.ValidationErrorsAsync());

        var otherSlug = (await other.Viewer.GetJsonAsync($"/api/v1/organizations/{other.Identity.OrganizationId}"))["slug"]!.GetValue<string>();
        await (await tenant.Admin.PatchAsync($"/api/v1/organizations/{id}", new { slug = otherSlug })).AssertProblemAsync(409, "organization.already_exists");
    }

    [RequiresDatabaseFact]
    public async Task OtherOrganizations_AreNotFound()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var foreign = b.Identity.OrganizationId;
        await (await a.Admin.GetAsync($"/api/v1/organizations/{foreign}")).AssertProblemAsync(404, "organization.not_found");
        await (await a.Admin.PatchAsync($"/api/v1/organizations/{foreign}", new { name = "Hijacked" })).AssertProblemAsync(404, "organization.not_found");
        Assert.NotEqual("Hijacked", (await b.Viewer.GetJsonAsync($"/api/v1/organizations/{foreign}"))["name"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Patch_NeedsAdminRoleAndAdminScope()
    {
        var tenant = await fixture.NewTenantAsync();
        var url = $"/api/v1/organizations/{tenant.Identity.OrganizationId}";
        await (await tenant.Developer.PatchAsync(url, new { name = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await tenant.Viewer.PatchAsync(url, new { name = "x" })).AssertProblemAsync(403, "auth.forbidden");
        var denied = await tenant.Token(OrganizationRole.Admin, "write").PatchAsync(url, new { name = "x" });
        await denied.AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal("admin", (await denied.ReadAsync())["requiredScope"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await tenant.Token(OrganizationRole.Admin, "admin").PatchAsync(url, new { name = "ok" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Token(OrganizationRole.Viewer, "read").GetAsync(url)).StatusCode);
    }
}

[Collection(ResourcesCollection.Name)]
public sealed class ServerTests(ResourcesFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task Create_ReturnsRecordWithSeparateStatusAxes()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Admin.PostAsync("/api/v1/servers", new
        {
            name = "edge-1", host = "EDGE-1.Example.com", sshPort = 2222, sshUser = "deploy", roles = new[] { "master", "build" },
            publicIp = "203.0.113.7", maxConcurrentBuilds = 2, resources = new { cpuCores = 4, memoryBytes = 8_589_934_592L, diskBytes = 100_000_000_000L, os = "Ubuntu 24.04", architecture = "amd64" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var server = await response.ReadAsync();
        Assert.Equal("edge-1.example.com", server["host"]!.GetValue<string>());
        Assert.Equal(2222, server["sshPort"]!.GetValue<int>());
        Assert.Equal("agent", server["transport"]!.GetValue<string>());
        Assert.Equal("pending", server["lifecycle"]!.GetValue<string>());
        Assert.Equal(["master", "build"], server["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(4, server["resources"]!["cpuCores"]!.GetValue<int>());
        Assert.Equal(0, server["workloadCount"]!.GetValue<int>());

        var status = server["status"]!;
        Assert.Equal("unknown", status["reachability"]!["status"]!.GetValue<string>());
        Assert.Equal("unknown", status["agent"]!["status"]!.GetValue<string>());
        Assert.Equal("unknown", status["docker"]!["status"]!.GetValue<string>());
        Assert.Null(status["reachability"]!["checkedAt"]);
        Assert.Null(status["agent"]!["lastHeartbeatAt"]);

        var ssh = await tenant.Admin.CreateAsync("/api/v1/servers", new { name = "bare", host = "10.0.0.5", transport = "ssh" });
        Assert.Equal("notInstalled", ssh["status"]!["agent"]!["status"]!.GetValue<string>());
        Assert.Contains(await tenant.AuditAsync(server.Id()), e => e.Action == "server.created");
    }

    [RequiresDatabaseFact]
    public async Task Validation_AndDuplicates()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Admin.PostAsync("/api/v1/servers", new
        {
            name = "", host = "http://bad host:22", sshPort = 70000, sshUser = "Bad User", transport = "carrier-pigeon", roles = new[] { "king", "master", "master" },
            publicIp = "999.1.1.1", maxConcurrentBuilds = 0, resources = new { cpuCores = 0, memoryBytes = -5 },
        });
        var errors = await response.ValidationErrorsAsync();
        Assert.Contains(("/name", "required"), errors);
        Assert.Contains(("/host", "pattern"), errors);
        Assert.Contains(("/sshPort", "range"), errors);
        Assert.Contains(("/sshUser", "pattern"), errors);
        Assert.Contains(("/transport", "invalid_enum"), errors);
        Assert.Contains(("/roles/0", "invalid_enum"), errors);
        Assert.Contains(("/roles", "not_unique"), errors);
        Assert.Contains(("/publicIp", "pattern"), errors);
        Assert.Contains(("/maxConcurrentBuilds", "range"), errors);
        Assert.Contains(("/resources/cpuCores", "range"), errors);
        Assert.Contains(("/resources/memoryBytes", "range"), errors);

        await tenant.CreateServerAsync("dup");
        await (await tenant.Admin.PostAsync("/api/v1/servers", new { name = "dup", host = "other.example.com" })).AssertProblemAsync(409, "server.already_exists");
        // The same name is fine in another organization.
        var other = await fixture.NewTenantAsync();
        await other.CreateServerAsync("dup");
    }

    [RequiresDatabaseFact]
    public async Task Patch_MergesAndKeepsTheRest()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync("patchme", publicIp: "203.0.113.1");
        var url = $"/api/v1/servers/{server.Id()}";

        var patched = await tenant.Admin.PatchOkAsync(url, new { sshUser = "root", lifecycle = "active", roles = new[] { "storage" }, resources = new { cpuCores = 8 } });
        Assert.Equal("root", patched["sshUser"]!.GetValue<string>());
        Assert.Equal("active", patched["lifecycle"]!.GetValue<string>());
        Assert.Equal(["storage"], patched["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(8, patched["resources"]!["cpuCores"]!.GetValue<int>());
        Assert.Equal("203.0.113.1", patched["publicIp"]!.GetValue<string>());

        var cleared = await tenant.Admin.PatchOkAsync(url, new { publicIp = (string?)null, sshUser = (string?)null });
        Assert.Null(cleared["publicIp"]);
        Assert.Null(cleared["sshUser"]);
        Assert.Equal(8, cleared["resources"]!["cpuCores"]!.GetValue<int>());

        await (await tenant.Admin.PatchAsync(url, new { host = (string?)null })).ValidationErrorsAsync();
        await (await tenant.Admin.PatchAsync(url, new { name = "patchme", transport = "bad" })).ValidationErrorsAsync();
        var other = await tenant.CreateServerAsync("taken");
        await (await tenant.Admin.PatchAsync(url, new { name = "taken" })).AssertProblemAsync(409, "server.already_exists");
        Assert.NotNull(other);
    }

    [RequiresDatabaseFact]
    public async Task Delete_NeedsConfirmation_AndFailsWhileWorkloadsAreAssigned()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, _, _, applicationId) = await tenant.CreateStackAsync();
        var server = await tenant.Viewer.GetJsonAsync($"/api/v1/servers/{serverId}");
        var name = server["name"]!.GetValue<string>();
        Assert.Equal(1, server["workloadCount"]!.GetValue<int>());

        await (await tenant.Admin.DeleteAsync($"/api/v1/servers/{serverId}")).AssertProblemAsync(428, "confirmation.required");
        await (await tenant.Admin.DeleteAsync($"/api/v1/servers/{serverId}?confirm={name}")).AssertProblemAsync(409, "server.in_use");

        var app = await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{applicationId}");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/applications/{applicationId}?confirm={app["slug"]!.GetValue<string>()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync($"/api/v1/servers/{serverId}?confirm={name}")).StatusCode);
        await (await tenant.Viewer.GetAsync($"/api/v1/servers/{serverId}")).AssertProblemAsync(404, "server.not_found");
        // Deleted names are reusable.
        await tenant.CreateServerAsync(name);
    }

    [RequiresDatabaseFact]
    public async Task RolesAndScopes_OnlyAdminsWithServersWriteMayMutate()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var url = $"/api/v1/servers/{server.Id()}";
        var body = new { name = Tenant.Unique("s"), host = "h.example.com" };

        await (await tenant.Developer.PostAsync("/api/v1/servers", body)).AssertProblemAsync(403, "auth.forbidden");
        await (await tenant.Developer.PatchAsync(url, new { sshUser = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await tenant.Developer.DeleteAsync(url + "?confirm=x")).AssertProblemAsync(403, "auth.forbidden");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Viewer.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Viewer.GetAsync("/api/v1/servers")).StatusCode);

        var writeToken = await tenant.Token(OrganizationRole.Admin, "write").PostAsync("/api/v1/servers", body);
        await writeToken.AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal("servers:write", (await writeToken.ReadAsync())["requiredScope"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Created, (await tenant.Token(OrganizationRole.Admin, "servers:write").PostAsync("/api/v1/servers", body)).StatusCode);
        await (await tenant.Token(OrganizationRole.Owner, "servers:write").GetAsync("/api/v1/servers")).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Token(OrganizationRole.Viewer, "read").GetAsync("/api/v1/servers")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task CrossOrganization_IsNotFound_AndSshCredentialMustBeOwn()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var server = await a.CreateServerAsync();
        var url = $"/api/v1/servers/{server.Id()}";
        await (await b.Admin.GetAsync(url)).AssertProblemAsync(404, "server.not_found");
        await (await b.Admin.PatchAsync(url, new { sshUser = "x" })).AssertProblemAsync(404, "server.not_found");
        await (await b.Admin.DeleteAsync(url + "?confirm=x")).AssertProblemAsync(404, "server.not_found");
        Assert.Empty((await b.Admin.GetJsonAsync("/api/v1/servers"))["items"]!.AsArray());

        var foreignSecret = await b.CreateSecretAsync();
        var response = await a.Admin.PostAsync("/api/v1/servers", new { name = "s", host = "h.example.com", sshCredentialSecretId = foreignSecret.Id() });
        Assert.Contains(("/sshCredentialSecretId", "not_found"), await response.ValidationErrorsAsync());

        var ownSecret = await a.CreateSecretAsync();
        var created = await a.Admin.CreateAsync("/api/v1/servers", new { name = "with-key", host = "h.example.com", sshCredentialSecretId = ownSecret.Id() });
        Assert.Equal(ownSecret.Id(), created["sshCredentialSecretId"]!.GetValue<string>());
        // A secret that a server uses cannot be deleted.
        var secretName = ownSecret["name"]!.GetValue<string>();
        await (await a.Developer.DeleteAsync($"/api/v1/secrets/{ownSecret.Id()}?confirm={secretName}")).AssertProblemAsync(409, "secret.in_use");
    }

    [RequiresDatabaseFact]
    public async Task List_FiltersSortsAndPages()
    {
        var tenant = await fixture.NewTenantAsync();
        foreach (var name in new[] { "c-server", "a-server", "b-server" }) await tenant.CreateServerAsync(name);
        var ssh = await tenant.Admin.CreateAsync("/api/v1/servers", new { name = "d-ssh", host = "d.example.com", transport = "ssh" });

        var all = await tenant.Viewer.GetJsonAsync("/api/v1/servers?limit=3");
        Assert.Equal(["a-server", "b-server", "c-server"], all["items"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));
        var next = await tenant.Viewer.GetJsonAsync("/api/v1/servers?limit=3&cursor=" + Uri.EscapeDataString(all["nextCursor"]!.GetValue<string>()));
        Assert.Equal(ssh.Id(), Assert.Single(next["items"]!.AsArray())!.Id());
        Assert.Null(next["nextCursor"]);

        var onlySsh = await tenant.Viewer.GetJsonAsync("/api/v1/servers?transport=ssh");
        Assert.Single(onlySsh["items"]!.AsArray());
        Assert.Equal(4, (await tenant.Viewer.GetJsonAsync("/api/v1/servers?lifecycle=pending,active"))["items"]!.AsArray().Count);
        await (await tenant.Viewer.GetAsync("/api/v1/servers?lifecycle=bogus")).AssertProblemAsync(400, "validation.invalid_parameter");
    }
}

internal static class ProblemAssertions
{
    /// <summary>Synchronous status check used where the body does not matter.</summary>
    public static void EnsureProblem(this HttpResponseMessage response, int status) => Assert.Equal(status, (int)response.StatusCode);
}
