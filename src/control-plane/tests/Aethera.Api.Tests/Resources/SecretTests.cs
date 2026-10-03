using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Resources;

[Collection(ResourcesCollection.Name)]
public sealed class SecretTests(ResourcesFixture fixture)
{
    private const string Marker = "PLAINTEXT-MARKER-7f3a9c";

    [RequiresDatabaseFact]
    public async Task Create_ReturnsMetadataAndAMask_NeverThePlaintext()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "DB_PASSWORD", description = "prod db", value = Marker });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Marker, text);
        var secret = JsonNode.Parse(text)!;
        Assert.Equal("********", secret["value"]!.GetValue<string>());
        Assert.Equal(1, secret["currentVersion"]!.GetValue<int>());
        Assert.Equal("organization", secret["scope"]!.GetValue<string>());
        Assert.Null(secret["rotatedAt"]);
        Assert.Null(secret["projectId"]);
        Assert.Equal($"/api/v1/secrets/{secret.Id()}", response.Headers.Location!.ToString());
        Assert.NotNull(response.Headers.ETag);

        var fetched = await tenant.Owner.GetStringAsync($"/api/v1/secrets/{secret.Id()}");
        Assert.DoesNotContain(Marker, fetched);
        Assert.Contains("\"value\":\"********\"", fetched);
    }

    [RequiresDatabaseFact]
    public async Task Plaintext_IsOnlyEverInTheRevealResponse()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, projectId, appId) = await tenant.CreateStackAsync();
        var secret = await tenant.CreateSecretAsync("API_KEY", Marker);
        var id = secret.Id();

        // Use it from an env var, rotate it, rename it: collect every response body along the way.
        var bodies = new List<string>();
        async Task Collect(Task<HttpResponseMessage> request)
        {
            var response = await request;
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        await Collect(tenant.Admin.PostAsync($"/api/v1/applications/{appId}/env-vars", new { key = "API_KEY", secretId = id })); // organization-wide: Admin
        await Collect(tenant.Developer.PatchAsync($"/api/v1/secrets/{id}", new { description = "renamed" }));
        await Collect(tenant.Developer.PostAsync($"/api/v1/secrets/{id}/rotate", new { value = Marker + "-v2" }));
        await Collect(tenant.Owner.GetAsync("/api/v1/secrets"));
        await Collect(tenant.Owner.GetAsync($"/api/v1/secrets/{id}"));
        await Collect(tenant.Viewer.GetAsync($"/api/v1/applications/{appId}/env-vars"));
        await Collect(tenant.Viewer.GetAsync($"/api/v1/applications/{appId}/env-vars/export"));
        await Collect(tenant.Viewer.GetAsync($"/api/v1/applications/{appId}"));
        await Collect(tenant.Developer.DeleteAsync($"/api/v1/secrets/{id}?confirm=API_KEY")); // 409: in use
        Assert.All(bodies, b => Assert.DoesNotContain(Marker, b));

        // Not in the audit trail either, and not in the database in clear.
        Assert.All(await tenant.AuditAsync(), e => Assert.DoesNotContain(Marker, e.MetadataJson));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var versions = await db.SecretVersions.AsNoTracking().Where(v => v.SecretId == Guid.Parse(id)).OrderBy(v => v.Version).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.All(versions, v =>
        {
            Assert.DoesNotContain(Marker, Encoding.UTF8.GetString(v.Ciphertext));
            Assert.Equal(12, v.Nonce.Length);
            Assert.Equal(48, v.WrappedDataKey.Length);
            Assert.Equal(12, v.WrappedDataKeyNonce.Length);
            Assert.Equal(1, v.MasterKeyVersion);
        });
        Assert.NotEqual(versions[0].Nonce, versions[1].Nonce);

        // Reveal is the one place it appears.
        var reveal = await tenant.Admin.PostAsync($"/api/v1/secrets/{id}/reveal", null);
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
        Assert.Equal("no-store", reveal.Headers.CacheControl!.ToString());
        var revealed = await reveal.ReadAsync();
        Assert.Equal(Marker + "-v2", revealed["value"]!.GetValue<string>());
        Assert.Equal(2, revealed["version"]!.GetValue<int>());
        Assert.Equal("API_KEY", revealed["name"]!.GetValue<string>());
        Assert.NotNull(serverId);
        Assert.NotNull(envId);
        Assert.NotNull(projectId);
    }

    [RequiresDatabaseFact]
    public async Task Reveal_IsAdminOnly_ScopedAndAudited()
    {
        var tenant = await fixture.NewTenantAsync();
        var secret = await tenant.CreateSecretAsync("TOKEN", Marker);
        var url = $"/api/v1/secrets/{secret.Id()}/reveal";

        await (await tenant.Viewer.PostAsync(url, null)).AssertProblemAsync(403, "auth.forbidden");
        await (await tenant.Developer.PostAsync(url, null)).AssertProblemAsync(403, "auth.forbidden");
        await (await tenant.Token(OrganizationRole.Developer, "secrets:write").PostAsync(url, null)).AssertProblemAsync(403, "auth.forbidden");
        Assert.Empty(await tenant.AuditAsync(secret.Id(), "secret.revealed"));

        var readOnly = await tenant.Token(OrganizationRole.Admin, "secrets:read").PostAsync(url, null);
        await readOnly.AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal("secrets:write", (await readOnly.ReadAsync())["requiredScope"]!.GetValue<string>());
        await (await tenant.Token(OrganizationRole.Admin, "read", "write").PostAsync(url, null)).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Empty(await tenant.AuditAsync(secret.Id(), "secret.revealed"));

        Assert.Equal(HttpStatusCode.OK, (await tenant.Admin.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Token(OrganizationRole.Admin, "secrets:write").PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Token(OrganizationRole.Admin, "admin").PostAsync(url, null)).StatusCode);

        var events = await tenant.AuditAsync(secret.Id(), "secret.revealed");
        Assert.Equal(4, events.Count);
        Assert.Contains(events, e => e.ActorType == AuditActorType.ApiToken && e.ActorApiTokenId is not null);
        Assert.All(events, e =>
        {
            Assert.Contains(e.ActorType, new[] { AuditActorType.User, AuditActorType.ApiToken });
            Assert.Equal(tenant.Identity.UserId, e.ActorUserId);
            Assert.DoesNotContain(Marker, e.MetadataJson);
            Assert.Contains("TOKEN", e.MetadataJson);
        });
    }

    [RequiresDatabaseFact]
    public async Task Rotate_AddsAVersion_OldVersionsStayRevealable()
    {
        var tenant = await fixture.NewTenantAsync();
        var secret = await tenant.CreateSecretAsync("ROTATING", "first");
        var id = secret.Id();

        var rotated = await tenant.Developer.PostAsync($"/api/v1/secrets/{id}/rotate", new { value = "second" });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var body = await rotated.ReadAsync();
        Assert.Equal(2, body["currentVersion"]!.GetValue<int>());
        Assert.NotNull(body["rotatedAt"]);
        Assert.Equal("********", body["value"]!.GetValue<string>());
        await tenant.Developer.PostAsync($"/api/v1/secrets/{id}/rotate", new { value = "third" });

        async Task<string> Reveal(string query) => (await (await tenant.Admin.PostAsync($"/api/v1/secrets/{id}/reveal{query}", null)).ReadAsync())["value"]!.GetValue<string>();
        Assert.Equal("third", await Reveal(""));
        Assert.Equal("first", await Reveal("?version=1"));
        Assert.Equal("second", await Reveal("?version=2"));
        await (await tenant.Admin.PostAsync($"/api/v1/secrets/{id}/reveal?version=9", null)).AssertProblemAsync(404, "secret_version.not_found");

        Assert.Equal(2, (await tenant.AuditAsync(id, "secret.rotated")).Count);
        Assert.Equal(3, JsonNode.Parse((await tenant.AuditAsync(id, "secret.rotated")).OrderBy(e => e.OccurredAt).Last().MetadataJson)!["version"]!.GetValue<int>());

        var invalid = await tenant.Developer.PostAsync($"/api/v1/secrets/{id}/rotate", new { value = (string?)null });
        Assert.Contains(("/value", "required"), await invalid.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task CiphertextIsBoundToItsSecret_MovingARowBreaksReveal()
    {
        var tenant = await fixture.NewTenantAsync();
        var a = await tenant.CreateSecretAsync("A", "value-a");
        var b = await tenant.CreateSecretAsync("B", "value-b");

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var from = await db.SecretVersions.AsNoTracking().SingleAsync(v => v.SecretId == Guid.Parse(a.Id()));
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE secret_versions SET ciphertext = {from.Ciphertext}, nonce = {from.Nonce}, wrapped_data_key = {from.WrappedDataKey}, wrapped_data_key_nonce = {from.WrappedDataKeyNonce} WHERE secret_id = {Guid.Parse(b.Id())}");
        }

        var response = await tenant.Admin.PostAsync($"/api/v1/secrets/{b.Id()}/reveal", null);
        await response.AssertProblemAsync(500, "secret.undecryptable");
        Assert.Equal("value-a", (await (await tenant.Admin.PostAsync($"/api/v1/secrets/{a.Id()}/reveal", null)).ReadAsync())["value"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Scopes_UniqueNamesAndFilters()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, envId, projectId, appId) = await tenant.CreateStackAsync();

        var org = await tenant.CreateSecretAsync("SHARED");
        var project = await tenant.CreateSecretAsync("SHARED", scope: new { projectId });
        var environment = await tenant.CreateSecretAsync("SHARED", scope: new { environmentId = envId });
        var workload = await tenant.CreateSecretAsync("SHARED", scope: new { workloadId = appId });
        Assert.Equal(["organization", "project", "environment", "workload"],
            new[] { org, project, environment, workload }.Select(s => s["scope"]!.GetValue<string>()));
        Assert.Equal(projectId, project["projectId"]!.GetValue<string>());
        Assert.Equal(envId, environment["environmentId"]!.GetValue<string>());
        Assert.Equal(appId, workload["workloadId"]!.GetValue<string>());

        // Unique per scope among live secrets.
        foreach (var body in new object[] { new { name = "SHARED", value = "x" }, new { name = "SHARED", value = "x", projectId }, new { name = "SHARED", value = "x", workloadId = appId } })
            await (await tenant.Developer.PostAsync("/api/v1/secrets", body)).AssertProblemAsync(409, "secret.already_exists");

        async Task<List<string>> Ids(string query) => (await tenant.Owner.GetJsonAsync("/api/v1/secrets" + query))["items"]!.AsArray().Select(s => s!.Id()).ToList();
        Assert.Equal(4, (await Ids("")).Count);
        Assert.Equal([project.Id()], await Ids($"?projectId={projectId}"));
        Assert.Equal([workload.Id()], await Ids($"?workloadId={appId}"));
        Assert.Equal([org.Id()], await Ids("?scope=organization"));
        Assert.Equal([environment.Id()], await Ids("?scope=environment"));
        Assert.Equal(2, (await Ids("?scope=organization,project")).Count);
        Assert.Equal(4, (await Ids("?q=shar")).Count);
        await (await tenant.Owner.GetAsync("/api/v1/secrets?scope=galaxy")).AssertProblemAsync(400, "validation.invalid_parameter");
    }

    [RequiresDatabaseFact]
    public async Task Create_Validation()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var foreignProject = await other.CreateProjectAsync();

        var errors = await (await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "", description = new string('x', 501) })).ValidationErrorsAsync();
        Assert.Contains(("/name", "required"), errors);
        Assert.Contains(("/value", "required"), errors);
        Assert.Contains(("/description", "too_long"), errors);

        var two = await (await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "x", value = "v", projectId = Guid.NewGuid(), environmentId = Guid.NewGuid() })).ValidationErrorsAsync();
        Assert.Contains(("/projectId", "not_unique"), two);

        Assert.Contains(("/projectId", "not_found"), await (await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "x", value = "v", projectId = Guid.NewGuid() })).ValidationErrorsAsync());
        Assert.Contains(("/projectId", "not_found"), await (await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "x", value = "v", projectId = foreignProject.Id() })).ValidationErrorsAsync());
        Assert.Contains(("/environmentId", "not_found"), await (await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "x", value = "v", environmentId = Guid.NewGuid() })).ValidationErrorsAsync());
        Assert.Contains(("/workloadId", "not_found"), await (await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "x", value = "v", workloadId = Guid.NewGuid() })).ValidationErrorsAsync());

        Assert.Contains(("/value", "too_long"), await (await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "x", value = new string('v', 65 * 1024) })).ValidationErrorsAsync());
        Assert.Equal(HttpStatusCode.Created, (await tenant.Developer.PostAsync("/api/v1/secrets", new { name = "empty-is-fine", value = "" })).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Patch_RenamesAndDescribes_ButNeverChangesTheValue()
    {
        var tenant = await fixture.NewTenantAsync();
        var secret = await tenant.CreateSecretAsync("OLD", "keep-me");
        await tenant.CreateSecretAsync("TAKEN");
        var url = $"/api/v1/secrets/{secret.Id()}";
        var etag = (await tenant.Owner.GetAsync(url)).Headers.ETag!.Tag;

        var patched = await tenant.Developer.PatchAsync(url, new { name = "NEW", description = "d", value = "ignored" }, etag);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var body = await patched.ReadAsync();
        Assert.Equal("NEW", body["name"]!.GetValue<string>());
        Assert.Equal("d", body["description"]!.GetValue<string>());
        Assert.Equal(1, body["currentVersion"]!.GetValue<int>());
        Assert.Equal("keep-me", (await (await tenant.Admin.PostAsync(url + "/reveal", null)).ReadAsync())["value"]!.GetValue<string>());

        await (await tenant.Developer.PatchAsync(url, new { name = "TAKEN" })).AssertProblemAsync(409, "secret.already_exists");
        await (await tenant.Developer.PatchAsync(url, new { description = "x" }, etag)).AssertProblemAsync(412, "precondition.failed");
        Assert.Contains(("/name", "required"), await (await tenant.Developer.PatchAsync(url, new { name = (string?)null })).ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task Delete_RequiresConfirmation_AndFailsWhileReferenced()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var secret = await tenant.CreateSecretAsync("IN_USE");
        var url = $"/api/v1/secrets/{secret.Id()}";
        var variable = await tenant.Admin.CreateAsync($"/api/v1/applications/{appId}/env-vars", new { key = "X", secretId = secret.Id() });

        await (await tenant.Developer.DeleteAsync(url)).AssertProblemAsync(428, "confirmation.required");
        var inUse = await tenant.Developer.DeleteAsync(url + "?confirm=IN_USE");
        await inUse.AssertProblemAsync(409, "secret.in_use");
        Assert.Contains("environment variable", (await inUse.ReadAsync())["detail"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/applications/{appId}/env-vars/{variable.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync(url + "?confirm=IN_USE")).StatusCode);
        await (await tenant.Owner.GetAsync(url)).AssertProblemAsync(404, "secret.not_found");
        await (await tenant.Admin.PostAsync(url + "/reveal", null)).AssertProblemAsync(404, "secret.not_found");
        Assert.Contains(await tenant.AuditAsync(secret.Id()), e => e.Action == "secret.deleted");
        await tenant.CreateSecretAsync("IN_USE"); // the name is free again
    }

    [RequiresDatabaseFact]
    public async Task Delete_IgnoresReferencesFromDeletedWorkloads()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var secret = await tenant.CreateSecretAsync("ORPHAN");
        await tenant.Admin.CreateAsync($"/api/v1/applications/{appId}/env-vars", new { key = "X", secretId = secret.Id() });
        var slug = (await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}"))["slug"]!.GetValue<string>();
        await tenant.Developer.DeleteAsync($"/api/v1/applications/{appId}?confirm={slug}");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/secrets/{secret.Id()}?confirm=ORPHAN")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Roles_TokenScopes_AndTenantIsolation()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var secret = await a.CreateSecretAsync("ISOLATED");
        var url = $"/api/v1/secrets/{secret.Id()}";

        // Viewers read the (masked) metadata but cannot write.
        Assert.Equal(HttpStatusCode.OK, (await a.Viewer.GetAsync(url)).StatusCode);
        await (await a.Viewer.PostAsync("/api/v1/secrets", new { name = "x", value = "v" })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.PostAsync(url + "/rotate", new { value = "v" })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.PatchAsync(url, new { description = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.DeleteAsync(url + "?confirm=ISOLATED")).AssertProblemAsync(403, "auth.forbidden");

        // Tokens: the general read/write scopes do NOT cover secrets.
        foreach (var scopes in new[] { new[] { "read" }, new[] { "write" }, new[] { "read", "write", "deploy", "servers:write" } })
        {
            var token = a.Token(OrganizationRole.Owner, scopes);
            var list = await token.GetAsync("/api/v1/secrets");
            await list.AssertProblemAsync(403, "auth.insufficient_scope");
            await (await token.PostAsync("/api/v1/secrets", new { name = "x", value = "v" })).AssertProblemAsync(403, "auth.insufficient_scope");
        }

        var readToken = a.Token(OrganizationRole.Viewer, "secrets:read");
        Assert.Equal(HttpStatusCode.OK, (await readToken.GetAsync("/api/v1/secrets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readToken.GetAsync(url)).StatusCode);
        var denied = await a.Token(OrganizationRole.Developer, "secrets:read").PostAsync(url + "/rotate", new { value = "v" });
        await denied.AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal("secrets:write", (await denied.ReadAsync())["requiredScope"]!.GetValue<string>());
        var writeToken = a.Token(OrganizationRole.Developer, "secrets:write");
        Assert.Equal(HttpStatusCode.OK, (await writeToken.GetAsync("/api/v1/secrets")).StatusCode); // secrets:write implies secrets:read
        Assert.Equal(HttpStatusCode.OK, (await writeToken.PostAsync(url + "/rotate", new { value = "v2" })).StatusCode);

        // Other organizations see nothing.
        await (await b.Owner.GetAsync(url)).AssertProblemAsync(404, "secret.not_found");
        await (await b.Owner.PatchAsync(url, new { description = "x" })).AssertProblemAsync(404, "secret.not_found");
        await (await b.Owner.PostAsync(url + "/rotate", new { value = "x" })).AssertProblemAsync(404, "secret.not_found");
        await (await b.Owner.PostAsync(url + "/reveal", null)).AssertProblemAsync(404, "secret.not_found");
        await (await b.Owner.DeleteAsync(url + "?confirm=ISOLATED")).AssertProblemAsync(404, "secret.not_found");
        Assert.Empty((await b.Owner.GetJsonAsync("/api/v1/secrets"))["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task List_PaginatesAcrossPages()
    {
        var tenant = await fixture.NewTenantAsync();
        for (var i = 0; i < 5; i++) await tenant.CreateSecretAsync($"S{i}");

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await tenant.Owner.GetJsonAsync("/api/v1/secrets?limit=2&sort=name" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            seen.AddRange(page["items"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(["S0", "S1", "S2", "S3", "S4"], seen);
    }
}
