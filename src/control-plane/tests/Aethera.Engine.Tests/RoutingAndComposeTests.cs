using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Tests;

/// <summary>Routing and compose (ADR 0010): unique route names, domain options, compose from the repository, per-service overrides, DNS-01.</summary>
public partial class RoutingAndComposeTests
{
    private static readonly Guid Wid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Did = Guid.Parse("0190f3c2-0000-7000-8000-000000000009");
    private static readonly Guid Env = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SecretId = Guid.NewGuid();
    private static readonly Guid AuthId = Guid.NewGuid();
    private static readonly Guid ProviderId = Guid.NewGuid();

    // Not a real hash: shaped like one (60 characters, $2y$10$ and 53 characters of the bcrypt alphabet).
    private const string Htpasswd = "alice:$2y$10$abcdefghijklmnopqrstuuabcdefghijklmnopqrstuvwxyz01234";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,62}$")]
    private static partial Regex ValidName();

    private static DeploymentSnapshot Web(Func<DeploymentSnapshot, DeploymentSnapshot>? edit = null, Guid? workload = null, string slug = "website")
    {
        var s = new DeploymentSnapshot
        {
            WorkloadId = workload ?? Wid, EnvironmentId = Env, Slug = slug, SourceKind = ApplicationSourceKind.DockerImage,
            Image = new ImageSnapshot("nginx", "1", ImagePullPolicy.IfNotPresent, null),
            Ports = [new(8080, PortProtocol.Tcp, null, true)],
            Domains = [new("a.example.com", "/", true, null)],
            Networks = [new(DeploymentPlanBuilder.EnvironmentNetwork(Env), false, [slug])],
        };
        return edit?.Invoke(s) ?? s;
    }

    private static PlanContext Context(bool acme = true, IReadOnlyList<ComposeServiceDefinition>? services = null, GitCredentialSpec? git = null) => new()
    {
        ServerId = Guid.NewGuid(), DeploymentId = Did, DeploymentNumber = 1, Proxy = new TraefikProxyProvider(), AcmeAvailable = acme, ComposeServices = services,
        GitCredential = git,
        ResolveSecret = (id, _) => id == AuthId ? Htpasswd : id == SecretId ? "p@ss$word" : throw new InvalidOperationException("unexpected secret"),
    };

    private static IReadOnlyDictionary<string, string> Labels(DeploymentSnapshot s, PlanContext? c = null) =>
        DeploymentPlanBuilder.Build(s, c ?? Context()).Container.Labels!;

    // ---- unique names ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_workloads_with_the_same_slug_get_different_router_and_service_names()
    {
        var prod = Labels(Web());
        var dev = Labels(Web(workload: Guid.Parse("99999999-2222-3333-4444-555555555555")));

        var prodRouters = prod.Keys.Where(k => k.StartsWith("traefik.http.routers.")).ToHashSet();
        var devRouters = dev.Keys.Where(k => k.StartsWith("traefik.http.routers.")).ToHashSet();
        Assert.Empty(prodRouters.Intersect(devRouters));
        Assert.Contains("traefik.http.routers.website-11111111-https.rule", prod.Keys);
        Assert.Contains("traefik.http.services.website-99999999.loadbalancer.server.port", dev.Keys);
    }

    [Fact]
    public void A_workload_slugged_aethera_cannot_collide_with_the_control_planes_own_labels()
    {
        var labels = Labels(Web(slug: "aethera"));
        Assert.Contains("traefik.http.routers.aethera-11111111-https.rule", labels.Keys);
        Assert.DoesNotContain("traefik.http.routers.aethera-https.rule", labels.Keys);
        Assert.DoesNotContain("traefik.http.services.aethera.loadbalancer.server.port", labels.Keys);
        Assert.All(new[] { "aethera", "aethera-http", "aethera-https", "aethera-redirect" }, n => Assert.True(RouteNames.IsReserved(n)));

        var route = new RouteSpec("aethera", ["a.example.com"], 80, "aethera-proxy");
        Assert.Throws<ArgumentException>(() => new TraefikProxyProvider().RouteLabels(route));
    }

    [Fact]
    public void Names_are_stable_per_workload_short_enough_and_valid()
    {
        Assert.Equal(RouteNames.ForWorkload("website", Wid), RouteNames.ForWorkload("website", Wid));
        foreach (var slug in new[] { "a", "website", new string('x', 63), "a-b-c", "x--y" })
        {
            var name = RouteNames.ForWorkload(slug, Wid);
            Assert.True(name.Length <= RouteNames.MaxBaseLength, name);
            Assert.Matches(ValidName(), name);
            Assert.EndsWith("-11111111", name);
            Assert.True(RouteNames.Group(name, 98).Length <= 63);
        }
    }

    // ---- domain options -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Redirect_domain_gets_a_permanent_redirectregex_to_the_target_keeping_path_and_query()
    {
        var snap = Web(s => s with { Domains = [new("www.example.com", "/", true, null, RedirectTo: "example.com")] });
        var l = Labels(snap);

        Assert.Equal("^https?://[^/]+(.*)$", l["traefik.http.middlewares.website-11111111-redir.redirectregex.regex"]);
        Assert.Equal("https://example.com${1}", l["traefik.http.middlewares.website-11111111-redir.redirectregex.replacement"]);
        Assert.Equal("true", l["traefik.http.middlewares.website-11111111-redir.redirectregex.permanent"]);
        Assert.Equal("noop@internal", l["traefik.http.routers.website-11111111-https.service"]);
        Assert.Equal("website-11111111-redir", l["traefik.http.routers.website-11111111-http.middlewares"]);
        Assert.Equal("website-11111111-redir", l["traefik.http.routers.website-11111111-https.middlewares"]);
        Assert.DoesNotContain(l.Keys, k => k.Contains(".loadbalancer."));
    }

    [Fact]
    public void Redirect_target_must_be_a_plain_hostname()
    {
        var p = new TraefikProxyProvider();
        foreach (var bad in new[] { "*.example.com", "a b.com", "evil.com`) || Host(`x", "example.com/path", "x.com:80" })
            Assert.Throws<ArgumentException>(() => p.RouteLabels(new RouteSpec("w-11111111", ["a.example.com"], 80, "net") { RedirectTo = bad }));
    }

    [Fact]
    public void Strip_prefix_adds_the_stripprefix_middleware_only_with_a_prefix()
    {
        var withPrefix = Labels(Web(s => s with { Domains = [new("a.example.com", "/api", true, null, StripPathPrefix: true)] }));
        Assert.Equal("/api", withPrefix["traefik.http.middlewares.website-11111111-strip.stripprefix.prefixes"]);
        Assert.Equal("website-11111111-strip", withPrefix["traefik.http.routers.website-11111111-https.middlewares"]);
        Assert.Equal("(Host(`a.example.com`)) && PathPrefix(`/api`)", withPrefix["traefik.http.routers.website-11111111-https.rule"]);

        var without = Labels(Web(s => s with { Domains = [new("a.example.com", "/", true, null, StripPathPrefix: true)] }));
        Assert.DoesNotContain(without.Keys, k => k.Contains("stripprefix"));
    }

    [Fact]
    public void Basic_auth_puts_the_users_on_the_https_router_and_keeps_them_out_of_the_snapshot()
    {
        var snap = Web(s => s with { Domains = [new("a.example.com", "/", true, null, BasicAuthSecretId: AuthId)] });
        var l = Labels(snap);

        Assert.Equal(Htpasswd, l["traefik.http.middlewares.website-11111111-auth.basicauth.users"]);
        Assert.Equal("true", l["traefik.http.middlewares.website-11111111-auth.basicauth.removeheader"]);
        Assert.Equal("website-11111111-auth", l["traefik.http.routers.website-11111111-https.middlewares"]);
        // The http router only redirects to https: nobody is asked for a password over http.
        Assert.Equal("website-11111111-redirect", l["traefik.http.routers.website-11111111-http.middlewares"]);
        Assert.DoesNotContain("$2y$", snap.ToJson());
        Assert.Contains(AuthId.ToString(), snap.ToJson());
    }

    [Fact]
    public void Only_bcrypt_htpasswd_lines_are_accepted_for_basic_auth()
    {
        var p = new TraefikProxyProvider();
        foreach (var bad in new[] { "alice:plain", "alice:{SHA}x", "a:b:$2y$10$abcdefghijklmnopqrstuuabcdefghijklmnopqrstuvwxyz01234", "x\"y:$2y$10$abcdefghijklmnopqrstuuabcdefghijklmnopqrstuvwxyz01234" })
            Assert.Throws<ArgumentException>(() => p.RouteLabels(new RouteSpec("w-11111111", ["a.example.com"], 80, "net") { BasicAuthUsers = [bad] }));
        Assert.True(TraefikProxyProvider.IsValidHtpasswdLine(Htpasswd));
    }

    [Fact]
    public void Wildcard_host_uses_hostregexp_the_cloudflare_resolver_and_lists_the_certificate_domains()
    {
        var snap = Web(s => s with { Domains = [new("example.com", "/", true, null, DnsProviderId: ProviderId), new("*.example.com", "/", true, null, DnsProviderId: ProviderId)] });
        var l = Labels(snap);

        Assert.Equal("Host(`example.com`) || HostRegexp(`^[a-z0-9-]+\\.example\\.com$`)", l["traefik.http.routers.website-11111111-https.rule"]);
        Assert.Equal("cloudflare", l["traefik.http.routers.website-11111111-https.tls.certresolver"]);
        Assert.Equal("example.com", l["traefik.http.routers.website-11111111-https.tls.domains[0].main"]);
        Assert.Equal("*.example.com", l["traefik.http.routers.website-11111111-https.tls.domains[1].main"]);
    }

    [Fact]
    public void Wildcard_without_a_dns_provider_cannot_be_routed()
    {
        var snap = Web(s => s with { Domains = [new("*.example.com", "/", true, null)] });
        var e = Assert.Throws<ProxyRoutingException>(() => DeploymentPlanBuilder.Build(snap, Context()));
        Assert.Contains("DNS provider", e.Message);
        Assert.Throws<ArgumentException>(() => new TraefikProxyProvider().RouteLabels(new RouteSpec("w-11111111", ["*.example.com"], 80, "net")));
    }

    [Fact]
    public void Dns_provider_domain_uses_the_cloudflare_resolver_without_a_wildcard_too()
    {
        var l = Labels(Web(s => s with { Domains = [new("a.example.com", "/", true, null, DnsProviderId: ProviderId)] }));
        Assert.Equal("cloudflare", l["traefik.http.routers.website-11111111-https.tls.certresolver"]);
        Assert.DoesNotContain(l.Keys, k => k.Contains("tls.domains"));
    }

    [Fact]
    public void Without_an_acme_email_https_routes_name_no_certificate_resolver()
    {
        var l = Labels(Web(s => s with { Domains = [new("a.example.com", "/", true, null, DnsProviderId: ProviderId)] }), Context(acme: false));
        Assert.Equal("true", l["traefik.http.routers.website-11111111-https.tls"]);
        Assert.DoesNotContain(l.Keys, k => k.EndsWith("certresolver"));
        Assert.Equal("websecure", l["traefik.http.routers.website-11111111-https.entrypoints"]);
    }

    [Fact]
    public void Host_names_cannot_inject_into_a_rule()
    {
        var p = new TraefikProxyProvider();
        foreach (var bad in new[] { "a.com`) || Host(`b.com", "a.com`", "a b.com", "a.com\nx", "*.a.com`)", "a..com", "-a.com" })
            Assert.Throws<ArgumentException>(() => p.RouteLabels(new RouteSpec("w-11111111", [bad], 80, "net")));
    }

    // ---- custom labels --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Custom_labels_are_merged_last_but_never_override_aethera_or_the_proxy_network()
    {
        var snap = Web(s => s with
        {
            CustomLabels = new()
            {
                ["traefik.http.middlewares.cors.headers.accesscontrolalloworiginlist"] = "https://x.example.com",
                ["traefik.http.routers.website-11111111-https.middlewares"] = "cors",
                ["aethera.workload"] = "evil", ["traefik.docker.network"] = "host", ["Aethera.managed"] = "false",
            },
        });
        var l = Labels(snap);

        Assert.Equal("cors", l["traefik.http.routers.website-11111111-https.middlewares"]);
        Assert.Equal("https://x.example.com", l["traefik.http.middlewares.cors.headers.accesscontrolalloworiginlist"]);
        Assert.Equal(Wid.ToString(), l["aethera.workload"]);
        Assert.Equal("aethera-proxy", l["traefik.docker.network"]);
        Assert.Equal("true", l["aethera.managed"]);
    }

    [Fact]
    public void Custom_label_rules_refuse_protected_keys_bad_names_and_oversize_maps()
    {
        Assert.Empty(CustomLabelRules.Validate(new Dictionary<string, string> { ["traefik.http.middlewares.x.headers.customrequestheaders.X-A"] = "b" }));
        Assert.Contains(CustomLabelRules.Validate(new Dictionary<string, string> { ["aethera.x"] = "1" }), e => e.Key == "aethera.x");
        Assert.Contains(CustomLabelRules.Validate(new Dictionary<string, string> { ["traefik.docker.network"] = "1" }), e => e.Key == "traefik.docker.network");
        Assert.NotEmpty(CustomLabelRules.Validate(new Dictionary<string, string> { ["a b"] = "1" }));
        Assert.NotEmpty(CustomLabelRules.Validate(new Dictionary<string, string> { ["a"] = "x\ny" }));
        Assert.NotEmpty(CustomLabelRules.Validate(Enumerable.Range(0, 51).ToDictionary(i => $"k{i}", _ => "v")));
        Assert.NotEmpty(CustomLabelRules.Validate(Enumerable.Range(0, 10).ToDictionary(i => $"k{i}", _ => new string('v', 2000))));
        Assert.NotEmpty(CustomLabelRules.Validate(new Dictionary<string, string> { ["a"] = new string('v', 2049) }));
    }

    // ---- static configuration -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Static_config_adds_the_cloudflare_resolver_and_passes_the_token_as_a_secret_variable()
    {
        var p = new TraefikProxyProvider();
        var command = p.BuildEnsureCommand(new ProxySettings("ops@example.com") { DnsChallenge = new DnsChallengeSettings("cloudflare", "cf-token-123") });
        var yaml = Encoding.UTF8.GetString(command.Config);

        Assert.Contains("  letsencrypt:", yaml);
        Assert.Contains("  cloudflare:", yaml);
        Assert.Contains("storage: /acme/acme-cloudflare.json", yaml);
        Assert.Contains("provider: cloudflare", yaml);
        Assert.DoesNotContain("cf-token-123", yaml);
        var env = Assert.Single(command.Env!);
        Assert.Equal("CF_DNS_API_TOKEN", env.Name);
        Assert.Equal("cf-token-123", env.Secret!.Value);
    }

    [Fact]
    public void A_rotated_token_changes_the_configuration_so_the_proxy_restarts_with_it()
    {
        var p = new TraefikProxyProvider();
        string Config(string token) => Encoding.UTF8.GetString(p.BuildEnsureCommand(new ProxySettings("ops@example.com") { DnsChallenge = new DnsChallengeSettings("cloudflare", token) }).Config);
        Assert.NotEqual(Config("one"), Config("two"));
        Assert.Equal(Config("one"), Config("one"));
    }

    [Fact]
    public void Static_config_without_a_dns_provider_or_an_email_has_no_cloudflare_resolver()
    {
        var p = new TraefikProxyProvider();
        var plain = p.BuildEnsureCommand(new ProxySettings("ops@example.com"));
        Assert.DoesNotContain("cloudflare", Encoding.UTF8.GetString(plain.Config));
        Assert.Null(plain.Env);

        var noEmail = p.BuildEnsureCommand(new ProxySettings(null) { DnsChallenge = new DnsChallengeSettings("cloudflare", "t") });
        var yaml = Encoding.UTF8.GetString(noEmail.Config);
        Assert.DoesNotContain("certificatesResolvers", yaml);
        Assert.DoesNotContain("cloudflare", yaml);
        Assert.Null(noEmail.Env);
    }

    // ---- compose from the repository ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<ComposeServiceDefinition> Services() =>
    [
        new("web", "", true, [3000]), new("worker", "", true, []), new("db", "postgres:17", false, [5432]),
    ];

    private static DeploymentSnapshot ComposeFromGit(Func<DeploymentSnapshot, DeploymentSnapshot>? edit = null)
    {
        var s = new DeploymentSnapshot
        {
            WorkloadId = Wid, EnvironmentId = Env, Slug = "turnone", SourceKind = ApplicationSourceKind.Compose,
            Git = new GitSnapshot("git@github.com:acme/turnone.git", "main", null, Guid.NewGuid()),
            Compose = new ComposeSnapshot("", null, "deploy/docker-compose.yml"),
            Env =
            [
                new("PLAIN", "v$1", null, null, false, true), new("DB_PASSWORD", null, SecretId, 1, false, true),
                new("NPM_TOKEN", "tok", null, null, true, false), new("BOTH", "b", null, null, true, true),
            ],
            Domains = [new("turnone.example.com", "/", true, null, ComposeService: "web"), new("api.example.com", "/", true, 8080, ComposeService: "web")],
            Networks = [new(DeploymentPlanBuilder.EnvironmentNetwork(Env), false, ["turnone"]), new("custom-net", true, ["tn-custom"])],
        };
        return edit?.Invoke(s) ?? s;
    }

    private static JsonElement Override(DeploymentPlan plan) => JsonDocument.Parse(plan.Compose!.OverrideFile!).RootElement;

    [Fact]
    public void Compose_from_the_repository_sends_git_path_and_build_args_not_the_file()
    {
        var credential = new GitCredentialSpec("cred", SshPrivateKey: new SecretValue("key"));
        var plan = DeploymentPlanBuilder.Build(ComposeFromGit(), Context(services: Services(), git: credential));
        var spec = plan.Compose!;

        Assert.Equal("", spec.ComposeFile);
        Assert.Equal("deploy/docker-compose.yml", spec.ComposePath);
        Assert.Equal("git@github.com:acme/turnone.git", spec.Git!.Url);
        Assert.Equal("main", spec.Git.Ref);
        Assert.Equal(1, spec.Git.Depth);
        Assert.Same(credential, spec.Git.Credentials);
        Assert.Equal(["NPM_TOKEN", "BOTH"], spec.BuildArgs!.Select(a => a.Name));
        Assert.Equal("turnone-11111111", spec.ProjectName);
        Assert.Contains("PLAIN", spec.Env!.Select(e => e.Name));
    }

    [Fact]
    public void A_commit_pin_makes_a_full_checkout_at_that_commit()
    {
        var snap = ComposeFromGit(s => s with { Git = s.Git! with { CommitPin = "abc1234" } });
        var spec = DeploymentPlanBuilder.Build(snap, Context(services: Services())).Compose!;
        Assert.Equal("abc1234", spec.Git!.Commit);
        Assert.Equal(0, spec.Git.Depth);
    }

    [Fact]
    public void Inline_compose_keeps_the_file_and_has_no_git()
    {
        var snap = ComposeFromGit(s => s with { Git = null, Compose = new ComposeSnapshot("services:\n  web:\n    image: nginx\n", null) });
        var spec = DeploymentPlanBuilder.Build(snap, Context(services: Services())).Compose!;
        Assert.Equal("services:\n  web:\n    image: nginx\n", spec.ComposeFile);
        Assert.Null(spec.Git);
        Assert.Null(spec.ComposePath);
        Assert.Null(spec.BuildArgs);
    }

    [Fact]
    public void Override_routes_each_service_and_groups_by_port_and_prefix()
    {
        var plan = DeploymentPlanBuilder.Build(ComposeFromGit(s => s with
        {
            Domains =
            [
                new("turnone.example.com", "/", true, null, ComposeService: "web"), new("www.turnone.example.com", "/", true, null, ComposeService: "web"),
                new("api.example.com", "/", true, 8080, ComposeService: "web"), new("db.example.com", "/", true, null, ComposeService: "db"),
            ],
        }), Context(services: Services()));
        var services = Override(plan).GetProperty("services");

        var web = services.GetProperty("web").GetProperty("labels");
        // Groups are ordered by service: db first, then web with port 3000 (from the file) and two hosts in one router, then web on 8080.
        Assert.Equal("Host(`turnone.example.com`) || Host(`www.turnone.example.com`)", web.GetProperty("traefik.http.routers.turnone-11111111-2-https.rule").GetString());
        Assert.Equal("3000", web.GetProperty("traefik.http.services.turnone-11111111-2.loadbalancer.server.port").GetString());
        Assert.Equal("8080", web.GetProperty("traefik.http.services.turnone-11111111-3.loadbalancer.server.port").GetString());
        var db = services.GetProperty("db").GetProperty("labels");
        Assert.Equal("5432", db.GetProperty("traefik.http.services.turnone-11111111.loadbalancer.server.port").GetString());
        Assert.DoesNotContain(services.GetProperty("worker").GetProperty("labels").EnumerateObject(), p => p.Name.StartsWith("traefik.http"));
        Assert.Equal("aethera-proxy", services.GetProperty("web").GetProperty("networks").EnumerateObject().Single(p => p.Name == "aethera-proxy").Name);
        Assert.False(services.GetProperty("worker").GetProperty("networks").TryGetProperty("aethera-proxy", out _));
    }

    [Fact]
    public void Every_service_joins_the_environment_network_with_aliases_and_custom_networks_stay()
    {
        var plan = DeploymentPlanBuilder.Build(ComposeFromGit(), Context(services: Services()));
        var root = Override(plan);
        var envNet = DeploymentPlanBuilder.EnvironmentNetwork(Env);

        foreach (var name in new[] { "web", "worker", "db" })
        {
            var networks = root.GetProperty("services").GetProperty(name).GetProperty("networks");
            Assert.True(networks.TryGetProperty("default", out _), name);
            Assert.True(networks.TryGetProperty("custom-net", out _), name);
            var aliases = networks.GetProperty(envNet).GetProperty("aliases").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains(name, aliases);
            Assert.Contains($"turnone-{name}", aliases);
            // The workload's own name is the primary (first routed) service's alias.
            Assert.Equal(name == "web", aliases.Contains("turnone"));
        }

        Assert.True(root.GetProperty("networks").GetProperty(envNet).GetProperty("external").GetBoolean());
        Assert.Equal(envNet, root.GetProperty("networks").GetProperty(envNet).GetProperty("name").GetString());
        Assert.Contains(envNet, plan.Networks);
        Assert.Contains("custom-net", plan.InternalNetworks);
    }

    [Fact]
    public void Runtime_variables_reach_every_service_through_environment_with_dollars_escaped()
    {
        var plan = DeploymentPlanBuilder.Build(ComposeFromGit(), Context(services: Services()));
        foreach (var name in new[] { "web", "worker", "db" })
        {
            var env = Override(plan).GetProperty("services").GetProperty(name).GetProperty("environment");
            Assert.Equal("v$$1", env.GetProperty("PLAIN").GetString()); // compose interpolates the override too
            Assert.Equal("p@ss$$word", env.GetProperty("DB_PASSWORD").GetString());
            Assert.Equal("b", env.GetProperty("BOTH").GetString());
            Assert.False(env.TryGetProperty("NPM_TOKEN", out _)); // build-time only
        }
    }

    [Fact]
    public void Override_escapes_dollars_in_labels_like_redirect_replacements_and_regular_expressions()
    {
        var plan = DeploymentPlanBuilder.Build(ComposeFromGit(s => s with
        {
            Domains = [new("www.example.com", "/", true, null, ComposeService: "web", RedirectTo: "example.com"), new("*.example.com", "/", true, null, ComposeService: "web", DnsProviderId: ProviderId)],
        }), Context(services: Services()));
        var labels = Override(plan).GetProperty("services").GetProperty("web").GetProperty("labels");

        Assert.Equal("https://example.com$${1}", labels.GetProperty("traefik.http.middlewares.turnone-11111111-redir.redirectregex.replacement").GetString());
        Assert.Equal("^https?://[^/]+(.*)$$", labels.GetProperty("traefik.http.middlewares.turnone-11111111-redir.redirectregex.regex").GetString());
        Assert.Contains("$$`)", labels.GetProperty("traefik.http.routers.turnone-11111111-2-https.rule").GetString());
    }

    [Fact]
    public void Basic_auth_hashes_are_escaped_in_the_override_and_only_there()
    {
        var plan = DeploymentPlanBuilder.Build(ComposeFromGit(s => s with
        {
            Domains = [new("turnone.example.com", "/", true, null, ComposeService: "web", BasicAuthSecretId: AuthId)],
        }), Context(services: Services()));
        var users = Override(plan).GetProperty("services").GetProperty("web").GetProperty("labels")
            .GetProperty("traefik.http.middlewares.turnone-11111111-auth.basicauth.users").GetString();
        Assert.Equal(Htpasswd.Replace("$", "$$"), users);
    }

    [Fact]
    public void A_domain_naming_a_service_that_is_not_in_the_file_fails_with_a_clear_message()
    {
        var snap = ComposeFromGit(s => s with { Domains = [new("x.example.com", "/", true, 80, ComposeService: "frontend")] });
        var e = Assert.Throws<ProxyRoutingException>(() => DeploymentPlanBuilder.Build(snap, Context(services: Services())));
        Assert.Equal("The domain x.example.com routes to the service frontend, which is not in the compose file.", e.Message);
    }

    [Fact]
    public void A_compose_domain_without_a_service_or_a_port_fails_clearly()
    {
        var noService = ComposeFromGit(s => s with { Domains = [new("x.example.com", "/", true, 80)] });
        Assert.Contains("names no compose service", Assert.Throws<ProxyRoutingException>(() => DeploymentPlanBuilder.Build(noService, Context(services: Services()))).Message);

        var noPort = ComposeFromGit(s => s with { Domains = [new("x.example.com", "/", true, null, ComposeService: "worker")] });
        Assert.Contains("needs a target port", Assert.Throws<ProxyRoutingException>(() => DeploymentPlanBuilder.Build(noPort, Context(services: Services()))).Message);
    }

    [Fact]
    public void Domains_of_snapshots_from_before_the_service_field_use_the_routed_service()
    {
        var snap = ComposeFromGit(s => s with
        {
            Git = null, Compose = new ComposeSnapshot("services:\n  web:\n    image: x\n", "web"), Domains = [new("x.example.com", "/", true, 8080)],
        });
        var labels = Override(DeploymentPlanBuilder.Build(snap, Context())).GetProperty("services").GetProperty("web").GetProperty("labels");
        Assert.Equal("8080", labels.GetProperty("traefik.http.services.turnone-11111111.loadbalancer.server.port").GetString());
    }

    [Fact]
    public void Without_a_service_list_only_routed_services_are_named()
    {
        var plan = DeploymentPlanBuilder.Build(ComposeFromGit(s => s with { Domains = [new("turnone.example.com", "/", true, 3000, ComposeService: "web")] }), Context(services: null));
        Assert.Equal(["web"], Override(plan).GetProperty("services").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Custom_labels_of_a_compose_app_go_on_the_primary_service_and_cannot_touch_aethera_labels()
    {
        var plan = DeploymentPlanBuilder.Build(ComposeFromGit(s => s with { CustomLabels = new() { ["traefik.http.middlewares.x.compress"] = "true", ["aethera.workload"] = "evil" } }),
            Context(services: Services()));
        var services = Override(plan).GetProperty("services");

        Assert.Equal("true", services.GetProperty("web").GetProperty("labels").GetProperty("traefik.http.middlewares.x.compress").GetString());
        Assert.False(services.GetProperty("db").GetProperty("labels").TryGetProperty("traefik.http.middlewares.x.compress", out _));
        Assert.Equal(Wid.ToString(), services.GetProperty("web").GetProperty("labels").GetProperty("aethera.workload").GetString());
    }

    [Fact]
    public void Old_snapshots_without_the_adr_0010_fields_still_read()
    {
        const string json = """
            {"version":1,"workloadId":"11111111-2222-3333-4444-555555555555","environmentId":"aaaaaaaa-0000-0000-0000-000000000001","slug":"old",
             "sourceKind":"compose","compose":{"content":"services: {}","routedService":"web"},
             "domains":[{"hostname":"a.example.com","pathPrefix":"/","https":true,"targetPort":80}],"networks":[]}
            """;
        var s = DeploymentSnapshot.FromJson(json);
        Assert.Equal("web", s.Compose!.RoutedService);
        Assert.Null(s.Compose.FilePath);
        var d = Assert.Single(s.Domains);
        Assert.Null(d.ComposeService);
        Assert.False(d.StripPathPrefix);
        Assert.Null(d.BasicAuthSecretId);
        Assert.Null(s.CustomLabels);
        Assert.Equal(s.ToJson(), DeploymentSnapshot.FromJson(s.ToJson()).ToJson());
    }
}
