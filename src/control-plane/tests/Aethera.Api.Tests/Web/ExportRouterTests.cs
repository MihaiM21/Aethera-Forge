using Aethera.Api.Web;

namespace Aethera.Api.Tests.Web;

/// <summary>
/// The cases of <c>src/web/scripts/export-routing.test.ts</c> (the reference implementation of ADR 0005), run against the C# port, plus
/// the rules the reference leaves implicit. If one side changes, the other must follow.
/// </summary>
public sealed class ExportRouterTests
{
    // The same fake export as the reference test.
    private static readonly ExportRouter Router = new(
    [
        "index.html", "404.html", "_not-found.html",
        "login.html", "login.txt", "login/__next.login.__PAGE__.txt",
        "dashboard.html",
        "projects.html", "projects/_.html", "projects/_.txt",
        "applications.html", "applications/_.html", "applications/_/deployments.html", "applications/_/deployments/_.html",
        "_next/static/chunks/app.js", "favicon.ico",
    ]);

    private static RouteResolution R(string path) => Router.Resolve(path);

    private static RouteResolution File(string file) => new(RouteKind.File, file);

    [Fact]
    public void Root_IsIndexHtml() => Assert.Equal(File("index.html"), R("/"));

    [Fact]
    public void ExactFiles_WinFirst()
    {
        Assert.Equal(File("_next/static/chunks/app.js"), R("/_next/static/chunks/app.js"));
        Assert.Equal(File("favicon.ico"), R("/favicon.ico"));
        Assert.Equal(File("login.txt"), R("/login.txt"));
        Assert.Equal(File("login/__next.login.__PAGE__.txt"), R("/login/__next.login.__PAGE__.txt"));
    }

    [Fact]
    public void ADirectory_IsNeverAnIndex_NameMapsToNameHtml()
    {
        // login/ is a directory of payload files next to login.html: no directory-index lookup.
        Assert.Equal(File("login.html"), R("/login"));
        Assert.Equal(File("projects.html"), R("/projects"));
    }

    [Fact]
    public void ADynamicSegment_MapsToTheShell()
    {
        Assert.Equal(File("projects/_.html"), R("/projects/demo-project"));
        Assert.Equal(File("applications/_.html"), R("/applications/0192f3"));
        Assert.Equal(File("projects/_.html"), R("/projects/a%20b"));
        Assert.Equal(File("projects/_.html"), R("/projects/0192f3c8-7b1e-7c3a-9f4d-2a6b8e1d4c55"));
    }

    [Fact]
    public void NestedDynamicRoutes_PreferLiteralSegments()
    {
        Assert.Equal(File("applications/_/deployments.html"), R("/applications/abc/deployments"));
        Assert.Equal(File("applications/_/deployments/_.html"), R("/applications/abc/deployments/xyz"));
    }

    [Fact]
    public void AWildcard_NeverMatchesZeroOrExtraSegments()
    {
        Assert.Equal(RouteKind.NotFound, R("/projects/a/b").Kind);
        Assert.Equal(RouteKind.NotFound, R("/applications/abc/nothing").Kind);
    }

    [Fact]
    public void AWildcard_NeverMatchesAnEmptySegment()
    {
        Assert.Equal(RouteKind.NotFound, R("/projects//x").Kind);
        Assert.Equal(RouteKind.NotFound, R("/applications//deployments").Kind);
    }

    [Fact]
    public void TrailingSlashes_RedirectToTheCanonicalPath()
    {
        Assert.Equal(new RouteResolution(RouteKind.Redirect, Location: "/login"), R("/login/"));
        Assert.Equal(new RouteResolution(RouteKind.Redirect, Location: "/projects/abc"), R("/projects/abc/"));
        Assert.Equal(new RouteResolution(RouteKind.Redirect, Location: "/projects/abc"), R("/projects/abc//"));
    }

    [Fact]
    public void ATrailingSlash_OnAnUnknownPath_IsNotFound_NotARedirect()
    {
        Assert.Equal(new RouteResolution(RouteKind.NotFound, LooksLikeAsset: false), R("/nope/"));
    }

