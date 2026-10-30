using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Aethera.Api.Tests.Placement.PlacementTestSupport;

namespace Aethera.Api.Tests.Placement;

/// <summary><c>/placement/suggest</c>, <c>/servers/capacity</c> and creating workloads without a server (ADR 0012), against a real database.</summary>
[Collection(ResourcesCollection.Name)]
public sealed class PlacementApiTests(ResourcesFixture fixture)
{
    private static string[] Ids(JsonNode body) => body["candidates"]!.AsArray().Select(c => c!["serverId"]!.GetValue<string>()).ToArray();

    private static JsonNode Candidate(JsonNode body, Guid server) => body["candidates"]!.AsArray().Single(c => c!["serverId"]!.GetValue<string>() == server.ToString())!;

    // ---- /placement/suggest ----------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Suggest_ranks_the_servers_and_explains_each_one()
    {
        var t = await fixture.NewTenantAsync();
        var big = await t.SeedServerAsync("big", [ServerRole.Worker], memoryGib: 16);
        var small = await t.SeedServerAsync("small", [ServerRole.Worker], memoryGib: 4);
        var pending = await t.SeedServerAsync("pending", lifecycle: ServerLifecycle.Pending);

        var body = await t.Viewer.GetJsonAsync(Suggest("application", memoryBytes: Gib));

        Assert.Equal("application", body["kind"]!.GetValue<string>());
        Assert.Equal(big.ToString(), body["suggestedServerId"]!.GetValue<string>());
        Assert.Equal([big.ToString(), small.ToString(), pending.ToString()], Ids(body));
        Assert.True(Candidate(body, big)["eligible"]!.GetValue<bool>());
        Assert.False(Candidate(body, pending)["eligible"]!.GetValue<bool>());
        Assert.Contains("not active", Candidate(body, pending)["reasons"]![0]!.GetValue<string>());
        Assert.Equal(16 * Gib, Candidate(body, big)["memoryFreeBytes"]!.GetValue<long>());
        Assert.All(body["candidates"]!.AsArray(), c => Assert.NotEmpty(c!["reasons"]!.AsArray()));
        Assert.Null(body["message"]);
    }

    [RequiresDatabaseFact]
    public async Task Suggest_subtracts_what_is_reserved_on_each_server()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("a", memoryGib: 8);
        var b = await t.SeedServerAsync("b", memoryGib: 6);
        await t.ReservedApplicationAsync(env, a, memoryGib: 5);

        var body = await t.Viewer.GetJsonAsync(Suggest("application"));

