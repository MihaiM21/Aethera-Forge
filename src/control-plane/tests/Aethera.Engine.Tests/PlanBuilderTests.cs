using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Tests;

public class PlanBuilderTests
{
    private static readonly Guid Wid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Did = Guid.Parse("0190f3c2-0000-7000-8000-000000000009");
    private static readonly Guid SecretId = Guid.NewGuid();

    private static DeploymentSnapshot Snapshot(Func<DeploymentSnapshot, DeploymentSnapshot>? edit = null)
    {
        var s = new DeploymentSnapshot
        {
            WorkloadId = Wid, EnvironmentId = Guid.NewGuid(), Slug = "api", SourceKind = ApplicationSourceKind.Git,
            Git = new GitSnapshot("https://github.com/acme/api.git", "main", null, null),
            Build = new BuildSnapshot(BuildEngines.Dockerfile, "services/api", "Dockerfile.prod", null, null, null, null, true, "linux/amd64"),
            Runtime = new RuntimeSnapshot { HealthType = HealthCheckType.Http, HealthPath = "healthz", HealthRetries = 5, MemoryLimitBytes = 512 << 20 },
            Env =
            [
                new("PLAIN", "v", null, null, false, true), new("DB_PASSWORD", null, SecretId, 3, false, true),
                new("NPM_TOKEN", null, SecretId, 3, true, false),
            ],
            Ports = [new(8080, PortProtocol.Tcp, null, true), new(9000, PortProtocol.Tcp, 9000, false)],
            Volumes = [new("data", "/data", null, false), new("cfg", "/etc/cfg", "/srv/cfg", true)],
            Domains = [new("api.example.com", "/", true, null), new("www.api.example.com", "/", true, null)],
            Networks = [new("proj-net", false, ["api"])],
        };
        return edit?.Invoke(s) ?? s;
    }

    private static PlanContext Context(string? existing = null) => new()
    {
        ServerId = Guid.NewGuid(), DeploymentId = Did, DeploymentNumber = 4, PreviousContainer = "api-old",
        ResolveSecret = (id, v) => id == SecretId && v == 3 ? "s3cr3t" : throw new InvalidOperationException("wrong secret pin"),
        Proxy = new TraefikProxyProvider(), ExistingImage = existing,
    };

