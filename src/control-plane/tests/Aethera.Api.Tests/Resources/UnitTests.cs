using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.DomainNames;
using Aethera.Api.Features.Resources.Projects;
using Aethera.Api.Features.Resources.Services;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Api.Security;
using Aethera.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aethera.Api.Tests.Resources;

public sealed class DotEnvTests
{
    [Fact]
    public void Parse_HandlesCommentsExportQuotesAndMultilineValues()
    {
        var (values, errors) = DotEnv.Parse(
            "# comment\n\nA=1\nexport B = two words # trailing\nC=\"quoted # not a comment\"\nD='single $raw \\n'\nE=\"line1\\nline2\"\n"
            + "F=\"multi\nline\"\nG=\nH=a#b\r\nI=\"esc \\\" quote \\\\ slash\"\n");
        Assert.Empty(errors);
        var map = values.ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal("1", map["A"]);
        Assert.Equal("two words", map["B"]);
        Assert.Equal("quoted # not a comment", map["C"]);
        Assert.Equal("single $raw \\n", map["D"]);
        Assert.Equal("line1\nline2", map["E"]);
        Assert.Equal("multi\nline", map["F"]);
        Assert.Equal("", map["G"]);
        Assert.Equal("a#b", map["H"]);
        Assert.Equal("esc \" quote \\ slash", map["I"]);
    }

    [Fact]
    public void Parse_LastDuplicateWins_AndKeepsFirstPosition()
    {
        var (values, _) = DotEnv.Parse("A=1\nB=2\nA=3\n");
        Assert.Equal(["A", "B"], values.Select(v => v.Key));
        Assert.Equal("3", values[0].Value);
    }

    [Theory]
    [InlineData("not a pair", 1)]
    [InlineData("1BAD=x", 1)]
    [InlineData("A=\"unclosed", 1)]
    [InlineData("A=\"x\" junk", 1)]
    [InlineData("OK=1\n=empty", 2)]
    public void Parse_ReportsTheLineOfEachError(string content, int line)
    {
        var (_, errors) = DotEnv.Parse(content);
        Assert.Contains(errors, e => e.Line == line);
    }

    [Fact]
    public void Format_ThenParse_RoundTrips_EvenForAwkwardValues()
    {
        var original = new[]
        {
            KeyValuePair.Create("PLAIN", "value"), KeyValuePair.Create("EMPTY", ""), KeyValuePair.Create("SPACES", "  padded  "),
            KeyValuePair.Create("HASH", "a #b"), KeyValuePair.Create("QUOTES", "say \"hi\" and 'bye'"), KeyValuePair.Create("SLASH", "C:\\dir\\n"),
            KeyValuePair.Create("NEWLINE", "a\nb\nc\td"), KeyValuePair.Create("EQUALS", "k=v=w"), KeyValuePair.Create("DOLLAR", "$HOME"),
            KeyValuePair.Create("ONLY_QUOTE", "'"), KeyValuePair.Create("QUOTE_EDGES", "'a'"), KeyValuePair.Create("BACKTICK", "`id` $(id) ${X}"),
            KeyValuePair.Create("BACKSLASH_QUOTE", "it\\'s"), KeyValuePair.Create("QUOTE_NEWLINE", "a'\nb'"), KeyValuePair.Create("DOUBLE", "\"x\""),
        };
        var (parsed, errors) = DotEnv.Parse(DotEnv.Format(original));
        Assert.Empty(errors);
        Assert.Equal(original, parsed);
    }
}

