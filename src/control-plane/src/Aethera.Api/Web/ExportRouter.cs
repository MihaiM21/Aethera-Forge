using System.Text;

namespace Aethera.Api.Web;

/// <summary>What a request path maps to (ADR 0005, "URL to file rules").</summary>
public enum RouteKind
{
    /// <summary>Serve <see cref="RouteResolution.File"/> with status 200.</summary>
    File,

    /// <summary>Answer 308 to <see cref="RouteResolution.Location"/> (the path without its trailing slash).</summary>
    Redirect,

    /// <summary>Nothing matches: 404 (<see cref="RouteResolution.LooksLikeAsset"/> tells whether to skip the HTML body).</summary>
    NotFound,

    /// <summary>Malformed or dangerous path: 400.</summary>
    BadRequest,

    /// <summary>Reserved for the API (<c>/api/*</c>, <c>/hubs/*</c>): never served from static files.</summary>
    Api,
}

public readonly record struct RouteResolution(RouteKind Kind, string? File = null, string? Location = null, bool LooksLikeAsset = false);

/// <summary>
/// The URL to file rules of ADR 0005 for the Next.js static export, as a pure function over the list of exported files (relative, with
/// <c>/</c> separators). A port of <c>src/web/scripts/export-routing.mjs</c>, which is the reference implementation: the two must agree
/// (the test suite carries the reference's cases).
/// </summary>
public sealed class ExportRouter
{
    /// <summary>Paths that belong to the API and are never served from static files.</summary>
    private static readonly string[] ReservedPrefixes = ["/api/", "/hubs/"];

    /// <summary>Exported pages that are not routes.</summary>
    private static readonly HashSet<string> NonRouteHtml = new(StringComparer.Ordinal) { "index.html", "404.html", "_not-found.html" };

    /// <summary>Directory segment of dynamic route parameters (<c>generateStaticParams</c> returns the single id <c>_</c>).</summary>
    public const string Placeholder = "_";

    private readonly HashSet<string> _files;
    private readonly Template[] _templates;

    private sealed record Template(string File, string[] Segments, int Wildcards);

    /// <param name="files">Posix paths relative to the export root, e.g. <c>login.html</c>, <c>_next/static/x.js</c>.</param>
    public ExportRouter(IEnumerable<string> files)
    {
        var list = files.ToList();
        _files = new HashSet<string>(list, StringComparer.Ordinal);

        // The template table, built once: dynamic pages (a "_" segment), fewest wildcards first, then by name so the order is deterministic.
        _templates = list
            .Where(f => f.EndsWith(".html", StringComparison.Ordinal) && !NonRouteHtml.Contains(f))
            .Select(f => new Template(f, f[..^".html".Length].Split('/'), 0))
            .Where(t => t.Segments.Contains(Placeholder))
            .Select(t => t with { Wildcards = t.Segments.Count(s => s == Placeholder) })
            .OrderBy(t => t.Wildcards)
            .ThenBy(t => t.File, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The dynamic page files in match order (fewest wildcards first).</summary>
    public IReadOnlyList<string> Templates => _templates.Select(t => t.File).ToArray();

    public int FileCount => _files.Count;

    public bool IsReserved(string rawPath) =>
        ReservedPrefixes.Any(p => rawPath.StartsWith(p, StringComparison.OrdinalIgnoreCase) || string.Equals(rawPath, p[..^1], StringComparison.OrdinalIgnoreCase));

    /// <param name="rawPath">The request path without the query string, still percent-encoded (decoded here, once).</param>
    public RouteResolution Resolve(string rawPath)
    {
        if (!rawPath.StartsWith('/')) return new(RouteKind.BadRequest);
        if (IsReserved(rawPath)) return new(RouteKind.Api);

        if (!TryDecode(rawPath, out var decoded)) return new(RouteKind.BadRequest);
        if (decoded.Contains('\0') || decoded.Contains('\\')) return new(RouteKind.BadRequest);

        var trailingSlash = decoded.Length > 1 && decoded.EndsWith('/');
        var trimmed = trailingSlash ? decoded.TrimEnd('/') : decoded;
        var segments = trimmed.Split('/')[1..];
        if (segments.Any(s => s is ".." or ".")) return new(RouteKind.BadRequest);

        if (trimmed.Length == 0 || trimmed == "/") return new(RouteKind.File, "index.html");

        var rel = string.Join('/', segments);
        string? target;
        if (_files.Contains(rel)) target = rel;                         // assets, _next/*, *.txt payloads, favicon: no directory-index lookup
        else if (_files.Contains(rel + ".html")) target = rel + ".html"; // /login -> login.html
        else target = MatchTemplate(segments);                          // /projects/abc -> projects/_.html

        if (target is not null)
        {
            // Canonical form has no trailing slash (next.config trailingSlash: false).
            return trailingSlash ? new(RouteKind.Redirect, Location: trimmed) : new(RouteKind.File, target);
        }

        return new(RouteKind.NotFound, LooksLikeAsset: segments[^1].Contains('.'));
    }

    private string? MatchTemplate(string[] segments)
    {
        foreach (var template in _templates)
        {
            if (template.Segments.Length != segments.Length) continue;
            var matches = true;
            for (var i = 0; i < segments.Length && matches; i++)
                matches = template.Segments[i] == Placeholder ? segments[i].Length > 0 : template.Segments[i] == segments[i];
            if (matches) return template.File;
        }

        return null;
    }

    /// <summary>
    /// <c>decodeURIComponent</c>: one pass of percent-decoding, UTF-8, strict. A <c>%</c> not followed by two hex digits, or bytes that are not
    /// valid UTF-8, fail (JavaScript throws a URIError; <see cref="Uri.UnescapeDataString"/> would silently keep them).
    /// </summary>
    internal static bool TryDecode(string value, out string decoded)
    {
        decoded = value;
        if (!value.Contains('%')) return true;

        var bytes = new List<byte>(value.Length);
        var plain = new StringBuilder();
        void FlushPlain()
        {
            if (plain.Length == 0) return;
            bytes.AddRange(Encoding.UTF8.GetBytes(plain.ToString()));
            plain.Clear();
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                plain.Append(value[i]);
                continue;
            }

            FlushPlain();
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return false;
            bytes.Add((byte)((Uri.FromHex(value[i + 1]) << 4) | Uri.FromHex(value[i + 2])));
            i += 2;
        }

        FlushPlain();

        try
        {
            decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