    [Fact]
    public void Source_build_plan_separates_build_and_runtime_env_and_tags_the_image()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(), Context());

        var build = plan.Build!;
        Assert.Equal(BuildEngineKind.Dockerfile, build.Engine);
        Assert.Equal(["aethera/api:" + Did], build.ImageTags);
        Assert.Equal("services/api", build.ContextPath);
        Assert.Equal(["NPM_TOKEN"], build.BuildArgs!.Select(a => a.Name));
        Assert.Equal("s3cr3t", build.BuildArgs![0].Secret!.Value);
        Assert.Equal(["PLAIN", "DB_PASSWORD"], plan.Container.Env!.Select(e => e.Name));
        Assert.Equal(["linux/amd64"], build.TargetPlatforms);
        Assert.Equal(1, build.Git!.Depth);
    }

    [Fact]
    public void Container_spec_has_ports_mounts_networks_limits_and_labels()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(), Context());
        var c = plan.Container;

        Assert.Equal("api-11111111", c.Name);
        Assert.Equal([9000], c.Ports!.Select(p => p.HostPort));
        Assert.Contains(c.Mounts!, m => m.Type == MountKind.Volume && m.Source == "data");
        Assert.Contains(c.Mounts!, m => m.Type == MountKind.Bind && m.Source == "/srv/cfg" && m.ReadOnly);
        Assert.Equal(["aethera-proxy", "proj-net"], c.Networks!.Select(n => n.Network));
        Assert.Equal(512L << 20, c.Resources!.MemoryLimitBytes);
        Assert.Equal(Wid.ToString(), c.Labels!["aethera.workload"]);
        Assert.Equal("Host(`api.example.com`) || Host(`www.api.example.com`)", c.Labels["traefik.http.routers.api-https.rule"]);
        Assert.Equal("8080", c.Labels["traefik.http.services.api.loadbalancer.server.port"]);
        Assert.Equal(["aethera-proxy", "proj-net"], plan.Networks.OrderBy(n => n == "proj-net").ThenBy(n => n));
    }

    [Fact]
    public void Health_probe_targets_the_container_by_name_and_normalises_the_path()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(), Context());
        var probe = Assert.IsType<HttpProbeTarget>(plan.HealthFor!("api-4"));
        Assert.Equal("http://api-4:8080/healthz", probe.Url);
        Assert.Equal(5, plan.HealthRetries);
    }

    [Fact]
    public void Rollback_uses_the_existing_image_from_the_server_and_pulls_nothing()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(), Context(existing: "aethera/api:old"));
        Assert.Null(plan.Build);
        Assert.True(plan.LocalImage);
        Assert.Equal("aethera/api:old", plan.ImageReference);
    }

    [Fact]
    public void Image_source_pulls_with_registry_credentials()
    {
        var snap = Snapshot(s => s with { Git = null, Build = null, Image = new ImageSnapshot("ghcr.io/acme/api", "v2", ImagePullPolicy.Always, null) });
        var ctx = Context();
        var plan = DeploymentPlanBuilder.Build(snap, new PlanContext
        {
            ServerId = ctx.ServerId, DeploymentId = Did, DeploymentNumber = 1, ResolveSecret = ctx.ResolveSecret, Proxy = ctx.Proxy,
            Registry = new RegistryCredentials("ghcr.io", "bot", new SecretValue("pw")),
        });
        Assert.Equal("ghcr.io/acme/api:v2", plan.ImageReference);
        Assert.Equal("ghcr.io", plan.PullAuth!.Server);
        Assert.False(plan.LocalImage);
    }

    [Fact]
    public void Domains_on_different_ports_become_separate_routes()
    {
        var snap = Snapshot(s => s with { Domains = [new("a.example.com", "/", true, 8080), new("b.example.com", "/", true, 9000)] });
        var labels = DeploymentPlanBuilder.Build(snap, Context()).Container.Labels!;
        Assert.Equal("9000", labels["traefik.http.services.api-2.loadbalancer.server.port"]);
    }

    [Fact]
    public void Without_domains_the_proxy_network_is_not_attached()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(s => s with { Domains = [] }), Context());
        Assert.DoesNotContain(plan.Container.Networks!, n => n.Network == "aethera-proxy");
        Assert.DoesNotContain(plan.Container.Labels!.Keys, k => k.StartsWith("traefik"));
    }

    [Fact]
    public void Compose_plan_keeps_the_user_file_and_adds_an_override_for_routing()
    {
        var snap = Snapshot(s => s with { SourceKind = ApplicationSourceKind.Compose, Git = null, Build = null, Compose = new ComposeSnapshot("services:\n  web:\n    image: nginx\n", "web") });
        var plan = DeploymentPlanBuilder.Build(snap, Context());

        Assert.Equal("services:\n  web:\n    image: nginx\n", plan.Compose!.ComposeFile);
        Assert.Contains("traefik.http.routers", plan.Compose.OverrideFile);
        Assert.Contains("\"aethera-proxy\"", plan.Compose.OverrideFile);
        Assert.Equal("api-11111111", plan.Compose.ProjectName);
    }

    [Fact]
    public void Snapshot_round_trips_and_never_contains_secret_values()
    {
        var json = Snapshot().ToJson();
        Assert.DoesNotContain("s3cr3t", json);
        Assert.Contains(SecretId.ToString(), json);
        var back = DeploymentSnapshot.FromJson(json);
        Assert.Equal(Snapshot().Env, back.Env);
        Assert.Equal(HealthCheckType.Http, back.Runtime.HealthType);
        Assert.False(DeploymentSnapshot.TryFromJson("{}", out _));
    }
}

