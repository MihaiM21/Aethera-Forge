using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

[Collection(ResourcesCollection.Name)]
public sealed class ApplicationTests(ResourcesFixture fixture)
{
    private static object GitApp(string environmentId, string serverId, string? name = null) => new
    {
        name = name ?? Tenant.Unique("app"), environmentId, serverId, sourceKind = "git",
        gitSource = new { repositoryUrl = "https://github.com/acme/web.git", branch = "develop", autoDeploy = true },
        build = new { engine = "nixpacks", buildCommand = "npm run build", buildArgs = new Dictionary<string, string> { ["NODE_ENV"] = "production" } },
        runtime = new
        {
            restartPolicy = "onFailure", strategy = "low-downtime",
            ports = new object[] { new { containerPort = 3000, isHttp = true }, new { containerPort = 9229, protocol = "udp", publishedPort = 9229 } },
            resources = new { cpuLimitCores = 1.5, memoryLimitBytes = 536_870_912L, pidsLimit = 200 },
            healthCheck = new { type = "http", path = "/healthz", port = 3000, intervalSeconds = 20, retries = 4 },
        },
    };

    [RequiresDatabaseFact]
    public async Task Create_GitApplication_WithAllNestedConfig()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var (project, environmentId) = await tenant.CreateProjectWithEnvironmentAsync();

