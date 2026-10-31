using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Aethera.Api.Tests.Placement.PlacementTestSupport;

namespace Aethera.Api.Tests.Placement;

/// <summary>The settings R2.1 adds to servers, registries and workloads, and moving a workload before anything runs (ADR 0012).</summary>
[Collection(ResourcesCollection.Name)]
public sealed class PlacementSettingsTests(ResourcesFixture fixture)
{
    private static async Task<JsonNode> CreateRegistryAsync(Tenant t, string? ns = null, string? name = null) =>
        await t.Admin.CreateAsync("/api/v1/registries", new { name = name ?? Tenant.Unique("reg"), url = "ghcr.io", username = "mihaim21", password = "ghp_secret", @namespace = ns });

    private async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    // ---- registries ------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_registry_namespace_is_stored_in_lower_case_edited_and_cleared()
    {
        var t = await fixture.NewTenantAsync();

        var registry = await CreateRegistryAsync(t, ns: "MihaiM21");
        Assert.Equal("mihaim21", registry["namespace"]!.GetValue<string>());
        Assert.DoesNotContain("ghp_secret", registry.ToJsonString());

        var patched = await t.Admin.PatchOkAsync($"/api/v1/registries/{registry.Id()}", new { @namespace = "Team/Apps" });
        Assert.Equal("team/apps", patched["namespace"]!.GetValue<string>());

        var cleared = await t.Admin.PatchOkAsync($"/api/v1/registries/{registry.Id()}", new { @namespace = (string?)null });
        Assert.Null(cleared["namespace"]);
        Assert.Null((await CreateRegistryAsync(t))["namespace"]);
    }

    [RequiresDatabaseFact]
    public async Task A_registry_namespace_must_look_like_a_repository_path()
    {
        var t = await fixture.NewTenantAsync();
        foreach (var bad in new[] { "has space", "-leading", "a//b", "UPPER:colon", "semi;colon" })
        {
            var response = await t.Admin.PostAsync("/api/v1/registries", new { name = Tenant.Unique("reg"), url = "ghcr.io", @namespace = bad });
            Assert.Contains(("/namespace", "pattern"), await response.ValidationErrorsAsync());
        }
    }

    [RequiresDatabaseFact]
    public async Task A_registry_that_is_a_build_registry_cannot_be_deleted()
    {
        var t = await fixture.NewTenantAsync();
        var registry = await CreateRegistryAsync(t);
        var server = await t.SeedServerAsync("builder");
        await t.Admin.PatchOkAsync($"/api/v1/servers/{server}", new { buildRegistryId = registry.Id() });

        await (await t.Admin.DeleteAsync($"/api/v1/registries/{registry.Id()}?confirm={registry["name"]!.GetValue<string>()}")).AssertProblemAsync(409, "registry.in_use");

        await t.Admin.PatchOkAsync($"/api/v1/servers/{server}", new { buildRegistryId = (string?)null });
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/registries/{registry.Id()}?confirm={registry["name"]!.GetValue<string>()}")).StatusCode);
    }

    // ---- servers ---------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_servers_build_quota_and_registry_round_trip()
    {
        var t = await fixture.NewTenantAsync();
        var registry = await CreateRegistryAsync(t, "mihaim21");
        var server = await t.SeedServerAsync("builder", [ServerRole.Build]);

        var patched = await t.Admin.PatchOkAsync($"/api/v1/servers/{server}", new { buildCpuLimit = 2.5, buildMemoryLimitBytes = 2 * Gib, buildRegistryId = registry.Id() });
        Assert.Equal(2.5, patched["buildCpuLimit"]!.GetValue<double>());
        Assert.Equal(2 * Gib, patched["buildMemoryLimitBytes"]!.GetValue<long>());
        Assert.Equal(registry.Id(), patched["buildRegistryId"]!.GetValue<string>());

        var read = await t.Viewer.GetJsonAsync($"/api/v1/servers/{server}");
        Assert.Equal(2.5, read["buildCpuLimit"]!.GetValue<double>());

        var cleared = await t.Admin.PatchOkAsync($"/api/v1/servers/{server}", new { buildCpuLimit = (double?)null, buildMemoryLimitBytes = (long?)null, buildRegistryId = (string?)null });
        Assert.Null(cleared["buildCpuLimit"]);
        Assert.Null(cleared["buildMemoryLimitBytes"]);
        Assert.Null(cleared["buildRegistryId"]);
        Assert.Contains(await t.AuditAsync(server.ToString(), "server.updated"), e => e.MetadataJson.Contains("buildRegistryId"));
    }

