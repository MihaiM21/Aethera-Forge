using System.Globalization;
using Aethera.Api.Http.Errors;
using Aethera.Infrastructure;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Aethera.Api.Web;

/// <summary>
/// Hosting of the Next.js static export (ADR 0005). The export root is <c>Aethera:Web:Root</c> (environment
/// <c>Aethera__Web__Root</c>); without it, a <c>wwwroot</c> folder next to the application, and in Development the repository's
/// <c>src/web/out</c> (<c>../../../web/out</c> from the Api project) if it exists. When no root exists nothing is served and one
/// information message says where was looked.
/// </summary>
public static class StaticWebSetup
{
    public const string RootKey = "Aethera:Web:Root";

    /// <summary>Adds the static UI to the pipeline <b>after</b> the endpoints were mapped: it only handles requests no endpoint claimed.</summary>
    public static IApplicationBuilder UseAetheraStaticWeb(this WebApplication app)
    {
        if (AetheraHost.IsOpenApiGeneration) return app; // build-time OpenAPI generation: nothing to serve, no logging

        var site = StaticWebSite.Locate(app.Configuration, app.Environment, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Aethera.Web"));
        return site is null ? app : app.UseMiddleware<StaticWebMiddleware>(site);
    }
}

/// <summary>An export directory with its routing tables, scanned once at start-up.</summary>
public sealed class StaticWebSite
{
    private StaticWebSite(string root, ExportRouter router)
    {
        Root = root;
        Router = router;
    }

    public string Root { get; }

    public ExportRouter Router { get; }

    /// <summary>Finds the export, scans it and logs the outcome (a single information message). Null when there is none.</summary>
    public static StaticWebSite? Locate(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var candidates = Candidates(configuration, environment);
        var root = candidates.FirstOrDefault(Directory.Exists);
        if (root is null)
        {
            logger.LogInformation("No web UI to serve: none of {Candidates} exists. Only the API is served. Set {Key} to the Next.js export (src/web/out).",
                string.Join(", ", candidates), StaticWebSetup.RootKey);
            return null;
        }

        var site = FromDirectory(root);
        logger.LogInformation("Serving the web UI from {Root} ({Files} files, {Templates} dynamic routes)",
            site.Root, site.Router.FileCount, site.Router.Templates.Count);
        return site;
    }

    /// <summary>Builds a site over a directory (also used by tests).</summary>
    public static StaticWebSite FromDirectory(string root)
    {
        var full = Path.GetFullPath(root);
        return new StaticWebSite(full, new ExportRouter(Scan(full)));
    }

    public static IReadOnlyList<string> Candidates(IConfiguration configuration, IHostEnvironment environment)
    {
        if (configuration[StaticWebSetup.RootKey] is { Length: > 0 } configured)
            return [Path.GetFullPath(configured, environment.ContentRootPath)];

        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "wwwroot") };
        if (environment.IsDevelopment())
            candidates.Add(Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "..", "web", "out")));
        return candidates;
    }

    /// <summary>All files below the root as posix paths, without hidden files (dot-prefixed names; <c>.well-known</c> excepted).</summary>
    private static List<string> Scan(string root)
    {
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.') && segment != ".well-known")) continue;
            files.Add(relative);
        }

        return files;
    }
}

