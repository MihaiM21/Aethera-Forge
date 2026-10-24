using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Tests.Deployments;

/// <summary>Like <see cref="DeploymentsFixture"/>, with an ACME email configured (so the proxy gets its certificate resolvers).</summary>
public sealed class ComposeApiFixture : IDisposable
{
    public ComposeApiFixture()
    {
        Base = new AetheraApiFactory();
        Transport = new FakeServerTransport();
        Factory = Jobs.JobsApiFixture.Configure(Base).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Aethera:Proxy:AcmeEmail", "ops@example.com");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IServerTransport>();
                services.AddSingleton<IServerTransport>(Transport);
            });
        });
    }

    public AetheraApiFactory Base { get; }

    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; }

    public FakeServerTransport Transport { get; }

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class ComposeApiCollection : ICollectionFixture<ComposeApiFixture>
{
    public const string Name = "compose-api";
}

/// <summary>Compose from the repository (ADR 0010) end to end: API, deployment job, engine and the (fake) agent.</summary>
[Collection(ComposeApiCollection.Name)]
public sealed class ComposeFromGitApiTests(ComposeApiFixture fixture)
{
    private FakeServerTransport Transport => fixture.Transport;

    private static ComposeServiceInfo Svc(string name, ContainerRunState state = ContainerRunState.Running) =>
        new(name, "id-" + name, name, "img", state, ContainerHealthState.Healthy, 0, []);

    private static IReadOnlyList<ComposeServiceDefinition> Services() =>
        [new("web", "", true, [3000]), new("worker", "", true, []), new("db", "postgres:17", false, [5432])];

    private FakeServerTransport Healthy(IReadOnlyList<ComposeServiceDefinition>? services = null, IReadOnlyList<string>? violations = null)
    {
        Transport.Commands.Clear();
        return Transport
            .On<NetworkCreateCommand, DockerNetwork>(c => new DockerNetwork("n", c.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []))
            .On<ProxyEnsureCommand, ProxyEnsured>(_ => new ProxyEnsured("traefik", "proxy-1", "v3.1", true, true, "abc"))
            .On<ComposeInspectCommand, ComposeInspection>(_ => new ComposeInspection(services ?? Services(), "abc123def456", "sha", violations ?? []))
            .On<ComposeUpCommand, ComposeOutcome>(_ => new ComposeOutcome("p", [Svc("web"), Svc("worker"), Svc("db")], "abc123def456"));
    }

