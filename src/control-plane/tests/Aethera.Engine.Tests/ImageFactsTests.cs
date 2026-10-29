using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Tests;

/// <summary>A workload without a declared port runs on its image's EXPOSE port; one without a health check still has to stay up.</summary>
public class ImageFactsTests
{
    private static readonly Guid Wid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static DeploymentSnapshot Snapshot(Func<DeploymentSnapshot, DeploymentSnapshot>? edit = null)
    {
        var s = new DeploymentSnapshot
        {
            WorkloadId = Wid, EnvironmentId = Guid.NewGuid(), Slug = "web", SourceKind = ApplicationSourceKind.DockerImage,
            Image = new ImageSnapshot("nginx", "1.27", ImagePullPolicy.Always, null),
            Domains = [new("web-1a2b3c.203-0-113-7.sslip.io", "/", false, null)],
        };
        return edit?.Invoke(s) ?? s;
    }

    private static PlanContext Context() => new()
    {
        ServerId = Guid.NewGuid(), DeploymentId = Guid.NewGuid(), DeploymentNumber = 1,
        ResolveSecret = (_, _) => throw new InvalidOperationException(), Proxy = new TraefikProxyProvider(),
    };

    private static DockerImage Image(bool healthcheck = false, params string[] exposed) =>
        new("sha256:i", ["nginx:1.27"], [], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0, exposed, healthcheck);

    [Theory]
    [InlineData(new[] { "8080/tcp" }, 8080)]
    [InlineData(new[] { "443/tcp", "80/tcp" }, 80)]
    [InlineData(new[] { "9000/tcp", "3000/tcp", "53/udp" }, 3000)]
    [InlineData(new[] { "9001/tcp", "9000/tcp" }, 9000)]
    [InlineData(new[] { "53/udp" }, null)]
    [InlineData(new string[0], null)]
    public void The_preferred_port_is_the_only_one_else_the_most_web_like(string[] exposed, int? expected) =>
        Assert.Equal(expected, ImageFacts.From(Image(false, exposed)).PreferredPort);

    [Fact]
    public void Before_the_image_is_known_a_portless_domain_waits_for_the_rebuild()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(), Context());

        Assert.NotNull(plan.WithImage);
        Assert.DoesNotContain(plan.Container.Labels!.Keys, k => k.StartsWith("traefik.http.routers", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rebuild_routes_to_the_exposed_port_and_reports_it()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(), Context()).WithImage!(ImageFacts.From(Image(false, "80/tcp")));

        Assert.Null(plan.WithImage);
        Assert.Equal(80, plan.DetectedPort);
        Assert.Equal("80", plan.Container.Labels!["traefik.http.services.web-11111111.loadbalancer.server.port"]);
        Assert.Contains("aethera-proxy", plan.Networks);
    }

    [Fact]
    public void A_declared_port_wins_over_the_image()
    {
        var snapshot = Snapshot(s => s with { Ports = [new(5000, PortProtocol.Tcp, null, true)] });
        var plan = DeploymentPlanBuilder.Build(snapshot, Context()).WithImage!(ImageFacts.From(Image(false, "80/tcp")));

        Assert.Null(plan.DetectedPort);
        Assert.Equal("5000", plan.Container.Labels!["traefik.http.services.web-11111111.loadbalancer.server.port"]);
    }

    [Fact]
    public void A_domain_without_any_port_fails_loudly_once_the_image_is_known()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(), Context());

        var e = Assert.Throws<ProxyRoutingException>(() => plan.WithImage!(ImageFacts.Unknown));
        Assert.Contains("needs a port", e.Message);
    }

    [Fact]
    public void The_static_engine_defaults_to_port_80()
    {
        var snapshot = Snapshot(s => s with
        {
            Image = null, SourceKind = ApplicationSourceKind.Static, Git = new GitSnapshot("https://github.com/acme/site.git", "main", null, null),
            Build = new BuildSnapshot(BuildEngines.Static, ".", null, null, "npm run build", null, "dist", true, null),
        });

        Assert.Equal(80, DeploymentPlanBuilder.Build(snapshot, Context()).WithImage!(ImageFacts.Unknown).DetectedPort);
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 90)]
    public void Without_a_health_check_the_container_must_stay_up_and_an_image_healthcheck_must_pass(bool healthcheck, int retries)
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(), Context()).WithImage!(ImageFacts.From(Image(healthcheck, "80/tcp")));

        Assert.IsType<ContainerHealthProbeTarget>(plan.HealthFor!("web-1"));
        Assert.Equal(retries, plan.HealthRetries);
        Assert.Equal(3, plan.HealthSuccessThreshold);
    }

    [Fact]
    public void Http_probes_send_the_primary_domain_as_host()
    {
        var snapshot = Snapshot(s => s with { Runtime = new RuntimeSnapshot { HealthType = HealthCheckType.Http, HealthPath = "/up" } });
        var plan = DeploymentPlanBuilder.Build(snapshot, Context()).WithImage!(ImageFacts.From(Image(false, "3000/tcp")));

        var probe = Assert.IsType<HttpProbeTarget>(plan.HealthFor!("web-1"));
        Assert.Equal("http://web-1:3000/up", probe.Url);
        Assert.Equal("web-1a2b3c.203-0-113-7.sslip.io", probe.Headers!["Host"]);
    }
}
