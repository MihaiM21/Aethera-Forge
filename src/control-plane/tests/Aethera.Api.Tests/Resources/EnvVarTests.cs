using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

[Collection(ResourcesCollection.Name)]
public sealed class EnvVarTests(ResourcesFixture fixture)
{
    private static string Url(string appId, string suffix = "") => $"/api/v1/applications/{appId}/env-vars{suffix}";

    private static async Task<string> ExportAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.MediaType + "");
        return await response.Content.ReadAsStringAsync();
    }

    [RequiresDatabaseFact]
    public async Task Crud_PlainAndSecretBackedVariables()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var secret = await tenant.CreateSecretAsync("DB_PASS", "hunter2-marker");

        var plain = await tenant.Developer.CreateAsync(Url(appId), new { key = "LOG_LEVEL", value = "debug", isBuildTime = true });
        Assert.Equal("debug", plain["value"]!.GetValue<string>());
        Assert.False(plain["isSecret"]!.GetValue<bool>());
        Assert.Null(plain["secretId"]);
        Assert.True(plain["isBuildTime"]!.GetValue<bool>());
        Assert.True(plain["isRuntime"]!.GetValue<bool>());
        Assert.Equal(appId, plain["workloadId"]!.GetValue<string>());

        var backed = await tenant.Developer.CreateAsync(Url(appId), new { key = "DATABASE_PASSWORD", secretId = secret.Id() });
        Assert.True(backed["isSecret"]!.GetValue<bool>());
        Assert.Equal("********", backed["value"]!.GetValue<string>());
        Assert.Equal(secret.Id(), backed["secretId"]!.GetValue<string>());
        Assert.Equal("DB_PASS", backed["secretName"]!.GetValue<string>());
        Assert.Equal(1, backed["secretVersion"]!.GetValue<int>());

        var list = (await tenant.Viewer.GetJsonAsync(Url(appId)))["items"]!.AsArray();
        Assert.Equal(["DATABASE_PASSWORD", "LOG_LEVEL"], list.Select(v => v!["key"]!.GetValue<string>()));
        Assert.DoesNotContain("hunter2-marker", list.ToJsonString());

        var get = await tenant.Viewer.GetAsync(Url(appId, "/" + plain.Id()));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var etag = get.Headers.ETag!.Tag;

        var patched = await tenant.Developer.PatchAsync(Url(appId, "/" + plain.Id()), new { value = "info", key = "LOG", isRuntime = false }, etag);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var body = await patched.ReadAsync();
        Assert.Equal("LOG", body["key"]!.GetValue<string>());
        Assert.Equal("info", body["value"]!.GetValue<string>());
        Assert.False(body["isRuntime"]!.GetValue<bool>());
        await (await tenant.Developer.PatchAsync(Url(appId, "/" + plain.Id()), new { value = "x" }, etag)).AssertProblemAsync(412, "precondition.failed");

        // switch a plain variable to a secret reference and back
        var toSecret = await tenant.Developer.PatchOkAsync(Url(appId, "/" + plain.Id()), new { secretId = secret.Id() });
        Assert.True(toSecret["isSecret"]!.GetValue<bool>());
        Assert.Equal("********", toSecret["value"]!.GetValue<string>());
        var toPlain = await tenant.Developer.PatchOkAsync(Url(appId, "/" + plain.Id()), new { value = "plain-again" });
        Assert.False(toPlain["isSecret"]!.GetValue<bool>());
        Assert.Equal("plain-again", toPlain["value"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync(Url(appId, "/" + plain.Id()))).StatusCode);
        await (await tenant.Viewer.GetAsync(Url(appId, "/" + plain.Id()))).AssertProblemAsync(404, "env_var.not_found");
        var actions = (await tenant.AuditAsync()).Where(e => e.Action.StartsWith("env_var.")).Select(e => e.Action).ToList();
        Assert.Equal(2, actions.Count(x => x == "env_var.created"));
        Assert.Equal(3, actions.Count(x => x == "env_var.updated"));
        Assert.Single(actions, "env_var.deleted");
        Assert.All((await tenant.AuditAsync()).Where(e => e.Action.StartsWith("env_var.")), e => Assert.DoesNotContain("plain-again", e.MetadataJson));
    }

    [RequiresDatabaseFact]
    public async Task Validation_AndDuplicates()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        var secret = await tenant.CreateSecretAsync();
        var foreign = await other.CreateSecretAsync();

        var errors = await (await tenant.Developer.PostAsync(Url(appId), new { key = "1 BAD-KEY", value = "x" })).ValidationErrorsAsync();
        Assert.Contains(("/key", "pattern"), errors);
        Assert.Contains(("/key", "required"), await (await tenant.Developer.PostAsync(Url(appId), new { value = "x" })).ValidationErrorsAsync());
        Assert.Contains(("/value", "required"), await (await tenant.Developer.PostAsync(Url(appId), new { key = "A" })).ValidationErrorsAsync());
        Assert.Contains(("/secretId", "not_unique"), await (await tenant.Developer.PostAsync(Url(appId), new { key = "A", value = "x", secretId = secret.Id() })).ValidationErrorsAsync());
        Assert.Contains(("/isRuntime", "invalid"), await (await tenant.Developer.PostAsync(Url(appId), new { key = "A", value = "x", isBuildTime = false, isRuntime = false })).ValidationErrorsAsync());
        Assert.Contains(("/value", "too_long"), await (await tenant.Developer.PostAsync(Url(appId), new { key = "A", value = new string('v', 64 * 1024 + 1) })).ValidationErrorsAsync());
        Assert.Contains(("/secretId", "not_found"), await (await tenant.Developer.PostAsync(Url(appId), new { key = "A", secretId = Guid.NewGuid() })).ValidationErrorsAsync());
        Assert.Contains(("/secretId", "not_found"), await (await tenant.Developer.PostAsync(Url(appId), new { key = "A", secretId = foreign.Id() })).ValidationErrorsAsync());

        // A workload-scoped secret may only be used by its own workload; a project-scoped one only inside its project.
        var otherApp = await tenant.CreateApplicationAsync(envId, serverId);
        var scoped = await tenant.CreateSecretAsync("SCOPED", scope: new { workloadId = otherApp.Id() });
        Assert.Contains(("/secretId", "scope_mismatch"), await (await tenant.Developer.PostAsync(Url(appId), new { key = "A", secretId = scoped.Id() })).ValidationErrorsAsync());
        Assert.Equal(HttpStatusCode.Created, (await tenant.Developer.PostAsync(Url(otherApp.Id()), new { key = "A", secretId = scoped.Id() })).StatusCode);

        await tenant.Developer.CreateAsync(Url(appId), new { key = "DUP", value = "1" });
        await (await tenant.Developer.PostAsync(Url(appId), new { key = "DUP", value = "2" })).AssertProblemAsync(409, "env_var.already_exists");
        var second = await tenant.Developer.CreateAsync(Url(appId), new { key = "OTHER", value = "2" });
        await (await tenant.Developer.PatchAsync(Url(appId, "/" + second.Id()), new { key = "DUP" })).AssertProblemAsync(409, "env_var.already_exists");
        // The same key is fine on another application.
        await tenant.Developer.CreateAsync(Url(otherApp.Id()), new { key = "DUP", value = "3" });

        Assert.Contains(("/value", "required"), await (await tenant.Developer.PatchAsync(Url(appId, "/" + second.Id()), new { value = (string?)null })).ValidationErrorsAsync());
        Assert.Contains(("/secretId", "not_unique"), await (await tenant.Developer.PatchAsync(Url(appId, "/" + second.Id()), new { value = "a", secretId = secret.Id() })).ValidationErrorsAsync());
        Assert.Contains(("/key", "pattern"), await (await tenant.Developer.PatchAsync(Url(appId, "/" + second.Id()), new { key = "no good" })).ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task ImportExport_RoundTrips_AndExportNeverIncludesSecrets()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        var secret = await tenant.CreateSecretAsync("TOP", "super-secret-marker");
        await tenant.Developer.CreateAsync(Url(appId), new { key = "API_TOKEN", secretId = secret.Id() });

        var dotenv = "# config\nPORT=8080\nNAME=\"My App\"\nGREETING='hello $world'\nEMPTY=\nMULTI=\"line1\\nline2\"\nexport MODE=production # comment\nURL=postgres://u:p@h:5432/db?x=1#frag\n";
        var imported = await tenant.Developer.PostAsync(Url(appId, "/import"), new { content = dotenv });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var result = await imported.ReadAsync();
        Assert.Equal(["PORT", "NAME", "GREETING", "EMPTY", "MULTI", "MODE", "URL"], result["created"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Empty(result["updated"]!.AsArray());
        Assert.Empty(result["skipped"]!.AsArray());

        var exported = await ExportAsync(tenant.Viewer, Url(appId, "/export"));
        Assert.DoesNotContain("API_TOKEN", exported);
        Assert.DoesNotContain("super-secret-marker", exported);
        var (exportedValues, errors) = DotEnv.Parse(exported);
        Assert.Empty(errors);
        var map = exportedValues.ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal(7, map.Count);
        Assert.Equal("My App", map["NAME"]);
        Assert.Equal("hello $world", map["GREETING"]);
        Assert.Equal("", map["EMPTY"]);
        Assert.Equal("line1\nline2", map["MULTI"]);
        Assert.Equal("production", map["MODE"]);
        Assert.Equal("postgres://u:p@h:5432/db?x=1#frag", map["URL"]);

        // Importing the export into a fresh application reproduces the variables exactly.
        var clone = await tenant.CreateApplicationAsync(envId, serverId);
        await tenant.Developer.PostAsync(Url(clone.Id(), "/import"), new { content = exported });
        Assert.Equal(exported, await ExportAsync(tenant.Viewer, Url(clone.Id(), "/export")));
        var cloneValues = (await tenant.Viewer.GetJsonAsync(Url(clone.Id())))["items"]!.AsArray();
        Assert.Equal(7, cloneValues.Count);
        Assert.DoesNotContain(cloneValues, v => v!["isSecret"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Import_OverwriteFalse_KeepsExisting_OverwriteTrue_UpdatesPlain_ButNeverSecretBacked()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var secret = await tenant.CreateSecretAsync();
        await tenant.Developer.CreateAsync(Url(appId), new { key = "KEEP", value = "old" });
        await tenant.Developer.CreateAsync(Url(appId), new { key = "SECRET_BACKED", secretId = secret.Id() });

        var content = "KEEP=new\nSECRET_BACKED=plain-attempt\nFRESH=1\n";
        var first = await (await tenant.Developer.PostAsync(Url(appId, "/import"), new { content, overwrite = false })).ReadAsync();
        Assert.Equal(["FRESH"], first["created"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal(["KEEP", "SECRET_BACKED"], first["skipped"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Contains("KEEP=old", await ExportAsync(tenant.Viewer, Url(appId, "/export")));

        var second = await (await tenant.Developer.PostAsync(Url(appId, "/import"), new { content = content.Replace("FRESH=1", "FRESH=2"), overwrite = true })).ReadAsync();
        Assert.Empty(second["created"]!.AsArray());
        Assert.Equal(["KEEP", "FRESH"], second["updated"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal(["SECRET_BACKED"], second["skipped"]!.AsArray().Select(k => k!.GetValue<string>()));
        var exported = await ExportAsync(tenant.Viewer, Url(appId, "/export"));
        Assert.Contains("KEEP=new", exported);
        Assert.Contains("FRESH=2", exported);
        Assert.DoesNotContain("plain-attempt", exported);
        var list = (await tenant.Viewer.GetJsonAsync(Url(appId)))["items"]!.AsArray();
        Assert.True(list.Single(v => v!["key"]!.GetValue<string>() == "SECRET_BACKED")!["isSecret"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Import_InvalidContent_IsRejectedAtomically_WithLineNumbers()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();

        var response = await tenant.Developer.PostAsync(Url(appId, "/import"), new { content = "GOOD=1\nthis is not valid\n2BAD=x\nTRAIL=\"open" });
        var errors = (await response.ReadAsync())["errors"]!.AsArray();
        Assert.Equal(422, (int)response.StatusCode);
        Assert.All(errors, e => Assert.Equal("/content", e!["pointer"]!.GetValue<string>()));
        Assert.All(errors, e => Assert.Equal("dotenv.invalid_line", e!["code"]!.GetValue<string>()));
        Assert.Contains(errors, e => e!["message"]!.GetValue<string>().StartsWith("Line 2:"));
        Assert.Contains(errors, e => e!["message"]!.GetValue<string>().StartsWith("Line 3:"));
        Assert.Contains(errors, e => e!["message"]!.GetValue<string>().StartsWith("Line 4:"));
        Assert.Empty((await tenant.Viewer.GetJsonAsync(Url(appId)))["items"]!.AsArray()); // GOOD=1 was not applied

        Assert.Contains(("/content", "required"), await (await tenant.Developer.PostAsync(Url(appId, "/import"), new { })).ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task Import_AllowsBodiesUpToTenMegabytes()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();

        // ~2 MiB: over the default 1 MiB JSON limit but within the 10 MiB limit of imports.
        var builder = new StringBuilder();
        for (var i = 0; i < 400; i++) builder.Append($"BIG_{i}={new string('x', 5000)}\n");
        Assert.True(builder.Length > 1024 * 1024);
        var big = await tenant.Developer.PostAsync(Url(appId, "/import"), new { content = builder.ToString() });
        Assert.Equal(HttpStatusCode.OK, big.StatusCode);
        Assert.Equal(400, (await big.ReadAsync())["created"]!.AsArray().Count);

        // The ordinary create endpoint keeps the 1 MiB limit.
        var tooBig = await tenant.Developer.PostAsync(Url(appId), new { key = "HUGE", value = new string('y', 2 * 1024 * 1024) });
        await tooBig.AssertProblemAsync(413, "request.too_large");
    }

    [RequiresDatabaseFact]
    public async Task Services_HaveTheSameSubResource()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "cache", environmentId = envId, serverId, templateKey = "redis" });
        var url = $"/api/v1/services/{service.Id()}/env-vars";

        var response = await tenant.Developer.PostAsync(url, new { key = "EXTRA", value = "1" });
        var created = await response.ReadAsync();
        Assert.Equal($"{url}/{created.Id()}", response.Headers.Location!.ToString());
        await tenant.Developer.PostAsync(url + "/import", new { content = "IMPORTED=yes\n" });
        var exported = await ExportAsync(tenant.Viewer, url + "/export");
        Assert.Contains("EXTRA=1", exported);
        Assert.Contains("IMPORTED=yes", exported);
        Assert.DoesNotContain("REDIS_PASSWORD", exported);
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"{url}/{created.Id()}")).StatusCode);

        // An application id is not a service id and vice versa.
        await (await tenant.Viewer.GetAsync($"/api/v1/services/{Guid.NewGuid()}/env-vars")).AssertProblemAsync(404, "service.not_found");
        var app = await tenant.CreateApplicationAsync(envId, serverId);
        await (await tenant.Viewer.GetAsync($"/api/v1/services/{app.Id()}/env-vars")).AssertProblemAsync(404, "service.not_found");
        await (await tenant.Viewer.GetAsync($"/api/v1/applications/{service.Id()}/env-vars")).AssertProblemAsync(404, "application.not_found");
    }

    [RequiresDatabaseFact]
    public async Task RolesScopesAndTenantIsolation()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await a.CreateStackAsync();
        var variable = await a.Developer.CreateAsync(Url(appId), new { key = "K", value = "v" });
        var item = Url(appId, "/" + variable.Id());

        Assert.Equal(HttpStatusCode.OK, (await a.Viewer.GetAsync(Url(appId))).StatusCode);
        await (await a.Viewer.PostAsync(Url(appId), new { key = "X", value = "1" })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.PatchAsync(item, new { value = "2" })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.DeleteAsync(item)).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.PostAsync(Url(appId, "/import"), new { content = "A=1" })).AssertProblemAsync(403, "auth.forbidden");

        await (await a.Token(OrganizationRole.Owner, "read").PostAsync(Url(appId), new { key = "X", value = "1" })).AssertProblemAsync(403, "auth.insufficient_scope");
        await (await a.Token(OrganizationRole.Owner, "secrets:read").GetAsync(Url(appId))).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.OK, (await a.Token(OrganizationRole.Viewer, "read").GetAsync(Url(appId, "/export"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await a.Token(OrganizationRole.Developer, "write").PostAsync(Url(appId), new { key = "VIA_TOKEN", value = "1" })).StatusCode);

        await (await b.Owner.GetAsync(Url(appId))).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.GetAsync(item)).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.PostAsync(Url(appId), new { key = "X", value = "1" })).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.DeleteAsync(item)).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.GetAsync(Url(appId, "/export"))).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.PostAsync(Url(appId, "/import"), new { content = "A=1" })).AssertProblemAsync(404, "application.not_found");

        // A variable id under the wrong application is not found either.
        var (serverId, envId, _, _) = await a.CreateStackAsync();
        var sibling = await a.CreateApplicationAsync(envId, serverId);
        await (await a.Viewer.GetAsync(Url(sibling.Id(), "/" + variable.Id()))).AssertProblemAsync(404, "env_var.not_found");
    }

    [RequiresDatabaseFact]
    public async Task List_PaginatesAndSearches()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        await tenant.Developer.PostAsync(Url(appId, "/import"), new { content = string.Join('\n', Enumerable.Range(0, 7).Select(i => $"VAR_{i}={i}")) });

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await tenant.Viewer.GetJsonAsync(Url(appId) + "?limit=3" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            seen.AddRange(page["items"]!.AsArray().Select(v => v!["key"]!.GetValue<string>()));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"VAR_{i}"), seen);
        Assert.Single((await tenant.Viewer.GetJsonAsync(Url(appId) + "?q=var_3"))["items"]!.AsArray());
        await (await tenant.Viewer.GetAsync(Url(appId) + "?sort=value")).AssertProblemAsync(400, "validation.invalid_parameter");
    }
}
