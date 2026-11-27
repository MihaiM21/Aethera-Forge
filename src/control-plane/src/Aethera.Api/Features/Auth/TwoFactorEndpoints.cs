using Aethera.Api.Http;
using Aethera.Api.Security;
using Aethera.Domain;

namespace Aethera.Api.Features.Auth;

/// <summary>Two-factor authentication (ADR 0013): the second sign-in step, the caller's own settings and the organization policy.</summary>
internal static class TwoFactorEndpoints
{
    public static void MapTwoFactorEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth").AddEndpointFilter<NoStoreFilter>();

        auth.MapPost("/login/two-factor", async (TwoFactorLoginRequest body, AuthService service, HttpContext http, CancellationToken ct) =>
                Results.Ok(await service.LoginWithTwoFactorAsync(body, http, ct)))
            .WithName("loginTwoFactor")
            .WithSummary("Finish signing in with a two-factor code")
            .WithDescription(
                "The second step after POST /auth/login answered 401 auth.two_factor_required: send its `challenge` (valid for `expiresIn` seconds) " +
                "with the 6-digit `code` from the authenticator app or one unused `recoveryCode`. Sets the session cookie and returns the same body as " +
                "GET /auth/me. A wrong code counts towards the account lockout like a wrong password; a code is accepted only once. Rate limited per IP.")
            .AllowAnonymous()
            .AddEndpointFilter<LoginRateLimitFilter>()
            .Validate<TwoFactorLoginRequest>()
            .Produces<MeResponse>()
            .ProducesProblems(
                (StatusCodes.Status401Unauthorized,
                    "Wrong, expired or reused code (auth.two_factor_invalid_code), or the challenge is invalid or expired (auth.two_factor_challenge_invalid)."),
                (StatusCodes.Status422UnprocessableEntity, "Neither or both of code and recoveryCode, or a malformed value (validation.failed)."),
                (StatusCodes.Status423Locked, "Account locked after repeated failures (auth.locked_out)."),
                (StatusCodes.Status429TooManyRequests, "Too many attempts from this IP (rate_limited)."));

        var twoFactor = api.MapGroup("/auth/two-factor").WithTags("Auth").AddEndpointFilter<NoStoreFilter>();

        twoFactor.MapGet("", async (TwoFactorService service, ICurrentActor actor, CancellationToken ct) => Results.Ok(await service.StatusAsync(actor, ct)))
            .WithName("getTwoFactorStatus")
            .WithSummary("Your two-factor authentication status")
            .WithDescription("Whether it is on, an enrolment is pending, how many recovery codes are left, and whether the organization requires it.")
            .AllowDuringTwoFactorEnrolment()
            .Produces<TwoFactorStatusResponse>();

        twoFactor.MapPost("/enrolment", async (StartTwoFactorEnrolmentRequest body, TwoFactorService service, HttpContext http, CancellationToken ct) =>
                Results.Ok(await service.StartEnrolmentAsync(body, http, ct)))
            .WithName("startTwoFactorEnrolment")
            .WithSummary("Start enrolling an authenticator app")
            .WithDescription(
                "Needs a browser session and the current password. Returns a new secret and its otpauth:// URI (for the QR code), shown once. Sign-in " +
                "does not change until POST /auth/two-factor/enrolment/confirm accepted a code from it. Starting again replaces a pending enrolment.")
            .AllowDuringTwoFactorEnrolment()
            .AddEndpointFilter<LoginRateLimitFilter>()
            .Validate<StartTwoFactorEnrolmentRequest>()
            .Produces<TwoFactorEnrolmentResponse>()
            .ProducesProblems(
                (StatusCodes.Status403Forbidden, "An API token was used (auth.forbidden) or the CSRF token is missing (auth.csrf_invalid)."),
                (StatusCodes.Status409Conflict, "Two-factor authentication is already on (auth.two_factor_already_enabled)."),
                (StatusCodes.Status422UnprocessableEntity, "Wrong current password (validation.failed; errors[] code incorrect)."),
                (StatusCodes.Status423Locked, "Account locked after repeated wrong passwords (auth.locked_out)."));