    private async Task<(Tenant Tenant, string ServerId, string EnvironmentId, string AppId)> ComposeAppAsync(bool inline = false)
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync(publicIp: "203.0.113.10");
        var (_, environmentId) = await tenant.CreateProjectWithEnvironmentAsync();
        object body = inline
            ? new { name = Tenant.Unique("compose"), environmentId, serverId = server.Id(), sourceKind = "compose", compose = new { inlineContent = "services:\n  web:\n    image: nginx\n    ports: ['8080:80']\n  cache:\n    image: redis\n" } }
            : new
            {
                name = Tenant.Unique("compose"), environmentId, serverId = server.Id(), sourceKind = "compose",
                gitSource = new { repositoryUrl = "https://github.com/acme/turnone.git", branch = "main", autoDeploy = true },
                compose = new { filePath = "deploy/docker-compose.yml" },
            };
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", body);
        return (tenant, server.Id(), environmentId, app.Id());
    }

    private async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    private async Task<Deployment> DeployAndWaitAsync(Tenant tenant, string appId)
    {
        var response = await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/deployments", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.ReadAsync()).Id();
        Deployment? last = null;
        await GatewayFixture.EventuallyAsync(async () =>
        {
            last = await WithDbAsync(db => db.Deployments.AsNoTracking().Include(d => d.Steps).FirstAsync(d => d.Id == Guid.Parse(id)));
            return last.Status is DeploymentStatus.Running or DeploymentStatus.Failed or DeploymentStatus.Cancelled;
        }, "the deployment to finish", timeoutMs: 20000);
        // The job (and the application's status) finish a moment after the deployment row.
        await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(db => db.Jobs.AsNoTracking()
            .AnyAsync(j => j.Id == last!.JobId && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled))),
            "the job to finish", timeoutMs: 20000);
        return await WithDbAsync(db => db.Deployments.AsNoTracking().Include(d => d.Steps).FirstAsync(d => d.Id == Guid.Parse(id)));
    }

    [RequiresDatabaseFact]
    public async Task Deleting_a_compose_application_takes_its_project_down_without_volumes()
    {
        Healthy()
            .On<ComposeDownCommand, ComposeOutcome>(_ => new ComposeOutcome("p", [], null))
            .On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => [])
            .On<ContainerStopCommand, DockerContainer>(c => throw new InvalidOperationException("no container expected: " + c.Container));
        var (tenant, _, _, appId) = await ComposeAppAsync(inline: true);
        await DeployAndWaitAsync(tenant, appId);
        var slug = (await tenant.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["slug"]!.GetValue<string>();
        Transport.Commands.Clear();

        var response = await tenant.Developer.DeleteAsync($"/api/v1/applications/{appId}?confirm={slug}");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = (await response.ReadAsync())["jobId"]!.GetValue<string>();
        await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(db => db.Jobs.AsNoTracking()
            .AnyAsync(j => j.Id == Guid.Parse(jobId) && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled))), "the teardown job", timeoutMs: 20000);
        var finished = await WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == Guid.Parse(jobId)));
        Assert.True(finished.Status == JobStatus.Succeeded, finished.ErrorJson);
        var down = Assert.Single(Transport.Commands.Select(c => c.Command).OfType<ComposeDownCommand>());
        Assert.False(down.RemoveVolumes);
        Assert.True(down.RemoveOrphans);
        Assert.Contains("image: nginx", down.Project.ComposeFile);
    }

    // ---- inspect endpoint ------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Inspect_reads_the_file_from_the_repository_on_the_applications_server()
    {
        Healthy();
        var (tenant, serverId, _, appId) = await ComposeAppAsync();

        var result = await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/compose/inspect", null);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = await result.ReadAsync();
        Assert.Equal(["web", "worker", "db"], body["services"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));
        Assert.Equal([3000], body["services"]![0]!["ports"]!.AsArray().Select(p => p!.GetValue<int>()));
        Assert.True(body["services"]![0]!["hasBuild"]!.GetValue<bool>());
        Assert.Equal("abc123def456", body["commitSha"]!.GetValue<string>());
        Assert.Empty(body["violations"]!.AsArray());

        var recorded = Assert.Single(Transport.Commands, c => c.Command is ComposeInspectCommand);
        Assert.Equal(Guid.Parse(serverId), recorded.ServerId);
        var spec = ((ComposeInspectCommand)recorded.Command).Project;
        Assert.Equal("deploy/docker-compose.yml", spec.ComposePath);
        Assert.Equal("https://github.com/acme/turnone.git", spec.Git!.Url);
        Assert.Equal("main", spec.Git.Ref);
        Assert.Equal("", spec.ComposeFile);
    }

    [RequiresDatabaseFact]
    public async Task Inspect_of_an_inline_file_sends_the_content_and_returns_violations()
    {
        Healthy(violations: ["services.web.privileged is not allowed"]);
        var (tenant, _, _, appId) = await ComposeAppAsync(inline: true);

        var body = await (await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/compose/inspect", null)).ReadAsync();
        Assert.Equal(["services.web.privileged is not allowed"], body["violations"]!.AsArray().Select(v => v!.GetValue<string>()));
        var spec = ((ComposeInspectCommand)Transport.Commands.Last(c => c.Command is ComposeInspectCommand).Command).Project;
        Assert.Contains("image: nginx", spec.ComposeFile);
        Assert.Null(spec.Git);
    }

    [RequiresDatabaseFact]
    public async Task Inspect_needs_write_access_a_compose_application_and_a_current_agent()
    {
        Healthy();
        var (tenant, _, _, appId) = await ComposeAppAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Viewer.PostAsync($"/api/v1/applications/{appId}/compose/inspect", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.Developer.PostAsync($"/api/v1/applications/{Guid.NewGuid()}/compose/inspect", null)).StatusCode);

        var (_, _, _, imageApp) = await tenant.CreateStackAsync();
        await (await tenant.Developer.PostAsync($"/api/v1/applications/{imageApp}/compose/inspect", null)).AssertProblemAsync(422, "application.not_compose");

        // An agent without compose.git answers "unsupported": the problem says to update the agent.
        Transport.Throw<ComposeInspectCommand>(new ServerTransportException(TransportErrors.Unsupported, "The agent does not support 'compose.git'. Update the agent."));
        var old = await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/compose/inspect", null);
        await old.AssertProblemAsync(422, "transport.unsupported");
        Assert.Contains("update the agent", (await old.ReadAsync())["detail"]!.GetValue<string>());

        Transport.Fail<ComposeInspectCommand, ComposeInspection>(CommandErrorCode.Internal, "git clone failed");
        var failed = await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/compose/inspect", null);
        await failed.AssertProblemAsync(502, "compose.inspect_failed");
    }

    // ---- deployment ------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Deploying_from_the_repository_builds_routes_each_service_and_records_the_commit()
    {
        Healthy();
        var (tenant, _, environmentId, appId) = await ComposeAppAsync();
        var domain = await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/domains", new { hostname = $"tn-{Guid.NewGuid():N}.example.com"[..24] + ".example.com", composeService = "web" });
        await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/domains", new { hostname = $"db-{Guid.NewGuid():N}.example.com"[..24] + ".example.com", composeService = "db", redirectTo = "example.com" });

        var d = await DeployAndWaitAsync(tenant, appId);
        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Equal("abc123def456", d.CommitSha);
        Assert.Equal(ApplicationSourceKind.Compose, d.SourceType);

        var up = Assert.Single(Transport.Received<ComposeUpCommand>());
        Assert.True(up.Build);
        Assert.Equal("deploy/docker-compose.yml", up.Project.ComposePath);
        Assert.Equal("", up.Project.ComposeFile);
        Assert.Equal("https://github.com/acme/turnone.git", up.Project.Git!.Url);

        var services = JsonDocument.Parse(up.Project.OverrideFile!).RootElement.GetProperty("services");
        var envNetwork = DeploymentPlanBuilder.EnvironmentNetwork(Guid.Parse(environmentId));
        foreach (var name in new[] { "web", "worker", "db" })
            Assert.True(services.GetProperty(name).GetProperty("networks").TryGetProperty(envNetwork, out _), name);
        Assert.Contains(services.GetProperty("web").GetProperty("labels").EnumerateObject(), p => p.Name.EndsWith("-https.rule") && p.Value.GetString()!.Contains(domain["hostname"]!.GetValue<string>()));
        Assert.Contains(services.GetProperty("db").GetProperty("labels").EnumerateObject(), p => p.Name.Contains("redirectregex.replacement"));
        Assert.DoesNotContain(services.GetProperty("worker").GetProperty("labels").EnumerateObject(), p => p.Name.StartsWith("traefik.http"));

        // The environment network is created before the project starts, the proxy before that.
        var names = Transport.Commands.Select(c => c.Command.GetType().Name).ToList();
        Assert.True(names.IndexOf(nameof(ProxyEnsureCommand)) < names.IndexOf(nameof(ComposeUpCommand)));
        Assert.Contains(Transport.Received<NetworkCreateCommand>(), n => n.NetworkName == envNetwork);
        Assert.True(names.LastIndexOf(nameof(NetworkCreateCommand)) < names.IndexOf(nameof(ComposeUpCommand)));
    }

    [RequiresDatabaseFact]
    public async Task A_domain_naming_a_service_that_is_not_in_the_file_fails_the_deployment_with_proxy_failed()
    {
        Healthy();
        var (tenant, _, _, appId) = await ComposeAppAsync();
        await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/domains", new { hostname = $"x-{Guid.NewGuid():N}"[..14] + ".example.com", composeService = "frontend", targetPort = 80 });

        var d = await DeployAndWaitAsync(tenant, appId);
        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal("proxy.failed", d.FailureCode);
        Assert.Contains("frontend", d.FailureReason);
        Assert.Contains("not in the compose file", d.FailureReason);
        Assert.Empty(Transport.Received<ComposeUpCommand>());
    }

    [RequiresDatabaseFact]
    public async Task A_file_that_breaks_the_policy_or_an_old_agent_fails_before_anything_is_built()
    {
        Healthy(violations: ["services.web.privileged is not allowed"]);
        var (tenant, _, _, appId) = await ComposeAppAsync();
        var violated = await DeployAndWaitAsync(tenant, appId);
        Assert.Equal(DeploymentStatus.Failed, violated.Status);
        Assert.Equal("compose.policy_violation", violated.FailureCode);
        Assert.Contains("privileged", violated.FailureReason);
        Assert.Empty(Transport.Received<ComposeUpCommand>());

        Healthy();
        Transport.Throw<ComposeInspectCommand>(new ServerTransportException(TransportErrors.Unsupported, "no compose.git"));
        var old = await DeployAndWaitAsync(tenant, appId);
        Assert.Equal("compose.agent_outdated", old.FailureCode);
        Assert.Contains("update the agent", old.FailureReason);
    }

    [RequiresDatabaseFact]
    public async Task An_inline_compose_file_is_read_locally_and_keeps_working()
    {
        Healthy();
        var (tenant, _, _, appId) = await ComposeAppAsync(inline: true);
        await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/domains", new { hostname = $"i-{Guid.NewGuid():N}"[..14] + ".example.com", composeService = "web" });

        var d = await DeployAndWaitAsync(tenant, appId);
        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Empty(Transport.Received<ComposeInspectCommand>());
        var up = Assert.Single(Transport.Received<ComposeUpCommand>());
        Assert.False(up.Build);
        Assert.Contains("image: nginx", up.Project.ComposeFile);
        var services = JsonDocument.Parse(up.Project.OverrideFile!).RootElement.GetProperty("services");
        Assert.Equal(["web", "cache"], services.EnumerateObject().Select(p => p.Name));
        Assert.Contains(services.GetProperty("web").GetProperty("labels").EnumerateObject(), p => p.Name.EndsWith(".loadbalancer.server.port") && p.Value.GetString() == "80");
    }

    // ---- DNS-01 through the proxy ---------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Proxy_ensure_adds_the_cloudflare_resolver_and_token_when_a_domain_uses_a_dns_provider()
    {
        Healthy();
        var (tenant, serverId, _, appId) = await ComposeAppAsync(inline: true);

        await tenant.Admin.PostAsync($"/api/v1/servers/{serverId}/proxy/ensure", null);
        var plain = Transport.Received<ProxyEnsureCommand>().Last();
        Assert.DoesNotContain("cloudflare", Encoding.UTF8.GetString(plain.Config));
        Assert.Contains("letsencrypt", Encoding.UTF8.GetString(plain.Config));
        Assert.Null(plain.Env);

        var provider = await tenant.SeedDnsProviderAsync("cf-secret-token-1");
        await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/domains",
            new { hostname = $"*.w-{Guid.NewGuid():N}"[..14] + ".example.com", composeService = "web", dnsProviderId = provider });

        var response = await tenant.Admin.PostAsync($"/api/v1/servers/{serverId}/proxy/ensure", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("cf-secret-token-1", await response.Content.ReadAsStringAsync());
        var withDns = Transport.Received<ProxyEnsureCommand>().Last();
        var yaml = Encoding.UTF8.GetString(withDns.Config);
        Assert.Contains("  cloudflare:", yaml);
        Assert.Contains("provider: cloudflare", yaml);
        Assert.DoesNotContain("cf-secret-token-1", yaml);
        var env = Assert.Single(withDns.Env!);
        Assert.Equal(("CF_DNS_API_TOKEN", "cf-secret-token-1"), (env.Name, env.Secret!.Value));
        Assert.NotEqual(Encoding.UTF8.GetString(plain.Config), yaml);
    }

    [RequiresDatabaseFact]
    public async Task A_deployment_with_a_wildcard_domain_ensures_the_proxy_with_the_token_and_routes_through_cloudflare()
    {
        Healthy();
        var (tenant, _, _, appId) = await ComposeAppAsync(inline: true);
        var provider = await tenant.SeedDnsProviderAsync("cf-token-deploy");
        await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/domains",
            new { hostname = $"*.d-{Guid.NewGuid():N}"[..14] + ".example.com", composeService = "web", dnsProviderId = provider });

        var d = await DeployAndWaitAsync(tenant, appId);
        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Equal("cf-token-deploy", Assert.Single(Transport.Received<ProxyEnsureCommand>().Last().Env!).Secret!.Value);
        var labels = JsonDocument.Parse(Transport.Received<ComposeUpCommand>().Single().Project.OverrideFile!).RootElement
            .GetProperty("services").GetProperty("web").GetProperty("labels");
        Assert.Contains(labels.EnumerateObject(), p => p.Name.EndsWith("-https.tls.certresolver") && p.Value.GetString() == "cloudflare");
        Assert.Contains(labels.EnumerateObject(), p => p.Name.EndsWith("-https.rule") && p.Value.GetString()!.Contains("HostRegexp"));
    }
}

