using System.IO.Compression;
using System.Net;
using System.Text;
using Aethera.Api.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aethera.Api.Tests.Web;

/// <summary>The API serving a (fake) Next.js export: ADR 0005 end to end.</summary>
public sealed class StaticWebTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aethera-web-" + Guid.NewGuid().ToString("N"));
    private readonly AetheraApiFactory _base = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public StaticWebTests()
    {
        foreach (var (path, content) in Export)
        {
            var full = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        // Hidden files must never be served.
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET=1");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "x");

        _factory = _base.WithWebHostBuilder(b => b.UseSetting(StaticWebSetup.RootKey, _root));
        _client = Client();
    }

    private static readonly Dictionary<string, string> Export = new()
    {
        ["index.html"] = "<html>index</html>",
        ["404.html"] = "<html>not found page</html>",
        ["_not-found.html"] = "<html>_not-found</html>",
        ["login.html"] = "<html>login</html>",
        ["login.txt"] = "login rsc payload",
        ["login/__next.login.__PAGE__.txt"] = "login page payload",
        ["dashboard.html"] = "<html>dashboard</html>",
        ["projects.html"] = "<html>projects</html>",
        ["projects/_.html"] = "<html>project shell</html>",
        ["applications.html"] = "<html>applications</html>",
        ["applications/_.html"] = "<html>application shell</html>",
        ["applications/_/deployments.html"] = "<html>deployments of an application</html>",
        ["applications/_/deployments/_.html"] = "<html>deployment shell</html>",
        ["_next/static/chunks/app.js"] = "console.log('app');" + new string(' ', 2000),
        ["_next/static/css/app.css"] = "body{}",
        ["favicon.ico"] = "icon",
        // Files that must lose against the API:
        ["api/shadow.html"] = "shadow of the api",
        ["hubs/shadow.html"] = "shadow of a hub",
        ["health.html"] = "shadow of /health",
    };

    private HttpClient Client(WebApplicationFactory<Program>? factory = null) =>
        (factory ?? _factory).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _base.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static HttpRequestMessage Get(string path, string? accept = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    // ------------------------------------------------------------------ routing

    [Fact]
    public async Task Root_ServesIndexHtml()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<html>index</html>", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Theory]
    [InlineData("/login", "<html>login</html>")]
    [InlineData("/dashboard", "<html>dashboard</html>")]
    [InlineData("/projects", "<html>projects</html>")]
    [InlineData("/projects/demo-project", "<html>project shell</html>")]
    [InlineData("/projects/0192f3c8-7b1e-7c3a-9f4d-2a6b8e1d4c55", "<html>project shell</html>")]
    [InlineData("/projects/a%20b", "<html>project shell</html>")]
    [InlineData("/applications/abc", "<html>application shell</html>")]
    [InlineData("/applications/abc/deployments", "<html>deployments of an application</html>")]
    [InlineData("/applications/abc/deployments/xyz", "<html>deployment shell</html>")]
    [InlineData("/login.txt", "login rsc payload")]
    [InlineData("/login/__next.login.__PAGE__.txt", "login page payload")]
    [InlineData("/index.html", "<html>index</html>")]
    public async Task Paths_MapToFiles_WithStatus200(string path, string body)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ADetailRoute_KeepsTheRealUrl_NoRedirect()
    {
        var response = await _client.GetAsync("/projects/demo-project?tab=env");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("/projects/a/b")]
    [InlineData("/applications/abc/nothing")]
    [InlineData("/nope")]
    [InlineData("/login/extra")]
    [InlineData("/projects//x")]
    public async Task UnknownRoutes_ForABrowser_Get404WithThe404Page(string path)
    {
        var response = await _client.SendAsync(Get(path, "text/html,application/xhtml+xml,*/*;q=0.8"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("<html>not found page</html>", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData("/nope", null)]
    [InlineData("/nope", "application/json")]
    [InlineData("/nope", "*/*")]
    [InlineData("/missing.js", "text/html")]      // looks like a file: never the HTML page
    [InlineData("/_next/static/gone.css", "text/html,*/*")]
    public async Task UnknownRoutes_ForNonBrowsersAndAssets_Get404WithoutTheHtmlBody(string path, string? accept)
    {
        var response = await _client.SendAsync(Get(path, accept));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("not found page", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/login/", "/login")]
    [InlineData("/projects/abc/", "/projects/abc")]
    [InlineData("/applications/abc/deployments//", "/applications/abc/deployments")]
    public async Task ATrailingSlash_Redirects308_ToTheCanonicalPath(string path, string location)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task TheRedirect_KeepsTheQueryString()
    {
        var response = await _client.GetAsync("/login/?next=%2Fdashboard&a=1");

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("/login?next=%2Fdashboard&a=1", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ATrailingSlash_OnAnUnknownPath_Is404NotARedirect()
    {
        var response = await _client.SendAsync(Get("/nope/", "text/html"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/a%5Cb")]
    [InlineData("/a%5cb/c")]
    [InlineData("/%5C")]
    public async Task Backslashes_Are400(string path)
    {
        var response = await _client.GetAsync(path);

        response.AssertProblem(await response.ReadJsonAsync(), 400, "request.malformed");
    }

    [Theory]
    [InlineData("/api/shadow")]
    [InlineData("/api/shadow.html")]
    [InlineData("/api/v1/nothing-here")]
    [InlineData("/hubs/shadow")]
    [InlineData("/hubs/shadow.html")]
    [InlineData("/hubs/unknown")]
    [InlineData("/api")]
    [InlineData("/hubs")]
    public async Task ApiAndHubs_AreNeverServedFromStaticFiles(string path)
    {
        var response = await _client.SendAsync(Get(path, "text/html"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("shadow of", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EndpointsWinOverFiles()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/.env")]
    [InlineData("/.git/config")]
    public async Task HiddenFiles_AreNeverServed(string path)
    {
        var response = await _client.SendAsync(Get(path, "text/html"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("SECRET", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("POST", "/login")]
    [InlineData("PUT", "/projects/abc")]
    [InlineData("DELETE", "/")]
    [InlineData("POST", "/_next/static/chunks/app.js")]
    [InlineData("POST", "/login/")]
    public async Task OtherMethods_OnStaticPaths_Get405_WithAllow(string method, string path)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.AssertProblem(await response.ReadJsonAsync(), 405, "request.method_not_allowed");
        Assert.Equal("GET, HEAD", string.Join(", ", response.Content.Headers.Allow.Concat(response.Headers.TryGetValues("Allow", out var v) ? v : [])).Trim(' ', ','));
    }

    [Fact]
    public async Task OtherMethods_OnUnknownPaths_FallThroughToTheApis404()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/nothing-here"));

        response.AssertProblem(await response.ReadJsonAsync(), 404, "route.not_found");
    }

    [Fact]
    public async Task Head_HasTheHeadersOfGet_AndNoBody()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/login"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<html>login</html>".Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    // -------------------------------------------------------------- caching, headers

    [Fact]
    public async Task NextStaticAssets_AreImmutable_HtmlIsNoCache()
    {
        var asset = await _client.GetAsync("/_next/static/chunks/app.js");
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        Assert.Equal("text/javascript; charset=utf-8", asset.Content.Headers.ContentType?.ToString());
        Assert.Equal("nosniff", asset.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(asset.Headers.Contains("Content-Security-Policy")); // only documents carry it

        var css = await _client.GetAsync("/_next/static/css/app.css");
        Assert.Equal("text/css; charset=utf-8", css.Content.Headers.ContentType?.ToString());

        foreach (var path in new[] { "/", "/login", "/projects/abc", "/login.txt", "/favicon.ico" })
        {
            var response = await _client.GetAsync(path);
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        }

        Assert.Equal("text/plain; charset=utf-8", (await _client.GetAsync("/login.txt")).Content.Headers.ContentType?.ToString());
        Assert.Equal("image/x-icon", (await _client.GetAsync("/favicon.ico")).Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/projects/abc")]
    public async Task HtmlResponses_CarryTheSecurityHeaders(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
            + "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'",
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task ConditionalGet_ReturnsNotModified()
    {
        var first = await _client.GetAsync("/login");
        var etag = first.Headers.ETag!.ToString();
        Assert.NotNull(first.Content.Headers.LastModified);

        var second = await _client.SendAsync(Get("/login", null, ("If-None-Match", etag)));
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());

        var other = await _client.SendAsync(Get("/login", null, ("If-None-Match", "\"something-else\"")));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task TextAssets_AreCompressed_WhenTheClientAsks()
    {
        var response = await _client.SendAsync(Get("/_next/static/chunks/app.js", null, ("Accept-Encoding", "gzip")));

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        await using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        Assert.StartsWith("console.log('app');", await reader.ReadToEndAsync());
    }

    // ------------------------------------------------------------ root discovery

    [Fact]
    public async Task WithoutAnExport_NothingIsServed_AndOneInfoMessageSaysSo()
    {
        var logs = new Jobs.CapturingLoggerProvider();
        using var factory = _base.WithWebHostBuilder(b => b
            .UseSetting(StaticWebSetup.RootKey, Path.Combine(_root, "does-not-exist"))
            .ConfigureLogging(l => l.AddProvider(logs)));
        using var client = Client(factory);

        var response = await client.SendAsync(Get("/login", "text/html"));
        await client.GetAsync("/");

        response.AssertProblem(await response.ReadJsonAsync(), 404, "route.not_found");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode); // the API is unaffected
        var messages = logs.Entries.Where(e => e.Category == "Aethera.Web").ToList();
        var single = Assert.Single(messages);
        Assert.Equal(LogLevel.Information, single.Level);
        Assert.Contains("No web UI to serve", single.Message);
    }

    [Fact]
    public void ARootIsServedAndLogged_Once()
    {
        var logs = new Jobs.CapturingLoggerProvider();
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs)));
        using var _ = factory.CreateClient();

        var messages = logs.Entries.Where(e => e.Category == "Aethera.Web").ToList();
        var single = Assert.Single(messages);
        Assert.Equal(LogLevel.Information, single.Level);
        Assert.Contains("Serving the web UI", single.Message);
        Assert.Contains("dynamic routes", single.Message);
    }

    [Fact]
    public void TheTemplateTable_IsBuiltFromTheExportAtStartup()
    {
        var site = StaticWebSite.FromDirectory(_root);

        Assert.Equal(["applications/_.html", "applications/_/deployments.html", "projects/_.html", "applications/_/deployments/_.html"],
            site.Router.Templates);
    }

    [Fact]
    public void DefaultRoot_IsWwwrootNextToTheApp_AndInDevelopmentTheRepositorysExport()
    {
        var production = StaticWebSite.Candidates(new ConfigurationBuilder().Build(), new Env("Production", "/srv/aethera/src/Aethera.Api"));
        Assert.Equal([Path.Combine(AppContext.BaseDirectory, "wwwroot")], production);

        var development = StaticWebSite.Candidates(new ConfigurationBuilder().Build(), new Env("Development", "/repo/src/control-plane/src/Aethera.Api"));
        Assert.Equal([Path.Combine(AppContext.BaseDirectory, "wwwroot"), "/repo/src/web/out"], development);
    }

    [Fact]
    public void TheConfiguredRoot_ReplacesTheDefaults_AndIsRelativeToTheContentRoot()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [StaticWebSetup.RootKey] = "../site" }).Build();

        Assert.Equal(["/srv/aethera/site"], StaticWebSite.Candidates(configuration, new Env("Development", "/srv/aethera/app")));
        Assert.Equal(["/var/www"], StaticWebSite.Candidates(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [StaticWebSetup.RootKey] = "/var/www" }).Build(),
            new Env("Production", "/srv/aethera/app")));
    }

    [Fact]
    public void Locate_PicksTheFirstCandidateThatExists()
    {
        var logs = new Jobs.CapturingLoggerProvider();
        var logger = logs.CreateLogger("Aethera.Web");
        var contentRoot = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(contentRoot);
        var fallback = Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "..", "web", "out"));
        Directory.CreateDirectory(fallback);
        File.WriteAllText(Path.Combine(fallback, "index.html"), "dev");

        var site = StaticWebSite.Locate(new ConfigurationBuilder().Build(), new Env("Development", contentRoot), logger);

        Assert.NotNull(site);
        Assert.Equal(fallback, site.Root);
        Assert.Null(StaticWebSite.Locate(new ConfigurationBuilder().Build(), new Env("Production", contentRoot), logger));
    }

    private sealed class Env(string name, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