        Assert.Equal(b.ToString(), body["suggestedServerId"]!.GetValue<string>()); // 6 GiB left beats 3 GiB
        Assert.Equal(3 * Gib, Candidate(body, a)["memoryFreeBytes"]!.GetValue<long>());
    }

    [RequiresDatabaseFact]
    public async Task Suggest_for_a_workload_does_not_count_its_own_reservation()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("a", memoryGib: 8);
        await t.SeedServerAsync("b", memoryGib: 6);
        var app = await t.ReservedApplicationAsync(env, a, memoryGib: 5);

        var body = await t.Viewer.GetJsonAsync($"/api/v1/placement/suggest?workloadId={app}");

        Assert.Equal("application", body["kind"]!.GetValue<string>());
        Assert.Equal(a.ToString(), body["suggestedServerId"]!.GetValue<string>()); // 8 GiB once its own 5 GiB do not count
        Assert.Equal(8 * Gib, Candidate(body, a)["memoryFreeBytes"]!.GetValue<long>());
    }

    [RequiresDatabaseFact]
    public async Task Suggest_names_why_a_server_does_not_qualify()
    {
        var t = await fixture.NewTenantAsync();
        var away = await t.SeedServerAsync("away", connected: false);
        var storage = await t.SeedServerAsync("storage", [ServerRole.Storage]);
        var tiny = await t.SeedServerAsync("tiny", memoryGib: 1);

        var body = await t.Viewer.GetJsonAsync(Suggest("application", memoryBytes: 2 * Gib));

        Assert.Null(body["suggestedServerId"]);
        Assert.NotNull(body["message"]);
        Assert.Contains("agent is not connected", Candidate(body, away)["reasons"]![0]!.GetValue<string>());
        Assert.Contains("worker", Candidate(body, storage)["reasons"]![0]!.GetValue<string>());
        Assert.Contains("unreserved", Candidate(body, tiny)["reasons"]![0]!.GetValue<string>());
        Assert.All(body["candidates"]!.AsArray(), c => Assert.False(c!["eligible"]!.GetValue<bool>()));
    }

    [RequiresDatabaseFact]
    public async Task Suggest_sends_a_database_to_a_storage_server()
    {
        var t = await fixture.NewTenantAsync();
        await t.SeedServerAsync("workers", [ServerRole.Worker], memoryGib: 32);
        var storage = await t.SeedServerAsync("storage", [ServerRole.Storage], memoryGib: 4);

        var body = await t.Viewer.GetJsonAsync(Suggest("statefulService"));

        Assert.Equal(storage.ToString(), body["suggestedServerId"]!.GetValue<string>());
        Assert.Equal("statefulService", body["kind"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Suggest_validates_its_query_and_hides_other_organizations()
    {
        var t = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var foreign = await other.SeedServerAsync("foreign");

        await (await t.Viewer.GetAsync("/api/v1/placement/suggest")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await t.Viewer.GetAsync("/api/v1/placement/suggest?kind=banana")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await t.Viewer.GetAsync("/api/v1/placement/suggest?kind=application&cpu=0")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await t.Viewer.GetAsync("/api/v1/placement/suggest?kind=application&extra=1")).AssertProblemAsync(400, "validation.invalid_parameter");
        Assert.Equal(HttpStatusCode.NotFound, (await t.Viewer.GetAsync($"/api/v1/placement/suggest?workloadId={Guid.NewGuid()}")).StatusCode);

        var body = await t.Viewer.GetJsonAsync(Suggest("application"));
        Assert.DoesNotContain(foreign.ToString(), Ids(body));
        Assert.Null(body["suggestedServerId"]);
        Assert.Contains("no servers", body["message"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Suggest_needs_authentication()
    {
        var anonymous = fixture.Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Suggest("application"))).StatusCode);
    }

    // ---- /servers/capacity -----------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Capacity_sums_reservations_and_limits_and_flags_over_commitment()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var server = await t.SeedServerAsync("crowded", [ServerRole.Worker], memoryGib: 4, cores: 2, diskGib: 100, diskUsedGib: 30, cpuPercent: 42.5, memoryUsedGib: 3);
        var roomy = await t.SeedServerAsync("roomy", memoryGib: 16, cores: 8);
        await t.ReservedApplicationAsync(env, server, memoryGib: 3, cpu: 1.5, limitGib: 5);
        await t.ReservedApplicationAsync(env, server, memoryGib: 2, cpu: 1);
        var deleted = await t.ReservedApplicationAsync(env, server, memoryGib: 4);
        var deletedSlug = (await t.Developer.GetJsonAsync($"/api/v1/applications/{deleted}"))["slug"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await t.Developer.DeleteAsync($"/api/v1/applications/{deleted}?confirm={deletedSlug}")).StatusCode); // never deployed: nothing to tear down

        var list = (await t.Viewer.GetJsonAsync("/api/v1/servers/capacity")).AsArray();
        var crowded = list.Single(s => s!["serverId"]!.GetValue<string>() == server.ToString())!;
        var empty = list.Single(s => s!["serverId"]!.GetValue<string>() == roomy.ToString())!;

        Assert.Equal(2, list.Count);
        Assert.Equal("crowded", crowded["name"]!.GetValue<string>());
        Assert.Equal(2, crowded["cores"]!.GetValue<int>());
        Assert.Equal(4 * Gib, crowded["memoryBytes"]!.GetValue<long>());
        Assert.Equal(2, crowded["workloadCount"]!.GetValue<int>());
        Assert.Equal(5 * Gib, crowded["reservations"]!["memoryBytes"]!.GetValue<long>());
        Assert.Equal(2.5, crowded["reservations"]!["cpuCores"]!.GetValue<double>());
        Assert.Equal(5 * Gib, crowded["limits"]!["memoryBytes"]!.GetValue<long>());
        Assert.Equal(5 * Gib, crowded["committed"]!["memoryBytes"]!.GetValue<long>());
        Assert.True(crowded["overCommitted"]!.GetValue<bool>());
        Assert.NotEmpty(crowded["warnings"]!.AsArray());
        Assert.Equal(42.5, crowded["usage"]!["cpuPercent"]!.GetValue<double>());
        Assert.Equal(3 * Gib, crowded["usage"]!["memoryUsedBytes"]!.GetValue<long>());
        Assert.Equal(70 * Gib, crowded["free"]!["diskBytes"]!.GetValue<long>());
        Assert.True(crowded["connected"]!.GetValue<bool>());
        Assert.Equal("worker", crowded["roles"]![0]!.GetValue<string>());

        Assert.False(empty["overCommitted"]!.GetValue<bool>());
        Assert.Equal(0, empty["workloadCount"]!.GetValue<int>());
        Assert.Equal(16 * Gib, empty["free"]!["memoryBytes"]!.GetValue<long>());
        Assert.Null(empty["usage"]);
        Assert.Null(empty["free"]!["diskBytes"]);
    }

    [RequiresDatabaseFact]
    public async Task Capacity_is_per_organization_and_read_only_for_viewers()
    {
        var t = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        await t.SeedServerAsync("mine");
        await other.SeedServerAsync("theirs");

        var list = (await t.Viewer.GetJsonAsync("/api/v1/servers/capacity")).AsArray();

        Assert.Equal(["mine"], list.Select(s => s!["name"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateAnonymousClient().GetAsync("/api/v1/servers/capacity")).StatusCode);
    }

    // ---- creating without a server ---------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task An_application_created_without_a_server_goes_to_the_best_eligible_one()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        await t.SeedServerAsync("storage-only", [ServerRole.Storage], memoryGib: 64);
        await t.SeedServerAsync("small", [ServerRole.Worker], memoryGib: 4);
        var big = await t.SeedServerAsync("big", [ServerRole.Worker], memoryGib: 16);

        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("auto"), environmentId = env, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" }, autoDomain = false,
        });

        Assert.Equal(big.ToString(), app["serverId"]!.GetValue<string>());
        Assert.Empty(app["warnings"]!.AsArray());
        var audit = Assert.Single(await t.AuditAsync(app.Id(), "application.created"));
        Assert.Contains("automatic", audit.MetadataJson);
    }

    [RequiresDatabaseFact]
    public async Task An_application_asking_for_memory_skips_servers_that_lack_it()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        await t.SeedServerAsync("tiny", memoryGib: 1);
        var fits = await t.SeedServerAsync("fits", memoryGib: 8);

        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("hungry"), environmentId = env, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" }, autoDomain = false,
            runtime = new { resources = new { memoryLimitBytes = 2 * Gib } },
        });

        Assert.Equal(fits.ToString(), app["serverId"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Without_an_eligible_server_creating_is_a_clear_422()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var away = await t.SeedServerAsync("away", connected: false);

        var response = await t.Developer.PostAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("nowhere"), environmentId = env, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ReadAsync();
        Assert.Equal("placement.no_eligible_server", problem["code"]!.GetValue<string>());
        Assert.Equal("/serverId", problem["errors"]![0]!["pointer"]!.GetValue<string>());
        Assert.Contains("away", problem["detail"]!.GetValue<string>());
        Assert.Contains("choose a server yourself", problem["detail"]!.GetValue<string>());
        var candidate = problem["candidates"]![0]!;
        Assert.Equal(away.ToString(), candidate["serverId"]!.GetValue<string>());
        Assert.False(candidate["eligible"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task With_no_servers_at_all_the_422_says_to_add_one()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();

        var response = await t.Developer.PostAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("lonely"), environmentId = env, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" },
        });

        var problem = await response.ReadAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("placement.no_eligible_server", problem["code"]!.GetValue<string>());
        Assert.Contains("Add a server", problem["detail"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_server_chosen_against_its_roles_is_accepted_with_a_warning()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var storage = await t.SeedServerAsync("storage-box", [ServerRole.Storage]);

        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("manual"), environmentId = env, serverId = storage, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" }, autoDomain = false,
        });

        Assert.Equal(storage.ToString(), app["serverId"]!.GetValue<string>());
        var warning = Assert.Single(app["warnings"]!.AsArray())!.GetValue<string>();
        Assert.Contains("storage-box", warning);
        Assert.Contains("worker", warning);
        // A read does not repeat the warning.
        Assert.Empty((await t.Viewer.GetJsonAsync($"/api/v1/applications/{app.Id()}"))["warnings"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task An_explicit_server_is_still_warned_when_it_would_be_over_committed()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var server = await t.SeedServerAsync("small", [ServerRole.Worker], memoryGib: 2);

        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("greedy"), environmentId = env, serverId = server, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" }, autoDomain = false,
            runtime = new { resources = new { memoryReservationBytes = 3 * Gib } },
        });

        Assert.Contains("over-committed", Assert.Single(app["warnings"]!.AsArray())!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_database_without_a_server_goes_to_a_storage_server_and_the_worker_does_not_take_it()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        await t.SeedServerAsync("workers", [ServerRole.Worker], memoryGib: 64);
        var storage = await t.SeedServerAsync("storage", [ServerRole.Storage], memoryGib: 4);

        var service = await t.Developer.CreateAsync("/api/v1/services", new { name = Tenant.Unique("pg"), environmentId = env, templateKey = "postgres", autoDomain = false });

        Assert.Equal(storage.ToString(), service["serverId"]!.GetValue<string>());
        Assert.Empty(service["warnings"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task A_database_without_a_server_and_no_storage_server_is_refused_with_the_reason()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        await t.SeedServerAsync("workers-only", [ServerRole.Worker]);

        var response = await t.Developer.PostAsync("/api/v1/services", new { name = Tenant.Unique("pg"), environmentId = env, templateKey = "postgres" });

        var problem = await response.ReadAsync();
        Assert.Equal("placement.no_eligible_server", problem["code"]!.GetValue<string>());
        Assert.Contains("storage", problem["detail"]!.GetValue<string>());
        Assert.Contains("database", problem["detail"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_database_chosen_on_a_worker_is_accepted_with_a_warning()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var worker = await t.SeedServerAsync("only-worker", [ServerRole.Worker]);

        var service = await t.Developer.CreateAsync("/api/v1/services", new
        {
            name = Tenant.Unique("pg"), environmentId = env, serverId = worker, templateKey = "postgres", autoDomain = false,
        });

        Assert.Equal(worker.ToString(), service["serverId"]!.GetValue<string>());
        Assert.Contains("storage", Assert.Single(service["warnings"]!.AsArray())!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_server_of_another_organization_is_not_found()
    {
        var t = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var foreign = await other.SeedServerAsync("foreign");

        var response = await t.Developer.PostAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("x"), environmentId = env, serverId = foreign, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" },
        });

        Assert.Contains(("/serverId", "not_found"), await response.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task A_one_server_install_with_the_installers_roles_places_everything_without_a_choice()
    {
        // The local server is created with master + build: it must still take applications and databases.
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var local = await t.SeedServerAsync("local", [ServerRole.Master, ServerRole.Build]);

        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("web"), environmentId = env, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" }, autoDomain = false,
        });
        var db = await t.Developer.CreateAsync("/api/v1/services", new { name = Tenant.Unique("pg"), environmentId = env, templateKey = "postgres", autoDomain = false });

        Assert.Equal(local.ToString(), app["serverId"]!.GetValue<string>());
        Assert.Equal(local.ToString(), db["serverId"]!.GetValue<string>());
        Assert.Empty(app["warnings"]!.AsArray());
    }

    // ---- reading the roles back from the database ------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task The_roles_of_a_server_can_be_edited_and_drive_the_next_placement()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var server = await t.SeedServerAsync("flexible", [ServerRole.Storage]);
        await (await t.Developer.PostAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("a"), environmentId = env, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" },
        })).AssertProblemAsync(422, "placement.no_eligible_server");

        var patched = await t.Admin.PatchOkAsync($"/api/v1/servers/{server}", new { roles = new[] { "worker", "storage" } });
        Assert.Equal(["storage", "worker"], patched["roles"]!.AsArray().Select(r => r!.GetValue<string>()).Order());

        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("b"), environmentId = env, sourceKind = "dockerImage", image = new { image = "nginx", tag = "1" }, autoDomain = false,
        });
        Assert.Equal(server.ToString(), app["serverId"]!.GetValue<string>());
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AetheraDbContext>().Servers.AsNoTracking().FirstAsync(s => s.Id == server);
        Assert.Contains(ServerRole.Worker, stored.Roles);
    }
}