/// <summary>The snapshot factory (ADR 0010): environment network, domain options, custom labels, compose from the repository.</summary>
public class DeploymentSnapshotFactoryTests
{
    private static readonly Guid EnvId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static Application App(ApplicationSourceKind kind = ApplicationSourceKind.Compose) => new()
    {
        Name = "App", Slug = "app", EnvironmentId = EnvId, ServerId = Guid.NewGuid(), SourceKind = kind,
    };

    [Fact]
    public void A_custom_network_does_not_drop_the_environment_network()
    {
        var app = App();
        app.ComposeSource = new ComposeSource { InlineContent = "services: {}" };
        var net = new Network { ProjectId = Guid.NewGuid(), ServerId = app.ServerId, Name = "shared", DockerName = "proj-shared", IsInternal = true };
        app.Networks.Add(new WorkloadNetwork { Network = net, Aliases = ["alias-a"] });

        var snapshot = DeploymentSnapshotFactory.Create(app);
        Assert.Equal([DeploymentPlanBuilder.EnvironmentNetwork(EnvId), "proj-shared"], snapshot.Networks.Select(n => n.DockerName));
        Assert.Equal(["app"], snapshot.Networks[0].Aliases);
        Assert.Equal(["alias-a"], snapshot.Networks[1].Aliases);
        Assert.True(snapshot.Networks[1].Internal);

        // Nothing custom: the environment network alone, as before.
        var plain = App();
        plain.ComposeSource = new ComposeSource { InlineContent = "services: {}" };
        Assert.Equal([DeploymentPlanBuilder.EnvironmentNetwork(EnvId)], DeploymentSnapshotFactory.Create(plain).Networks.Select(n => n.DockerName));
    }

