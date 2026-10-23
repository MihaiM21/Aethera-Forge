namespace Aethera.Api.Features.Auth;

/// <summary>
/// Reads, writes and clears the session cookie. The form is decided <b>per request</b>, from the scheme the application sees (after the
/// forwarded-headers middleware, so only a trusted proxy can say "https"):
/// <list type="bullet">
/// <item>HTTPS: <c>__Host-aethera_session</c> with <c>HttpOnly; Secure; SameSite=Lax; Path=/</c> and no <c>Domain</c>. The <c>__Host-</c>
/// prefix makes browsers refuse the cookie unless it is Secure, host-only and Path=/, so a sibling subdomain cannot plant or shadow it.</item>
/// <item>Plain HTTP: <c>aethera_session</c> with <c>HttpOnly; SameSite=Lax; Path=/</c> and no <c>Secure</c>. Browsers never store a
/// <c>Secure</c> cookie, and so never a <c>__Host-</c> one, from an <c>http://</c> origin other than localhost. A fresh install is reached
/// on <c>http://&lt;ip&gt;:8080</c> before any certificate exists; with the secure form nobody could register or sign in (the response
/// "succeeds" and the next request is 401). A cookie that crosses plain HTTP can be sniffed, which is the nature of that origin: HTTPS for
/// the UI is the fix, and the login page says so.</item>
/// </list>
/// Reading accepts either name (the request's own form first), so a session started on one origin form keeps working when the other form
/// is used on the same host. CSRF protection does not depend on the cookie form: the token is derived from the session secret.
/// Behind a TLS-terminating proxy the application only sees HTTPS if that proxy is trusted for <c>X-Forwarded-Proto</c>
/// (<c>Aethera:Http:TrustedProxies</c>); otherwise it sees plain HTTP and uses the non-prefixed cookie, which still works over HTTPS.
/// </summary>
public sealed class SessionCookies
{
    public const string SecureName = "__Host-aethera_session";
    public const string InsecureName = "aethera_session";

    /// <summary>The cookie name written for this request.</summary>
    public string NameFor(HttpContext http) => UseSecureCookie(http) ? SecureName : InsecureName;

    /// <summary>True when the request is HTTPS as the application sees it.</summary>
    public bool UseSecureCookie(HttpContext http) => http.Request.IsHttps;

    /// <summary>The raw cookie value sent by the browser (the request's own form first, then the other), or null.</summary>
    public string? Read(HttpContext http)
    {
        var secure = UseSecureCookie(http);
        return ReadNamed(http, secure ? SecureName : InsecureName) ?? ReadNamed(http, secure ? InsecureName : SecureName);
    }

    /// <summary>Sets the session cookie. A persistent ("remember me") cookie expires with the session; otherwise it lives until the browser closes.</summary>
    public void Write(HttpContext http, string value, bool persistent, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(NameFor(http), value, Attributes(http, persistent ? expiresAt : null));

    /// <summary>
    /// Clears the session cookie. On HTTPS a leftover plain cookie (set earlier over plain HTTP on the same host) goes too, since
    /// <see cref="Read"/> would otherwise still accept it after the sign-out.
    /// </summary>
    public void Delete(HttpContext http)
    {
        http.Response.Cookies.Delete(NameFor(http), Attributes(http, null));
        if (UseSecureCookie(http) && http.Request.Cookies.ContainsKey(InsecureName))
            http.Response.Cookies.Delete(InsecureName, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true });
    }

    private static string? ReadNamed(HttpContext http, string name) =>
        http.Request.Cookies.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

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
