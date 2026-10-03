using Aethera.Api.Features.Auth;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;

namespace Aethera.Api.Security;

/// <summary>
/// Cross-site WebSocket / negotiate protection for <c>/hubs/*</c>. The session cookie is <c>SameSite=Lax</c>, but "same site" includes
/// sibling subdomains, and the apps Aethera deploys usually live on sibling subdomains of the panel (<c>app.example.com</c> vs
/// <c>aethera.example.com</c>). A page there could open a WebSocket (or send negotiate / long-poll requests with a simple CORS-less
/// request) to a hub and the browser would attach the administrator's cookie. CSRF tokens do not help: SignalR has no per-request token.
/// <para>
/// So every <c>/hubs/*</c> request that was authenticated by the <b>session</b> must carry an <c>Origin</c> equal to the request's own
/// origin (scheme + host[:port] as seen after forwarded headers) or listed in <c>AETHERA_CORS_ORIGINS</c>; otherwise it is
/// <c>403 auth.origin_not_allowed</c>. Bearer-token requests have no ambient credentials and are exempt, and so are anonymous requests
/// (they are answered 401 by authorization).
/// </para>
/// <para>
/// Browsers send <c>Origin</c> on WebSocket handshakes and on every same-origin POST, but not on same-origin GETs (the SSE and long-polling
/// transports). For those, the browser-controlled, unforgeable <c>Sec-Fetch-Site: same-origin</c> header is accepted instead; a sibling
/// subdomain sends <c>same-site</c> and so is still refused. A cookie request with neither is not from a browser page and is refused too.
/// </para>
/// Must run after authentication (it reads the principal) and before authorization.
/// </summary>
public sealed class HubOriginMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly HashSet<string> _allowed = CorsSetup.ParseOrigins(configuration[CorsSetup.OriginsKey])
        .Select(Normalize)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHubRequest(context) && UsesSession(context) && !IsAllowed(context.Request))
        {
            await ApiProblems.OriginNotAllowed().ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsHubRequest(HttpContext context) => context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);

    private static bool UsesSession(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && string.Equals(context.User.FindFirst(AetheraClaimTypes.AuthMethod)?.Value, AetheraAuthMethods.Session, StringComparison.Ordinal);

    private bool IsAllowed(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
            return string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "same-origin", StringComparison.OrdinalIgnoreCase);

        var normalized = Normalize(origin);
        if (normalized is null) return false; // "null", garbage, non-http(s)
        return _allowed.Contains(normalized) || normalized == Normalize(request.Scheme, request.Host);
    }

    /// <summary>The comparable form <c>scheme://host:port</c> (lower case, explicit port) of an origin, or null when it is not an http(s) origin.</summary>
    internal static string? Normalize(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (uri.UserInfo.Length > 0 || uri.PathAndQuery != "/" || uri.Fragment.Length > 0) return null; // an origin is scheme://host[:port]
        return Normalize(uri.Scheme, uri.Host, uri.Port);
    }

    private static string? Normalize(string scheme, HostString host) =>
        host.HasValue ? Normalize(scheme, host.Host, host.Port ?? (scheme == "https" ? 443 : 80)) : null;

    private static string Normalize(string scheme, string host, int port) =>
        $"{scheme.ToLowerInvariant()}://{host.ToLowerInvariant()}:{port}";
}