        var response = await tenant.Developer.PostAsync("/api/v1/applications", GitApp(environmentId, server.Id(), "Web Frontend"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var app = await response.ReadAsync();
        Assert.Equal($"/api/v1/applications/{app.Id()}", response.Headers.Location!.ToString());
        Assert.Equal("web-frontend", app["slug"]!.GetValue<string>());
        Assert.Equal(project.Id(), app["projectId"]!.GetValue<string>());
        Assert.Equal("git", app["sourceKind"]!.GetValue<string>());
        Assert.Equal("unknown", app["state"]!["status"]!.GetValue<string>());
        Assert.Equal("running", app["state"]!["desiredState"]!.GetValue<string>());

        var git = app["gitSource"]!;
        Assert.Equal("https://github.com/acme/web.git", git["repositoryUrl"]!.GetValue<string>());
        Assert.Equal("develop", git["branch"]!.GetValue<string>());
        Assert.True(git["autoDeploy"]!.GetValue<bool>());
        Assert.Null(app["image"]);
        Assert.Null(app["compose"]);
        var build = app["build"]!;
        Assert.Equal("nixpacks", build["engine"]!.GetValue<string>());
        Assert.Equal("npm run build", build["buildCommand"]!.GetValue<string>());
        Assert.Equal("production", build["buildArgs"]!["NODE_ENV"]!.GetValue<string>());
        Assert.Equal(".", build["context"]!.GetValue<string>());

        var runtime = app["runtime"]!;
        Assert.Equal("onFailure", runtime["restartPolicy"]!.GetValue<string>());
        Assert.Equal("low-downtime", runtime["strategy"]!.GetValue<string>());
        Assert.Equal([3000, 9229], runtime["ports"]!.AsArray().Select(p => p!["containerPort"]!.GetValue<int>()));
        Assert.Equal("udp", runtime["ports"]![1]!["protocol"]!.GetValue<string>());
        Assert.Equal(1.5, runtime["resources"]!["cpuLimitCores"]!.GetValue<double>());
        Assert.Null(runtime["resources"]!["memoryReservationBytes"]);
        Assert.Equal("http", runtime["healthCheck"]!["type"]!.GetValue<string>());
        Assert.Equal(20, runtime["healthCheck"]!["intervalSeconds"]!.GetValue<int>());
        Assert.Equal(5, runtime["healthCheck"]!["timeoutSeconds"]!.GetValue<int>()); // default

        var fetched = await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{app.Id()}");
        Assert.Equal(app.ToJsonString().Length, fetched.ToJsonString().Length);
        Assert.Contains(await tenant.AuditAsync(app.Id()), e => e.Action == "application.created");
    }

    [RequiresDatabaseFact]
    public async Task Create_EachSourceKind_NeedsItsOwnConfig()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var (_, env) = await tenant.CreateProjectWithEnvironmentAsync();
        string Name() => Tenant.Unique("a");

        var image = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Name(), environmentId = env, serverId = server.Id(), sourceKind = "dockerImage", image = new { image = "ghcr.io/acme/api", tag = "v1.2.3", pullPolicy = "always" },
        });
        Assert.Equal("always", image["image"]!["pullPolicy"]!.GetValue<string>());
        Assert.Null(image["build"]);

        var inlineCompose = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Name(), environmentId = env, serverId = server.Id(), sourceKind = "compose", compose = new { inlineContent = "services:\n  web:\n    image: nginx\n" },
        });
        Assert.Contains("nginx", inlineCompose["compose"]!["inlineContent"]!.GetValue<string>());

        var repoCompose = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Name(), environmentId = env, serverId = server.Id(), sourceKind = "compose", compose = new { filePath = "deploy/compose.yml" },
            gitSource = new { repositoryUrl = "git@github.com:acme/stack.git" },
        });
        Assert.Equal("deploy/compose.yml", repoCompose["compose"]!["filePath"]!.GetValue<string>());

        foreach (var kind in new[] { "dockerfile", "static", "nixpacks" })
        {
            var app = await tenant.Developer.CreateAsync("/api/v1/applications", new
            {
                name = Name(), environmentId = env, serverId = server.Id(), sourceKind = kind, gitSource = new { repositoryUrl = "https://example.com/r.git" },
            });
            // The build engine follows the kind.
            Assert.Equal(kind, app["build"]!["engine"]!.GetValue<string>());
            Assert.Equal("main", app["gitSource"]!["branch"]!.GetValue<string>());
        }
    }

    [RequiresDatabaseFact]
    public async Task Create_ValidationPerKind_ReportsPointers()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var (_, env) = await tenant.CreateProjectWithEnvironmentAsync();

        async Task<IReadOnlyList<(string? Location, string Code)>> Errors(object body) =>
            await (await tenant.Developer.PostAsync("/api/v1/applications", body)).ValidationErrorsAsync();

        var empty = await Errors(new { });
        Assert.Contains(("/name", "required"), empty);
        Assert.Contains(("/environmentId", "required"), empty);
        Assert.Contains(("/serverId", "required"), empty);
        Assert.Contains(("/sourceKind", "required"), empty);

        Assert.Contains(("/sourceKind", "invalid_enum"), await Errors(new { name = "a", environmentId = env, serverId = server.Id(), sourceKind = "svn" }));
        Assert.Contains(("/gitSource", "required"), await Errors(new { name = "a", environmentId = env, serverId = server.Id(), sourceKind = "git" }));
        Assert.Contains(("/image", "required"), await Errors(new { name = "a", environmentId = env, serverId = server.Id(), sourceKind = "dockerImage" }));
        Assert.Contains(("/compose", "required"), await Errors(new { name = "a", environmentId = env, serverId = server.Id(), sourceKind = "compose" }));
        Assert.Contains(("/gitSource", "required"), await Errors(new { name = "a", environmentId = env, serverId = server.Id(), sourceKind = "compose", compose = new { filePath = "c.yml" } }));
        Assert.Contains(("/compose/filePath", "required"), await Errors(new
        {
            name = "a", environmentId = env, serverId = server.Id(), sourceKind = "compose", compose = new { filePath = "c.yml", inlineContent = "x" }, gitSource = new { repositoryUrl = "https://x.io/r.git" },
        }));

        var misplaced = await Errors(new
        {
            name = "a", environmentId = env, serverId = server.Id(), sourceKind = "dockerImage", image = new { image = "nginx" },
            gitSource = new { repositoryUrl = "https://x.io/r.git" }, compose = new { inlineContent = "x" }, build = new { },
        });
        Assert.Contains(("/gitSource", "not_applicable"), misplaced);
        Assert.Contains(("/compose", "not_applicable"), misplaced);
        Assert.Contains(("/build", "not_applicable"), misplaced);

        var invalid = await Errors(new
        {
            name = "a", environmentId = env, serverId = server.Id(), sourceKind = "git",
            gitSource = new { repositoryUrl = "ftp://nope", provider = "bitbucket", commitPin = "xyz", branch = "has space" },
            build = new { engine = "magic", targetPlatform = "windows", buildArgs = new Dictionary<string, string> { ["bad key"] = "x" } },
            runtime = new
            {
                restartPolicy = "sometimes", strategy = "yolo",
                ports = new object[] { new { containerPort = 0 }, new { containerPort = 80 }, new { containerPort = 80, protocol = "tcp" }, new { containerPort = 81, protocol = "sctp" } },
                resources = new { cpuLimitCores = 0, cpuReservationCores = 2.0, memoryLimitBytes = 1024, pidsLimit = 0 },
                healthCheck = new { type = "ping", path = "healthz", intervalSeconds = 0, retries = 101 },
            },
        });
        Assert.Contains(("/gitSource/repositoryUrl", "pattern"), invalid);
        Assert.Contains(("/gitSource/provider", "invalid_enum"), invalid);
        Assert.Contains(("/gitSource/commitPin", "pattern"), invalid);
        Assert.Contains(("/gitSource/branch", "pattern"), invalid);
        Assert.Contains(("/build/engine", "invalid_enum"), invalid);
        Assert.Contains(("/build/targetPlatform", "pattern"), invalid);
        Assert.Contains(("/build/buildArgs", "pattern"), invalid);
        Assert.Contains(("/runtime/restartPolicy", "invalid_enum"), invalid);
        Assert.Contains(("/runtime/strategy", "invalid_enum"), invalid);
        Assert.Contains(("/runtime/ports", "not_unique"), invalid);
        Assert.Contains(("/runtime/ports/0/containerPort", "range"), invalid);
        Assert.Contains(("/runtime/ports/3/protocol", "invalid_enum"), invalid);
        Assert.Contains(("/runtime/resources/cpuLimitCores", "range"), invalid);
        Assert.Contains(("/runtime/resources/cpuReservationCores", "range"), invalid);
        Assert.Contains(("/runtime/resources/memoryLimitBytes", "range"), invalid);
        Assert.Contains(("/runtime/resources/pidsLimit", "range"), invalid);
        Assert.Contains(("/runtime/healthCheck/type", "invalid_enum"), invalid);
        Assert.Contains(("/runtime/healthCheck/path", "pattern"), invalid);
        Assert.Contains(("/runtime/healthCheck/intervalSeconds", "range"), invalid);
        Assert.Contains(("/runtime/healthCheck/retries", "range"), invalid);

        Assert.Contains(("/build/engine", "mismatch"), await Errors(new
        {
            name = "a", environmentId = env, serverId = server.Id(), sourceKind = "static", gitSource = new { repositoryUrl = "https://x.io/r.git" }, build = new { engine = "nixpacks" },
        }));

        // References that do not exist (or belong to someone else) are field errors too.
        var refs = await Errors(new { name = "a", environmentId = Guid.NewGuid(), serverId = Guid.NewGuid(), sourceKind = "dockerImage", image = new { image = "nginx" } });
        Assert.Contains(("/environmentId", "not_found"), refs);
        Assert.Contains(("/serverId", "not_found"), refs);
        var badRegistry = await Errors(new { name = "a", environmentId = env, serverId = server.Id(), sourceKind = "dockerImage", image = new { image = "nginx", registryId = Guid.NewGuid() } });
        Assert.Contains(("/image/registryId", "not_found"), badRegistry);
    }

    [RequiresDatabaseFact]
    public async Task Create_DuplicateSlugInEnvironment_Is409_ButOtherEnvironmentsAreFine()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, projectId, _) = await tenant.CreateStackAsync();
        var first = await tenant.CreateApplicationAsync(envId, serverId, "Api");
        await (await tenant.Developer.PostAsync("/api/v1/applications", new
        {
            name = "Other", slug = "api", environmentId = envId, serverId, sourceKind = "dockerImage", image = new { image = "nginx" },
        })).AssertProblemAsync(409, "application.already_exists");

        var staging = await tenant.Developer.CreateAsync($"/api/v1/projects/{projectId}/environments", new { name = "Staging" });
        await tenant.CreateApplicationAsync(staging.Id(), serverId, "Api");

        // Applications and services share the slug space of an environment.
        await (await tenant.Developer.PostAsync("/api/v1/services", new { name = "Api", environmentId = envId, serverId, templateKey = "redis" }))
            .AssertProblemAsync(409, "service.already_exists");
        Assert.NotNull(first);
    }

    [RequiresDatabaseFact]
    public async Task Patch_MergesNestedObjects_AndReplacesArrays()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var other = await tenant.CreateServerAsync();
        var (_, env) = await tenant.CreateProjectWithEnvironmentAsync();
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", GitApp(env, server.Id()));
        var url = $"/api/v1/applications/{app.Id()}";

        var patched = await tenant.Developer.PatchOkAsync(url, new
        {
            description = "now described",
            serverId = other.Id(),
            gitSource = new { branch = "release" },
            build = new { installCommand = "npm ci", buildArgs = new Dictionary<string, string?> { ["NODE_ENV"] = null, ["EXTRA"] = "1" } },
            runtime = new
            {
                strategy = "recreate",
                resources = new { memoryLimitBytes = 1_073_741_824L },
                healthCheck = new { path = "/ready" },
                ports = new object[] { new { containerPort = 3000, isHttp = false }, new { containerPort = 8080 } },
            },
        });

        Assert.Equal("now described", patched["description"]!.GetValue<string>());
        Assert.Equal(other.Id(), patched["serverId"]!.GetValue<string>());
        // gitSource: merged
        Assert.Equal("release", patched["gitSource"]!["branch"]!.GetValue<string>());
        Assert.Equal("https://github.com/acme/web.git", patched["gitSource"]!["repositoryUrl"]!.GetValue<string>());
        Assert.True(patched["gitSource"]!["autoDeploy"]!.GetValue<bool>());
        // build: merged, buildArgs merged as an object (null removes a key)
        Assert.Equal("npm ci", patched["build"]!["installCommand"]!.GetValue<string>());
        Assert.Equal("npm run build", patched["build"]!["buildCommand"]!.GetValue<string>());
        Assert.Equal("nixpacks", patched["build"]!["engine"]!.GetValue<string>());
        Assert.Equal(["EXTRA"], patched["build"]!["buildArgs"]!.AsObject().Select(p => p.Key));
        // runtime: merged objects, replaced array
        var runtime = patched["runtime"]!;
        Assert.Equal("recreate", runtime["strategy"]!.GetValue<string>());
        Assert.Equal("onFailure", runtime["restartPolicy"]!.GetValue<string>());
        Assert.Equal(1.5, runtime["resources"]!["cpuLimitCores"]!.GetValue<double>());
        Assert.Equal(1_073_741_824L, runtime["resources"]!["memoryLimitBytes"]!.GetValue<long>());
        Assert.Equal("/ready", runtime["healthCheck"]!["path"]!.GetValue<string>());
        Assert.Equal(20, runtime["healthCheck"]!["intervalSeconds"]!.GetValue<int>());
        Assert.Equal([3000, 8080], runtime["ports"]!.AsArray().Select(p => p!["containerPort"]!.GetValue<int>()));
        Assert.False(runtime["ports"]![0]!["isHttp"]!.GetValue<bool>());

        // null resets a nested value to its default / clears it
        var reset = await tenant.Developer.PatchOkAsync(url, new { runtime = new { resources = new { cpuLimitCores = (double?)null }, restartPolicy = (string?)null }, gitSource = new { commitPin = (string?)null } });
        Assert.Null(reset["runtime"]!["resources"]!["cpuLimitCores"]);
        Assert.Equal("unlessStopped", reset["runtime"]!["restartPolicy"]!.GetValue<string>());
        Assert.Equal(1_073_741_824L, reset["runtime"]!["resources"]!["memoryLimitBytes"]!.GetValue<long>());

        // build: null resets the build config to defaults
        var buildReset = await tenant.Developer.PatchOkAsync(url, new { build = (object?)null });
        Assert.Equal("git", buildReset["sourceKind"]!.GetValue<string>());
        Assert.Null(buildReset["build"]!["buildCommand"]);

        // empty runtime ports clears them
        var noPorts = await tenant.Developer.PatchOkAsync(url, new { runtime = new { ports = Array.Empty<object>() } });
        Assert.Empty(noPorts["runtime"]!["ports"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task Patch_Validation_AndKindMismatches()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, env, _, imageAppId) = await tenant.CreateStackAsync();
        var url = $"/api/v1/applications/{imageAppId}";

        var invalid = await (await tenant.Developer.PatchAsync(url, new
        {
            name = "", runtime = new { strategy = "x", ports = new object[] { new { containerPort = 70000 } } }, image = new { tag = "bad tag!" },
        })).ValidationErrorsAsync();
        Assert.Contains(("/name", "required"), invalid);
        Assert.Contains(("/runtime/strategy", "invalid_enum"), invalid);
        Assert.Contains(("/runtime/ports/0/containerPort", "range"), invalid);
        Assert.Contains(("/image/tag", "pattern"), invalid);

        Assert.Contains(("/name", "required"), await (await tenant.Developer.PatchAsync(url, new { name = (string?)null })).ValidationErrorsAsync());
        Assert.Contains(("/gitSource", "not_applicable"), await (await tenant.Developer.PatchAsync(url, new { gitSource = new { branch = "x" } })).ValidationErrorsAsync());
        Assert.Contains(("/build", "not_applicable"), await (await tenant.Developer.PatchAsync(url, new { build = new { context = "." } })).ValidationErrorsAsync());
        Assert.Contains(("/serverId", "not_found"), await (await tenant.Developer.PatchAsync(url, new { serverId = Guid.NewGuid() })).ValidationErrorsAsync());

        var ok = await tenant.Developer.PatchOkAsync(url, new { image = new { tag = "1.28", pullPolicy = "never" } });
        Assert.Equal("1.28", ok["image"]!["tag"]!.GetValue<string>());
        Assert.Equal("never", ok["image"]!["pullPolicy"]!.GetValue<string>());
        Assert.Equal("nginx", ok["image"]!["image"]!.GetValue<string>());
        Assert.NotNull(serverId);
        Assert.NotNull(env);
    }

    [RequiresDatabaseFact]
    public async Task Patch_PlaceholderApplication_NeedsRepositoryUrlWhenAddingGitSource()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var project = await tenant.Developer.PostAsync("/api/v1/projects/from-template", new { templateKey = "static-site", name = "Site", serverId = server.Id() });
        var placeholder = (await project.ReadAsync())["workloads"]![0]!;
        var url = $"/api/v1/applications/{placeholder.Id()}";

        Assert.Null((await tenant.Viewer.GetJsonAsync(url))["gitSource"]);
        Assert.Contains(("/gitSource/repositoryUrl", "required"), await (await tenant.Developer.PatchAsync(url, new { gitSource = new { branch = "main" } })).ValidationErrorsAsync());
        var configured = await tenant.Developer.PatchOkAsync(url, new { gitSource = new { repositoryUrl = "https://github.com/acme/site.git" }, build = new { outputDirectory = "dist" } });
        Assert.Equal("static", configured["build"]!["engine"]!.GetValue<string>());
        Assert.Equal("dist", configured["build"]!["outputDirectory"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task ETag_IfMatch_IsHonoured()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var url = $"/api/v1/applications/{appId}";
        var etag = (await tenant.Viewer.GetAsync(url)).Headers.ETag!.Tag;

        await (await tenant.Developer.PatchAsync(url, new { description = "a" }, "\"1\"")).AssertProblemAsync(412, "precondition.failed");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Developer.PatchAsync(url, new { description = "b" }, etag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Developer.PatchAsync(url, new { description = "c" }, "*")).StatusCode);
        await (await tenant.Developer.PatchAsync(url, new { description = "d" }, etag)).AssertProblemAsync(412, "precondition.failed");
    }

    [RequiresDatabaseFact]
    public async Task List_FiltersByProjectEnvironmentServerStatusAndText()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverA, envA, projectA, _) = await tenant.CreateStackAsync();
        var serverB = (await tenant.CreateServerAsync()).Id();
        var (projectB, envB) = await tenant.CreateProjectWithEnvironmentAsync();
        var onB = await tenant.CreateApplicationAsync(envB, serverB, "Billing Worker");

        Assert.Equal(2, (await tenant.Viewer.GetJsonAsync("/api/v1/applications"))["items"]!.AsArray().Count);
        async Task<List<string>> Ids(string query) => (await tenant.Viewer.GetJsonAsync("/api/v1/applications" + query))["items"]!.AsArray().Select(a => a!.Id()).ToList();

        Assert.Equal([onB.Id()], await Ids($"?projectId={projectB.Id()}"));
        Assert.Equal([onB.Id()], await Ids($"?environmentId={envB}"));
        Assert.Equal([onB.Id()], await Ids($"?serverId={serverB}"));
        Assert.Single(await Ids($"?projectId={projectA}&serverId={serverA}"));
        Assert.Empty(await Ids($"?projectId={projectA}&serverId={serverB}"));
        Assert.Equal([onB.Id()], await Ids("?q=billing"));
        Assert.Equal(2, (await Ids("?status=unknown,running")).Count);
        Assert.Empty(await Ids("?status=running"));
        Assert.Equal(2, (await Ids("?sourceKind=dockerImage")).Count);
        Assert.Empty(await Ids("?sourceKind=git"));
        Assert.NotNull(envA);
        await (await tenant.Viewer.GetAsync("/api/v1/applications?status=exploded")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await tenant.Viewer.GetAsync("/api/v1/applications?projectId=not-a-guid")).AssertProblemAsync(400, "request.malformed");

        var summary = (await tenant.Viewer.GetJsonAsync("/api/v1/applications?q=billing"))["items"]![0]!;
        Assert.Equal("nginx:1.27", summary["image"]!.GetValue<string>());
        Assert.Null(summary["runtime"]); // the list uses the lighter summary shape
    }

    [RequiresDatabaseFact]
    public async Task List_PaginatesWithSortAndTies()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        // identical names force the id tiebreaker of the keyset to do its work
        for (var i = 0; i < 5; i++) await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = "same", slug = $"same-{i}", environmentId = envId, serverId, sourceKind = "dockerImage", image = new { image = "nginx" },
        });

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await tenant.Viewer.GetJsonAsync("/api/v1/applications?sort=name&limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            seen.AddRange(page["items"]!.AsArray().Select(a => a!.Id()));
            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    [RequiresDatabaseFact]
    public async Task Delete_NeedsConfirmation_SoftDeletesAndFreesTheSlug()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        var app = await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}");
        var slug = app["slug"]!.GetValue<string>();

        await (await tenant.Developer.DeleteAsync($"/api/v1/applications/{appId}")).AssertProblemAsync(428, "confirmation.required");
        await (await tenant.Developer.DeleteAsync($"/api/v1/applications/{appId}?confirm=nope")).AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/applications/{appId}?confirm={slug}")).StatusCode);
        await (await tenant.Viewer.GetAsync($"/api/v1/applications/{appId}")).AssertProblemAsync(404, "application.not_found");
        Assert.Empty((await tenant.Viewer.GetJsonAsync("/api/v1/applications"))["items"]!.AsArray());
        await (await tenant.Developer.DeleteAsync($"/api/v1/applications/{appId}?confirm={slug}")).AssertProblemAsync(404, "application.not_found");
        Assert.Contains(await tenant.AuditAsync(appId), e => e.Action == "application.deleted");

        await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = "Again", slug, environmentId = envId, serverId, sourceKind = "dockerImage", image = new { image = "nginx" },
        });
    }

    [RequiresDatabaseFact]
    public async Task CrossOrganization_Role_AndScopeMatrix()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await a.CreateStackAsync();
        var url = $"/api/v1/applications/{appId}";

        await (await b.Owner.GetAsync(url)).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.PatchAsync(url, new { description = "x" })).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.DeleteAsync(url + "?confirm=x")).AssertProblemAsync(404, "application.not_found");
        Assert.Empty((await b.Owner.GetJsonAsync("/api/v1/applications"))["items"]!.AsArray());

        // Cannot create into someone else's environment/server.
        var cross = await b.Developer.PostAsync("/api/v1/applications", new
        {
            name = "x", environmentId = envId, serverId, sourceKind = "dockerImage", image = new { image = "nginx" },
        });
        var errors = await cross.ValidationErrorsAsync();
        Assert.Contains(("/environmentId", "not_found"), errors);
        Assert.Contains(("/serverId", "not_found"), errors);

        await (await a.Viewer.PatchAsync(url, new { description = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Viewer.DeleteAsync(url + "?confirm=x")).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Token(OrganizationRole.Owner, "read").PatchAsync(url, new { description = "x" })).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.OK, (await a.Token(OrganizationRole.Developer, "write").PatchAsync(url, new { description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.Token(OrganizationRole.Viewer, "read").GetAsync(url)).StatusCode);
        await (await a.Token(OrganizationRole.Viewer, "deploy").GetAsync(url)).AssertProblemAsync(403, "auth.insufficient_scope");
    }
}
