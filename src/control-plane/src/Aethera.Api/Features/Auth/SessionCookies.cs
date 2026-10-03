using Aethera.Infrastructure.Auth;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Auth;

/// <summary>
/// Reads, writes and clears the session cookie.
/// <list type="bullet">
/// <item>Normal operation (HTTPS, or any non-development environment): <c>__Host-aethera_session</c> with <c>HttpOnly; Secure;
/// SameSite=Lax; Path=/</c> and no <c>Domain</c>. The <c>__Host-</c> prefix makes browsers refuse the cookie unless it is Secure, host-only
/// and Path=/, so a sibling subdomain cannot plant or shadow it.</item>
/// <item>Plain HTTP in <c>Development</c>/<c>Testing</c> (or with <c>Aethera:Auth:AllowInsecureCookies=true</c>): <c>aethera_session</c>
/// without <c>Secure</c>. A <c>Secure</c> cookie is never stored or returned by most browsers over http://, and <c>__Host-</c> cookies
/// cannot exist without it, so local development and test servers (and a first install before TLS is set up) could not sign in
/// otherwise. HttpOnly, SameSite=Lax and Path=/ still apply.</item>
/// </list>
/// A TLS-terminating proxy in front of a production install is fine: production never downgrades on the request scheme alone.
/// </summary>
public sealed class SessionCookies(IHostEnvironment environment, IOptionsMonitor<AuthOptions> options)
{
    public const string SecureName = "__Host-aethera_session";
    public const string InsecureName = "aethera_session";

    /// <summary>The cookie name in effect for this request.</summary>
    public string NameFor(HttpContext http) => UseSecureCookie(http) ? SecureName : InsecureName;

    public bool UseSecureCookie(HttpContext http) =>
        http.Request.IsHttps
        || !(environment.IsDevelopment() || environment.IsEnvironment("Testing") || options.CurrentValue.AllowInsecureCookies);

    /// <summary>The raw cookie value sent by the browser, or null.</summary>
    public string? Read(HttpContext http) =>
        http.Request.Cookies.TryGetValue(NameFor(http), out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>Sets the session cookie. A persistent ("remember me") cookie expires with the session; otherwise it lives until the browser closes.</summary>
    public void Write(HttpContext http, string value, bool persistent, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(NameFor(http), value, Attributes(http, persistent ? expiresAt : null));

    public void Delete(HttpContext http) => http.Response.Cookies.Delete(NameFor(http), Attributes(http, null));

    private CookieOptions Attributes(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = UseSecureCookie(http),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };
}