    [RequiresDatabaseFact]
    public async Task A_server_can_be_created_with_a_build_quota_and_registry()
    {
        var t = await fixture.NewTenantAsync();
        var registry = await CreateRegistryAsync(t);

        var server = await t.Admin.CreateAsync("/api/v1/servers", new
        {
            name = Tenant.Unique("srv"), host = Tenant.Unique("h") + ".example.com", roles = new[] { "build" },
            buildCpuLimit = 1, buildMemoryLimitBytes = 512L << 20, buildRegistryId = registry.Id(),
        });

        Assert.Equal(1, server["buildCpuLimit"]!.GetValue<double>());
        Assert.Equal(512L << 20, server["buildMemoryLimitBytes"]!.GetValue<long>());
        Assert.Equal(registry.Id(), server["buildRegistryId"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task The_build_registry_must_be_a_registry_of_the_same_organization()
    {
        var t = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var foreign = await CreateRegistryAsync(other);
        var server = await t.SeedServerAsync("builder");

        var response = await t.Admin.PatchAsync($"/api/v1/servers/{server}", new { buildRegistryId = foreign.Id() });
        Assert.Contains(("/buildRegistryId", "not_found"), await response.ValidationErrorsAsync());

        var create = await t.Admin.PostAsync("/api/v1/servers", new { name = Tenant.Unique("s"), host = "h.example.com", buildRegistryId = foreign.Id() });
        Assert.Contains(("/buildRegistryId", "not_found"), await create.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task The_build_quota_is_validated()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.SeedServerAsync("builder");

        Assert.Contains(("/buildCpuLimit", "range"), await (await t.Admin.PatchAsync($"/api/v1/servers/{server}", new { buildCpuLimit = 0 })).ValidationErrorsAsync());
        Assert.Contains(("/buildMemoryLimitBytes", "range"), await (await t.Admin.PatchAsync($"/api/v1/servers/{server}", new { buildMemoryLimitBytes = 1000 })).ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task Only_administrators_edit_the_settings_of_a_server()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.SeedServerAsync("builder");

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.PatchAsync($"/api/v1/servers/{server}", new { buildCpuLimit = 1 })).StatusCode);
    }

    // ---- applications and services ---------------------------------------------------------------------------------------------------

    private static object GitApp(string env, Guid server, object? extra = null) => new
    {
        name = Tenant.Unique("api"), environmentId = env, serverId = server, sourceKind = "dockerfile", autoDomain = false,
        gitSource = new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" },
    };

    [RequiresDatabaseFact]
    public async Task An_application_can_name_a_build_server_and_go_back_to_building_where_it_runs()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var registry = await CreateRegistryAsync(t, "mihaim21");
        var run = await t.SeedServerAsync("runner", [ServerRole.Worker]);
        var build = await t.SeedServerAsync("builder", [ServerRole.Build]);
        await t.Admin.PatchOkAsync($"/api/v1/servers/{build}", new { buildRegistryId = registry.Id() });

        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("api"), environmentId = env, serverId = run, buildServerId = build, sourceKind = "dockerfile", autoDomain = false,
            gitSource = new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" },
        });
        Assert.Equal(build.ToString(), app["buildServerId"]!.GetValue<string>());
        Assert.Empty(app["warnings"]!.AsArray());

        var cleared = await t.Developer.PatchOkAsync($"/api/v1/applications/{app.Id()}", new { buildServerId = (string?)null });
        Assert.Null(cleared["buildServerId"]);

        var set = await t.Developer.PatchOkAsync($"/api/v1/applications/{app.Id()}", new { buildServerId = build });
        Assert.Equal(build.ToString(), set["buildServerId"]!.GetValue<string>());
        Assert.Equal(build.ToString(), (await t.Viewer.GetJsonAsync($"/api/v1/applications/{app.Id()}"))["buildServerId"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_build_server_without_the_build_role_or_a_registry_is_accepted_with_warnings()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var run = await t.SeedServerAsync("runner", [ServerRole.Worker]);
        var worker = await t.SeedServerAsync("busy-worker", [ServerRole.Worker]);
        var open = await t.SeedServerAsync("open-box"); // no roles: accepts everything

        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("api"), environmentId = env, serverId = run, buildServerId = worker, sourceKind = "git", autoDomain = false,
            gitSource = new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" },
        });
        var warnings = app["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("busy-worker") && w.Contains("build"));
        Assert.Contains(warnings, w => w.Contains("no build registry"));

        // A build server without roles accepts builds, so only the registry is missing.
        var patched = await t.Developer.PatchOkAsync($"/api/v1/applications/{app.Id()}", new { buildServerId = open });
        var patchedWarnings = patched["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Single(patchedWarnings);
        Assert.Contains("no build registry", patchedWarnings[0]);

        // Building on the server it runs on needs no registry.
        var local = await t.Developer.PatchOkAsync($"/api/v1/applications/{app.Id()}", new { buildServerId = run });
        Assert.Contains(local["warnings"]!.AsArray().Select(w => w!.GetValue<string>()), w => w.Contains("runner") && w.Contains("build"));
        Assert.DoesNotContain(local["warnings"]!.AsArray().Select(w => w!.GetValue<string>()), w => w.Contains("registry"));
    }

    [RequiresDatabaseFact]
    public async Task A_build_server_must_exist_be_active_and_make_sense_for_the_source()
    {
        var t = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var run = await t.SeedServerAsync("runner");
        var pending = await t.SeedServerAsync("pending", lifecycle: ServerLifecycle.Pending);
        var foreign = await other.SeedServerAsync("foreign");
        var git = new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" };

        async Task<IReadOnlyList<(string? Location, string Code)>> Errors(object body) =>
            await (await t.Developer.PostAsync("/api/v1/applications", body)).ValidationErrorsAsync();

        Assert.Contains(("/buildServerId", "not_active"),
            await Errors(new { name = "a", environmentId = env, serverId = run, buildServerId = pending, sourceKind = "git", gitSource = git }));
        Assert.Contains(("/buildServerId", "not_found"),
            await Errors(new { name = "a", environmentId = env, serverId = run, buildServerId = foreign, sourceKind = "git", gitSource = git }));
        Assert.Contains(("BuildServerId", "not_applicable").ToPointer(),
            (await Errors(new { name = "a", environmentId = env, serverId = run, buildServerId = run, sourceKind = "dockerImage", image = new { image = "nginx" } })));

        var image = await t.CreateApplicationAsync(env, run.ToString());
        await (await t.Developer.PatchAsync($"/api/v1/applications/{image.Id()}", new { buildServerId = run })).AssertProblemAsync(422, "validation.failed");
    }

    [RequiresDatabaseFact]
    public async Task Priority_is_set_on_applications_and_services_and_defaults_to_normal()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var server = await t.SeedServerAsync("box");

        var app = await t.CreateApplicationAsync(env, server.ToString());
        Assert.Equal("normal", app["runtime"]!["priority"]!.GetValue<string>());

        var high = await t.Developer.PatchOkAsync($"/api/v1/applications/{app.Id()}", new { runtime = new { priority = "high" } });
        Assert.Equal("high", high["runtime"]!["priority"]!.GetValue<string>());
        Assert.Equal("high", (await t.Viewer.GetJsonAsync($"/api/v1/applications/{app.Id()}"))["runtime"]!["priority"]!.GetValue<string>());
        var reset = await t.Developer.PatchOkAsync($"/api/v1/applications/{app.Id()}", new { runtime = new { priority = (string?)null } });
        Assert.Equal("normal", reset["runtime"]!["priority"]!.GetValue<string>());

        var created = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("low"), environmentId = env, serverId = server, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" }, autoDomain = false,
            runtime = new { priority = "low" },
        });
        Assert.Equal("low", created["runtime"]!["priority"]!.GetValue<string>());

