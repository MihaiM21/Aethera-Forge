using System.Security.Claims;
using System.Text.Encodings.Web;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Auth;

/// <summary>Extra claims issued by the session handler on top of <see cref="AetheraClaimTypes"/>.</summary>
public static class AuthClaims
{
    /// <summary>The <c>user_sessions</c> id of the session that authenticated the request (sessions only).</summary>
    public const string SessionId = "aethera:session_id";

    public static Guid? ReadSessionId(ClaimsPrincipal principal) => AetheraPrincipal.ReadGuid(principal, SessionId);
}

/// <summary>
/// Scheme <c>Aethera.Session</c>: authenticates the browser session cookie. The cookie is an opaque 256-bit secret whose SHA-256 hash is
/// looked up in <c>user_sessions</c>; the principal's role comes from <c>organization_members</c> on every request, so a role change or
/// deactivation applies immediately. Any failure is <c>401 auth.unauthenticated</c> as JSON, never a redirect.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionCookies cookies)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookie = cookies.Read(Context);
        if (cookie is null) return AuthenticateResult.NoResult();

        if (!SessionSecret.TryParse(cookie, out var secret)) return AuthenticateResult.Fail("Malformed session cookie.");

        // Resolved here, not injected: requests without a cookie (health checks, anonymous endpoints) must not need the database.
        var sessions = Context.RequestServices.GetRequiredService<SessionStore>();
        var session = await sessions.ResolveAsync(secret, Context.RequestAborted);
        if (!session.IsValid) return AuthenticateResult.Fail($"Session is not valid ({session.Status}).");

        // A persistent cookie carries its own Expires: slide it together with the server-side expiry.
        if (session.Refreshed && session.Persistent) cookies.Write(Context, cookie, persistent: true, session.ExpiresAt);

        var principal = AetheraPrincipal.Create(AetheraAuthSchemes.Session, session.UserId, session.OrganizationId, session.Role);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(AuthClaims.SessionId, session.SessionId.ToString()));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AetheraAuthSchemes.Session));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiProblems.Unauthenticated().ExecuteAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.Forbidden().ExecuteAsync(Context);
}
