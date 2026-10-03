using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Auth;

namespace Aethera.Api.Features.Auth;

internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth").AddEndpointFilter<NoStoreFilter>();

        auth.MapGet("/setup", async (AuthService service, CancellationToken ct) => new SetupStatusResponse(await service.IsSetupRequiredAsync(ct)))
            .WithName("getSetupStatus")
            .WithSummary("Is first-run setup still open?")
            .WithDescription("True only while no user exists. The UI uses it to decide between the setup form and the login form.")
            .AllowAnonymous()
            .Produces<SetupStatusResponse>();

        auth.MapPost("/setup", async (SetupRequest body, AuthService service, HttpContext http, CancellationToken ct) =>
            {
                var me = await service.SetupAsync(body, http, ct);
                return Results.Created("/api/v1/auth/me", me);
            })
            .WithName("completeSetup")
            .WithSummary("First-run setup")
            .WithDescription(
                "Creates the default organization, the first user and an Owner membership in one transaction, signs the user in (sets the " +
                "session cookie) and returns the same body as GET /auth/me. Works only while no user exists, however many requests race.")
            .AllowAnonymous()
            .Validate<SetupRequest>()
            .Produces<MeResponse>(StatusCodes.Status201Created)
            .ProducesProblems((StatusCodes.Status409Conflict, "Setup already completed (auth.setup_completed)."));

        auth.MapPost("/login", async (LoginRequest body, AuthService service, HttpContext http, CancellationToken ct) =>
                Results.Ok(await service.LoginAsync(body, http, ct)))
            .WithName("login")
            .WithSummary("Sign in with email and password")
            .WithDescription(
                "Sets the session cookie and returns the same body as GET /auth/me. Unknown email and wrong password are indistinguishable " +
                "(auth.invalid_credentials). After repeated failures the account is locked (auth.locked_out with retryAfter seconds). " +
                "Rate limited per client IP.")
            .AllowAnonymous()
            .AddEndpointFilter<LoginRateLimitFilter>()
            .Validate<LoginRequest>()
            .Produces<MeResponse>()
            .ProducesProblems(
                (StatusCodes.Status401Unauthorized, "Wrong email or password (auth.invalid_credentials)."),
                (StatusCodes.Status423Locked, "Account locked after repeated failures (auth.locked_out); retryAfter and Retry-After give the seconds left."),
                (StatusCodes.Status429TooManyRequests, "Too many attempts from this IP (rate_limited); Retry-After gives the seconds."));

        auth.MapPost("/logout", async (AuthService service, HttpContext http, CancellationToken ct) =>
            {
                await service.LogoutAsync(http, ct);
                return Results.NoContent();
            })
            .WithName("logout")
            .WithSummary("Sign out")
            .WithDescription("Revokes the current browser session and clears the cookie. With an API token there is no session, so nothing happens.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems((StatusCodes.Status403Forbidden, "Missing or wrong X-CSRF-Token on a session request (auth.csrf_invalid)."));

        auth.MapGet("/me", async (AuthService service, ICurrentActor actor, CancellationToken ct) =>
                Results.Ok(await service.MeAsync(actor, ct)))
            .WithName("getMe")
            .WithSummary("The current user, organization and role")
            .WithDescription("Works for browser sessions and API tokens; for a token it describes the token's owner.")
            .Produces<MeResponse>();

        auth.MapGet("/csrf", (HttpContext http, SessionCookies cookies) =>
                Csrf.TokenFor(http, cookies) is { } token && Csrf.IsCookieAuthenticated(http)
                    ? Results.Ok(new CsrfTokenResponse(token))
                    : ApiProblems.Forbidden("CSRF tokens exist only for browser sessions; API tokens are exempt from CSRF checks."))
            .WithName("getCsrfToken")
            .WithSummary("CSRF token for the current session")
            .WithDescription(
                "Send the value in the X-CSRF-Token header of every POST, PUT, PATCH and DELETE made with the session cookie. " +
                "The token is bound to the session and changes at the next login.")
            .Produces<CsrfTokenResponse>()
            .ProducesProblems((StatusCodes.Status403Forbidden, "Called with an API token, which has no session (auth.forbidden)."));

        auth.MapPost("/password", async (ChangePasswordRequest body, AuthService service, HttpContext http, CancellationToken ct) =>
            {
                await service.ChangePasswordAsync(body, http, ct);
                return Results.NoContent();
            })
            .WithName("changePassword")
            .WithSummary("Change your password")
            .WithDescription(
                "Needs a browser session and the current password. All other sessions of the user are revoked. Wrong current passwords count " +
                "towards the account lockout.")
            .Validate<ChangePasswordRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(
                (StatusCodes.Status403Forbidden, "An API token was used (auth.forbidden) or the CSRF token is missing (auth.csrf_invalid)."),
                (StatusCodes.Status422UnprocessableEntity, "Policy violation or wrong current password (validation.failed; errors[] codes too_short, too_long, password.same_as_email, incorrect)."),
                (StatusCodes.Status423Locked, "Account locked after repeated wrong current passwords (auth.locked_out)."));
    }
}
