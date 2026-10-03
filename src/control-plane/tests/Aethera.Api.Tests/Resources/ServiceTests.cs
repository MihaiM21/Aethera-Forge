using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Features.Resources.Services;
using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

[Collection(ResourcesCollection.Name)]
public sealed class ServiceTests(ResourcesFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task Templates_AreServedFromTheCatalogue()
    {
        var tenant = await fixture.NewTenantAsync();
        var templates = (await tenant.Viewer.GetJsonAsync("/api/v1/service-templates")).AsArray();
        Assert.Equal(ServiceTemplates.All.Select(t => t.Key), templates.Select(t => t!["key"]!.GetValue<string>()));

        var postgres = templates[0]!;
        Assert.Equal("17", postgres["defaultVersion"]!.GetValue<string>());
        Assert.Equal("postgres:17", postgres["defaultImage"]!.GetValue<string>());
        Assert.Equal(["17", "16", "15", "14"], postgres["versions"]!.AsArray().Select(v => v!["version"]!.GetValue<string>()));
        Assert.Equal(5432, postgres["ports"]![0]!["containerPort"]!.GetValue<int>());
        Assert.Equal("/var/lib/postgresql/data", postgres["volumes"]![0]!["mountPath"]!.GetValue<string>());
        Assert.Equal("tcp", postgres["healthCheck"]!["type"]!.GetValue<string>());
        var password = postgres["env"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == "POSTGRES_PASSWORD")!;
        Assert.Equal("password", password["generate"]!.GetValue<string>());
        Assert.Null(password["value"]);
        // The catalogue never carries a concrete credential.
        Assert.DoesNotContain("changeme", postgres.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [RequiresDatabaseFact]
    public async Task Create_FromTemplate_BuildsPortsVolumesHealthCheckAndSecretEnv()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, projectId, _) = await tenant.CreateStackAsync();

        var response = await tenant.Developer.PostAsync("/api/v1/services", new { name = "Main DB", environmentId = envId, serverId, templateKey = "postgres" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var service = await response.ReadAsync();
        var id = service.Id();
        Assert.Equal($"/api/v1/services/{id}", response.Headers.Location!.ToString());
        Assert.Equal("main-db", service["slug"]!.GetValue<string>());
        Assert.Equal(projectId, service["projectId"]!.GetValue<string>());
        Assert.Equal("postgres", service["templateKey"]!.GetValue<string>());
        Assert.Equal("17", service["templateVersion"]!.GetValue<string>());
        Assert.Equal("postgres:17", service["image"]!.GetValue<string>());
        Assert.Equal(5432, service["runtime"]!["ports"]![0]!["containerPort"]!.GetValue<int>());
        Assert.Equal("tcp", service["runtime"]!["healthCheck"]!["type"]!.GetValue<string>());
        Assert.Equal(5432, service["runtime"]!["healthCheck"]!["port"]!.GetValue<int>());
        Assert.Equal("unknown", service["state"]!["status"]!.GetValue<string>());

        var volumes = (await tenant.Viewer.GetJsonAsync($"/api/v1/services/{id}/volumes"))["items"]!.AsArray();
        Assert.Equal("/var/lib/postgresql/data", Assert.Single(volumes)!["mountPath"]!.GetValue<string>());

        // Credentials: generated, stored as secrets, linked as secret-backed variables.
        var envVars = (await tenant.Viewer.GetJsonAsync($"/api/v1/services/{id}/env-vars"))["items"]!.AsArray();
        Assert.Equal(["POSTGRES_DB", "POSTGRES_PASSWORD", "POSTGRES_USER"], envVars.Select(v => v!["key"]!.GetValue<string>()));
        var plainUser = envVars.Single(v => v!["key"]!.GetValue<string>() == "POSTGRES_USER")!;
        Assert.False(plainUser["isSecret"]!.GetValue<bool>());
        Assert.Equal("postgres", plainUser["value"]!.GetValue<string>());
        var secretVar = envVars.Single(v => v!["key"]!.GetValue<string>() == "POSTGRES_PASSWORD")!;
        Assert.True(secretVar["isSecret"]!.GetValue<bool>());
        Assert.Equal("********", secretVar["value"]!.GetValue<string>());
        Assert.Equal("POSTGRES_PASSWORD", secretVar["secretName"]!.GetValue<string>());

        var secrets = (await tenant.Owner.GetJsonAsync($"/api/v1/secrets?workloadId={id}"))["items"]!.AsArray();
        var secret = Assert.Single(secrets)!;
        Assert.Equal(secretVar["secretId"]!.GetValue<string>(), secret.Id());
        Assert.Equal("workload", secret["scope"]!.GetValue<string>());
        Assert.Equal("********", secret["value"]!.GetValue<string>());

        // The generated value is strong and random, and appears nowhere except reveal.
        var revealed = (await (await tenant.Admin.PostAsync($"/api/v1/secrets/{secret.Id()}/reveal", null)).ReadAsync())["value"]!.GetValue<string>();
        Assert.Equal(32, revealed.Length);
        Assert.Matches("^[A-Za-z0-9]+$", revealed);
        Assert.True(revealed.Distinct().Count() > 10);
        foreach (var body in new[] { service.ToJsonString(), envVars.ToJsonString(), secrets.ToJsonString() })
            Assert.DoesNotContain(revealed, body);
        foreach (var audit in await tenant.AuditAsync())
            Assert.DoesNotContain(revealed, audit.MetadataJson);

        var second = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "Second DB", environmentId = envId, serverId, templateKey = "postgres" });
        var secondSecret = (await tenant.Owner.GetJsonAsync($"/api/v1/secrets?workloadId={second.Id()}"))["items"]![0]!;
        var secondValue = (await (await tenant.Admin.PostAsync($"/api/v1/secrets/{secondSecret.Id()}/reveal", null)).ReadAsync())["value"]!.GetValue<string>();
        Assert.NotEqual(revealed, secondValue);

        Assert.Contains(await tenant.AuditAsync(id), e => e.Action == "service.created");
    }

    [RequiresDatabaseFact]
    public async Task EveryTemplate_CanBeCreated_WithItsSecretsAndVolumes()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        foreach (var template in ServiceTemplates.All)
        {
            var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name = template.Key, environmentId = envId, serverId, templateKey = template.Key });
            Assert.Equal(template.DefaultImage, service["image"]!.GetValue<string>());
            var envVars = (await tenant.Viewer.GetJsonAsync($"/api/v1/services/{service.Id()}/env-vars"))["items"]!.AsArray();
            Assert.Equal(template.Env.Count, envVars.Count);
            Assert.Equal(template.Env.Count(e => e.Generate is not null), envVars.Count(v => v!["isSecret"]!.GetValue<bool>()));
            var secrets = (await tenant.Owner.GetJsonAsync($"/api/v1/secrets?workloadId={service.Id()}&limit=50"))["items"]!.AsArray();
            Assert.Equal(template.Env.Count(e => e.Generate is not null), secrets.Count);
            Assert.Equal(template.Volumes.Count, (await tenant.Viewer.GetJsonAsync($"/api/v1/services/{service.Id()}/volumes"))["items"]!.AsArray().Count);
            Assert.Equal(template.Ports.Count, service["runtime"]!["ports"]!.AsArray().Count);
        }
    }

    [RequiresDatabaseFact]
    public async Task Create_VersionImageConfigAndRuntimeOverrides()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();

        var versioned = await tenant.Developer.CreateAsync("/api/v1/services", new
        {
            name = "old", environmentId = envId, serverId, templateKey = "REDIS", version = "6.2",
            config = new { maxmemory = "256mb", nested = new { a = 1 } },
            runtime = new { restartPolicy = "always", resources = new { memoryLimitBytes = 268_435_456L } },
        });
        Assert.Equal("redis:6.2", versioned["image"]!.GetValue<string>());
        Assert.Equal("redis", versioned["templateKey"]!.GetValue<string>());
        Assert.Equal("256mb", versioned["config"]!["maxmemory"]!.GetValue<string>());
        Assert.Equal(1, versioned["config"]!["nested"]!["a"]!.GetValue<int>());
        Assert.Equal("always", versioned["runtime"]!["restartPolicy"]!.GetValue<string>());
        // Template defaults the request did not touch are kept.
        Assert.Equal(6379, versioned["runtime"]!["ports"]![0]!["containerPort"]!.GetValue<int>());
        Assert.Equal("tcp", versioned["runtime"]!["healthCheck"]!["type"]!.GetValue<string>());

        var custom = await tenant.Developer.CreateAsync("/api/v1/services", new
        {
            name = "custom", environmentId = envId, serverId, templateKey = "redis", image = "registry.example.com/redis-fork:1", version = "custom",
        });
        Assert.Equal("registry.example.com/redis-fork:1", custom["image"]!.GetValue<string>());
        Assert.Equal("custom", custom["templateVersion"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Create_Validation()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();

        var empty = await (await tenant.Developer.PostAsync("/api/v1/services", new { })).ValidationErrorsAsync();
        Assert.Contains(("/name", "required"), empty);
        Assert.Contains(("/environmentId", "required"), empty);
        Assert.Contains(("/serverId", "required"), empty);
        Assert.Contains(("/templateKey", "required"), empty);

        var bad = await (await tenant.Developer.PostAsync("/api/v1/services", new
        {
            name = "x", environmentId = envId, serverId, templateKey = "oracle", slug = "BAD", image = "not an image",
            runtime = new { strategy = "no" },
        })).ValidationErrorsAsync();
        Assert.Contains(("/templateKey", "not_found"), bad);
        Assert.Contains(("/slug", "pattern"), bad);
        Assert.Contains(("/image", "pattern"), bad);
        Assert.Contains(("/runtime/strategy", "invalid_enum"), bad);

        Assert.Contains(("/version", "not_found"), await (await tenant.Developer.PostAsync("/api/v1/services", new
        {
            name = "x", environmentId = envId, serverId, templateKey = "postgres", version = "99",
        })).ValidationErrorsAsync());

        var refs = await (await tenant.Developer.PostAsync("/api/v1/services", new
        {
            name = "x", environmentId = Guid.NewGuid(), serverId = Guid.NewGuid(), templateKey = "postgres",
        })).ValidationErrorsAsync();
        Assert.Contains(("/environmentId", "not_found"), refs);
        Assert.Contains(("/serverId", "not_found"), refs);
    }

    [RequiresDatabaseFact]
    public async Task Patch_Get_List_Delete()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var other = await tenant.CreateServerAsync();
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new
        {
            name = "cache", environmentId = envId, serverId, templateKey = "redis", config = new { a = 1, b = new { c = 2, d = 3 } },
        });
        var url = $"/api/v1/services/{service.Id()}";

        var patched = await tenant.Developer.PatchOkAsync(url, new
        {
            description = "the cache", image = "redis:7.4-alpine", templateVersion = "7.4", serverId = other.Id(),
            config = new { a = (int?)null, b = new { c = 20, e = 5 } },
            runtime = new { resources = new { cpuLimitCores = 0.5 }, healthCheck = new { retries = 9 } },
        });
        Assert.Equal("the cache", patched["description"]!.GetValue<string>());
        Assert.Equal("redis:7.4-alpine", patched["image"]!.GetValue<string>());
        Assert.Equal(other.Id(), patched["serverId"]!.GetValue<string>());
        Assert.Null(patched["config"]!["a"]); // RFC 7396: null removes
        Assert.Equal(20, patched["config"]!["b"]!["c"]!.GetValue<int>());
        Assert.Equal(3, patched["config"]!["b"]!["d"]!.GetValue<int>());
        Assert.Equal(5, patched["config"]!["b"]!["e"]!.GetValue<int>());
        Assert.Equal(0.5, patched["runtime"]!["resources"]!["cpuLimitCores"]!.GetValue<double>());
        Assert.Equal(9, patched["runtime"]!["healthCheck"]!["retries"]!.GetValue<int>());
        Assert.Equal("tcp", patched["runtime"]!["healthCheck"]!["type"]!.GetValue<string>());

        Assert.Contains(("/name", "required"), await (await tenant.Developer.PatchAsync(url, new { name = (string?)null })).ValidationErrorsAsync());
        Assert.Contains(("/serverId", "not_found"), await (await tenant.Developer.PatchAsync(url, new { serverId = Guid.NewGuid() })).ValidationErrorsAsync());
        Assert.Contains(("/runtime/healthCheck/type", "invalid_enum"), await (await tenant.Developer.PatchAsync(url, new { runtime = new { healthCheck = new { type = "x" } } })).ValidationErrorsAsync());

        var list = await tenant.Viewer.GetJsonAsync("/api/v1/services?templateKey=redis");
        Assert.Equal(service.Id(), Assert.Single(list["items"]!.AsArray())!.Id());
        Assert.Empty((await tenant.Viewer.GetJsonAsync("/api/v1/services?templateKey=postgres"))["items"]!.AsArray());
        Assert.Single((await tenant.Viewer.GetJsonAsync($"/api/v1/services?serverId={other.Id()}"))["items"]!.AsArray());
        Assert.Single((await tenant.Viewer.GetJsonAsync($"/api/v1/services?environmentId={envId}&q=CACH"))["items"]!.AsArray());

        await (await tenant.Developer.DeleteAsync(url)).AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync(url + "?confirm=cache")).StatusCode);
        await (await tenant.Viewer.GetAsync(url)).AssertProblemAsync(404, "service.not_found");
        Assert.Contains(await tenant.AuditAsync(service.Id()), e => e.Action == "service.deleted");
        // The slug is free again.
        await tenant.Developer.CreateAsync("/api/v1/services", new { name = "cache", environmentId = envId, serverId, templateKey = "redis" });
    }

    [RequiresDatabaseFact]
    public async Task Services_AreNotApplications_AndStayInTheirOrganization()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await a.CreateStackAsync();
        var service = await a.Developer.CreateAsync("/api/v1/services", new { name = "db", environmentId = envId, serverId, templateKey = "postgres" });

        await (await a.Viewer.GetAsync($"/api/v1/services/{appId}")).AssertProblemAsync(404, "service.not_found");
        await (await a.Viewer.GetAsync($"/api/v1/applications/{service.Id()}")).AssertProblemAsync(404, "application.not_found");
        await (await b.Owner.GetAsync($"/api/v1/services/{service.Id()}")).AssertProblemAsync(404, "service.not_found");
        await (await b.Owner.PatchAsync($"/api/v1/services/{service.Id()}", new { description = "x" })).AssertProblemAsync(404, "service.not_found");
        await (await b.Owner.DeleteAsync($"/api/v1/services/{service.Id()}?confirm=db")).AssertProblemAsync(404, "service.not_found");
        Assert.Empty((await b.Owner.GetJsonAsync("/api/v1/services"))["items"]!.AsArray());

        await (await a.Viewer.PostAsync("/api/v1/services", new { name = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await a.Token(OrganizationRole.Owner, "read").PostAsync("/api/v1/services", new { name = "x" })).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.OK, (await a.Token(OrganizationRole.Viewer, "read").GetAsync("/api/v1/service-templates")).StatusCode);
    }
}