    [Fact]
    public void A_service_template_keeps_the_environment_network_next_to_a_custom_one()
    {
        var service = new Service { Name = "db", Slug = "db", EnvironmentId = EnvId, ServerId = Guid.NewGuid(), TemplateKey = "postgres", Image = "postgres:17" };
        service.Networks.Add(new WorkloadNetwork { Network = new Network { ProjectId = Guid.NewGuid(), ServerId = service.ServerId, Name = "n", DockerName = "n-1" } });
        Assert.Equal([DeploymentPlanBuilder.EnvironmentNetwork(EnvId), "n-1"], DeploymentSnapshotFactory.Create(service).Networks.Select(n => n.DockerName));
    }

    [Fact]
    public void Domain_options_and_custom_labels_are_frozen_into_the_snapshot()
    {
        var app = App();
        app.ComposeSource = new ComposeSource { InlineContent = "services: {}" };
        var secret = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var domain = new WorkloadDomain { WorkloadId = app.Id, ServerId = app.ServerId, ComposeService = "web", RedirectTo = null, StripPathPrefix = true, BasicAuthSecretId = secret, DnsProviderId = provider, PathPrefix = "/x" };
        domain.SetHostname("A.example.com");
        app.Domains.Add(domain);
        app.CustomLabels = new() { ["traefik.http.middlewares.c.compress"] = "true" };

        var snapshot = DeploymentSnapshotFactory.Create(app);
        var d = Assert.Single(snapshot.Domains);
        Assert.Equal(("a.example.com", "/x", "web", true, secret, provider), (d.Hostname, d.PathPrefix, d.ComposeService, d.StripPathPrefix, d.BasicAuthSecretId, d.DnsProviderId));
        Assert.Equal("true", snapshot.CustomLabels!["traefik.http.middlewares.c.compress"]);
        Assert.DoesNotContain("$2y$", snapshot.ToJson());
    }

