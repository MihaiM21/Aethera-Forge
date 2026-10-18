using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.SelfOps;
using Aethera.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Tests.SelfOps;

/// <summary>The API with a stubbed GitHub, a temporary updater folder, fast job workers and a fake agent transport.</summary>
public sealed class SystemApiFixture : IDisposable
{
    public const string AgentSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public SystemApiFixture()
    {
        ControlDirectory = Path.Combine(Path.GetTempPath(), "aethera-system-api-" + Guid.NewGuid().ToString("N"));
        Transport = new FakeServerTransport();
        Base = new AetheraApiFactory();
        Factory = Jobs.JobsApiFixture.Configure(Base, builder =>
        {
            builder.UseSetting("Aethera:Updates:ControlDirectory", ControlDirectory);
            builder.UseSetting("Aethera:Maintenance:BackupEnabled", "false");
            builder.UseSetting("Aethera:Maintenance:PruneEnabled", "false");
            builder.UseSetting("Aethera:Agents:AutoLocalServer", "false"); // tests drive LocalServerAutoEnroller themselves
        }).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IServerTransport>();
            services.AddSingleton<IServerTransport>(Transport);
            services.RemoveAll<ReleaseChecker>();
            services.AddSingleton(sp => new ReleaseChecker(
                new HttpClient(new GitHubStub(this)), sp.GetRequiredService<IOptions<UpdateOptions>>(), sp.GetRequiredService<IClock>(),
                NullLogger<ReleaseChecker>.Instance) { CurrentVersion = "1.0.0" });
        }));
    }

    public AetheraApiFactory Base { get; }
    public WebApplicationFactory<Program> Factory { get; }
    public FakeServerTransport Transport { get; }
    public string ControlDirectory { get; }

    /// <summary>The tag the stubbed GitHub answers with.</summary>
    public string LatestTag { get; set; } = "v1.2.0";

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void MarkUpdaterAlive()
    {
        Directory.CreateDirectory(ControlDirectory);
        File.WriteAllText(Path.Combine(ControlDirectory, "alive"), "");
    }

    /// <summary>What the agent container of the compose project does while it waits to be enrolled.</summary>
    public void MarkLocalAgentWaiting()
    {
        Directory.CreateDirectory(ControlDirectory);
        File.WriteAllText(Path.Combine(ControlDirectory, "agent-alive"), "");
    }

    public void ResetUpdater()
    {
        if (Directory.Exists(ControlDirectory)) Directory.Delete(ControlDirectory, recursive: true);
    }

    public void Dispose()
    {
        Base.Dispose();
        ResetUpdater();
    }

    private sealed class GitHubStub(SystemApiFixture owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.EndsWith("/SHA256SUMS", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{AgentSha}  aethera-agent-linux-amd64\n") });
            var version = owner.LatestTag.TrimStart('v');
            var json = $$"""
                {"tag_name":"{{owner.LatestTag}}","html_url":"https://example.test/r/{{version}}","body":"Notes for {{version}}","prerelease":false,"draft":false,
                 "assets":[{"name":"aethera-agent-linux-amd64","browser_download_url":"https://dl.example.test/{{version}}/aethera-agent-linux-amd64","size":10},
                           {"name":"SHA256SUMS","browser_download_url":"https://dl.example.test/{{version}}/SHA256SUMS","size":5}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SystemApiCollection : ICollectionFixture<SystemApiFixture>
{
    public const string Name = "system-api";
}

[Collection(SystemApiCollection.Name)]
public sealed class SystemApiTests(SystemApiFixture fixture)
{
    private async Task<JsonNode> CheckAsync(Tenant t)
    {
        var response = await t.Admin.PostAsync("/api/v1/system/version/check", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync();
    }

    // ================================================================ version

    [RequiresDatabaseFact]
    public async Task Any_member_can_read_the_version_and_only_an_administrator_can_ask_for_a_check()
    {
        var t = await fixture.NewTenantAsync();
        fixture.ResetUpdater();
        fixture.LatestTag = "v1.2.0";

        var version = await t.Viewer.GetJsonAsync("/api/v1/system/version");
        Assert.Equal("1.0.0", version["current"]!.GetValue<string>());
        Assert.False(version["updater"]!["available"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.PostAsync("/api/v1/system/version/check", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsync("/api/v1/system/update", new { })).StatusCode);

        var checkedNow = await CheckAsync(t);
        Assert.Equal("1.2.0", checkedNow["latest"]!["version"]!.GetValue<string>());
        Assert.Equal("Notes for 1.2.0", checkedNow["latest"]!["notes"]!.GetValue<string>());
        Assert.True(checkedNow["updateAvailable"]!.GetValue<bool>());
        Assert.NotNull(checkedNow["checkedAt"]);
    }

    [RequiresDatabaseFact]
    public async Task The_version_endpoints_require_a_login()
    {
        var anonymous = fixture.Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/system/version")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/system/maintenance")).StatusCode);
    }

    // ================================================================ update

    [RequiresDatabaseFact]
    public async Task Updating_without_an_updater_explains_how_to_update_on_the_host()
    {
        var t = await fixture.NewTenantAsync();
        fixture.ResetUpdater();
        fixture.LatestTag = "v1.2.0";
        await CheckAsync(t);

        var response = await t.Admin.PostAsync("/api/v1/system/update", new { });

        await response.AssertProblemAsync(409, "system.updater_unavailable");
        Assert.Contains("aethera update", await response.Content.ReadAsStringAsync());
    }

    [RequiresDatabaseFact]
    public async Task An_update_is_handed_to_the_updater_audited_and_not_started_twice()
    {
        var t = await fixture.NewTenantAsync();
        fixture.ResetUpdater();
        fixture.LatestTag = "v1.2.0";
        await CheckAsync(t);
        fixture.MarkUpdaterAlive();

        var started = await t.Admin.PostAsync("/api/v1/system/update", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var body = await started.ReadAsync();
        Assert.Equal("requested", body["state"]!.GetValue<string>());
        Assert.Equal("1.2.0", body["targetVersion"]!.GetValue<string>());

        var request = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.ControlDirectory, "request.json")))!;
        Assert.Equal("1.2.0", request["version"]!.GetValue<string>());
        Assert.Equal(t.Identity.UserId.ToString(), request["requestedBy"]!.GetValue<string>());

        (await t.Admin.PostAsync("/api/v1/system/update", new { })).AssertProblemAsync(409, "system.update_in_progress").GetAwaiter().GetResult();
        Assert.Single(await t.AuditAsync(action: "system.update_requested"));

        var version = await t.Viewer.GetJsonAsync("/api/v1/system/version");
        Assert.Equal("requested", version["updater"]!["state"]!.GetValue<string>());
        Assert.True(version["updater"]!["available"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task There_is_nothing_to_update_to_when_the_running_version_is_the_newest()
    {
        var t = await fixture.NewTenantAsync();
        fixture.ResetUpdater();
        fixture.MarkUpdaterAlive();
        fixture.LatestTag = "v1.0.0";
        var version = await CheckAsync(t);
        Assert.False(version["updateAvailable"]!.GetValue<bool>());

        (await t.Admin.PostAsync("/api/v1/system/update", new { })).AssertProblemAsync(422, "system.no_update_available").GetAwaiter().GetResult();
        Assert.False(File.Exists(Path.Combine(fixture.ControlDirectory, "request.json")));
        fixture.LatestTag = "v1.2.0";
    }

    [RequiresDatabaseFact]
    public async Task Only_the_newest_release_can_be_requested()
    {
        var t = await fixture.NewTenantAsync();
        fixture.ResetUpdater();
        fixture.MarkUpdaterAlive();
        fixture.LatestTag = "v1.2.0";
        await CheckAsync(t);

        (await t.Admin.PostAsync("/api/v1/system/update", new { version = "1.1.0" })).AssertProblemAsync(422, "system.version_unavailable").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Accepted, (await t.Admin.PostAsync("/api/v1/system/update", new { version = "v1.2.0" })).StatusCode);
    }

    // ================================================================ maintenance

    [RequiresDatabaseFact]
    public async Task Maintenance_is_for_administrators_and_reports_settings_and_the_last_run()
    {
        var t = await fixture.NewTenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.GetAsync("/api/v1/system/maintenance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.PostAsync("/api/v1/system/maintenance/run", new { })).StatusCode);

        var run = await t.Admin.PostAsync("/api/v1/system/maintenance/run", new { });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var report = await run.ReadAsync();
        Assert.Empty(report["errors"]!.AsArray());
        Assert.Equal(0, report["pruneJobsQueued"]!.GetValue<int>());

        var maintenance = await t.Admin.GetJsonAsync("/api/v1/system/maintenance");
        Assert.Equal(30, maintenance["settings"]!["logRetentionDays"]!.GetValue<int>());
        Assert.Equal(365, maintenance["settings"]!["auditRetentionDays"]!.GetValue<int>());
        Assert.False(maintenance["settings"]!["backupEnabled"]!.GetValue<bool>());
        Assert.NotNull(maintenance["lastRun"]);
        Assert.Single(await t.AuditAsync(action: "system.maintenance_run"));
    }

    // ================================================================ agent update

    private async Task SetArchitectureAsync(string serverId, string? architecture, string? agentVersion = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var server = await db.Servers.FirstAsync(s => s.Id == Guid.Parse(serverId));
        server.Facts.Architecture = architecture;
        server.AgentVersion = agentVersion;
        await db.SaveChangesAsync();
    }

    private async Task<(string Status, string? Error, string? Result)> WaitForJobAsync(string jobId)
    {
        (string Status, string? Error, string? Result) last = default!;
        await GatewayFixture.EventuallyAsync(async () =>
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var job = await scope.ServiceProvider.GetRequiredService<AetheraDbContext>().Jobs.AsNoTracking().FirstAsync(j => j.Id == Guid.Parse(jobId));
            last = (job.Status.ToString(), job.ErrorJson, job.ResultJson);
            return job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
        }, "the agent update job to finish", timeoutMs: 15000);
        return last;
    }

    [RequiresDatabaseFact]
    public async Task An_agent_is_updated_to_the_release_asset_for_its_architecture_with_the_checksum_from_the_release()
    {
        var t = await fixture.NewTenantAsync();
        fixture.LatestTag = "v1.2.0";
        await CheckAsync(t);
        var server = await t.CreateServerAsync();
        await SetArchitectureAsync(server.Id(), "amd64", agentVersion: "1.0.0");
        AgentSelfUpdateCommand? sent = null;
        fixture.Transport.On<AgentSelfUpdateCommand, SelfUpdateOutcome>(c =>
        {
            sent = c;
            return new SelfUpdateOutcome("1.0.0", "1.2.0", Restarting: true);
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.PostAsync($"/api/v1/servers/{server.Id()}/agent/update", new { })).StatusCode);
        var accepted = await t.Admin.PostAsync($"/api/v1/servers/{server.Id()}/agent/update", new { });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var job = await WaitForJobAsync((await accepted.ReadAsync()).Id());

        Assert.Equal("Succeeded", job.Status);
        Assert.NotNull(sent);
        Assert.Equal("https://dl.example.test/1.2.0/aethera-agent-linux-amd64", sent.Url);
        Assert.Equal(SystemApiFixture.AgentSha, sent.Sha256);
        Assert.Equal("1.2.0", sent.TargetVersion);
        Assert.True(JsonNode.Parse(job.Result!)!["updated"]!.GetValue<bool>());
        Assert.Single(await t.AuditAsync(server.Id(), "server.agent_update_requested"));
    }

    [RequiresDatabaseFact]
    public async Task An_agent_that_is_already_current_is_left_alone()
    {
        var t = await fixture.NewTenantAsync();
        fixture.LatestTag = "v1.2.0";
        await CheckAsync(t);
        var server = await t.CreateServerAsync();
        await SetArchitectureAsync(server.Id(), "amd64", agentVersion: "1.2.0");
        fixture.Transport.Commands.Clear();

        var accepted = await t.Admin.PostAsync($"/api/v1/servers/{server.Id()}/agent/update", new { });
        var job = await WaitForJobAsync((await accepted.ReadAsync()).Id());

        Assert.Equal("Succeeded", job.Status);
        Assert.False(JsonNode.Parse(job.Result!)!["updated"]!.GetValue<bool>());
        Assert.DoesNotContain(fixture.Transport.Commands, c => c.Command is AgentSelfUpdateCommand);
    }

    [RequiresDatabaseFact]
    public async Task An_unknown_cpu_architecture_fails_the_job_with_a_clear_code_and_sends_nothing()
    {
        var t = await fixture.NewTenantAsync();
        fixture.LatestTag = "v1.2.0";
        await CheckAsync(t);
        var server = await t.CreateServerAsync();
        await SetArchitectureAsync(server.Id(), "riscv64");
        fixture.Transport.Commands.Clear();

        var accepted = await t.Admin.PostAsync($"/api/v1/servers/{server.Id()}/agent/update", new { });
        var job = await WaitForJobAsync((await accepted.ReadAsync()).Id());

        Assert.Equal("Failed", job.Status);
        Assert.Contains("agent_update.unsupported_architecture", job.Error);
        Assert.DoesNotContain(fixture.Transport.Commands, c => c.Command is AgentSelfUpdateCommand);
    }

    [RequiresDatabaseFact]
    public async Task Updating_the_agent_of_an_unknown_server_is_a_404()
    {
        var t = await fixture.NewTenantAsync();
        (await t.Admin.PostAsync($"/api/v1/servers/{Guid.NewGuid()}/agent/update", new { })).AssertProblemAsync(404, "server.not_found").GetAwaiter().GetResult();
    }

    // ================================================================ this machine as a server

    [RequiresDatabaseFact]
    public async Task Enabling_this_machine_without_a_waiting_agent_container_is_refused_and_creates_nothing()
    {
        var t = await fixture.NewTenantAsync();
        fixture.ResetUpdater();

        var response = await t.Admin.PostAsync("/api/v1/system/local-server", new { });

        await response.AssertProblemAsync(409, "system.updater_unavailable");
        var state = await t.Admin.GetJsonAsync("/api/v1/system/local-server");
        Assert.False(state["updaterAvailable"]!.GetValue<bool>());
        Assert.Null(state["serverId"]);
    }

    [RequiresDatabaseFact]
    public async Task Enabling_this_machine_creates_the_server_and_hands_a_join_token_to_the_waiting_agent()
    {
        var t = await fixture.NewTenantAsync();
        fixture.ResetUpdater();
        fixture.MarkLocalAgentWaiting();

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.PostAsync("/api/v1/system/local-server", new { })).StatusCode);
        var started = await t.Admin.PostAsync("/api/v1/system/local-server", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var body = await started.ReadAsync();
        Assert.Equal("requested", body["state"]!.GetValue<string>());
        Assert.False(body["connected"]!.GetValue<bool>());

        var request = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.ControlDirectory, "enable-agent.json")))!;
        Assert.StartsWith("aeth_join_", request["joinToken"]!.GetValue<string>());
        Assert.Equal("local", request["name"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", request["caSha256"]!.GetValue<string>());

        var server = await t.Admin.GetJsonAsync($"/api/v1/servers/{body["serverId"]!.GetValue<string>()}");
        Assert.Equal("local", server["name"]!.GetValue<string>());
        Assert.Contains("master", server["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Contains("build", server["roles"]!.AsArray().Select(r => r!.GetValue<string>()));

        // The request is waiting: a second click does not issue a second token.
        await (await t.Admin.PostAsync("/api/v1/system/local-server", new { })).AssertProblemAsync(409, "system.local_server_in_progress");
        Assert.Single(await t.AuditAsync(action: "system.local_server_requested"));
    }
}

// ================================================================ automatic enrollment of this machine

[CollectionDefinition(Name)]
public sealed class LocalServerAutoEnrollCollection : ICollectionFixture<SystemApiFixture>
{
    public const string Name = "local-server-auto-enroll";
}

[CollectionDefinition(Name)]
public sealed class LocalServerAutoEnrollSkippedCollection : ICollectionFixture<SystemApiFixture>
{
    public const string Name = "local-server-auto-enroll-skipped";
}

/// <summary>The first organization of a fresh database, with the agent container waiting: the machine connects itself, once.</summary>
[Collection(LocalServerAutoEnrollCollection.Name)]
public sealed class LocalServerAutoEnrollTests(SystemApiFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task A_fresh_installation_connects_its_own_machine_once_the_owner_exists_and_only_once()
    {
        fixture.ResetUpdater();
        var enroller = new LocalServerAutoEnroller(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AgentGatewayOptions()), NullLogger<LocalServerAutoEnroller>.Instance);
        var request = Path.Combine(fixture.ControlDirectory, "enable-agent.json");

        // Nobody has registered yet: nothing to do, whether or not the agent container is up.
        fixture.MarkLocalAgentWaiting();
        Assert.False(await enroller.RunOnceAsync(CancellationToken.None));

        // An owner exists but no agent container is waiting (not up yet, or not part of this installation): try again later.
        var t = await fixture.NewTenantAsync();
        fixture.ResetUpdater();
        Assert.False(await enroller.RunOnceAsync(CancellationToken.None));
        Assert.False(File.Exists(request));

        fixture.MarkLocalAgentWaiting();
        Assert.True(await enroller.RunOnceAsync(CancellationToken.None));
        var token = JsonNode.Parse(await File.ReadAllTextAsync(request))!["joinToken"]!.GetValue<string>();
        Assert.StartsWith("aeth_join_", token);
        var servers = await t.Admin.GetJsonAsync("/api/v1/servers");
        Assert.Contains("local", servers["items"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
        Assert.Single(await t.AuditAsync(action: "system.local_server_requested"));

        // The agent took the request; even if the server is deleted afterwards, the decision stands.
        File.Delete(request);
        Assert.True(await enroller.RunOnceAsync(CancellationToken.None));
        Assert.False(File.Exists(request));
        Assert.Single(await t.AuditAsync(action: "system.local_server_requested"));
    }
}

[Collection(SystemApiCollection.Name)]
public sealed class LocalAgentBeaconTests(SystemApiFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task The_ca_fingerprint_is_published_next_to_the_updater_files_for_the_agent_container()
    {
        fixture.ResetUpdater();
        var beacon = fixture.Factory.Services.GetRequiredService<LocalAgentBeacon>();

        await beacon.PublishOnceAsync(CancellationToken.None);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var info = await scope.ServiceProvider.GetRequiredService<Aethera.Infrastructure.Agents.Pki.IInternalCa>().GetPublicInfoAsync(CancellationToken.None);
        Assert.Equal(info.FingerprintSha256, (await File.ReadAllTextAsync(Path.Combine(fixture.ControlDirectory, "ca-sha256"))).Trim());
        Assert.False(File.Exists(Path.Combine(fixture.ControlDirectory, "ca-sha256.tmp")));
    }
}

/// <summary>An organization that already has a server (added by hand): the machine is left alone.</summary>
[Collection(LocalServerAutoEnrollSkippedCollection.Name)]
public sealed class LocalServerAutoEnrollSkippedTests(SystemApiFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task An_installation_that_already_has_a_server_is_left_alone()
    {
        fixture.ResetUpdater();
        fixture.MarkLocalAgentWaiting();
        var t = await fixture.NewTenantAsync();
        var created = await t.Admin.PostAsync("/api/v1/servers", new { name = "edge-1", host = "10.0.0.5" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var enroller = new LocalServerAutoEnroller(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AgentGatewayOptions()), NullLogger<LocalServerAutoEnroller>.Instance);

        Assert.True(await enroller.RunOnceAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(fixture.ControlDirectory, "enable-agent.json")));
    }
}
