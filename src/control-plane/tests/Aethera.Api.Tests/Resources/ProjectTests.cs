using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

[Collection(ResourcesCollection.Name)]
public sealed class ProjectTests(ResourcesFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task Create_WithoutEnvironments_AddsProduction_AndIsAudited()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Developer.PostAsync("/api/v1/projects", new { name = "My Shop" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = await response.ReadAsync();
        Assert.Equal($"/api/v1/projects/{project.Id()}", response.Headers.Location!.ToString());
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal("my-shop", project["slug"]!.GetValue<string>());
        Assert.Null(project["description"]);
        Assert.Null(project["templateKey"]);
        var environments = project["environments"]!.AsArray();
        var production = Assert.Single(environments)!;
        Assert.Equal("production", production["slug"]!.GetValue<string>());
        Assert.True(production["isProduction"]!.GetValue<bool>());
        Assert.Equal(project.Id(), production["projectId"]!.GetValue<string>());

        var audit = Assert.Single(await tenant.AuditAsync(project.Id(), "project.created"));
        Assert.Equal(tenant.Identity.UserId, audit.ActorUserId);
        Assert.Contains("my-shop", audit.MetadataJson);
    }

    [RequiresDatabaseFact]
    public async Task Create_WithEnvironments_UsesExactlyThose()
    {
        var tenant = await fixture.NewTenantAsync();
        var project = await tenant.Developer.CreateAsync("/api/v1/projects", new
        {
            name = "Multi",
            environments = new object[] { new { name = "Development" }, new { name = "Staging", slug = "stage" }, new { name = "Production" } },
        });
        Assert.Equal(["development", "stage", "production"], project["environments"]!.AsArray().Select(e => e!["slug"]!.GetValue<string>()));
        Assert.Equal([false, false, true], project["environments"]!.AsArray().Select(e => e!["isProduction"]!.GetValue<bool>()));

        var none = await tenant.Developer.CreateAsync("/api/v1/projects", new { name = "No Envs", environments = Array.Empty<object>() });
        Assert.Empty(none["environments"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task Get_List_Patch_RoundTrip_WithETags()
    {
        var tenant = await fixture.NewTenantAsync();
        var project = await tenant.CreateProjectAsync("Original");
        var id = project.Id();

        var get = await tenant.Viewer.GetAsync($"/api/v1/projects/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var etag = get.Headers.ETag!.Tag;
        Assert.Equal("Original", (await get.ReadAsync())["name"]!.GetValue<string>());

        var patch = await tenant.Developer.PatchAsync($"/api/v1/projects/{id}", new { name = "Renamed", description = "About" }, etag);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await patch.ReadAsync();
        Assert.Equal("Renamed", patched["name"]!.GetValue<string>());
        Assert.Equal("About", patched["description"]!.GetValue<string>());
        Assert.Equal("original", patched["slug"]!.GetValue<string>());
        Assert.NotEqual(etag, patch.Headers.ETag!.Tag);

        // The stale ETag is refused, the fresh one works, null clears.
        var stale = await tenant.Developer.PatchAsync($"/api/v1/projects/{id}", new { name = "Again" }, etag);
        await stale.AssertProblemAsync(412, "precondition.failed");
        var cleared = await tenant.Developer.PatchOkAsync($"/api/v1/projects/{id}", new { description = (string?)null });
        Assert.Null(cleared["description"]);

        var list = await tenant.Viewer.GetJsonAsync("/api/v1/projects");
        Assert.Equal(id, Assert.Single(list["items"]!.AsArray())!.Id());
        Assert.Null(list["nextCursor"]);

        Assert.Contains(await tenant.AuditAsync(id), e => e.Action == "project.updated");
    }

    [RequiresDatabaseFact]
    public async Task Patch_NullName_IsRejected_AndSlugCanChange()
    {
        var tenant = await fixture.NewTenantAsync();
        var project = await tenant.CreateProjectAsync("Slugged");
        var response = await tenant.Developer.PatchAsync($"/api/v1/projects/{project.Id()}", new { name = (string?)null });
        Assert.Contains(("/name", "required"), await response.ValidationErrorsAsync());

        var renamed = await tenant.Developer.PatchOkAsync($"/api/v1/projects/{project.Id()}", new { slug = "new-slug" });
        Assert.Equal("new-slug", renamed["slug"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Validation_ReportsJsonPointers()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Developer.PostAsync("/api/v1/projects", new
        {
            name = "",
            slug = "Not A Slug",
            environments = new object[] { new { name = "" }, new { name = "dup" }, new { name = "dup" } },
        });
        var errors = await response.ValidationErrorsAsync();
        Assert.Contains(("/name", "required"), errors);
        Assert.Contains(("/slug", "pattern"), errors);
        Assert.Contains(("/environments/0/name", "required"), errors);
        Assert.Contains(("/environments", "not_unique"), errors);
    }

    [RequiresDatabaseFact]
    public async Task DuplicateSlug_Is409_AndSlugIsFreedByDelete()
    {
        var tenant = await fixture.NewTenantAsync();
        var first = await tenant.CreateProjectAsync("Dup");
        var duplicate = await tenant.Developer.PostAsync("/api/v1/projects", new { name = "Other", slug = "dup" });
        await duplicate.AssertProblemAsync(409, "project.already_exists");

        // Slugs are unique per organization: another organization may use the same one.
        var other = await fixture.NewTenantAsync();
        await other.CreateProjectAsync("Dup");

        var delete = await tenant.Developer.DeleteAsync($"/api/v1/projects/{first.Id()}?confirm=dup");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await tenant.CreateProjectAsync("Dup");
    }

    [RequiresDatabaseFact]
    public async Task CrossOrganization_IsNotFound_EverywhereAndNeverListed()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var project = await a.CreateProjectAsync();
        var id = project.Id();
        var envId = project["environments"]![0]!.Id();

        await (await b.Admin.GetAsync($"/api/v1/projects/{id}")).AssertProblemAsync(404, "project.not_found");
        await (await b.Admin.PatchAsync($"/api/v1/projects/{id}", new { name = "x" })).AssertProblemAsync(404, "project.not_found");
        await (await b.Admin.DeleteAsync($"/api/v1/projects/{id}?confirm=anything")).AssertProblemAsync(404, "project.not_found");
        await (await b.Admin.GetAsync($"/api/v1/projects/{id}/environments")).AssertProblemAsync(404, "project.not_found");
        await (await b.Admin.PostAsync($"/api/v1/projects/{id}/environments", new { name = "x" })).AssertProblemAsync(404, "project.not_found");
        await (await b.Admin.GetAsync($"/api/v1/environments/{envId}")).AssertProblemAsync(404, "environment.not_found");
        await (await b.Admin.PatchAsync($"/api/v1/environments/{envId}", new { name = "x" })).AssertProblemAsync(404, "environment.not_found");
        await (await b.Admin.DeleteAsync($"/api/v1/environments/{envId}?confirm=production")).AssertProblemAsync(404, "environment.not_found");

        var list = await b.Admin.GetJsonAsync("/api/v1/projects");
        Assert.Empty(list["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task RolesAndTokenScopes()
    {
        var tenant = await fixture.NewTenantAsync();
        var project = await tenant.CreateProjectAsync();
        var id = project.Id();

        // Viewer: read yes, write no.
        Assert.Equal(HttpStatusCode.OK, (await tenant.Viewer.GetAsync($"/api/v1/projects/{id}")).StatusCode);
        await (await tenant.Viewer.PostAsync("/api/v1/projects", new { name = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await tenant.Viewer.PatchAsync($"/api/v1/projects/{id}", new { name = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await tenant.Viewer.DeleteAsync($"/api/v1/projects/{id}?confirm=x")).AssertProblemAsync(403, "auth.forbidden");

        // Token: read scope reads, cannot write; write scope writes (and reads).
        var readToken = tenant.Token(OrganizationRole.Owner, "read");
        Assert.Equal(HttpStatusCode.OK, (await readToken.GetAsync($"/api/v1/projects/{id}")).StatusCode);
        var denied = await readToken.PostAsync("/api/v1/projects", new { name = "x" });
        await denied.AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal("write", (await denied.ReadAsync())["requiredScope"]!.GetValue<string>());

        var writeToken = tenant.Token(OrganizationRole.Developer, "write");
        Assert.Equal(HttpStatusCode.Created, (await writeToken.PostAsync("/api/v1/projects", new { name = "by-token" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writeToken.GetAsync("/api/v1/projects")).StatusCode);

        // A viewer's token can never write, whatever its scopes (effective permission = scope AND role).
        await (await tenant.Token(OrganizationRole.Viewer, "write").PostAsync("/api/v1/projects", new { name = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await tenant.Token(OrganizationRole.Owner, "secrets:write").GetAsync("/api/v1/projects")).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.Created, (await tenant.Token(OrganizationRole.Developer, "admin").PostAsync("/api/v1/projects", new { name = "admin-scope" })).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Pagination_FollowsCursorsAcrossPages_WithStableOrder()
    {
        var tenant = await fixture.NewTenantAsync();
        var names = new[] { "delta", "alpha", "echo", "charlie", "bravo" };
        foreach (var name in names) await tenant.CreateProjectAsync(name);

        async Task<List<string>> Walk(string query, int limit)
        {
            var seen = new List<string>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var url = $"/api/v1/projects?limit={limit}{query}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
                var page = await tenant.Viewer.GetJsonAsync(url);
                seen.AddRange(page["items"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
                cursor = page["nextCursor"]?.GetValue<string>();
                pages++;
            }
            while (cursor is not null && pages < 10);
            Assert.Equal((int)Math.Ceiling(names.Length / (double)limit), pages);
            return seen;
        }

        Assert.Equal(names.Order(), await Walk("&sort=name", 2));
        Assert.Equal(names.OrderDescending(), await Walk("&sort=-name", 2));
        Assert.Equal(names.AsEnumerable().Reverse(), await Walk("", 2)); // default: newest first
        Assert.Equal(names.Order(), await Walk("&sort=name", 5));
        Assert.Equal(names.Order(), await Walk("&sort=name", 1));
    }

    [RequiresDatabaseFact]
    public async Task Pagination_RejectsBadInput()
    {
        var tenant = await fixture.NewTenantAsync();
        await tenant.CreateProjectAsync("one");
        await tenant.CreateProjectAsync("two");

        await (await tenant.Viewer.GetAsync("/api/v1/projects?limit=0")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await tenant.Viewer.GetAsync("/api/v1/projects?limit=201")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await tenant.Viewer.GetAsync("/api/v1/projects?sort=password")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await tenant.Viewer.GetAsync("/api/v1/projects?bogus=1")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await tenant.Viewer.GetAsync("/api/v1/projects?cursor=garbage")).AssertProblemAsync(400, "pagination.invalid_cursor");

        // A cursor only works with the sort and filters that produced it.
        var page = await tenant.Viewer.GetJsonAsync("/api/v1/projects?limit=1&sort=name");
        var cursor = Uri.EscapeDataString(page["nextCursor"]!.GetValue<string>());
        await (await tenant.Viewer.GetAsync($"/api/v1/projects?limit=1&sort=-name&cursor={cursor}")).AssertProblemAsync(400, "pagination.invalid_cursor");
        await (await tenant.Viewer.GetAsync($"/api/v1/projects?limit=1&sort=name&q=one&cursor={cursor}")).AssertProblemAsync(400, "pagination.invalid_cursor");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Viewer.GetAsync($"/api/v1/projects?limit=1&sort=name&cursor={cursor}")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Search_FiltersByNameOrSlug()
    {
        var tenant = await fixture.NewTenantAsync();
        await tenant.CreateProjectAsync("Billing Service");
        await tenant.CreateProjectAsync("Website");
        var found = await tenant.Viewer.GetJsonAsync("/api/v1/projects?q=BILL");
        Assert.Equal("Billing Service", Assert.Single(found["items"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Empty((await tenant.Viewer.GetJsonAsync("/api/v1/projects?q=100%25"))["items"]!.AsArray()); // % is not a wildcard
    }

    [RequiresDatabaseFact]
    public async Task Delete_RequiresConfirmation()
    {
        var tenant = await fixture.NewTenantAsync();
        var project = await tenant.CreateProjectAsync("Confirm Me");
        var url = $"/api/v1/projects/{project.Id()}";

        var missing = await tenant.Developer.DeleteAsync(url);
        await missing.AssertProblemAsync(428, "confirmation.required");
        Assert.Equal("confirm-me", (await missing.ReadAsync())["expectedConfirmation"]!.GetValue<string>());
        await (await tenant.Developer.DeleteAsync(url + "?confirm=wrong")).AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Developer.GetAsync(url)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync(url + "?confirm=confirm-me")).StatusCode);
        await (await tenant.Developer.GetAsync(url)).AssertProblemAsync(404, "project.not_found");
        Assert.Contains(await tenant.AuditAsync(project.Id()), e => e.Action == "project.deleted");
    }

    [RequiresDatabaseFact]
    public async Task Delete_NotEmpty_Is409_UnlessCascade()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, environmentId, projectId, applicationId) = await tenant.CreateStackAsync();
        var slug = (await tenant.Viewer.GetJsonAsync($"/api/v1/projects/{projectId}"))["slug"]!.GetValue<string>();

        await (await tenant.Developer.DeleteAsync($"/api/v1/projects/{projectId}?confirm={slug}")).AssertProblemAsync(409, "project.not_empty");
        await (await tenant.Developer.DeleteAsync($"/api/v1/environments/{environmentId}?confirm=production")).AssertProblemAsync(409, "environment.not_empty");

        var cascade = await tenant.Developer.DeleteAsync($"/api/v1/projects/{projectId}?confirm={slug}&cascade=true");
        Assert.Equal(HttpStatusCode.NoContent, cascade.StatusCode);
        await (await tenant.Viewer.GetAsync($"/api/v1/applications/{applicationId}")).AssertProblemAsync(404, "application.not_found");
        await (await tenant.Viewer.GetAsync($"/api/v1/environments/{environmentId}")).AssertProblemAsync(404, "environment.not_found");

        // The server is no longer in use, so it can go too.
        var serverName = (await tenant.Viewer.GetJsonAsync($"/api/v1/servers/{serverId}"))["name"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync($"/api/v1/servers/{serverId}?confirm={serverName}")).StatusCode);
    }

    // ---- environments -----------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Environments_Crud()
    {
        var tenant = await fixture.NewTenantAsync();
        var project = await tenant.CreateProjectAsync();
        var projectId = project.Id();

        var staging = await tenant.Developer.CreateAsync($"/api/v1/projects/{projectId}/environments", new { name = "Staging" });
        Assert.Equal("staging", staging["slug"]!.GetValue<string>());
        Assert.False(staging["isProduction"]!.GetValue<bool>());
        await (await tenant.Developer.PostAsync($"/api/v1/projects/{projectId}/environments", new { name = "Again", slug = "staging" }))
            .AssertProblemAsync(409, "environment.already_exists");

        var list = await tenant.Viewer.GetJsonAsync($"/api/v1/projects/{projectId}/environments?sort=slug");
        Assert.Equal(["production", "staging"], list["items"]!.AsArray().Select(e => e!["slug"]!.GetValue<string>()));

        var patched = await tenant.Developer.PatchOkAsync($"/api/v1/environments/{staging.Id()}", new { name = "QA", slug = "qa", isProduction = true, description = "d" });
        Assert.Equal("qa", patched["slug"]!.GetValue<string>());
        Assert.True(patched["isProduction"]!.GetValue<bool>());
        await (await tenant.Developer.PatchAsync($"/api/v1/environments/{staging.Id()}", new { slug = "production" })).AssertProblemAsync(409, "environment.already_exists");

        await (await tenant.Developer.DeleteAsync($"/api/v1/environments/{staging.Id()}")).AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/environments/{staging.Id()}?confirm=qa")).StatusCode);
        await (await tenant.Viewer.GetAsync($"/api/v1/environments/{staging.Id()}")).AssertProblemAsync(404, "environment.not_found");

        // The slug is free again.
        await tenant.Developer.CreateAsync($"/api/v1/projects/{projectId}/environments", new { name = "QA" });
    }

    // ---- templates --------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task ProjectTemplates_AreListed()
    {
        var tenant = await fixture.NewTenantAsync();
        var templates = (await tenant.Viewer.GetJsonAsync("/api/v1/project-templates")).AsArray();
        Assert.Equal(["empty", "web-app", "api", "storage", "static-site"], templates.Select(t => t!["key"]!.GetValue<string>()));
        var web = templates.Single(t => t!["key"]!.GetValue<string>() == "web-app")!;
        Assert.Equal(["frontend", "backend", "postgres", "redis"], web["workloads"]!.AsArray().Select(w => w!["slug"]!.GetValue<string>()));
    }

    [RequiresDatabaseFact]
    public async Task FromTemplate_WebApp_CreatesEnvironmentsApplicationsAndServicesWithSecrets()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();

        var response = await tenant.Developer.PostAsync("/api/v1/projects/from-template",
            new { templateKey = "web-app", name = "Storefront", serverId = server.Id() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadAsync();
        var project = body["project"]!;
        Assert.Equal("web-app", project["templateKey"]!.GetValue<string>());
        Assert.Equal("production", Assert.Single(project["environments"]!.AsArray())!["slug"]!.GetValue<string>());

        var workloads = body["workloads"]!.AsArray();
        Assert.Equal(["frontend", "backend", "postgres", "redis"], workloads.Select(w => w!["slug"]!.GetValue<string>()));
        Assert.Equal(["application", "application", "service", "service"], workloads.Select(w => w!["kind"]!.GetValue<string>()));

        // The applications are unconfigured placeholders on the chosen server; the services are real.
        var frontend = await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{workloads[0]!.Id()}");
        Assert.Equal("git", frontend["sourceKind"]!.GetValue<string>());
        Assert.Null(frontend["gitSource"]);
        Assert.Equal(server.Id(), frontend["serverId"]!.GetValue<string>());
        Assert.Equal(3000, frontend["runtime"]!["ports"]![0]!["containerPort"]!.GetValue<int>());

        var postgres = await tenant.Viewer.GetJsonAsync($"/api/v1/services/{workloads[2]!.Id()}");
        Assert.Equal("postgres", postgres["templateKey"]!.GetValue<string>());
        var envVars = await tenant.Viewer.GetJsonAsync($"/api/v1/services/{postgres.Id()}/env-vars");
        var password = envVars["items"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == "POSTGRES_PASSWORD")!;
        Assert.True(password["isSecret"]!.GetValue<bool>());
        Assert.Equal("********", password["value"]!.GetValue<string>());

        var secrets = await tenant.Owner.GetJsonAsync($"/api/v1/secrets?workloadId={postgres.Id()}");
        Assert.Single(secrets["items"]!.AsArray());
        var redisSecrets = await tenant.Owner.GetJsonAsync($"/api/v1/secrets?workloadId={workloads[3]!.Id()}");
        Assert.Single(redisSecrets["items"]!.AsArray());

        Assert.Contains(await tenant.AuditAsync(project.Id()), e => e.Action == "project.created" && e.MetadataJson.Contains("web-app"));
    }

    [RequiresDatabaseFact]
    public async Task FromTemplate_Empty_NeedsNoServer_AndOthersDo()
    {
        var tenant = await fixture.NewTenantAsync();
        var empty = await tenant.Developer.PostAsync("/api/v1/projects/from-template", new { templateKey = "empty", name = "Blank" });
        Assert.Equal(HttpStatusCode.Created, empty.StatusCode);
        Assert.Empty((await empty.ReadAsync())["workloads"]!.AsArray());

        var noServer = await tenant.Developer.PostAsync("/api/v1/projects/from-template", new { templateKey = "api", name = "Needs Server" });
        Assert.Contains(("/serverId", "required"), await noServer.ValidationErrorsAsync());

        var unknownServer = await tenant.Developer.PostAsync("/api/v1/projects/from-template",
            new { templateKey = "api", name = "Needs Server", serverId = Guid.NewGuid() });
        Assert.Contains(("/serverId", "not_found"), await unknownServer.ValidationErrorsAsync());

        var otherTenant = await fixture.NewTenantAsync();
        var foreignServer = await otherTenant.CreateServerAsync();
        var foreign = await tenant.Developer.PostAsync("/api/v1/projects/from-template",
            new { templateKey = "storage", name = "Foreign", serverId = foreignServer.Id() });
        Assert.Contains(("/serverId", "not_found"), await foreign.ValidationErrorsAsync());

        var unknownTemplate = await tenant.Developer.PostAsync("/api/v1/projects/from-template", new { templateKey = "nope", name = "x" });
        Assert.Contains(("/templateKey", "not_found"), await unknownTemplate.ValidationErrorsAsync());

        var duplicate = await tenant.Developer.PostAsync("/api/v1/projects/from-template", new { templateKey = "empty", name = "Blank" });
        await duplicate.AssertProblemAsync(409, "project.already_exists");
    }

    [RequiresDatabaseFact]
    public async Task FromTemplate_OtherTemplates_CreateTheirWorkloads()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        foreach (var (key, expected) in new[]
        {
            ("api", new[] { "api", "postgres" }), ("storage", new[] { "postgres", "minio" }), ("static-site", new[] { "website" }),
        })
        {
            var body = await (await tenant.Developer.PostAsync("/api/v1/projects/from-template", new { templateKey = key, name = key, serverId = server.Id() })).ReadAsync();
            Assert.Equal(expected, body["workloads"]!.AsArray().Select(w => w!["slug"]!.GetValue<string>()));
        }
    }
}
