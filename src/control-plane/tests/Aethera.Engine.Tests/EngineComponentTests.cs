using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Engine.Git;
using Aethera.Engine.Networking;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Tests;

public class TraefikProxyProviderTests
{
    private readonly TraefikProxyProvider _p = new();

    [Fact]
    public void Https_route_has_redirect_and_tls_routers_sharing_one_service()
    {
        var labels = _p.RouteLabels(new RouteSpec("web", ["a.example.com", "b.example.com"], 3000, "aethera-proxy"));

        Assert.Equal("Host(`a.example.com`) || Host(`b.example.com`)", labels["traefik.http.routers.web-https.rule"]);
        Assert.Equal("websecure", labels["traefik.http.routers.web-https.entrypoints"]);
        Assert.Equal("letsencrypt", labels["traefik.http.routers.web-https.tls.certresolver"]);
        Assert.Equal("web-redirect", labels["traefik.http.routers.web-http.middlewares"]);
        Assert.Equal("3000", labels["traefik.http.services.web.loadbalancer.server.port"]);
        Assert.Equal("true", labels["traefik.enable"]);
    }

    [Fact]
    public void Http_only_route_has_no_tls()
    {
        var labels = _p.RouteLabels(new RouteSpec("web", ["a.example.com"], 80, "n") { Https = false });
        Assert.DoesNotContain(labels.Keys, k => k.Contains("tls"));
        Assert.Equal("web", labels["traefik.http.routers.web.entrypoints"]);
    }

    [Fact]
    public void No_hosts_means_no_labels() => Assert.Empty(_p.RouteLabels(new RouteSpec("web", [], 80, "n")));

    [Theory]
    [InlineData("a`) || Host(`evil.com")]
    [InlineData("not a host")]
    [InlineData("-bad.example.com")]
    public void Hosts_that_could_inject_rules_are_rejected(string host) =>
        Assert.Throws<ArgumentException>(() => _p.RouteLabels(new RouteSpec("web", [host], 80, "n")));

    [Fact]
    public void Route_name_and_port_are_validated()
    {
        Assert.Throws<ArgumentException>(() => _p.RouteLabels(new RouteSpec("Bad Name", ["a.example.com"], 80, "n")));
        Assert.Throws<ArgumentException>(() => _p.RouteLabels(new RouteSpec("web", ["a.example.com"], 0, "n")));
    }

    [Fact]
    public void Ensure_command_configures_acme_only_with_an_email()
    {
        var withEmail = Encoding.UTF8.GetString(_p.BuildEnsureCommand(new ProxySettings("ops@example.com")).Config);
        Assert.Contains("httpChallenge", withEmail);
        Assert.Contains("ops@example.com", withEmail);
        Assert.Contains("exposedByDefault: false", withEmail);

        var without = Encoding.UTF8.GetString(_p.BuildEnsureCommand(new ProxySettings(null)).Config);
        Assert.DoesNotContain("certificatesResolvers", without);

        Assert.Throws<ArgumentException>(() => _p.BuildEnsureCommand(new ProxySettings("a\"b@x.io")));
    }
}

public class DnsCheckerTests
{
    private sealed class FakeResolver(params string[] addresses) : IDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(addresses.Select(IPAddress.Parse).ToList());
    }

    [Fact]
    public async Task Matching_address_is_ok() =>
        Assert.True((await new DnsChecker(new FakeResolver("203.0.113.5")).CheckAsync("a.example.com", ["203.0.113.5"], default)).Ok);

    [Fact]
    public async Task Mapped_ipv6_matches_ipv4() =>
        Assert.Equal(DnsStatus.Ok, (await new DnsChecker(new FakeResolver("::ffff:203.0.113.5")).CheckAsync("a", ["203.0.113.5"], default)).Status);

    [Fact]
    public async Task Other_address_is_a_mismatch_with_a_helpful_message()
    {
        var r = await new DnsChecker(new FakeResolver("198.51.100.1")).CheckAsync("a.example.com", ["203.0.113.5"], default);
        Assert.Equal(DnsStatus.Mismatch, r.Status);
        Assert.Contains("203.0.113.5", r.Message);
    }

    [Fact]
    public async Task Unresolvable_host_is_not_found() =>
        Assert.Equal(DnsStatus.Missing, (await new DnsChecker(new FakeResolver()).CheckAsync("a", ["203.0.113.5"], default)).Status);
}

public class GitProviderTests
{
    private static Dictionary<string, string> Headers(params (string, string)[] h) => h.ToDictionary(x => x.Item1, x => x.Item2);

    private const string PushBody = """
        {"ref":"refs/heads/main","after":"abc123","deleted":false,"head_commit":{"message":"fix","author":{"name":"Ann"}}}
        """;