    [Fact]
    public void A_compose_file_path_becomes_a_git_backed_snapshot()
    {
        var app = App();
        app.GitSource = new GitSource { RepositoryUrl = "https://github.com/acme/x.git", Branch = "release", CommitPin = "abc1234", GitCredentialId = Guid.NewGuid() };
        app.ComposeSource = new ComposeSource { FilePath = "deploy/compose.yml" };

        var snapshot = DeploymentSnapshotFactory.Create(app);
        Assert.Equal("deploy/compose.yml", snapshot.Compose!.FilePath);
        Assert.Equal("", snapshot.Compose.Content);
        Assert.Equal(("https://github.com/acme/x.git", "release", "abc1234", app.GitSource.GitCredentialId), (snapshot.Git!.Url, snapshot.Git.Branch, snapshot.Git.CommitPin, snapshot.Git.CredentialId));

        app.GitSource = null;
        Assert.Equal("source.not_configured", Assert.Throws<UnsupportedSourceException>(() => DeploymentSnapshotFactory.Create(app)).Code);
        app.ComposeSource = new ComposeSource();
        Assert.Throws<UnsupportedSourceException>(() => DeploymentSnapshotFactory.Create(app));
    }

    [Fact]
    public void Compose_service_discovery_reads_services_and_ports_of_an_inline_file()
    {
        var services = ComposeServiceDiscovery.FromInline("""
            services:
              web:
                build: .
                ports: ["8080:80", "127.0.0.1:9090:90/tcp", { target: 7000, published: 7001 }]
                expose: ["3000"]
              db:
                image: postgres:17
            """)!;
        Assert.Equal(["web", "db"], services.Select(s => s.Name));
        Assert.Equal([80, 90, 7000, 3000], services[0].Ports);
        Assert.True(services[0].HasBuild);
        Assert.Equal("postgres:17", services[1].Image);
        Assert.Null(ComposeServiceDiscovery.FromInline("not: [valid"));
        Assert.Null(ComposeServiceDiscovery.FromInline("foo: bar"));
    }
}