    [Fact]
    public void UnknownRoutes_AreNotFound_AndAssetLookingPathsAreFlagged()
    {
        Assert.Equal(new RouteResolution(RouteKind.NotFound, LooksLikeAsset: false), R("/nope"));
        Assert.Equal(new RouteResolution(RouteKind.NotFound, LooksLikeAsset: true), R("/missing.js"));
        Assert.Equal(new RouteResolution(RouteKind.NotFound, LooksLikeAsset: true), R("/some/dir/missing.png"));
    }

    [Theory]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/")]
    [InlineData("/api")]
    [InlineData("/hubs/jobs")]
    [InlineData("/hubs")]
    [InlineData("/API/v1/x")]
    public void ApiAndHubs_AreNeverServedFromFiles(string path) => Assert.Equal(RouteKind.Api, R(path).Kind);

    [Fact]
    public void ReservedPrefixes_AreNotMatchedByLookalikes()
    {
        Assert.NotEqual(RouteKind.Api, R("/apiary").Kind);
        Assert.NotEqual(RouteKind.Api, R("/hubspot").Kind);
    }

    [Theory]
    [InlineData("/projects/../etc/passwd")]
    [InlineData("/%2e%2e/secret")]
    [InlineData("/%2E%2E/secret")]
    [InlineData("/./login")]
    [InlineData("/a/%2e/b")]
    [InlineData("/%E0%A4%A")]          // malformed percent sequence
    [InlineData("/%")]
    [InlineData("/%zz")]
    [InlineData("/%C0%AF")]            // overlong UTF-8
    [InlineData("/a%5Cb")]             // backslash
    [InlineData("/a%5cb")]
    [InlineData("/a\\b")]
    [InlineData("/a%00b")]             // NUL
    [InlineData("login")]              // not absolute
    [InlineData("")]
    public void TraversalAndMalformedInput_AreBadRequests(string path) => Assert.Equal(RouteKind.BadRequest, R(path).Kind);

    [Fact]
    public void Decoding_HappensOnce()
    {
        // %252e%252e decodes once to the literal text "%2e%2e", which is just a name: not traversal.
        Assert.Equal(RouteKind.NotFound, R("/%252e%252e/secret").Kind);
        // %2F decodes to a slash inside the decoded path, like decodeURIComponent: it splits segments afterwards.
        Assert.Equal(File("projects/_.html"), R("/projects%2Fabc"));
        Assert.Equal(RouteKind.BadRequest, R("/projects%2F..%2Fx").Kind);
    }

    [Fact]
    public void Utf8_IsDecoded()
    {
        var router = new ExportRouter(["café.html"]);
        Assert.Equal(new RouteResolution(RouteKind.File, "café.html"), router.Resolve("/caf%C3%A9"));
    }

    [Fact]
    public void TheTemplateTable_ExcludesNonRoutes_AndOrdersByFewestWildcards()
    {
        var router = new ExportRouter(
        [
            "index.html", "404.html", "_not-found.html", "login.html",
            "a/_/_.html", "a/_/b.html", "a/_.html", "_/x.html", "_.html", "a/b/_.html",
        ]);

        // "_.html" is a one-segment template; the root pages never are. Ties are broken by ordinal file name (deterministic).
        Assert.Equal(["_.html", "_/x.html", "a/_.html", "a/_/b.html", "a/b/_.html", "a/_/_.html"], router.Templates);
    }

    [Fact]
    public void FewestWildcardsWins()
    {
        var router = new ExportRouter(["a/_/_.html", "a/_/b.html", "a/x/_.html"]);

        Assert.Equal(File("a/_/b.html"), router.Resolve("/a/q/b"));    // 1 wildcard beats 2
        Assert.Equal(File("a/_/b.html"), router.Resolve("/a/x/b"));    // equal count: the (ordinal) name order decides, deterministically
        Assert.Equal(File("a/x/_.html"), router.Resolve("/a/x/c"));
        Assert.Equal(File("a/_/_.html"), router.Resolve("/a/q/z"));
    }

    [Fact]
    public void AnExactFile_BeatsAPage_BeatsATemplate()
    {
        var router = new ExportRouter(["p/new", "p/new.html", "p/_.html", "p.html"]);

        Assert.Equal(File("p/new"), router.Resolve("/p/new"));
        Assert.Equal(File("p/_.html"), router.Resolve("/p/other"));
        Assert.Equal(File("p/new.html"), new ExportRouter(["p/new.html", "p/_.html"]).Resolve("/p/new"));
    }
}