public class ComposeAndRollbackRunnerTests
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class T(Func<object, object?> respond) : IServerTransport
    {
        public List<string> Names { get; } = [];
        public TransportKind Kind => TransportKind.Agent;
        public TransportCapabilities Capabilities => TransportCapabilities.AllAgent;
        public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken ct) => ValueTask.FromResult(TransportStatus.Up(Kind));

        public Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(Guid serverId, TCommand command, CommandOptions options, CancellationToken ct)
            where TCommand : IServerCommand<TResult>
        {
            Names.Add(command.Name);
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(respond(command) is TResult res
                ? new CommandOutcome<TResult>(CommandStatus.Succeeded, res, CommandErrorCode.None, null, 0, now, now, false)
                : new CommandOutcome<TResult>(CommandStatus.Failed, default, CommandErrorCode.NotFound, "not found", 1, now, now, false));
        }

        public IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Deployment Started(string strategy = DeploymentStrategies.Recreate)
    {
        var clock = new Clock();
        var d = Deployment.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, DeploymentTrigger.Rollback, clock.UtcNow, strategy);
        d.Start("{}", clock.UtcNow);
        return d;
    }

    private static DeploymentRunner Runner(T t) => new(t, [new RecreateStrategy(), new ComposeStrategy()], new Clock());

    private static ComposeServiceInfo Svc(string name, ContainerRunState state, ContainerHealthState health = ContainerHealthState.Healthy) =>
        new(name, "id-" + name, name, "img", state, health, 0, []);

    [Fact]
    public async Task Compose_deployment_runs_up_and_succeeds_when_all_services_run()
    {
        var d = Started();
        var t = new T(c => c is ComposeUpCommand ? new ComposeOutcome("p", [Svc("web", ContainerRunState.Running), Svc("db", ContainerRunState.Running)]) : null);
        var plan = new DeploymentPlan { ServerId = d.ServerId, Compose = new ComposeProjectSpec("p", "services: {}") };

        Assert.Equal(DeploymentRunResult.Running, await Runner(t).RunAsync(d, plan, (_, _) => Task.CompletedTask, default));
        Assert.Equal(["id-web", "id-db"], d.ContainerIds);
        Assert.Equal(["compose.up"], t.Names);
    }

    [Fact]
    public async Task Compose_deployment_fails_when_a_service_is_down()
    {
        var d = Started();
        var t = new T(c => c is ComposeUpCommand ? new ComposeOutcome("p", [Svc("web", ContainerRunState.Running), Svc("db", ContainerRunState.Exited, ContainerHealthState.None)]) : null);
        var plan = new DeploymentPlan { ServerId = d.ServerId, Compose = new ComposeProjectSpec("p", "services: {}") };

        Assert.Equal(DeploymentRunResult.Failed, await Runner(t).RunAsync(d, plan, (_, _) => Task.CompletedTask, default));
        Assert.Equal(DeploymentStep.HealthCheck, d.FailedStep);
        Assert.Contains("db", d.FailureReason);
    }

    [Fact]
    public async Task Rollback_to_a_pruned_image_fails_with_a_clear_reason_instead_of_pulling()
    {
        var d = Started();
        var t = new T(_ => null); // image inspect fails: not found
        var plan = new DeploymentPlan { ServerId = d.ServerId, ImageReference = "aethera/api:gone", LocalImage = true, Container = new ContainerSpec("x", "api") };

        Assert.Equal(DeploymentRunResult.Failed, await Runner(t).RunAsync(d, plan, (_, _) => Task.CompletedTask, default));
        Assert.Equal(FailureCodes.ImageMissing, d.FailureCode);
        Assert.Equal(DeploymentStep.Image, d.FailedStep);
        Assert.Contains("pruned", d.FailureReason);
        Assert.DoesNotContain("image.pull", t.Names);
    }
}
