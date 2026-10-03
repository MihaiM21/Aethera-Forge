using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProtoDisconnectReason = Aethera.Agent.V1.DisconnectReason;

namespace Aethera.Api.Tests.Agents;

/// <summary>The API with the real agent transport swapped for a <see cref="FakeServerTransport"/>.</summary>
public sealed class AgentsApiFixture : IDisposable
{
    public AgentsApiFixture()
    {
        Base = new AetheraApiFactory();
        Transport = new FakeServerTransport();
        Factory = Base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IServerTransport>();
            services.AddSingleton<IServerTransport>(Transport);
        }));
    }

    public AetheraApiFactory Base { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeServerTransport Transport { get; }

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class AgentsApiCollection : ICollectionFixture<AgentsApiFixture>
{
    public const string Name = "agents-api";
}

[Collection(AgentsApiCollection.Name)]
public sealed class AgentApiTests(AgentsApiFixture fixture)
{
    private static string Base(string serverId) => $"/api/v1/servers/{serverId}";

    private Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action) => WithScopeAsync(sp => action(sp.GetRequiredService<AetheraDbContext>()));

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    // ================================================================ join tokens

    [RequiresDatabaseFact]
    public async Task A_join_token_comes_with_the_install_command_and_is_shown_once()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var response = await tenant.Admin.PostAsync($"{Base(server.Id())}/join-tokens", new { });
        var body = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var token = body["token"]!.GetValue<string>();
        Assert.StartsWith("aeth_join_", token);
        Assert.Equal(server.Id(), body["serverId"]!.GetValue<string>());
        Assert.Equal("localhost:9443", body["endpoint"]!.GetValue<string>());
        var ttl = body["expiresAt"]!.GetValue<DateTimeOffset>() - DateTimeOffset.UtcNow;
        Assert.InRange(ttl.TotalMinutes, 58, 61); // default: 1 hour

        var ca = await tenant.Viewer.GetJsonAsync("/api/v1/agent/ca");
        Assert.Equal(ca["fingerprintSha256"]!.GetValue<string>(), body["caFingerprintSha256"]!.GetValue<string>());
        var command = body["installCommand"]!.GetValue<string>();
        Assert.Contains($"--token {token}", command);
        Assert.Contains("--endpoint localhost:9443", command);
        Assert.Contains($"--ca-sha256 {ca["fingerprintSha256"]!.GetValue<string>()}", command);
        Assert.Contains("/install-agent.sh", command);

        // Only the hash is kept: nothing in the list, the database or the audit trail contains the token.
        var list = await tenant.Admin.GetJsonAsync($"{Base(server.Id())}/join-tokens");
        Assert.Equal("active", list[0]!["state"]!.GetValue<string>());
        Assert.DoesNotContain(token, list.ToJsonString());
        var row = await WithDbAsync(db => db.JoinTokens.AsNoTracking().SingleAsync(t => t.ServerId == Guid.Parse(server.Id())));
        Assert.Equal(JoinTokenService.Hash(token), row.TokenHash);
        var audit = await tenant.AuditAsync(server.Id(), "server.join_token_created");
        Assert.Single(audit);
        Assert.DoesNotContain(token, audit[0].MetadataJson);
    }

    [RequiresDatabaseFact]
    public async Task The_lifetime_can_be_chosen_up_to_24_hours()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var day = await tenant.Admin.CreateAsync($"{Base(server.Id())}/join-tokens", new { ttlMinutes = 1440 });
        Assert.InRange((day["expiresAt"]!.GetValue<DateTimeOffset>() - DateTimeOffset.UtcNow).TotalHours, 23.9, 24.1);

        foreach (var invalid in new[] { 0, 1441, -5 })
        {
            var errors = await (await tenant.Admin.PostAsync($"{Base(server.Id())}/join-tokens", new { ttlMinutes = invalid })).ValidationErrorsAsync();
            Assert.Equal("/ttlMinutes", errors.Single().Location);
        }
    }

    [RequiresDatabaseFact]
    public async Task A_token_can_be_revoked_and_then_no_longer_enrolls()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var created = await tenant.Admin.CreateAsync($"{Base(server.Id())}/join-tokens", new { });
        var token = created["token"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync($"{Base(server.Id())}/join-tokens/{created.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.DeleteAsync($"{Base(server.Id())}/join-tokens/{created.Id()}")).StatusCode); // idempotent
        Assert.Equal("revoked", (await tenant.Admin.GetJsonAsync($"{Base(server.Id())}/join-tokens"))[0]!["state"]!.GetValue<string>());
        Assert.Null(await WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, token, DateTimeOffset.UtcNow, CancellationToken.None)));
        (await tenant.Admin.DeleteAsync($"{Base(server.Id())}/join-tokens/{Guid.NewGuid()}")).AssertProblemAsync(404, "join_token.not_found").GetAwaiter().GetResult();
    }

    [RequiresDatabaseFact]
    public async Task Join_tokens_need_an_administrator_with_the_servers_scope_and_stay_inside_the_organization()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var url = $"{Base(server.Id())}/join-tokens";

        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Developer.PostAsync(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Viewer.GetAsync(url)).StatusCode);
        await (await tenant.Token(OrganizationRole.Admin, "write").PostAsync(url, new { })).AssertProblemAsync(403, "auth.insufficient_scope"); // write does not cover servers
        Assert.Equal(HttpStatusCode.Created, (await tenant.Token(OrganizationRole.Admin, "servers:write").PostAsync(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateAnonymousClient().PostAsync(url, new { })).StatusCode);
        await (await other.Admin.PostAsync(url, new { })).AssertProblemAsync(404, "server.not_found");
    }

    [RequiresDatabaseFact]
    public async Task Resetting_the_agent_revokes_its_certificates_closes_the_stream_and_voids_open_tokens()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var serverId = Guid.Parse(server.Id());
        var name = server["name"]!.GetValue<string>();
        var token = (await tenant.Admin.CreateAsync($"{Base(server.Id())}/join-tokens", new { }))["token"]!.GetValue<string>();

        var ca = fixture.Factory.Services.GetRequiredService<IInternalCa>();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        IssuedAgentCertificate issued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            issued = await ca.IssueAgentCertificateAsync(db, serverId, CsrValidator.Validate(PkiHelpers.NewCsr(key, "CN=a").CsrPem), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await (await tenant.Admin.PostAsync($"{Base(server.Id())}/agent/reset", null)).AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Admin.PostAsync($"{Base(server.Id())}/agent/reset?confirm={Uri.EscapeDataString(name)}", null)).StatusCode);

        Assert.True(ca.IsRevoked(issued.Serial));
        var certificate = await WithDbAsync(db => db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Serial == issued.Serial));
        Assert.NotNull(certificate.RevokedAt);
        Assert.Equal("agent reset", certificate.RevokedReason);
        Assert.Null(await WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, token, DateTimeOffset.UtcNow, CancellationToken.None)));
        Assert.NotEmpty(await tenant.AuditAsync(server.Id(), "server.agent_reset"));
    }

    [RequiresDatabaseFact]
    public async Task Deleting_a_server_revokes_its_certificates_and_closes_its_live_stream()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var serverId = Guid.Parse(server.Id());
        var name = server["name"]!.GetValue<string>();

        var ca = fixture.Factory.Services.GetRequiredService<IInternalCa>();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        IssuedAgentCertificate issued;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            issued = await ca.IssueAgentCertificateAsync(db, serverId, CsrValidator.Validate(PkiHelpers.NewCsr(key, "CN=a").CsrPem), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // A live stream, driven through the same services the API host uses.
        await using var agent = FakeAgent.Start(fixture.Factory.Services, serverId, serial: issued.Serial);
        await agent.ExpectWelcomeAsync();
        Assert.True(fixture.Factory.Services.GetRequiredService<AgentSessionRegistry>().IsConnected(serverId));
        await GatewayFixture.EventuallyAsync(async () => (await WithDbAsync(db => db.Servers.AsNoTracking().SingleAsync(s => s.Id == serverId))) is { AgentStatus: AgentStatus.Connected, CertSerial: not null }, "the connect bookkeeping to finish");

        var response = await tenant.Admin.DeleteAsync($"{Base(server.Id())}?confirm={Uri.EscapeDataString(name)}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(ProtoDisconnectReason.Revoked, (await agent.NextDisconnectAsync()).Reason);
        Assert.True(ca.IsRevoked(issued.Serial));
        Assert.NotNull((await WithDbAsync(db => db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Serial == issued.Serial))).RevokedAt);
    }

    // ================================================================ CA

    [RequiresDatabaseFact]
    public async Task The_CA_endpoint_publishes_the_certificate_agents_pin()
    {
        var tenant = await fixture.NewTenantAsync();
        var ca = await tenant.Viewer.GetJsonAsync("/api/v1/agent/ca");
        using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(ca["certificatePem"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(), ca["fingerprintSha256"]!.GetValue<string>());
        Assert.DoesNotContain("PRIVATE", ca.ToJsonString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateAnonymousClient().GetAsync("/api/v1/agent/ca")).StatusCode);
    }

    // ================================================================ status

    [RequiresDatabaseFact]
    public async Task Status_is_reported_as_separate_axes_with_the_blocking_layer_named()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var serverId = Guid.Parse(server.Id());

        var fresh = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/status");
        Assert.Equal("unknown", fresh["agent"]!["health"]!.GetValue<string>());
        Assert.Null(fresh["firstFailingLayer"]);
        Assert.Null(fresh["session"]);

        var status = fixture.Factory.Services.GetRequiredService<Aethera.Infrastructure.Agents.Status.ServerStatusService>();
        await status.AgentConnectedAsync(serverId, "1.0.0", "linux", "amd64");
        await status.HeartbeatAsync(serverId, Aethera.Domain.DockerStatus.Running);
        var up = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/status");
        Assert.Equal("available", up["agent"]!["health"]!.GetValue<string>());
        Assert.Equal("available", up["docker"]!["health"]!.GetValue<string>());
        Assert.Equal("available", up["server"]!["health"]!.GetValue<string>());

        await status.AgentDisconnectedAsync(serverId, "stream closed");
        var down = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/status");
        Assert.Equal("unavailable", down["agent"]!["health"]!.GetValue<string>());
        Assert.Equal("unknown", down["docker"]!["health"]!.GetValue<string>());
        Assert.Equal("agent", down["docker"]!["blockedBy"]!.GetValue<string>());
        Assert.True(down["docker"]!["stale"]!.GetValue<bool>());
        Assert.Equal("agent", down["application"]!["blockedBy"]!.GetValue<string>());
        Assert.Equal("agent", down["firstFailingLayer"]!.GetValue<string>());

        var events = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/events?limit=10");
        Assert.Contains(events.AsArray(), e => e!["axis"]!.GetValue<string>() == "agent" && e["newValue"]!.GetValue<string>() == "unavailable");
        await (await tenant.Viewer.GetAsync($"{Base(server.Id())}/events?limit=1000")).AssertProblemAsync(400, "validation.invalid_parameter");
    }

    // ================================================================ metrics

    private async Task<Guid> SeedSamplesAsync(Guid serverId, int count, MetricResolution resolution, DateTimeOffset start, TimeSpan step, string? container = null)
    {
        await WithDbAsync(async db =>
        {
            for (var i = 0; i < count; i++)
                db.MetricSamples.Add(new MetricSample
                {
                    ServerId = serverId, ContainerId = container, Resolution = resolution, Timestamp = start + step * i, CpuPercent = i, MemoryUsedBytes = 100 + i,
                    MemoryTotalBytes = 1000, NetRxBytes = i * 1000L, NetTxBytes = 5000, Load1 = 0.5,
                });
            return await db.SaveChangesAsync();
        });
        return serverId;
    }

    [RequiresDatabaseFact]
    public async Task The_latest_metrics_return_the_newest_host_and_container_samples()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var serverId = Guid.Parse(server.Id());
        Assert.Null((await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/metrics/latest"))["host"]);

        var now = DateTimeOffset.UtcNow;
        await SeedSamplesAsync(serverId, 3, MetricResolution.Raw, now.AddSeconds(-40), TimeSpan.FromSeconds(10));
        await SeedSamplesAsync(serverId, 3, MetricResolution.Raw, now.AddSeconds(-40), TimeSpan.FromSeconds(10), container: "c1");
        var latest = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/metrics/latest");
        Assert.Equal(2, latest["host"]!["cpuPercent"]!.GetValue<double>());
        Assert.Equal("c1", Assert.Single(latest["containers"]!.AsArray())!["containerId"]!.GetValue<string>());
        Assert.False(latest["stale"]!.GetValue<bool>());

        await SeedSamplesAsync(serverId, 1, MetricResolution.Raw, now.AddMinutes(-30), TimeSpan.FromSeconds(1));
        var tenant2 = await fixture.NewTenantAsync();
        var idle = await tenant2.CreateServerAsync();
        await SeedSamplesAsync(Guid.Parse(idle.Id()), 1, MetricResolution.Raw, now.AddMinutes(-10), TimeSpan.FromSeconds(1));
        Assert.True((await tenant2.Viewer.GetJsonAsync($"{Base(idle.Id())}/metrics/latest"))["stale"]!.GetValue<bool>()); // an offline agent's last sample is flagged stale
    }

    [RequiresDatabaseFact]
    public async Task A_series_derives_rates_from_the_counters_and_respects_max_points()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var serverId = Guid.Parse(server.Id());
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        await SeedSamplesAsync(serverId, 60, MetricResolution.Raw, start, TimeSpan.FromSeconds(10));

        var from = Uri.EscapeDataString(start.AddSeconds(-1).ToString("O"));
        var series = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/metrics?from={from}&maxPoints=1000");
        Assert.Equal("raw", series["resolution"]!.GetValue<string>());
        var points = series["points"]!.AsArray();
        Assert.Equal(60, points.Count);
        Assert.Null(points[0]!["netRxBytesPerSecond"]);
        Assert.Equal(100, points[1]!["netRxBytesPerSecond"]!.GetValue<double>()); // 1000 bytes in 10 s
        Assert.Equal(0, points[1]!["netTxBytesPerSecond"]!.GetValue<double>());

        var thinned = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/metrics?from={from}&maxPoints=20");
        Assert.InRange(thinned["points"]!.AsArray().Count, 15, 20);
        Assert.Equal(0, thinned["points"]![0]!["cpuPercent"]!.GetValue<double>());
    }

    [RequiresDatabaseFact]
    public async Task The_resolution_follows_the_range_unless_chosen()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var serverId = Guid.Parse(server.Id());
        var now = DateTimeOffset.UtcNow;
        await SeedSamplesAsync(serverId, 5, MetricResolution.FiveMinutes, now.AddDays(-3), TimeSpan.FromMinutes(5));
        await SeedSamplesAsync(serverId, 5, MetricResolution.OneHour, now.AddDays(-60), TimeSpan.FromHours(1));

        string Query(TimeSpan range, string extra = "") => $"{Base(server.Id())}/metrics?from={Uri.EscapeDataString(now.Subtract(range).ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}{extra}";
        Assert.Equal("raw", (await tenant.Viewer.GetJsonAsync(Query(TimeSpan.FromHours(2))))["resolution"]!.GetValue<string>());
        Assert.Equal("fiveMinutes", (await tenant.Viewer.GetJsonAsync(Query(TimeSpan.FromDays(4))))["resolution"]!.GetValue<string>());
        Assert.Equal("oneHour", (await tenant.Viewer.GetJsonAsync(Query(TimeSpan.FromDays(90))))["resolution"]!.GetValue<string>());
        var chosen = await tenant.Viewer.GetJsonAsync(Query(TimeSpan.FromDays(4), "&resolution=fiveMinutes"));
        Assert.Equal(5, chosen["points"]!.AsArray().Count);
    }

    [RequiresDatabaseFact]
    public async Task A_container_series_is_selected_by_container_id_and_bad_parameters_are_rejected()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var serverId = Guid.Parse(server.Id());
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SeedSamplesAsync(serverId, 3, MetricResolution.Raw, start, TimeSpan.FromSeconds(10));
        await SeedSamplesAsync(serverId, 2, MetricResolution.Raw, start, TimeSpan.FromSeconds(10), container: "c9");
        var from = Uri.EscapeDataString(start.AddSeconds(-1).ToString("O"));

        var container = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/metrics?from={from}&containerId=c9");
        Assert.Equal("c9", container["containerId"]!.GetValue<string>());
        Assert.Equal(2, container["points"]!.AsArray().Count);

        await (await tenant.Viewer.GetAsync($"{Base(server.Id())}/metrics?resolution=weekly")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await tenant.Viewer.GetAsync($"{Base(server.Id())}/metrics?maxPoints=5")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await tenant.Viewer.GetAsync($"{Base(server.Id())}/metrics?bogus=1")).AssertProblemAsync(400, "validation.invalid_parameter");
        await (await tenant.Viewer.GetAsync($"{Base(server.Id())}/metrics?from=2026-01-01T00:00:00Z&to=2026-01-01T00:00:00Z")).AssertProblemAsync(400, "validation.invalid_parameter");
    }

    // ================================================================ discovery

    [RequiresDatabaseFact]
    public async Task Discovery_returns_the_facts_and_the_last_report_flagged_stale_while_the_agent_is_away()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var serverId = Guid.Parse(server.Id());

        var empty = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/discovery");
        Assert.Null(empty["report"]);
        Assert.True(empty["stale"]!.GetValue<bool>());

        var report = new Aethera.Agent.V1.DiscoveryReport
        {
            Host = new Aethera.Agent.V1.HostFacts { Hostname = "box", OsName = "Ubuntu", OsVersion = "24.04", CpuCoresLogical = 4, MemoryTotalBytes = 1 << 30 },
            Docker = new Aethera.Agent.V1.DockerInfo { Status = Aethera.Agent.V1.DockerStatus.Running, Version = "27.0.1" },
        };
        report.Containers.Add(new Aethera.Agent.V1.ContainerInfo { Id = "c1", Name = "web", State = Aethera.Agent.V1.ContainerState.Running });
        await fixture.Factory.Services.GetRequiredService<AgentDiscoveryStore>().SaveAsync(serverId, report, CancellationToken.None);

        var found = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/discovery");
        Assert.Equal("Ubuntu", found["facts"]!["os"]!.GetValue<string>());
        Assert.Equal("27.0.1", found["facts"]!["dockerVersion"]!.GetValue<string>());
        Assert.Equal("web", found["report"]!["containers"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("running", found["report"]!["docker"]!["status"]!.GetValue<string>());
        Assert.True(found["stale"]!.GetValue<bool>());
    }

    // ================================================================ Docker inventory

    [RequiresDatabaseFact]
    public async Task Docker_inventory_is_read_live_through_the_transport()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        fixture.Transport.Available = true;
        fixture.Transport
            .On<ContainerListCommand, IReadOnlyList<DockerContainer>>(c => [new DockerContainer("c1", "web", "nginx", "img", ContainerRunState.Running, "Up", ContainerHealthState.Healthy, null, null, null, 0, false, 0, new Dictionary<string, string>(), [], [], [], null)])
            .On<ImageListCommand, IReadOnlyList<DockerImage>>(_ => [new DockerImage("sha256:1", ["nginx:1.27"], [], 100, null, new Dictionary<string, string>(), "amd64", "linux", 1)])
            .On<VolumeListCommand, IReadOnlyList<DockerVolume>>(_ => [new DockerVolume("data", "local", "/var/lib/docker/volumes/data", new Dictionary<string, string>(), null, -1, 1)])
            .On<NetworkListCommand, IReadOnlyList<DockerNetwork>>(_ => [new DockerNetwork("n1", "aethera", "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, [])]);

        var containers = await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/docker/containers?all=false");
        Assert.Equal("web", containers[0]!["name"]!.GetValue<string>());
        Assert.Equal("running", containers[0]!["state"]!.GetValue<string>());
        Assert.False(fixture.Transport.Received<ContainerListCommand>().Last().All);
        Assert.Equal("nginx:1.27", (await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/docker/images"))[0]!["repoTags"]![0]!.GetValue<string>());
        Assert.Equal("data", (await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/docker/volumes"))[0]!["name"]!.GetValue<string>());
        Assert.Equal("aethera", (await tenant.Viewer.GetJsonAsync($"{Base(server.Id())}/docker/networks"))[0]!["name"]!.GetValue<string>());

        var last = fixture.Transport.Commands.Last(c => c.Command is NetworkListCommand);
        Assert.Equal(tenant.Identity.OrganizationId, last.Options.OrganizationId); // the audit event of the command names the organization
    }

    [RequiresDatabaseFact]
    public async Task Inventory_problems_are_reported_as_problem_details()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();

        fixture.Transport.Throw<ContainerListCommand>(new ServerTransportException(TransportErrors.AgentUnavailable, "No agent session is connected for this server."));
        var down = await tenant.Viewer.GetAsync($"{Base(server.Id())}/docker/containers");
        await down.AssertProblemAsync(503, "server.agent_unavailable");

        fixture.Transport.Fail<ImageListCommand, IReadOnlyList<DockerImage>>(CommandErrorCode.DockerUnavailable, "Cannot connect to the Docker daemon");
        await (await tenant.Viewer.GetAsync($"{Base(server.Id())}/docker/images")).AssertProblemAsync(502, "server.command_failed");

        await (await tenant.Viewer.GetAsync($"{Base(Guid.NewGuid().ToString())}/docker/containers")).AssertProblemAsync(404, "server.not_found");
        await (await tenant.Viewer.GetAsync($"{Base(server.Id())}/docker/containers?bogus=1")).AssertProblemAsync(400, "validation.invalid_parameter");
        fixture.Transport.On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => []); // restore for other tests
    }

    // ================================================================ maintenance

    [RequiresDatabaseFact]
    public async Task Pruning_enqueues_a_server_job_with_the_chosen_scopes()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var response = await tenant.Admin.PostAsync($"{Base(server.Id())}/maintenance/prune", new { danglingImages = true, buildCache = true, olderThanHours = 48 });
        var job = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("server.prune", job["type"]!.GetValue<string>());
        Assert.Equal("queued", job["status"]!.GetValue<string>());
        Assert.Equal("server", job["resource"]!["type"]!.GetValue<string>());
        Assert.Equal(server.Id(), job["resource"]!["id"]!.GetValue<string>());

        var row = await WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == Guid.Parse(job.Id())));
        Assert.Equal($"server:{server.Id()}:maintenance", row.LockKey);
        Assert.Equal(tenant.Identity.OrganizationId, row.OrganizationId);
        Assert.Equal(3, row.MaxAttempts);
        var payload = JsonNode.Parse(row.PayloadJson)!;
        Assert.True(payload["danglingImages"]!.GetValue<bool>());
        Assert.False(payload["volumes"]!.GetValue<bool>());
        Assert.Equal(48, payload["olderThanHours"]!.GetValue<int>());
        Assert.NotEmpty(await tenant.AuditAsync(server.Id(), "server.prune_requested"));
    }

    [RequiresDatabaseFact]
    public async Task Pruning_volumes_deletes_data_and_needs_the_server_name_as_confirmation()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var url = $"{Base(server.Id())}/maintenance/prune";
        await (await tenant.Admin.PostAsync(url, new { volumes = true })).AssertProblemAsync(428, "confirmation.required");
        await (await tenant.Admin.PostAsync($"{url}?confirm=wrong", new { volumes = true })).AssertProblemAsync(428, "confirmation.required");
        var confirmed = await tenant.Admin.PostAsync($"{url}?confirm={Uri.EscapeDataString(server["name"]!.GetValue<string>())}", new { volumes = true });
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_prune_without_a_scope_is_rejected()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var errors = await (await tenant.Admin.PostAsync($"{Base(server.Id())}/maintenance/prune", new { })).ValidationErrorsAsync();
        Assert.Contains(errors, e => e.Code == "required");
        await (await tenant.Admin.PostAsync($"{Base(server.Id())}/maintenance/prune", new { danglingImages = true, olderThanHours = 0 })).ValidationErrorsAsync();
    }

    [RequiresDatabaseFact]
    public async Task Maintenance_is_for_administrators_with_the_servers_scope()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var prune = $"{Base(server.Id())}/maintenance/prune";
        var refresh = $"{Base(server.Id())}/maintenance/refresh-discovery";

        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Developer.PostAsync(prune, new { danglingImages = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Viewer.PostAsync(refresh, null)).StatusCode);
        await (await tenant.Token(OrganizationRole.Admin, "write", "deploy").PostAsync(prune, new { danglingImages = true })).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.Accepted, (await tenant.Token(OrganizationRole.Admin, "servers:write").PostAsync(refresh, null)).StatusCode);
        var job = await (await tenant.Admin.PostAsync(refresh, null)).ReadAsync();
        Assert.Equal("server.discovery_refresh", job["type"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Reading_server_state_is_open_to_viewers_and_closed_to_other_organizations()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        fixture.Transport.On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => []);
        foreach (var path in new[] { "status", "events", "metrics/latest", "metrics", "discovery", "docker/containers" })
        {
            Assert.True((await tenant.Viewer.GetAsync($"{Base(server.Id())}/{path}")).StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway, path);
            await (await other.Viewer.GetAsync($"{Base(server.Id())}/{path}")).AssertProblemAsync(404, "server.not_found");
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateAnonymousClient().GetAsync($"{Base(server.Id())}/{path}")).StatusCode);
        }
    }

    [RequiresDatabaseFact]
    public async Task The_openapi_document_describes_the_agent_endpoints()
    {
        var client = fixture.Factory.CreateAnonymousClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/api/openapi/v1.json"))!;
        var paths = document["paths"]!.AsObject();
        foreach (var path in new[]
        {
            "/api/v1/servers/{id}/join-tokens", "/api/v1/servers/{id}/join-tokens/{tokenId}", "/api/v1/servers/{id}/agent/reset", "/api/v1/agent/ca",
            "/api/v1/servers/{id}/status", "/api/v1/servers/{id}/events", "/api/v1/servers/{id}/metrics", "/api/v1/servers/{id}/metrics/latest",
            "/api/v1/servers/{id}/discovery", "/api/v1/servers/{id}/docker/containers", "/api/v1/servers/{id}/docker/images", "/api/v1/servers/{id}/docker/volumes",
            "/api/v1/servers/{id}/docker/networks", "/api/v1/servers/{id}/maintenance/prune", "/api/v1/servers/{id}/maintenance/refresh-discovery",
        })
            Assert.True(paths.ContainsKey(path), $"missing {path}");
        Assert.DoesNotContain(paths.Select(p => p.Key), p => p.Contains("aethera.agent"));   // gRPC is not part of the REST contract
        Assert.Equal("servers:write", paths["/api/v1/servers/{id}/maintenance/prune"]!["post"]!["x-required-scope"]!.GetValue<string>());
        await Task.CompletedTask;
    }
}

