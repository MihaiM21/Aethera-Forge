using System.Net;
using System.Text.Json.Nodes;
using Aethera.Domain;
using Microsoft.AspNetCore.Hosting;

namespace Aethera.Api.Tests.Resources;

/// <summary>
/// WP1.6 part 2, the trust model at the API (ADR 0006): Developers deploy applications normally, anything root-equivalent on a server needs an
/// Administrator. Host paths, privileged and reserved ports, compose options and build inputs.
/// </summary>
[Collection(ResourcesCollection.Name)]
public sealed class TrustModelApiTests(ResourcesFixture fixture)
{
    private static JsonObject ImageApp(string environmentId, string serverId, JsonNode? runtime = null)
    {
        var body = new JsonObject
        {
            ["name"] = Tenant.Unique("app"), ["environmentId"] = environmentId, ["serverId"] = serverId, ["sourceKind"] = "dockerImage",
            ["image"] = new JsonObject { ["image"] = "nginx", ["tag"] = "1.27" },
        };
        if (runtime is not null) body["runtime"] = runtime;
        return body;
    }

    private static JsonObject ComposeApp(string environmentId, string serverId, string yaml) => new()
    {
        ["name"] = Tenant.Unique("stack"), ["environmentId"] = environmentId, ["serverId"] = serverId, ["sourceKind"] = "compose",
        ["compose"] = new JsonObject { ["inlineContent"] = yaml },
    };

    private static JsonNode Ports(params (int Container, int? Published, bool? AllowReserved)[] ports) => new JsonObject
    {
        ["ports"] = new JsonArray(ports.Select(p => (JsonNode)new JsonObject
        {
            ["containerPort"] = p.Container, ["publishedPort"] = p.Published, ["allowReserved"] = p.AllowReserved,
        }).ToArray()),
    };