/// <summary>
/// Applies the URL to file rules of ADR 0005 to requests that no endpoint claimed (so <c>/api/*</c>, <c>/hubs/*</c>, <c>/health</c>,
/// the OpenAPI document and the docs always win), and serves the file with the cache and security headers of the ADR.
/// </summary>
public sealed class StaticWebMiddleware(RequestDelegate next, StaticWebSite site)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public const string ImmutableCache = "public, max-age=31536000, immutable";

    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypes();

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is not null)
        {
            await next(context);
            return;
        }

        var request = context.Request;
        var resolution = site.Router.Resolve(request.Path.ToUriComponent());
        var read = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

        switch (resolution.Kind)
        {
            case RouteKind.Api:
                await next(context); // unknown API paths get the API's own 404 problem
                return;

            case RouteKind.BadRequest:
                await ApiProblems.Malformed("The request path is not valid.").ExecuteAsync(context);
                return;

            case RouteKind.NotFound when !read:
                await next(context);
                return;

            case var _ when !read:
                // A static path: only GET and HEAD are allowed.
                await new ApiProblem(StatusCodes.Status405MethodNotAllowed, ProblemCodes.MethodNotAllowed,
                    "This path only supports GET and HEAD.").WithHeader(HeaderNames.Allow, "GET, HEAD").ExecuteAsync(context);
                return;

            case RouteKind.Redirect:
                context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = resolution.Location + request.QueryString.Value;
                return;

            case RouteKind.File:
                await SendAsync(context, resolution.File!, StatusCodes.Status200OK);
                return;

            default:
                await NotFoundAsync(context, resolution.LooksLikeAsset);
                return;
        }
    }

    /// <summary>Rule 8: HTML requests for unknown routes get <c>404.html</c>; assets and non-HTML clients a plain 404.</summary>
    private async Task NotFoundAsync(HttpContext context, bool looksLikeAsset)
    {
        var wantsHtml = context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal);
        if (!looksLikeAsset && wantsHtml && File.Exists(PhysicalPath("404.html")))
        {
            await SendAsync(context, "404.html", StatusCodes.Status404NotFound);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync("Not found");
    }

    private async Task SendAsync(HttpContext context, string file, int status)
    {
        var info = new FileInfo(PhysicalPath(file));
        if (!info.Exists)
        {
            await NotFoundAsync(context, looksLikeAsset: true); // removed after start-up
            return;
        }

        var response = context.Response;
        var isHtml = file.EndsWith(".html", StringComparison.Ordinal);
        response.StatusCode = status;
        response.ContentType = ContentTypeOf(file);
        response.Headers.CacheControl = file.StartsWith("_next/static/", StringComparison.Ordinal) ? ImmutableCache : "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        if (isHtml)
        {
            response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
            response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            response.Headers.XFrameOptions = "DENY";
        }

        if (status == StatusCodes.Status200OK)
        {
            var lastModified = new DateTimeOffset(info.LastWriteTimeUtc.Ticks - info.LastWriteTimeUtc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
            var etag = new EntityTagHeaderValue($"\"{lastModified.ToUnixTimeSeconds():x}-{info.Length:x}\"");
            response.Headers.ETag = etag.ToString();
            response.Headers.LastModified = lastModified.ToString("R", CultureInfo.InvariantCulture);
            if (context.Request.Headers.IfNoneMatch.ToString() is { Length: > 0 } ifNoneMatch
                && ifNoneMatch.Split(',', StringSplitOptions.TrimEntries).Any(v => v == "*" || v.Replace("W/", "", StringComparison.Ordinal) == etag.Tag.Value))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                response.ContentType = null;
                return;
            }
        }

        response.ContentLength = info.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await response.SendFileAsync(info.FullName, context.RequestAborted);
    }

    private string PhysicalPath(string file) => Path.Combine(site.Root, file.Replace('/', Path.DirectorySeparatorChar));

    private static string ContentTypeOf(string file) =>
        ContentTypes.TryGetContentType(file, out var type) ? type : "application/octet-stream";

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".html"] = "text/html; charset=utf-8";
        provider.Mappings[".js"] = "text/javascript; charset=utf-8";
        provider.Mappings[".mjs"] = "text/javascript; charset=utf-8";
        provider.Mappings[".css"] = "text/css; charset=utf-8";
        provider.Mappings[".txt"] = "text/plain; charset=utf-8";
        provider.Mappings[".json"] = "application/json; charset=utf-8";
        provider.Mappings[".ico"] = "image/x-icon";
        provider.Mappings[".woff2"] = "font/woff2";
        provider.Mappings[".map"] = "application/json";
        return provider;
    }
}