    private static string GitHubSig(string secret, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    [Fact]
    public void GitHub_signature_is_verified_in_constant_time_form()
    {
        var p = new GitHubProvider();
        var body = Encoding.UTF8.GetBytes(PushBody);
        Assert.True(p.VerifySignature(Headers(("x-hub-signature-256", GitHubSig("s3", PushBody))), body, "s3"));
        Assert.False(p.VerifySignature(Headers(("X-Hub-Signature-256", GitHubSig("other", PushBody))), body, "s3"));
        Assert.False(p.VerifySignature(Headers(("X-Hub-Signature-256", "sha1=abc")), body, "s3"));
        Assert.False(p.VerifySignature(Headers(), body, "s3"));
    }

    [Fact]
    public void GitHub_push_is_parsed()
    {
        var e = new GitHubProvider().Parse(Headers(("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "d-1")), Encoding.UTF8.GetBytes(PushBody))!;
        Assert.True(e.IsPush);
        Assert.Equal("main", e.Branch);
        Assert.Equal("abc123", e.CommitSha);
        Assert.Equal("Ann", e.CommitAuthor);
        Assert.Equal("d-1", e.DeliveryId);
        Assert.False(e.BranchDeleted);
    }

    [Fact]
    public void GitHub_ping_is_not_a_push_and_branch_delete_is_flagged()
    {
        var p = new GitHubProvider();
        Assert.False(p.Parse(Headers(("X-GitHub-Event", "ping"), ("X-GitHub-Delivery", "d")), "{}"u8)!.IsPush);
        var del = p.Parse(Headers(("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "d")),
            """{"ref":"refs/heads/x","after":"0000000000000000000000000000000000000000"}"""u8)!;
        Assert.True(del.BranchDeleted);
        Assert.Null(p.Parse(Headers(("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "d")), "not json"u8));
        Assert.Null(p.Parse(Headers(), "{}"u8));
    }

    [Fact]
    public void GitLab_token_and_push()
    {
        var p = new GitLabProvider();
        Assert.True(p.VerifySignature(Headers(("X-Gitlab-Token", "tok")), [], "tok"));
        Assert.False(p.VerifySignature(Headers(("X-Gitlab-Token", "nope")), [], "tok"));
        var body = """{"ref":"refs/heads/dev","checkout_sha":"c0ffee","after":"c0ffee","commits":[{"message":"m1","author":{"name":"A"}},{"message":"m2","author":{"name":"B"}}]}""";
        var e = p.Parse(Headers(("X-Gitlab-Event", "Push Hook")), Encoding.UTF8.GetBytes(body))!;
        Assert.Equal("dev", e.Branch);
        Assert.Equal("m2", e.CommitMessage);
        Assert.Equal("B", e.CommitAuthor);
        Assert.Equal(64, e.DeliveryId.Length); // body hash when GitLab sends no UUID
    }

    [Fact]
    public void Generic_provider_uses_aethera_token()
    {
        var p = new GenericGitProvider();
        Assert.True(p.VerifySignature(Headers(("X-Aethera-Token", "t")), [], "t"));
        var e = p.Parse(Headers(), """{"ref":"refs/heads/main","commit":"abc"}"""u8)!;
        Assert.Equal("abc", e.CommitSha);
        Assert.True(e.IsPush);
    }

    [Theory]
    [InlineData(null, "main", "main", true)]
    [InlineData(null, "main", "dev", false)]
    [InlineData("release/*", "main", "release/1.0", true)]
    [InlineData("release/*", "main", "release/1/x", false)]
    [InlineData("release/**", "main", "release/1/x", true)]
    [InlineData("main, dev", "x", "dev", true)]
    [InlineData("ma.n", "x", "main", false)]
    public void Branch_filters(string? filter, string configured, string branch, bool expected) =>
        Assert.Equal(expected, BranchMatcher.Matches(filter, configured, branch));
}

public class ImagePolicyTests
{
    [Fact]
    public void Tag_follows_the_naming_convention()
    {
        var id = Guid.Parse("0190f3c2-0000-7000-8000-000000000001");
        Assert.Equal("aethera/api:0190f3c2-0000-7000-8000-000000000001", ImagePolicy.Tag("api", id));
    }

    [Fact]
    public void Cleanup_keeps_current_and_newest_rollback_points_only()
    {
        var now = DateTimeOffset.UnixEpoch.AddYears(50);
        ImagePolicy.Candidate C(string img, double daysAgo, bool rp, Guid? id = null) => new(img, id ?? Guid.NewGuid(), now.AddDays(-daysAgo), rp);
        var current = Guid.NewGuid();
        var images = new[]
        {
            C("live", 0, true, current), C("failed", 0.5, false), C("i1", 1, true), C("i2", 2, true), C("i3", 3, true), C("i4", 4, true),
        };

        var remove = ImagePolicy.ImagesToRemove(images, current, keepRollbackPoints: 2);

        // Kept: the live image and the two newest rollback points (live, i1). Failed builds are never kept.
        Assert.Equal(["failed", "i2", "i3", "i4"], remove.OrderBy(x => x).ToArray());
    }
}

public class StrategyTests
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Transport(Func<object, object?> respond) : IServerTransport
    {
        public List<(string Name, string? Container)> Commands { get; } = [];
        public TransportKind Kind => TransportKind.Agent;
        public TransportCapabilities Capabilities => TransportCapabilities.AllAgent;
        public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken ct) => ValueTask.FromResult(TransportStatus.Up(Kind));

        public Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(Guid serverId, TCommand command, CommandOptions options, CancellationToken ct)
            where TCommand : IServerCommand<TResult>
        {
            Commands.Add((command.Name, command switch
            {
                ContainerCreateCommand c => c.Spec.Name,
                ContainerRemoveCommand rm => rm.Container,
                _ => null,
            }));
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(respond(command) is TResult res
                ? new CommandOutcome<TResult>(CommandStatus.Succeeded, res, CommandErrorCode.None, null, 0, now, now, false)
                : new CommandOutcome<TResult>(CommandStatus.Failed, default, CommandErrorCode.Internal, "boom", 1, now, now, false));
        }

        public IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static object? Happy(object c) => c switch
    {
        ImagePullCommand => new ImagePulled("id", "dg", 1, [], false),
        NetworkCreateCommand n => new DockerNetwork("n", n.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []),
        ContainerCreateCommand => new ContainerCreated("c-new", "x", ContainerRunState.Running, []),
        ContainerRemoveCommand => Unit.Value,
        HealthProbeCommand => new HealthProbeOutcome(true, 1, 200, TimeSpan.Zero, "ok", null),
        _ => null,
    };

    private static async Task<(Deployment, Transport, DeploymentRunResult)> RunAsync(
        Func<object, object?> respond, string strategy, bool probe = true)
    {
        var clock = new Clock();
        var d = Deployment.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, DeploymentTrigger.Manual, clock.UtcNow, strategy);
        d.Start("{}", clock.UtcNow);
        var plan = new DeploymentPlan
        {
            ServerId = d.ServerId, ImageReference = "nginx", Container = new ContainerSpec("x", "web"),
            Health = probe ? new TcpProbeTarget("h", 80) : null, PreviousContainer = "web-6",
        };
        var t = new Transport(respond);
        var runner = new DeploymentRunner(t, [new RecreateStrategy(), new LowDowntimeStrategy()], clock);
        var result = await runner.RunAsync(d, plan, (_, _) => Task.CompletedTask, default);
        return (d, t, result);
    }

    [Fact]
    public async Task LowDowntime_starts_new_before_removing_old()
    {
        var (d, t, result) = await RunAsync(Happy, DeploymentStrategies.LowDowntime);

        Assert.Equal(DeploymentRunResult.Running, result);
        Assert.Equal(DeploymentStatus.Running, d.Status);
        var names = t.Commands.Select(c => c.Name).ToList();
        Assert.True(names.IndexOf("container.create") < names.IndexOf("health.probe"));
        Assert.True(names.IndexOf("health.probe") < names.IndexOf("container.remove"));
        Assert.Equal("web-7", t.Commands.Single(c => c.Name == "container.create").Container);
        Assert.Equal("web-6", t.Commands.Single(c => c.Name == "container.remove").Container);
    }

    [Fact]
    public async Task LowDowntime_keeps_old_container_and_removes_candidate_when_unhealthy()
    {
        var (d, t, result) = await RunAsync(
            c => c is HealthProbeCommand ? new HealthProbeOutcome(false, 3, 0, TimeSpan.Zero, "refused", null) : Happy(c), DeploymentStrategies.LowDowntime);

        Assert.Equal(DeploymentRunResult.Failed, result);
        Assert.Equal(DeploymentStep.HealthCheck, d.FailedStep);
        var removed = t.Commands.Where(c => c.Name == "container.remove").Select(c => c.Container).ToList();
        Assert.Equal(["c-new"], removed);
        Assert.DoesNotContain("web-6", removed);
    }

    [Fact]
    public async Task Recreate_removes_old_before_creating_new()
    {
        var (_, t, _) = await RunAsync(Happy, DeploymentStrategies.Recreate);
        var names = t.Commands.Select(c => c.Name).ToList();
        Assert.True(names.IndexOf("container.remove") < names.IndexOf("container.create"));
    }
}