        var service = await t.Developer.CreateAsync("/api/v1/services", new
        {
            name = Tenant.Unique("pg"), environmentId = env, serverId = server, templateKey = "postgres", autoDomain = false, runtime = new { priority = "high" },
        });
        Assert.Equal("high", service["runtime"]!["priority"]!.GetValue<string>());
        var lowered = await t.Developer.PatchOkAsync($"/api/v1/services/{service.Id()}", new { runtime = new { priority = "low" } });
        Assert.Equal("low", lowered["runtime"]!["priority"]!.GetValue<string>());

        var invalid = await t.Developer.PatchAsync($"/api/v1/applications/{app.Id()}", new { runtime = new { priority = "urgent" } });
        Assert.Contains(("/runtime/priority", "invalid_enum"), await invalid.ValidationErrorsAsync());
    }

    // ---- moving, before anything runs ------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_workload_that_never_ran_just_changes_server_and_its_domains_follow()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("a", publicIp: "203.0.113.10");
        var b = await t.SeedServerAsync("b", publicIp: "198.51.100.20");
        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("web"), environmentId = env, serverId = a, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" },
        });

        var response = await t.Developer.PostAsync($"/api/v1/applications/{app.Id()}/move", new { serverId = b });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadAsync();
        Assert.Equal(a.ToString(), body["fromServerId"]!.GetValue<string>());
        Assert.Equal(b.ToString(), body["toServerId"]!.GetValue<string>());
        Assert.Null(body["deploymentId"]);
        Assert.False(body["dataStaysBehind"]!.GetValue<bool>());
        Assert.Equal(b.ToString(), (await t.Viewer.GetJsonAsync($"/api/v1/applications/{app.Id()}"))["serverId"]!.GetValue<string>());
        var domain = await WithDbAsync(db => db.Domains.AsNoTracking().SingleAsync(d => d.WorkloadId == Guid.Parse(app.Id())));
        Assert.Equal(b, domain.ServerId);
        Assert.EndsWith(".198-51-100-20.sslip.io", domain.Hostname); // the automatic domain is named after its server
        Assert.Contains(await t.AuditAsync(app.Id(), "application.moved"), e => e.Action == "application.moved");
    }

    [RequiresDatabaseFact]
    public async Task A_workload_with_volumes_needs_confirmation_and_the_answer_says_the_data_stays()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("old-box");
        var b = await t.SeedServerAsync("new-box");
        var service = await t.Developer.CreateAsync("/api/v1/services", new { name = Tenant.Unique("pg"), environmentId = env, serverId = a, templateKey = "postgres", autoDomain = false });

        var refused = await t.Developer.PostAsync($"/api/v1/services/{service.Id()}/move", new { serverId = b });
        Assert.Equal(HttpStatusCode.PreconditionRequired, refused.StatusCode);
        var problem = await refused.ReadAsync();
        Assert.Equal("workload.move_confirmation_required", problem["code"]!.GetValue<string>());
        Assert.Contains("old-box", problem["detail"]!.GetValue<string>());
        Assert.Contains("Back it up", problem["detail"]!.GetValue<string>());
        Assert.NotEmpty(problem["volumes"]!.AsArray());
        Assert.Equal(a.ToString(), (await t.Viewer.GetJsonAsync($"/api/v1/services/{service.Id()}"))["serverId"]!.GetValue<string>());

        var moved = await t.Developer.PostAsync($"/api/v1/services/{service.Id()}/move", new { serverId = b, confirm = true });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var body = await moved.ReadAsync();
        Assert.True(body["dataStaysBehind"]!.GetValue<bool>());
        Assert.Contains("stay on old-box", body["message"]!.GetValue<string>());
        Assert.Equal(b.ToString(), (await t.Viewer.GetJsonAsync($"/api/v1/services/{service.Id()}"))["serverId"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_move_to_a_server_that_cannot_take_it_is_refused()
    {
        var t = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("a");
        var pending = await t.SeedServerAsync("pending", lifecycle: ServerLifecycle.Pending);
        var away = await t.SeedServerAsync("away", connected: false);
        var foreign = await other.SeedServerAsync("foreign");
        var app = await t.CreateApplicationAsync(env, a.ToString());
        var url = $"/api/v1/applications/{app.Id()}/move";

        Assert.Contains(("/serverId", "same_server"), await (await t.Developer.PostAsync(url, new { serverId = a })).ValidationErrorsAsync());
        Assert.Contains(("/serverId", "not_active"), await (await t.Developer.PostAsync(url, new { serverId = pending })).ValidationErrorsAsync());
        Assert.Contains(("/serverId", "not_connected"), await (await t.Developer.PostAsync(url, new { serverId = away })).ValidationErrorsAsync());
        Assert.Contains(("/serverId", "not_found"), await (await t.Developer.PostAsync(url, new { serverId = foreign })).ValidationErrorsAsync());
        Assert.Contains(("/serverId", "required"), await (await t.Developer.PostAsync(url, new { })).ValidationErrorsAsync());
        Assert.Equal(a.ToString(), (await t.Viewer.GetJsonAsync($"/api/v1/applications/{app.Id()}"))["serverId"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_move_is_warned_about_roles_and_room_and_checked_for_permission_and_tenancy()
    {
        var t = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("a");
        var storage = await t.SeedServerAsync("storage-only", [ServerRole.Storage], memoryGib: 1);
        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("web"), environmentId = env, serverId = a, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" }, autoDomain = false,
            runtime = new { resources = new { memoryReservationBytes = 2 * Gib } },
        });

        var body = await (await t.Developer.PostAsync($"/api/v1/applications/{app.Id()}/move", new { serverId = storage })).ReadAsync();

        var warnings = body["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("storage-only") && w.Contains("worker"));
        Assert.Contains(warnings, w => w.Contains("over-committed"));

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsync($"/api/v1/applications/{app.Id()}/move", new { serverId = a })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Developer.PostAsync($"/api/v1/applications/{app.Id()}/move", new { serverId = a })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Developer.PostAsync($"/api/v1/services/{app.Id()}/move", new { serverId = a })).StatusCode); // an application is not a service
    }
}

internal static class ErrorPointer
{
    /// <summary>The validator reports a property as the JSON pointer of its camelCase name; this keeps the intent of an assertion readable.</summary>
    public static (string? Location, string Code) ToPointer(this (string Property, string Code) error) =>
        ("/" + char.ToLowerInvariant(error.Property[0]) + error.Property[1..], error.Code);
}