public sealed class HostnameRulesTests
{
    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData("  app.example.com.  ", "app.example.com")]
    [InlineData("*.Example.com", "*.example.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("*.bücher.example", "*.xn--bcher-kva.example")]
    [InlineData("a-b.c-d.example.org", "a-b.c-d.example.org")]
    [InlineData("localhost", "localhost")]
    public void Normalizes(string input, string expected)
    {
        Assert.True(HostnameRules.TryNormalize(input, allowWildcard: true, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-bad.example.com")]
    [InlineData("bad-.example.com")]
    [InlineData("under_score.example.com")]
    [InlineData("a..b.example.com")]
    [InlineData("exa mple.com")]
    [InlineData("http://example.com")]
    [InlineData("example.com:8080")]
    [InlineData("example.com/path")]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    [InlineData("example.123")]
    [InlineData("foo.*.example.com")]
    [InlineData("*example.com")]
    [InlineData("**.example.com")]
    [InlineData("*.com")]
    [InlineData("*")]
    public void Rejects(string input) => Assert.False(HostnameRules.TryNormalize(input, allowWildcard: true, out _, out _));

    [Fact]
    public void Rejects_TooLongNamesAndLabels()
    {
        Assert.False(HostnameRules.TryNormalize(new string('a', 64) + ".example.com", true, out _, out _));
        Assert.True(HostnameRules.TryNormalize(new string('a', 63) + ".example.com", true, out _, out _));
        var longName = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)); // 304 characters
        Assert.False(HostnameRules.TryNormalize(longName, true, out _, out _));
    }

    [Fact]
    public void Wildcards_AreRefusedWhenNotAllowed() =>
        Assert.False(HostnameRules.TryNormalize("*.example.com", allowWildcard: false, out _, out _));
}

public sealed class PathRulesTests
{
    [Theory]
    [InlineData("/data", "/data")]
    [InlineData("/var/lib/data/", "/var/lib/data")]
    [InlineData("//var//lib", "/var/lib")]
    public void MountPaths_AreNormalized(string input, string expected)
    {
        Assert.True(VolumeRules.TryNormalizePath(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("relative/path")]
    [InlineData("data")]
    [InlineData("/a/../b")]
    [InlineData("/with space")]
    [InlineData("/a:b")]
    [InlineData("/a,b")]
    public void MountPaths_RejectRootRelativeAndUnsafe(string input) => Assert.False(VolumeRules.TryNormalizePath(input, out _));

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/", "/")]
    [InlineData("/api/", "/api")]
    [InlineData("/v1.0/_a~b-c/", "/v1.0/_a~b-c")]
    [InlineData("/.well-known/acme", "/.well-known/acme")]
    public void DomainPathPrefixes_AreNormalized(string? input, string expected)
    {
        Assert.True(DomainRules.TryNormalizePath(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("/a b")]
    [InlineData("/a?x=1")]
    [InlineData("/a#frag")]
    [InlineData("/../etc")]
    [InlineData("//api//v1")] // since WP1.6 a prefix with '//' is refused, not collapsed
    public void DomainPathPrefixes_RejectBadInput(string input) => Assert.False(DomainRules.TryNormalizePath(input, out _));
}

public sealed class CatalogueTests
{
    [Fact]
    public void ServiceTemplates_CoverTheRequiredCatalogue()
    {
        Assert.Equal(
            ["postgres", "mysql", "mariadb", "redis", "mongodb", "influxdb", "grafana", "prometheus", "minio"],
            ServiceTemplates.All.Select(t => t.Key));
        foreach (var template in ServiceTemplates.All)
        {
            Assert.NotEmpty(template.Versions);
            Assert.Single(template.Versions, v => v.IsDefault);
            Assert.Equal(template.DefaultVersion, template.Versions.Single(v => v.IsDefault).Version);
            Assert.Contains(template.DefaultImage, template.Versions.Select(v => v.Image));
            Assert.NotEmpty(template.Ports);
            Assert.NotEmpty(template.Volumes.Select(v => v.MountPath).Where(m => m.StartsWith('/')).DefaultIfEmpty(""));
            Assert.Contains(template.HealthCheck.Type, new[] { "http", "tcp", "container" });
            Assert.All(template.Ports, p => Assert.InRange(p.ContainerPort, 1, 65535));
            Assert.All(template.Env, e => Assert.True(EnvironmentVariable.IsValidKey(e.Key)));
            Assert.All(template.Env, e => Assert.True(e.Generate == ServiceTemplates.Password ? e.Value is null : e.Value is not null));
        }

        Assert.All(ServiceTemplates.All.Where(t => t.Key != "prometheus"), t => Assert.Contains(t.Env, e => e.Generate == ServiceTemplates.Password));
    }

    [Fact]
    public void ProjectTemplates_CoverTheRequiredCatalogue()
    {
        Assert.Equal(["empty", "web-app", "api", "storage", "static-site"], ProjectTemplates.All.Select(t => t.Key));
        Assert.Empty(ProjectTemplates.Find("empty")!.Workloads);
        var web = ProjectTemplates.Find("web-app")!;
        Assert.Equal(["frontend", "backend", "postgres", "redis"], web.Workloads.Select(w => w.Slug));
        Assert.Equal(["postgres", "minio"], ProjectTemplates.Find("storage")!.Workloads.Select(w => w.ServiceTemplateKey));
        Assert.Equal(ApplicationSourceKind.Static, ProjectTemplates.Find("static-site")!.Workloads.Single().SourceKind);
        Assert.All(ProjectTemplates.All, t => Assert.All(t.Workloads.Where(w => w.Kind == "service"), w => Assert.NotNull(ServiceTemplates.Find(w.ServiceTemplateKey))));
    }
}

/// <summary>Conventions every endpoint of this work package must keep (ADR 0003): name, tag, role and scope.</summary>
public sealed class ResourceEndpointConventionTests : IClassFixture<AetheraApiFactory>
{
    private static readonly string[] ResourcePrefixes =
    [
        "/api/v1/organizations", "/api/v1/projects", "/api/v1/project-templates", "/api/v1/environments", "/api/v1/servers", "/api/v1/applications",
        "/api/v1/services", "/api/v1/service-templates", "/api/v1/secrets", "/api/v1/registries", "/api/v1/volumes", "/api/v1/domains",
    ];

    private readonly IReadOnlyList<RouteEndpoint> _endpoints;

    public ResourceEndpointConventionTests(AetheraApiFactory factory)
    {
        _endpoints = factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => ResourcePrefixes.Any(p => e.RoutePattern.RawText!.StartsWith(p, StringComparison.Ordinal))).ToList();
    }

    [Fact]
    public void TheModuleMapsTheExpectedNumberOfEndpoints() => Assert.Equal(77, _endpoints.Count);

    [Fact]
    public void EveryEndpoint_HasAUniqueCamelCaseName_AndATag()
    {
        Assert.All(_endpoints, e =>
        {
            Assert.False(string.IsNullOrEmpty(e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName), e.RoutePattern.RawText);
            Assert.NotEmpty(e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.ITagsMetadata>()?.Tags ?? []);
        });
        Assert.Equal(_endpoints.Count, _endpoints.Select(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()!.EndpointName).Distinct().Count());
    }

    [Fact]
    public void EveryEndpoint_RequiresARole_AndAScope()
    {
        Assert.All(_endpoints, e =>
        {
            var label = $"{string.Join(',', e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}";
            var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).ToList();
            Assert.True(policies.Intersect([AetheraPolicies.Viewer, AetheraPolicies.Developer, AetheraPolicies.Admin, AetheraPolicies.Owner]).Any(), label + " has no role");
            Assert.NotNull(e.Metadata.GetMetadata<RequiredScopeMetadata>());
            Assert.Empty(e.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        });
    }

    [Fact]
    public void ReadsAreViewerAndReadScope_WritesAreDeveloperOrAdmin()
    {
        foreach (var e in _endpoints)
        {
            var method = e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()!.HttpMethods.Single();
            var scope = e.Metadata.GetMetadata<RequiredScopeMetadata>()!.Scope;
            var roles = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).ToList();
            var label = $"{method} {e.RoutePattern.RawText}";
            if (method == "GET" && !e.RoutePattern.RawText!.StartsWith("/api/v1/secrets"))
            {
                Assert.Contains(AetheraPolicies.Viewer, roles);
                Assert.Equal(Scopes.Read, scope);
            }
            else if (e.RoutePattern.RawText!.StartsWith("/api/v1/secrets"))
            {
                Assert.Contains(scope, new[] { Scopes.SecretsRead, Scopes.SecretsWrite });
            }
            else
            {
                Assert.True(roles.Contains(AetheraPolicies.Developer) || roles.Contains(AetheraPolicies.Admin), label);
            }
        }
    }
}

/// <summary>The application refuses to start without a master key outside Development and Testing.</summary>
public sealed class StartupTests
{
    [Fact]
    public void ProductionWithoutAMasterKey_FailsFast_WithAClearMessage()
    {
        if (Environment.GetEnvironmentVariable(Aethera.Infrastructure.Crypto.MasterKeyring.EnvironmentVariableName) is not null) return;

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("AETHERA_MASTER_KEY", error.ToString());
    }

    [Fact]
    public void ProductionWithAMasterKey_Starts()
    {
        var key = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 3)).ToArray());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production").UseSetting("Aethera:Security:MasterKey", key));
        using var client = factory.CreateClient();
        Assert.NotNull(client);
    }
}
