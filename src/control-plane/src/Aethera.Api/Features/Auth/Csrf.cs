using Microsoft.AspNetCore.Authorization;
using Aethera.Api.Security;
using Aethera.Infrastructure.Auth;
using Microsoft.AspNetCore.Http.Metadata;

namespace Aethera.Api.Features.Auth;

/// <summary>
/// CSRF protection for the browser session (ADR 0003 section 7).
/// <para><b>Design.</b> The CSRF token of a session is <c>HMAC-SHA256(stored session hash, "aethera:csrf:v1")</c>
/// (<see cref="SessionSecret.CsrfToken"/>). The browser fetches it from <c>GET /auth/csrf</c> and sends it back in <c>X-CSRF-Token</c>;
/// the server recomputes it from the session cookie of the same request and compares in constant time. So it is a double-submit token,
/// but bound to the session by a MAC instead of being a free-standing cookie: it cannot be forged without the session secret, it is
/// different per session, it dies with the session, and it needs no extra cookie, no server-side state and no key to distribute.
/// </para>
/// <para><b>Why not ASP.NET antiforgery.</b> It is built around a second cookie plus a request token and its data-protection key ring,
/// which has to be persisted and shared between instances, and its default validation is wired to MVC/Razor. For a JSON API whose only
/// ambient credential is already one server-side-checked cookie, deriving the token from that cookie is simpler and has the same
/// properties. Additional layers: <c>SameSite=Lax</c> on the cookie, JSON-only bodies (a cross-site HTML form cannot send
/// <c>application/json</c>) and no CORS by default.</para>
/// <para><b>Scope.</b> Applies to every unsafe method (anything but GET/HEAD/OPTIONS/TRACE) of a request authenticated by the session
/// cookie. Bearer-token requests are exempt (no ambient credentials) and so are anonymous endpoints.</para>
/// </summary>
public static class Csrf
{
    public const string HeaderName = "X-CSRF-Token";

    public static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    /// <summary>True when the request was authenticated by the session cookie handler (and so needs a CSRF token on unsafe methods).</summary>
    public static bool IsCookieAuthenticated(HttpContext http) =>
        http.User.Identity is { IsAuthenticated: true, AuthenticationType: AetheraAuthSchemes.Session };

    /// <summary>The CSRF token of the request's session, or null when the request has no valid session cookie.</summary>
    public static string? TokenFor(HttpContext http, SessionCookies cookies) =>
        SessionSecret.TryParse(cookies.Read(http), out var secret) ? SessionSecret.CsrfToken(secret.Hash) : null;
}

/// <summary>
/// Endpoint filter added to the whole <c>/api/v1</c> group by <c>MapAuth</c> (group conventions apply to endpoints mapped before and after
/// it), so every feature's unsafe endpoints are covered without touching <c>Program.cs</c>. It runs after authentication and
/// authorization, so unauthenticated callers still get 401 and role failures 403 <c>auth.forbidden</c> first.
/// </summary>
public sealed class CsrfEndpointFilter(SessionCookies cookies) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (Csrf.IsSafeMethod(http.Request.Method)
            || !Csrf.IsCookieAuthenticated(http)
            || http.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return next(context);

        if (SessionSecret.TryParse(cookies.Read(http), out var secret)
            && SessionSecret.IsValidCsrfToken(secret.Hash, http.Request.Headers[Csrf.HeaderName].ToString()))
            return next(context);

        return ValueTask.FromResult<object?>(AuthProblems.CsrfInvalid());
    }
}