        twoFactor.MapPost("/enrolment/confirm", async (ConfirmTwoFactorEnrolmentRequest body, TwoFactorService service, HttpContext http, CancellationToken ct) =>
                Results.Ok(await service.ConfirmEnrolmentAsync(body, http, ct)))
            .WithName("confirmTwoFactorEnrolment")
            .WithSummary("Confirm the authenticator and turn two-factor authentication on")
            .WithDescription(
                "Send a current code from the new authenticator. Turns two-factor authentication on, returns 10 one-time recovery codes (shown once) " +
                "and signs out every other session of the user.")
            .AllowDuringTwoFactorEnrolment()
            .AddEndpointFilter<LoginRateLimitFilter>()
            .Validate<ConfirmTwoFactorEnrolmentRequest>()
            .Produces<RecoveryCodesResponse>()
            .ProducesProblems(
                (StatusCodes.Status403Forbidden, "An API token was used (auth.forbidden) or the CSRF token is missing (auth.csrf_invalid)."),
                (StatusCodes.Status409Conflict, "No enrolment in progress (auth.two_factor_no_enrolment) or already on (auth.two_factor_already_enabled)."),
                (StatusCodes.Status422UnprocessableEntity, "Malformed or wrong code (validation.failed; errors[] codes pattern, incorrect)."));

        twoFactor.MapPost("/disable", async (DisableTwoFactorRequest body, TwoFactorService service, HttpContext http, CancellationToken ct) =>
            {
                await service.DisableAsync(body, http, ct);
                return Results.NoContent();
            })
            .WithName("disableTwoFactor")
            .WithSummary("Turn two-factor authentication off")
            .WithDescription(
                "Needs a browser session, the current password and a code from the authenticator or a recovery code. Deletes the secret and the " +
                "recovery codes. With only a pending enrolment, the password alone cancels it. Wrong passwords and codes count towards the lockout.")
            .AllowDuringTwoFactorEnrolment()
            .AddEndpointFilter<LoginRateLimitFilter>()
            .Validate<DisableTwoFactorRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(
                (StatusCodes.Status403Forbidden, "An API token was used (auth.forbidden) or the CSRF token is missing (auth.csrf_invalid)."),
                (StatusCodes.Status409Conflict, "Two-factor authentication is not on (auth.two_factor_not_enabled)."),
                (StatusCodes.Status422UnprocessableEntity, "Wrong password or code (validation.failed; errors[] code incorrect)."),
                (StatusCodes.Status423Locked, "Account locked after repeated failures (auth.locked_out)."));

        twoFactor.MapPost("/recovery-codes", async (RegenerateRecoveryCodesRequest body, TwoFactorService service, HttpContext http, CancellationToken ct) =>
                Results.Ok(await service.RegenerateRecoveryCodesAsync(body, http, ct)))
            .WithName("regenerateRecoveryCodes")
            .WithSummary("Replace your recovery codes")
            .WithDescription("Needs a browser session, the current password and a code from the authenticator. The old codes stop working.")
            .AddEndpointFilter<LoginRateLimitFilter>()
            .Validate<RegenerateRecoveryCodesRequest>()
            .Produces<RecoveryCodesResponse>()
            .ProducesProblems(
                (StatusCodes.Status403Forbidden, "An API token was used (auth.forbidden) or the CSRF token is missing (auth.csrf_invalid)."),
                (StatusCodes.Status409Conflict, "Two-factor authentication is not on (auth.two_factor_not_enabled)."),
                (StatusCodes.Status422UnprocessableEntity, "Wrong password or code (validation.failed; errors[] code incorrect)."),
                (StatusCodes.Status423Locked, "Account locked after repeated failures (auth.locked_out)."));

        twoFactor.MapPut("/policy", async (TwoFactorPolicyRequest body, TwoFactorService service, ICurrentActor actor, CancellationToken ct) =>
                Results.Ok(await service.SetPolicyAsync(body, actor, ct)))
            .WithName("setTwoFactorPolicy")
            .WithSummary("Require two-factor authentication for the organization")
            .WithDescription(
                "Owner only. With `required: true` every member must sign in to the UI with a second factor; members without one can only enrol " +
                "an authenticator until they have. API tokens keep working. Turning it on needs two-factor authentication on the caller's own account.")
            .RequireRole(AetheraPolicies.Owner)
            .RequireScope(Scopes.Admin)
            .Produces<TwoFactorStatusResponse>()
            .ProducesProblems((StatusCodes.Status409Conflict, "The caller has no two-factor authentication yet (auth.two_factor_not_enabled)."));
    }
}