    private static async Task<JsonNode> ErrorAt(HttpResponseMessage response, int status, string code, string pointer)
    {
        var body = await response.ReadAsync();
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, body["code"]!.GetValue<string>());
        Assert.Contains(body["errors"]!.AsArray(), e => e!["pointer"]!.GetValue<string>() == pointer && e["code"]!.GetValue<string>() == code);
        return body;
    }

    // ---- volume host paths ----------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task HostPath_IsAdminOnly_OnEveryWayToCreateOrChangeAVolume()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();

        // Developers: 403 on both create routes, whatever the path (the role is checked before the denylist).
        foreach (var path in new[] { "/srv/data", "/etc", "/", "/var/run/docker.sock" })
        {
            await ErrorAt(await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/volumes", new { mountPath = "/d", hostPath = path }),
                403, "volume.host_path_requires_admin", "/hostPath");
            await ErrorAt(await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/d", hostPath = path }),
                403, "volume.host_path_requires_admin", "/hostPath");
        }

        // A malformed path is a plain validation error for everybody.
        Assert.Contains(("/hostPath", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/d", hostPath = "rel" })).ValidationErrorsAsync());
        Assert.Empty((await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/volumes"))["items"]!.AsArray());

        // Administrators and the Owner may, and get the normalized path back.
        var created = await tenant.Admin.CreateAsync($"/api/v1/applications/{appId}/volumes", new { name = "files", mountPath = "/files", hostPath = "//srv///files/./" });
        Assert.Equal("/srv/files", created["hostPath"]!.GetValue<string>());
        var owner = await tenant.Owner.CreateAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/more", hostPath = "/var/lib/aethera/volumes/app/more" });
        Assert.Equal("/var/lib/aethera/volumes/app/more", owner["hostPath"]!.GetValue<string>());

        // PATCH: a Developer cannot point a volume at a host path, can drop one, and can keep editing a volume whose host path stays as it is.
        var url = $"/api/v1/volumes/{created.Id()}";
        await ErrorAt(await tenant.Developer.PatchAsync(url, new { hostPath = "/srv/other" }), 403, "volume.host_path_requires_admin", "/hostPath");
        await ErrorAt(await tenant.Developer.PatchAsync($"/api/v1/volumes/{(await tenant.Developer.CreateAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/plain" })).Id()}",
            new { hostPath = "/srv/x" }), 403, "volume.host_path_requires_admin", "/hostPath");
        Assert.Equal("/srv/files", (await tenant.Developer.PatchOkAsync(url, new { readOnly = true, hostPath = "/srv//files" }))["hostPath"]!.GetValue<string>());
        Assert.Equal("/srv/other", (await tenant.Admin.PatchOkAsync(url, new { hostPath = "/srv/other" }))["hostPath"]!.GetValue<string>());
        Assert.Null((await tenant.Developer.PatchOkAsync(url, new { hostPath = (string?)null }))["hostPath"]);

        // Audit: the Administrator's host path is on record.
        Assert.Contains(await tenant.AuditAsync(created.Id(), "volume.created"), e => e.MetadataJson.Contains("/srv/files"));
    }

    [RequiresDatabaseFact]
    public async Task HostPath_WithAToken_FollowsTheOwnersRole()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var url = $"/api/v1/applications/{appId}/volumes";

        await ErrorAt(await tenant.Token(OrganizationRole.Developer, "write").PostAsync(url, new { mountPath = "/a", hostPath = "/srv/a" }),
            403, "volume.host_path_requires_admin", "/hostPath");
        await ErrorAt(await tenant.Token(OrganizationRole.Developer, "*").PostAsync(url, new { mountPath = "/a", hostPath = "/srv/a" }),
            403, "volume.host_path_requires_admin", "/hostPath");
        Assert.Equal(HttpStatusCode.Created, (await tenant.Token(OrganizationRole.Admin, "write").PostAsync(url, new { mountPath = "/a", hostPath = "/srv/a" })).StatusCode);
        await ErrorAt(await tenant.Token(OrganizationRole.Admin, "write").PostAsync(url, new { mountPath = "/b", hostPath = "/etc" }),
            422, "volume.host_path_forbidden", "/hostPath");
    }

    [RequiresDatabaseTheory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/var/run")]
    [InlineData("/var/run/anything/at/all")]
    [InlineData("/run")]
    [InlineData("/run/docker.sock")]
    [InlineData("/proc")]
    [InlineData("/proc/1/root")]
    [InlineData("/sys")]
    [InlineData("/dev")]
    [InlineData("/dev/mem")]
    [InlineData("/etc")]
    [InlineData("/etc/shadow")]
    [InlineData("/boot")]
    [InlineData("/root")]
    [InlineData("/root/.ssh")]
    [InlineData("/var/lib/aethera")]
    [InlineData("/var/lib/aethera/pki")]
    [InlineData("/var/lib/docker")]
    [InlineData("//etc//passwd")]
    [InlineData("/./etc")]
    [InlineData("/var/./run/docker.sock")]
    [InlineData("/etc/")]
    public async Task HostPath_Denylist_AppliesEvenToAdministrators(string path)
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        foreach (var client in new[] { tenant.Admin, tenant.Owner })
        {
            await ErrorAt(await client.PostAsync($"/api/v1/applications/{appId}/volumes", new { mountPath = "/x", hostPath = path }),
                422, "volume.host_path_forbidden", "/hostPath");
        }

        // ...and on PATCH.
        var volume = await tenant.Admin.CreateAsync("/api/v1/volumes", new { workloadId = appId, mountPath = "/ok", hostPath = "/srv/ok" });
        await ErrorAt(await tenant.Admin.PatchAsync($"/api/v1/volumes/{volume.Id()}", new { hostPath = path }), 422, "volume.host_path_forbidden", "/hostPath");
        Assert.Equal("/srv/ok", (await tenant.Viewer.GetJsonAsync($"/api/v1/volumes/{volume.Id()}"))["hostPath"]!.GetValue<string>());
        Assert.Single((await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/volumes"))["items"]!.AsArray());
    }

    [RequiresDatabaseTheory]
    [InlineData("/srv/data", "/srv/data")]
    [InlineData("/mnt/disk1/app/", "/mnt/disk1/app")]
    [InlineData("/var/lib/aethera/volumes", "/var/lib/aethera/volumes")]
    [InlineData("/var/lib/aethera/volumes/app//data", "/var/lib/aethera/volumes/app/data")]
    [InlineData("/var/lib/aethera-backup", "/var/lib/aethera-backup")]
    [InlineData("/etcetera", "/etcetera")]
    public async Task HostPath_OutsideTheDenylist_IsAcceptedForAdministrators(string path, string normalized)
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var volume = await tenant.Admin.CreateAsync($"/api/v1/applications/{appId}/volumes", new { mountPath = "/x", hostPath = path });
        Assert.Equal(normalized, volume["hostPath"]!.GetValue<string>());
    }

    [RequiresDatabaseTheory]
    [InlineData("/srv/../etc")]
    [InlineData("/..")]
    [InlineData("/var/lib/aethera/volumes/../../../../etc")]
    [InlineData("relative")]
    [InlineData("/srv/a b")]
    [InlineData("/srv/a:b")]
    public async Task HostPath_DotDotAndBadSyntax_AreValidationErrors_ForEveryRole(string path)
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        foreach (var client in new[] { tenant.Developer, tenant.Admin })
            Assert.Contains(("/hostPath", "pattern"), await (await client.PostAsync($"/api/v1/applications/{appId}/volumes", new { mountPath = "/x", hostPath = path })).ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task TheAllowlistAndTheReservedPorts_AreConfigurable()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder
            .UseSetting("Aethera:Trust:HostPathAllowlist:0", "/etc/aethera-mounts/")
            .UseSetting("Aethera:Trust:ReservedPorts:0", "8123"));
        using var admin = factory.CreateClientAs(OrganizationRole.Admin, identity: tenant.Identity);

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"/api/v1/applications/{appId}/volumes", new { mountPath = "/a", hostPath = "/etc/aethera-mounts/app" })).StatusCode);
        await (await admin.PostAsync($"/api/v1/applications/{appId}/volumes", new { mountPath = "/b", hostPath = "/etc/passwd" })).AssertProblemAsync(422, "volume.host_path_forbidden");
        await (await admin.PostAsync($"/api/v1/applications/{appId}/volumes", new { mountPath = "/c", hostPath = "/var/lib/aethera/volumes/x" })).AssertProblemAsync(422, "volume.host_path_forbidden");

        // 8123 is reserved now, 5080 and 9443 no longer are (the list is replaced); the privileged range is not configurable.
        await (await admin.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((80, 8123, null))))).AssertProblemAsync(422, "port.reserved");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((80, 9443, null))))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((80, 5080, null))))).StatusCode);
    }

    // ---- published ports ------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseTheory]
    [InlineData(80)]
    [InlineData(22)]
    [InlineData(443)]
    [InlineData(1)]
    [InlineData(1023)]
    public async Task PrivilegedPorts_NeedAnAdministrator(int port)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();

        var create = await tenant.Developer.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, port, null))));
        var body = await ErrorAt(create, 403, "port.privileged_requires_admin", "/runtime/ports/0/publishedPort");
        Assert.Contains(port.ToString(), body["detail"]!.GetValue<string>());
        await ErrorAt(await tenant.Developer.PatchAsync($"/api/v1/applications/{appId}", new { runtime = new { ports = new[] { new { containerPort = 8080, publishedPort = port } } } }),
            403, "port.privileged_requires_admin", "/runtime/ports/0/publishedPort");
        // allowReserved is an Administrator's switch: it does not turn a Developer into one.
        await ErrorAt(await tenant.Developer.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, port, true)))),
            403, "port.privileged_requires_admin", "/runtime/ports/0/publishedPort");
        Assert.Empty((await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}"))["runtime"]!["ports"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task PrivilegedPorts_AreAllowedForAdministrators_WhenNotReserved()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        foreach (var client in new[] { tenant.Admin, tenant.Owner })
        {
            var app = await client.CreateAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, 1000, null), (8081, 1023, null))));
            Assert.Equal([1000, 1023], app["runtime"]!["ports"]!.AsArray().Select(p => p!["publishedPort"]!.GetValue<int>()).Order());
            Assert.Null(app["runtime"]!["ports"]![0]!["allowReserved"]); // a request switch, never stored or returned
        }
    }

    [RequiresDatabaseTheory]
    [InlineData(5080)]
    [InlineData(2375)]
    [InlineData(2376)]
    [InlineData(9443)]
    public async Task ReservedPorts_AreRefusedForEveryone_UnlessAnAdministratorAllowsThem(int port)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();

        foreach (var role in new[] { OrganizationRole.Developer, OrganizationRole.Admin, OrganizationRole.Owner })
        {
            var body = await ErrorAt(await tenant.As(role).PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, port, null)))),
                422, "port.reserved", "/runtime/ports/0/publishedPort");
            Assert.Contains(role == OrganizationRole.Developer ? "Only an Administrator" : "allowReserved", body["detail"]!.GetValue<string>());
        }

        // allowReserved: Developers still cannot, Administrators can.
        await ErrorAt(await tenant.Developer.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, port, true)))),
            422, "port.reserved", "/runtime/ports/0/publishedPort");
        await ErrorAt(await tenant.Admin.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, port, false)))),
            422, "port.reserved", "/runtime/ports/0/publishedPort");
        var allowed = await tenant.Admin.CreateAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, port, true))));
        Assert.Equal(port, allowed["runtime"]!["ports"]![0]!["publishedPort"]!.GetValue<int>());
    }

    [RequiresDatabaseTheory]
    [InlineData(80)]
    [InlineData(22)]
    [InlineData(443)]
    public async Task ReservedPrivilegedPorts_AreReservedForAdministratorsToo_UntilAllowed(int port)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        await ErrorAt(await tenant.Admin.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, port, null)))), 422, "port.reserved", "/runtime/ports/0/publishedPort");
        Assert.Equal(HttpStatusCode.Created, (await tenant.Admin.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, port, true))))).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Ports_ThatAreNotPublished_ArePlainDeveloperBusiness_AndEveryViolationIsListed()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();

        var app = await tenant.Developer.CreateAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((80, null, null), (443, null, null), (8080, 1024, null), (9000, 49152, null))));
        Assert.Equal(4, app["runtime"]!["ports"]!.AsArray().Count);

        var response = await tenant.Developer.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((1, 8080, null), (2, 80, null), (3, 22, null), (4, 8081, null))));
        var body = await ErrorAt(response, 403, "port.privileged_requires_admin", "/runtime/ports/1/publishedPort");
        Assert.Equal(["/runtime/ports/1/publishedPort", "/runtime/ports/2/publishedPort"], body["errors"]!.AsArray().Select(e => e!["pointer"]!.GetValue<string>()));

        // Privileged outranks reserved for a Developer: 403 first, then (for a port that is only reserved) 422.
        var mixed = await tenant.Developer.PostAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((1, 5080, null), (2, 80, null))));
        await ErrorAt(mixed, 403, "port.privileged_requires_admin", "/runtime/ports/1/publishedPort");
    }

    [RequiresDatabaseFact]
    public async Task Ports_ThatAreAlreadyConfigured_AreNotCheckedAgain_WhenADeveloperEditsTheList()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var app = await tenant.Admin.CreateAsync("/api/v1/applications", ImageApp(envId, serverId, Ports((8080, 443, true), (8081, 8081, null))));
        var url = $"/api/v1/applications/{app.Id()}";

        // The Developer re-sends the Administrator's port 443 unchanged and adds one: fine. Changing it, or adding another privileged one, is not.
        var patched = await tenant.Developer.PatchOkAsync(url, new { runtime = new { ports = new[] { new { containerPort = 8080, publishedPort = 443 }, new { containerPort = 8081, publishedPort = 8081 }, new { containerPort = 9090, publishedPort = 9090 } } } });
        Assert.Equal(3, patched["runtime"]!["ports"]!.AsArray().Count);
        await (await tenant.Developer.PatchAsync(url, new { runtime = new { ports = new[] { new { containerPort = 8080, publishedPort = 444 }, new { containerPort = 9, publishedPort = 80 } } } }))
            .AssertProblemAsync(403, "port.privileged_requires_admin");
        // Moving the Administrator's port to a reserved one is refused for the Developer; the original stays.
        await (await tenant.Developer.PatchAsync(url, new { runtime = new { ports = new[] { new { containerPort = 8080, publishedPort = 5080 } } } })).AssertProblemAsync(422, "port.reserved");
        Assert.Contains(443, (await tenant.Viewer.GetJsonAsync(url))["runtime"]!["ports"]!.AsArray().Select(p => p!["publishedPort"]!.GetValue<int>()));
    }

    [RequiresDatabaseFact]
    public async Task Services_FollowTheSamePortRules()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        object Service(int port, bool? allow = null) => new { name = Tenant.Unique("svc"), environmentId = envId, serverId, templateKey = "redis", runtime = new { ports = new[] { new { containerPort = 6379, publishedPort = port, allowReserved = allow } } } };

        await (await tenant.Developer.PostAsync("/api/v1/services", Service(80))).AssertProblemAsync(403, "port.privileged_requires_admin");
        await (await tenant.Developer.PostAsync("/api/v1/services", Service(5080))).AssertProblemAsync(422, "port.reserved");
        await (await tenant.Admin.PostAsync("/api/v1/services", Service(5080))).AssertProblemAsync(422, "port.reserved");
        Assert.Equal(HttpStatusCode.Created, (await tenant.Admin.PostAsync("/api/v1/services", Service(5080, true))).StatusCode);
        var service = await tenant.Developer.CreateAsync("/api/v1/services", Service(16379));
        await (await tenant.Developer.PatchAsync($"/api/v1/services/{service.Id()}", new { runtime = new { ports = new[] { new { containerPort = 6379, publishedPort = 22 } } } }))
            .AssertProblemAsync(403, "port.privileged_requires_admin");
    }

    // ---- compose --------------------------------------------------------------------------------------------------------------------

    private const string Benign = """
        services:
          web:
            image: nginx:1.27
            ports: ["8080:80"]
            volumes: ["data:/var/lib/data"]
            environment:
              MODE: prod
        volumes:
          data: {}
        """;

    private const string Privileged = """
        services:
          web:
            image: nginx
            privileged: true
            pid: host
          sidecar:
            image: busybox
            volumes:
              - /etc:/host-etc
        """;

    [RequiresDatabaseFact]
    public async Task Compose_OrdinaryContent_IsFineForDevelopers()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", ComposeApp(envId, serverId, Benign));
        Assert.Equal(Benign, app["compose"]!["inlineContent"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Compose_RootEquivalentOptions_NeedAnAdministrator_AndNameTheOffendingKeys()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();

        var response = await tenant.Developer.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, Privileged));
        var body = await ErrorAt(response, 403, "compose.option_requires_admin", "/compose/inlineContent");
        Assert.Equal(["/services/web/privileged", "/services/web/pid", "/services/sidecar/volumes/0"], body["pointers"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Contains("/services/web/privileged", body["detail"]!.GetValue<string>());

        // Tokens follow their owner's role.
        await (await tenant.Token(OrganizationRole.Developer, "*").PostAsync("/api/v1/applications", ComposeApp(envId, serverId, Privileged))).AssertProblemAsync(403, "compose.option_requires_admin");

        // Administrators may use them (the Docker socket and the host's /etc are the denylist's business, tested next).
        var allowed = Privileged.Replace("/etc:/host-etc", "/srv/shared:/shared");
        foreach (var client in new[] { tenant.Admin, tenant.Owner, tenant.Token(OrganizationRole.Admin, "write") })
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, allowed))).StatusCode);
    }

    [RequiresDatabaseTheory]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"/var/run/docker.sock:/var/run/docker.sock\"]\n", "/services/web/volumes/0")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"/etc:/e\"]\n", "/services/web/volumes/0")]
    [InlineData("services:\n  web:\n    image: x\n    volumes:\n      - type: bind\n        source: /proc\n        target: /p\n", "/services/web/volumes/0/source")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"/var/lib/aethera/pki:/pki:ro\"]\n", "/services/web/volumes/0")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"../escape:/e\"]\n", "/services/web/volumes/0")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"~/.ssh:/ssh\"]\n", "/services/web/volumes/0")]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"${HOME}:/home\"]\n", "/services/web/volumes/0")]
    [InlineData("services:\n  web:\n    image: x\nvolumes:\n  v:\n    driver_opts: { type: none, o: bind, device: /etc }\n", "/volumes/v/driver_opts/device")]
    [InlineData("services:\n  web:\n    image: x\nsecrets:\n  s:\n    file: /etc/shadow\n", "/secrets/s/file")]
    [InlineData("x-m: &m\n  volumes: [\"/var/run:/r\"]\nservices:\n  web:\n    <<: *m\n    image: x\n", "/services/web/volumes/0")]
    public async Task Compose_HostBindSources_StillPassTheDenylist_ForAdministrators(string yaml, string pointer)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        foreach (var client in new[] { tenant.Admin, tenant.Owner })
        {
            var body = await ErrorAt(await client.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, yaml)), 422, "volume.host_path_forbidden", "/compose/inlineContent");
            Assert.Equal([pointer], body["pointers"]!.AsArray().Select(p => p!.GetValue<string>()));
        }

        // Developers are stopped earlier, by the role.
        await (await tenant.Developer.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, yaml))).AssertProblemAsync(403, "compose.option_requires_admin");
    }

    [RequiresDatabaseTheory]
    [InlineData("services:\n  web:\n    image: x\n    volumes: [\"/srv/data:/data\", \"./local:/l\", \"/var/lib/aethera/volumes/x:/v\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    privileged: true\n    network_mode: host\n    cap_add: [SYS_ADMIN]\n    devices: [\"/dev/fuse\"]\n    security_opt: [\"seccomp=unconfined\"]\n")]
    [InlineData("services:\n  web:\n    image: x\n    ports: [\"80:80\", \"443:443\"]\n")]
    public async Task Compose_AdministratorsMayUseThem(string yaml)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        Assert.Equal(HttpStatusCode.Created, (await tenant.Admin.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, yaml))).StatusCode);
    }

    [RequiresDatabaseTheory]
    [InlineData("services: [unclosed")]
    [InlineData("- not\n- a mapping")]
    [InlineData("version: '3'\n")]
    [InlineData("services:\n  web:\n    image: a\n---\nservices: {}\n")]
    [InlineData("services:\n\tweb: {}\n")]
    public async Task Compose_InvalidYaml_Is422ComposeInvalid_ForEveryRole(string yaml)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        foreach (var client in new[] { tenant.Developer, tenant.Admin })
            await ErrorAt(await client.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, yaml)), 422, "compose.invalid", "/compose/inlineContent");
    }

    [RequiresDatabaseFact]
    public async Task Compose_YamlBombs_AndHugeDocuments_AreRefused()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();

        var bomb = "x-l0: &l0 [lol, lol, lol, lol, lol, lol, lol, lol, lol]\n"
            + string.Concat(Enumerable.Range(1, 9).Select(i => $"x-l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 9))}]\n"))
            + "services:\n  web:\n    image: nginx\n";
        foreach (var client in new[] { tenant.Developer, tenant.Admin })
        {
            var body = await ErrorAt(await client.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, bomb)), 422, "compose.invalid", "/compose/inlineContent");
            Assert.Contains("aliases", body["detail"]!.GetValue<string>());
        }

        // Past the character cap the request validator answers; between the byte and character caps the inspector does.
        var big = "services:\n  web:\n    image: nginx\n    command: \"" + new string('a', 270 * 1024) + "\"\n";
        Assert.Contains(("/compose/inlineContent", "too_long"), await (await tenant.Admin.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, big))).ValidationErrorsAsync());
        var wide = "services:\n  web:\n    image: nginx\n    command: \"" + new string('é', 140_000) + "\"\n";
        await ErrorAt(await tenant.Admin.PostAsync("/api/v1/applications", ComposeApp(envId, serverId, wide)), 422, "compose.invalid", "/compose/inlineContent");
    }

    [RequiresDatabaseFact]
    public async Task Compose_OnUpdate_IsCheckedAgain_ButNotWhenUnchanged()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var ok = await tenant.Developer.CreateAsync("/api/v1/applications", ComposeApp(envId, serverId, Benign));
        var url = $"/api/v1/applications/{ok.Id()}";

        await ErrorAt(await tenant.Developer.PatchAsync(url, new { compose = new { inlineContent = Privileged } }), 403, "compose.option_requires_admin", "/compose/inlineContent");
        await ErrorAt(await tenant.Developer.PatchAsync(url, new { compose = new { inlineContent = "services: [" } }), 422, "compose.invalid", "/compose/inlineContent");
        Assert.Equal(Benign, (await tenant.Viewer.GetJsonAsync(url))["compose"]!["inlineContent"]!.GetValue<string>());

        // The Administrator sets it up; the Developer can keep editing the application and even re-send the content as it is.
        var privileged = Privileged.Replace("/etc:/host-etc", "/srv/s:/s");
        await tenant.Admin.PatchOkAsync(url, new { compose = new { inlineContent = privileged } });
        Assert.Equal(HttpStatusCode.OK, (await tenant.Developer.PatchAsync(url, new { description = "edited by a developer" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Developer.PatchAsync(url, new { compose = new { inlineContent = privileged } })).StatusCode);
        await (await tenant.Developer.PatchAsync(url, new { compose = new { inlineContent = privileged + "# changed\n" } })).AssertProblemAsync(403, "compose.option_requires_admin");
    }

    [RequiresDatabaseFact]
    public async Task Compose_InAServiceConfig_IsInspectedToo()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        object Body(string yaml) => new { name = Tenant.Unique("svc"), environmentId = envId, serverId, templateKey = "redis", config = new { compose = yaml } };

        await ErrorAt(await tenant.Developer.PostAsync("/api/v1/services", Body(Privileged)), 403, "compose.option_requires_admin", "/config/compose");
        await ErrorAt(await tenant.Admin.PostAsync("/api/v1/services", Body("not: [valid")), 422, "compose.invalid", "/config/compose");
        var service = await tenant.Developer.CreateAsync("/api/v1/services", Body(Benign));
        var url = $"/api/v1/services/{service.Id()}";
        await ErrorAt(await tenant.Developer.PatchAsync(url, new { config = new { compose = Privileged } }), 403, "compose.option_requires_admin", "/config/compose");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Developer.PatchAsync(url, new { config = new { other = 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Admin.PatchAsync(url, new { config = new { compose = Privileged.Replace("/etc:/host-etc", "/srv:/s") } })).StatusCode);
    }

    // ---- build inputs ---------------------------------------------------------------------------------------------------------------

    private static JsonObject GitApp(string environmentId, string serverId, string? url = null, string? branch = null, JsonObject? build = null)
    {
        var app = new JsonObject
        {
            ["name"] = Tenant.Unique("git"), ["environmentId"] = environmentId, ["serverId"] = serverId, ["sourceKind"] = "git",
            ["gitSource"] = new JsonObject { ["repositoryUrl"] = url ?? "https://github.com/org/repo.git" },
        };
        if (branch is not null) app["gitSource"]!["branch"] = branch;
        if (build is not null) app["build"] = build;
        return app;
    }

    [RequiresDatabaseTheory]
    [InlineData("context", "..")]
    [InlineData("context", "../outside")]
    [InlineData("context", "a/../../b")]
    [InlineData("context", "/etc")]
    [InlineData("context", "~/x")]
    [InlineData("context", "C:\\x")]
    [InlineData("context", "a\\b")]
    [InlineData("context", "-evil")]
    [InlineData("dockerfilePath", "../Dockerfile")]
    [InlineData("dockerfilePath", "/Dockerfile")]
    [InlineData("dockerfilePath", "a\\Dockerfile")]
    [InlineData("outputDirectory", "../dist")]
    [InlineData("outputDirectory", "/var/www")]
    [InlineData("outputDirectory", "dist\\out")]
    public async Task BuildPaths_MustStayInsideTheRepository(string property, string value)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var build = new JsonObject { [property] = value };

        Assert.Contains(($"/build/{property}", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/applications", GitApp(envId, serverId, build: build))).ValidationErrorsAsync());

        var ok = await tenant.Developer.CreateAsync("/api/v1/applications", GitApp(envId, serverId));
        Assert.Contains(($"/build/{property}", "pattern"), await (await tenant.Developer.PatchAsync($"/api/v1/applications/{ok.Id()}", new { build })).ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task BuildPaths_RelativeOnesAreFine_AndSoIsTheComposeFilePathRule()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var build = new JsonObject { ["context"] = "./apps/web", ["dockerfilePath"] = "docker/Dockerfile.prod", ["outputDirectory"] = "dist" };
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", GitApp(envId, serverId, build: build));
        Assert.Equal("./apps/web", app["build"]!["context"]!.GetValue<string>());

        object Compose(string path) => new
        {
            name = Tenant.Unique("c"), environmentId = envId, serverId, sourceKind = "compose",
            gitSource = new { repositoryUrl = "https://github.com/org/repo.git" }, compose = new { filePath = path },
        };
        foreach (var bad in new[] { "../compose.yml", "/etc/compose.yml", "ops\\compose.yml", "-f" })
            Assert.Contains(("/compose/filePath", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/applications", Compose(bad))).ValidationErrorsAsync());
        Assert.Equal(HttpStatusCode.Created, (await tenant.Developer.PostAsync("/api/v1/applications", Compose("deploy/compose.yml"))).StatusCode);
    }

    [RequiresDatabaseTheory]
    [InlineData("-x")]
    [InlineData("--upload-pack=touch /tmp/x")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("main\n")]
    [InlineData("a\u0007b")]
    public async Task GitBranches_CannotInjectOptionsOrContainWhitespace(string branch)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        Assert.Contains(("/gitSource/branch", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/applications", GitApp(envId, serverId, branch: branch))).ValidationErrorsAsync());
        var ok = await tenant.Developer.CreateAsync("/api/v1/applications", GitApp(envId, serverId, branch: "feature/x"));
        Assert.Contains(("/gitSource/branch", "pattern"),
            await (await tenant.Developer.PatchAsync($"/api/v1/applications/{ok.Id()}", new { gitSource = new { branch } })).ValidationErrorsAsync());
    }

    [RequiresDatabaseTheory]
    [InlineData("--abcdef1")]
    [InlineData("-abcdef1")]
    [InlineData("abcdef1\n")]
    [InlineData("abc")]
    public async Task GitCommitPins_MustBeHex(string commit)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var app = GitApp(envId, serverId);
        app["gitSource"]!["commitPin"] = commit;
        Assert.Contains(("/gitSource/commitPin", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/applications", app)).ValidationErrorsAsync());
    }

    [RequiresDatabaseTheory]
    [InlineData("-oProxyCommand=touch/tmp/pwn")]
    [InlineData("ssh://-oProxyCommand=touch%20/tmp/pwn/repo")]
    [InlineData("ssh://git@-oProxyCommand=x/repo")]
    [InlineData("git@-oProxyCommand=x:org/repo.git")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ext::sh -c id")]
    [InlineData("ext::sh%20-c%20id")]
    [InlineData("https://github.com/org/repo.git\n")]
    public async Task RepositoryUrls_CannotInjectOptionsOrUseDangerousTransports(string url)
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        Assert.Contains(("/gitSource/repositoryUrl", "pattern"), await (await tenant.Developer.PostAsync("/api/v1/applications", GitApp(envId, serverId, url))).ValidationErrorsAsync());
        var ok = await tenant.Developer.CreateAsync("/api/v1/applications", GitApp(envId, serverId));
        Assert.Contains(("/gitSource/repositoryUrl", "pattern"),
            await (await tenant.Developer.PatchAsync($"/api/v1/applications/{ok.Id()}", new { gitSource = new { repositoryUrl = url } })).ValidationErrorsAsync());
    }
}
