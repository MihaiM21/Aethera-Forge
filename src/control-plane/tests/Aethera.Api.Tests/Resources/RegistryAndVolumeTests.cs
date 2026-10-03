using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

[Collection(ResourcesCollection.Name)]
public sealed class RegistryTests(ResourcesFixture fixture)
{
    private const string Password = "registry-password-marker";

    [RequiresDatabaseFact]
    public async Task Create_StoresTheCredentialAsASecret_AndNeverReturnsIt()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Admin.PostAsync("/api/v1/registries", new { name = "GitHub", url = "ghcr.io", username = "ci-bot", password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, text);
        var registry = JsonNode.Parse(text)!;
        Assert.Equal("ci-bot", registry["username"]!.GetValue<string>());
        Assert.True(registry["hasCredentials"]!.GetValue<bool>());
        Assert.Null(registry["password"]);
        Assert.Null(registry["passwordSecretId"]);

        foreach (var url in new[] { $"/api/v1/registries/{registry.Id()}", "/api/v1/registries" })
            Assert.DoesNotContain(Password, await tenant.Viewer.GetStringAsync(url));

        // The password lives in a secret: encrypted, versioned, and revealable by an admin only.
        var secrets = (await tenant.Owner.GetJsonAsync("/api/v1/secrets"))["items"]!.AsArray();
        var secret = Assert.Single(secrets)!;
        Assert.StartsWith("registry/", secret["name"]!.GetValue<string>());
        Assert.Equal("********", secret["value"]!.GetValue<string>());

        // It is listed, flagged as managed and points at its registry (WP1.6).
        Assert.True(secret["managed"]!.GetValue<bool>());
        Assert.Equal("registryCredential", secret["purpose"]!.GetValue<string>());
        Assert.Equal("registry", secret["managedBy"]!["type"]!.GetValue<string>());
        Assert.Equal(registry.Id(), secret["managedBy"]!["id"]!.GetValue<string>());
        Assert.Equal(Password, (await (await tenant.Admin.PostAsync($"/api/v1/secrets/{secret.Id()}/reveal", null)).ReadAsync())["value"]!.GetValue<string>());
        Assert.All(await tenant.AuditAsync(), e => Assert.DoesNotContain(Password, e.MetadataJson));
        Assert.Contains(await tenant.AuditAsync(registry.Id()), e => e.Action == "registry.created");

        // ...and cannot be changed or deleted from under the registry (was 409 secret.in_use before the secret had a purpose).
        await (await tenant.Developer.DeleteAsync($"/api/v1/secrets/{secret.Id()}?confirm={secret["name"]!.GetValue<string>()}")).AssertProblemAsync(409, "secret.managed");

        var anonymous = await tenant.Admin.CreateAsync("/api/v1/registries", new { name = "Public", url = "https://registry.example.com:5000/v2" });
        Assert.False(anonymous["hasCredentials"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Patch_ReplacesOrRemovesTheCredential()
    {
        var tenant = await fixture.NewTenantAsync();
        var registry = await tenant.Admin.CreateAsync("/api/v1/registries", new { name = "R", url = "docker.io", password = "one" });
        var url = $"/api/v1/registries/{registry.Id()}";
        var secretId = (await tenant.Owner.GetJsonAsync("/api/v1/secrets"))["items"]![0]!.Id();

        var renamed = await tenant.Admin.PatchOkAsync(url, new { name = "Docker Hub", url = "https://index.docker.io/v1/", username = "me" });
        Assert.Equal("Docker Hub", renamed["name"]!.GetValue<string>());
        Assert.True(renamed["hasCredentials"]!.GetValue<bool>());

        var rotated = await tenant.Admin.PatchOkAsync(url, new { password = "two" });
        Assert.True(rotated["hasCredentials"]!.GetValue<bool>());
        var current = await tenant.Owner.GetJsonAsync($"/api/v1/secrets/{secretId}");
        Assert.Equal(2, current["currentVersion"]!.GetValue<int>());
        Assert.Equal("two", (await (await tenant.Admin.PostAsync($"/api/v1/secrets/{secretId}/reveal", null)).ReadAsync())["value"]!.GetValue<string>());

        var removed = await tenant.Admin.PatchOkAsync(url, new { password = (string?)null });
        Assert.False(removed["hasCredentials"]!.GetValue<bool>());
        Assert.Empty((await tenant.Owner.GetJsonAsync("/api/v1/secrets"))["items"]!.AsArray());

        var added = await tenant.Admin.PatchOkAsync(url, new { password = "three" });
        Assert.True(added["hasCredentials"]!.GetValue<bool>());
        Assert.Single((await tenant.Owner.GetJsonAsync("/api/v1/secrets"))["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task Validation_Duplicates_AndConfirmation()
    {
        var tenant = await fixture.NewTenantAsync();
        var errors = await (await tenant.Admin.PostAsync("/api/v1/registries", new { name = "", url = "not a url!" })).ValidationErrorsAsync();
        Assert.Contains(("/name", "required"), errors);
        Assert.Contains(("/url", "pattern"), errors);
        Assert.Contains(("/url", "required"), await (await tenant.Admin.PostAsync("/api/v1/registries", new { name = "x" })).ValidationErrorsAsync());

        var registry = await tenant.Admin.CreateAsync("/api/v1/registries", new { name = "Dup", url = "ghcr.io" });
        await (await tenant.Admin.PostAsync("/api/v1/registries", new { name = "Dup", url = "other.io" })).AssertProblemAsync(409, "registry.already_exists");
        var other = await tenant.Admin.CreateAsync("/api/v1/registries", new { name = "Other", url = "other.io" });
        await (await tenant.Admin.PatchAsync($"/api/v1/registries/{other.Id()}", new { name = "Dup" })).AssertProblemAsync(409, "registry.already_exists");
        Assert.Contains(("/name", "required"), await (await tenant.Admin.PatchAsync($"/api/v1/registries/{other.Id()}", new { name = (string?)null })).ValidationErrorsAsync());

        var url = $"/api/v1/registries/{registry.Id()}";
        await (await tenant.Admin.DeleteAsync(url)).AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync(url + "?confirm=Dup")).StatusCode);
        await (await tenant.Viewer.GetAsync(url)).AssertProblemAsync(404, "registry.not_found");
        await tenant.Admin.CreateAsync("/api/v1/registries", new { name = "Dup", url = "ghcr.io" });
    }

    [RequiresDatabaseFact]
    public async Task Delete_FailsWhileAnApplicationPullsFromIt()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var registry = await tenant.Admin.CreateAsync("/api/v1/registries", new { name = "Private", url = "registry.example.com" });
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = "puller", environmentId = envId, serverId, sourceKind = "dockerImage", image = new { image = "acme/app", registryId = registry.Id() },
        });
        Assert.Equal(registry.Id(), app["image"]!["registryId"]!.GetValue<string>());

        await (await tenant.Admin.DeleteAsync($"/api/v1/registries/{registry.Id()}?confirm=Private")).AssertProblemAsync(409, "registry.in_use");
        var slug = app["slug"]!.GetValue<string>();
        await tenant.Developer.DeleteAsync($"/api/v1/applications/{app.Id()}?confirm={slug}");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync($"/api/v1/registries/{registry.Id()}?confirm=Private")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Test_IsNotImplementedYet()
    {
        var tenant = await fixture.NewTenantAsync();
        var registry = await tenant.Admin.CreateAsync("/api/v1/registries", new { name = "T", url = "ghcr.io" });
        var response = await tenant.Admin.PostAsync($"/api/v1/registries/{registry.Id()}/test", null);
        await response.AssertProblemAsync(501, "not_implemented");
        Assert.Equal("Not implemented", (await response.ReadAsync())["title"]!.GetValue<string>());
        await (await tenant.Admin.PostAsync($"/api/v1/registries/{Guid.NewGuid()}/test", null)).AssertProblemAsync(404, "registry.not_found");
        await (await tenant.Developer.PostAsync($"/api/v1/registries/{registry.Id()}/test", null)).AssertProblemAsync(403, "auth.forbidden");
    }

    [RequiresDatabaseFact]
    public async Task Roles_Scopes_AndTenantIsolation()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var registry = await a.Admin.CreateAsync("/api/v1/registries", new { name = "Mine", url = "ghcr.io" });
        var url = $"/api/v1/registries/{registry.Id()}";

        Assert.Equal(HttpStatusCode.OK, (await a.Viewer.GetAsync(url)).StatusCode);
        foreach (var client in new[] { a.Viewer, a.Developer })
        {
            await (await client.PostAsync("/api/v1/registries", new { name = "x", url = "x.io" })).AssertProblemAsync(403, "auth.forbidden");
            await (await client.PatchAsync(url, new { name = "x" })).AssertProblemAsync(403, "auth.forbidden");
            await (await client.DeleteAsync(url + "?confirm=Mine")).AssertProblemAsync(403, "auth.forbidden");
        }

        await (await a.Token(OrganizationRole.Admin, "read").PostAsync("/api/v1/registries", new { name = "x", url = "x.io" })).AssertProblemAsync(403, "auth.insufficient_scope");
        // Registry changes handle a credential: a token needs secrets:write on top of write (WP1.6).
        var writeOnly = await a.Token(OrganizationRole.Admin, "write").PostAsync("/api/v1/registries", new { name = "tok", url = "x.io" });
        await writeOnly.AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal("secrets:write", (await writeOnly.ReadAsync())["requiredScope"]!.GetValue<string>());
        await (await a.Token(OrganizationRole.Admin, "secrets:write").PostAsync("/api/v1/registries", new { name = "tok", url = "x.io" }))
            .AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.Created, (await a.Token(OrganizationRole.Admin, "write", "secrets:write").PostAsync("/api/v1/registries", new { name = "tok", url = "x.io" })).StatusCode);

        await (await b.Owner.GetAsync(url)).AssertProblemAsync(404, "registry.not_found");
        await (await b.Owner.PatchAsync(url, new { name = "x" })).AssertProblemAsync(404, "registry.not_found");
        await (await b.Owner.DeleteAsync(url + "?confirm=Mine")).AssertProblemAsync(404, "registry.not_found");
        Assert.Empty((await b.Owner.GetJsonAsync("/api/v1/registries"))["items"]!.AsArray());
        var crossApp = await b.Developer.PostAsync("/api/v1/applications", new
        {
            name = "x", environmentId = Guid.NewGuid(), serverId = Guid.NewGuid(), sourceKind = "dockerImage", image = new { image = "a", registryId = registry.Id() },
        });
        Assert.Contains(("/environmentId", "not_found"), await crossApp.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task List_SortsAndPages()
    {
        var tenant = await fixture.NewTenantAsync();
        foreach (var name in new[] { "c", "a", "b" }) await tenant.Admin.CreateAsync("/api/v1/registries", new { name, url = name + ".io" });
        var first = await tenant.Viewer.GetJsonAsync("/api/v1/registries?limit=2");
        Assert.Equal(["a", "b"], first["items"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()));
        var second = await tenant.Viewer.GetJsonAsync("/api/v1/registries?limit=2&cursor=" + Uri.EscapeDataString(first["nextCursor"]!.GetValue<string>()));
        Assert.Equal(["c"], second["items"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()));
        Assert.Null(second["nextCursor"]);
        Assert.Equal(["c", "b", "a"], (await tenant.Viewer.GetJsonAsync("/api/v1/registries?sort=-name"))["items"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()));
    }
}

[Collection(ResourcesCollection.Name)]
public sealed class VolumeTests(ResourcesFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task Crud_OnApplications_AndTopLevelAndNested()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();

        var response = await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/var/lib/app/", backupEnabled = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var volume = await response.ReadAsync();
        Assert.Equal($"/api/v1/volumes/{volume.Id()}", response.Headers.Location!.ToString());
        Assert.Equal("/var/lib/app", volume["mountPath"]!.GetValue<string>());
        Assert.Equal("var-lib-app", volume["name"]!.GetValue<string>());
        Assert.True(volume["backupEnabled"]!.GetValue<bool>());
        Assert.False(volume["readOnly"]!.GetValue<bool>());
        Assert.Null(volume["hostPath"]);
        Assert.Equal(appId, volume["workloadId"]!.GetValue<string>());

        var nested = await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/volumes", new { name = "cache", mountPath = "/cache", readOnly = true, hostPath = "/srv/cache" });
        Assert.Equal("/srv/cache", nested["hostPath"]!.GetValue<string>());

        var listing = await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/volumes");
        Assert.Equal(["/cache", "/var/lib/app"], listing["items"]!.AsArray().Select(v => v!["mountPath"]!.GetValue<string>()));
        Assert.Equal(2, (await tenant.Viewer.GetJsonAsync($"/api/v1/volumes?workloadId={appId}"))["items"]!.AsArray().Count);
        Assert.Single((await tenant.Viewer.GetJsonAsync("/api/v1/volumes?q=CACHE"))["items"]!.AsArray());

        var etag = (await tenant.Viewer.GetAsync($"/api/v1/volumes/{volume.Id()}")).Headers.ETag!.Tag;
        var patched = await tenant.Developer.PatchAsync($"/api/v1/volumes/{volume.Id()}", new { name = "data", mountPath = "/data", readOnly = true, hostPath = (string?)null }, etag);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var body = await patched.ReadAsync();
        Assert.Equal("/data", body["mountPath"]!.GetValue<string>());
        Assert.Equal("data", body["name"]!.GetValue<string>());
        Assert.True(body["readOnly"]!.GetValue<bool>());
        await (await tenant.Developer.PatchAsync($"/api/v1/volumes/{volume.Id()}", new { readOnly = false }, etag)).AssertProblemAsync(412, "precondition.failed");

        await (await tenant.Developer.DeleteAsync($"/api/v1/volumes/{volume.Id()}")).AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/volumes/{volume.Id()}?confirm=data")).StatusCode);
        await (await tenant.Viewer.GetAsync($"/api/v1/volumes/{volume.Id()}")).AssertProblemAsync(404, "volume.not_found");
        Assert.Equal(["volume.created", "volume.created", "volume.deleted", "volume.updated"],
            (await tenant.AuditAsync()).Where(e => e.Action.StartsWith("volume.")).Select(e => e.Action).Order());
    }

    [RequiresDatabaseFact]
    public async Task MountPathValidation_AndDuplicates()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();

        foreach (var bad in new[] { "/", "//", "relative", "", "/a/../b", "/with space", "/a:b" })
        {
            var errors = await (await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = bad })).ValidationErrorsAsync();
            Assert.Contains(errors, e => e.Location == "/mountPath" && e.Code is "pattern" or "required");
        }

        Assert.Contains(("/hostPath", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/ok", hostPath = "relative/dir" })).ValidationErrorsAsync());
        Assert.Contains(("/name", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/ok", name = "bad name!" })).ValidationErrorsAsync());
        Assert.Contains(("/workloadId", "not_found"), await (await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = Guid.NewGuid(), mountPath = "/ok" })).ValidationErrorsAsync());
        Assert.Contains(("/workloadId", "not_found"), await (await tenant.Developer.PostAsync("/api/v1/volumes", new { mountPath = "/ok" })).ValidationErrorsAsync());

        await tenant.Developer.CreateAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/data", name = "one" });
        await (await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/data/", name = "two" })).AssertProblemAsync(409, "volume.already_exists");
        await (await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/other", name = "one" })).AssertProblemAsync(409, "volume.already_exists");
        var second = await tenant.Developer.CreateAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/logs" });
        await (await tenant.Developer.PatchAsync($"/api/v1/volumes/{second.Id()}", new { mountPath = "/data" })).AssertProblemAsync(409, "volume.already_exists");
        Assert.Contains(("/mountPath", "pattern"), await (await tenant.Developer.PatchAsync($"/api/v1/volumes/{second.Id()}", new { mountPath = "/" })).ValidationErrorsAsync());
        Assert.Contains(("/mountPath", "required"), await (await tenant.Developer.PatchAsync($"/api/v1/volumes/{second.Id()}", new { mountPath = (string?)null })).ValidationErrorsAsync());

        // The same path on another workload is fine; the nested route rejects a mismatching workload id.
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var other = await tenant.CreateApplicationAsync(envId, serverId);
        await tenant.Developer.CreateAsync("/api/v1/volumes", new { workloadId = other.Id(), mountPath = "/data" });
        var mismatch = await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/volumes", new { workloadId = other.Id(), mountPath = "/zzz" });
        Assert.Contains(("/workloadId", "mismatch"), await mismatch.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task Roles_Scopes_AndTenantIsolation()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await a.CreateStackAsync();
        var volume = await a.Developer.CreateAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/data", name = "data" });
        var url = $"/api/v1/volumes/{volume.Id()}";

        Assert.Equal(HttpStatusCode.OK, (await a.Viewer.GetAsync(url)).StatusCode);
        await (await a.Viewer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.PatchAsync(url, new { readOnly = true })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.DeleteAsync(url + "?confirm=data")).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Token(OrganizationRole.Owner, "read").PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/x" })).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.Created, (await a.Token(OrganizationRole.Developer, "write").PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/viatoken" })).StatusCode);

        await (await b.Owner.GetAsync(url)).AssertProblemAsync(404, "volume.not_found");
        await (await b.Owner.PatchAsync(url, new { readOnly = true })).AssertProblemAsync(404, "volume.not_found");
        await (await b.Owner.DeleteAsync(url + "?confirm=data")).AssertProblemAsync(404, "volume.not_found");
        await (await b.Owner.GetAsync($"/api/v1/applications/{appId}/volumes")).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.PostAsync($"/api/v1/applications/{appId}/volumes", new { mountPath = "/x" })).AssertProblemAsync(404, "application.not_found");
        Assert.Empty((await b.Owner.GetJsonAsync("/api/v1/volumes"))["items"]!.AsArray());
        Assert.Contains(("/workloadId", "not_found"), await (await b.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/x" })).ValidationErrorsAsync());
    }
}
