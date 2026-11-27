using Aethera.Api.Http.Errors;

namespace Aethera.Api.Features.Auth;

/// <summary>The problem <c>code</c>s raised by the auth, users and api-tokens endpoints (ADR 0003 section 3). Never repurposed.</summary>
public static class AuthProblemCodes
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string LockedOut = "auth.locked_out";
    public const string SetupCompleted = "auth.setup_completed";
    public const string CsrfInvalid = "auth.csrf_invalid";
    public const string TokenExpired = "auth.token_expired";
    public const string TokenRevoked = "auth.token_revoked";
    public const string LastOwner = "users.last_owner";
    public const string UserNotFound = "user.not_found";
    public const string UserAlreadyExists = "user.already_exists";
    public const string ApiTokenNotFound = "api_token.not_found";
    public const string TwoFactorRequired = "auth.two_factor_required";
    public const string TwoFactorInvalidCode = "auth.two_factor_invalid_code";
    public const string TwoFactorChallengeInvalid = "auth.two_factor_challenge_invalid";
    public const string TwoFactorAlreadyEnabled = "auth.two_factor_already_enabled";
    public const string TwoFactorNotEnabled = "auth.two_factor_not_enabled";
    public const string TwoFactorNoEnrolment = "auth.two_factor_no_enrolment";
}

/// <summary>Factories for the auth-specific error responses.</summary>
public static class AuthProblems
{
    /// <summary>401: identical for an unknown email and a wrong password.</summary>
    public static ApiProblem InvalidCredentials() =>
        new ApiProblem(StatusCodes.Status401Unauthorized, AuthProblemCodes.InvalidCredentials,
                "The email address or password is not correct.", "Invalid credentials")
            .WithHeader("WWW-Authenticate", "Bearer");

    /// <summary>423: too many wrong passwords; <c>retryAfter</c> (seconds) says when the next attempt may succeed.</summary>
    public static ApiProblem LockedOut(TimeSpan remaining)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return new ApiProblem(StatusCodes.Status423Locked, AuthProblemCodes.LockedOut,
                "Too many failed sign-in attempts. Try again later.", "Account locked")
            .WithExtension("retryAfter", seconds)
            .WithHeader("Retry-After", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static ApiProblem SetupCompleted() =>
        new(StatusCodes.Status409Conflict, AuthProblemCodes.SetupCompleted,
            "First-run setup already happened; sign in instead.", "Setup already completed");

    public static ApiProblem CsrfInvalid() =>
        new(StatusCodes.Status403Forbidden, AuthProblemCodes.CsrfInvalid,
            "A valid X-CSRF-Token header is required for this request (get one from GET /api/v1/auth/csrf).", "CSRF token invalid");

    public static ApiProblem LastOwner() =>
        new(StatusCodes.Status409Conflict, AuthProblemCodes.LastOwner,
            "The organization must keep at least one active owner.", "Last owner");

    public static ApiProblem UserNotFound(Guid id) =>
        new(StatusCodes.Status404NotFound, AuthProblemCodes.UserNotFound, $"No user with id {id} exists.", "User not found");

    public static ApiProblem UserAlreadyExists() =>
        new(StatusCodes.Status409Conflict, AuthProblemCodes.UserAlreadyExists,
            "A user with this email address already exists.", "User already exists");

    public static ApiProblem ApiTokenNotFound(Guid id) =>
        new(StatusCodes.Status404NotFound, AuthProblemCodes.ApiTokenNotFound, $"No API token with id {id} exists.", "API token not found");

    /// <summary>
    /// 401 after a correct password when the account has two-factor authentication: no session yet. The <c>challenge</c> extension goes back to
    /// <c>POST /auth/login/two-factor</c> with a code within <c>expiresIn</c> seconds.
    /// </summary>
    public static ApiProblem TwoFactorRequired(string challenge, TimeSpan expiresIn) =>
        new ApiProblem(StatusCodes.Status401Unauthorized, AuthProblemCodes.TwoFactorRequired,
                "Enter the code from your authenticator app (or a recovery code) to finish signing in.", "Two-factor code required")
            .WithExtension("challenge", challenge)
            .WithExtension("expiresIn", (int)expiresIn.TotalSeconds);

    /// <summary>401: the authenticator code or recovery code is wrong, expired or was already used.</summary>
    public static ApiProblem TwoFactorInvalidCode() =>
        new(StatusCodes.Status401Unauthorized, AuthProblemCodes.TwoFactorInvalidCode,
            "The code is not correct, has expired or was already used.", "Invalid two-factor code");

    /// <summary>401: the sign-in challenge is malformed, expired, or the password changed since; sign in with the password again.</summary>
    public static ApiProblem TwoFactorChallengeInvalid() =>
        new(StatusCodes.Status401Unauthorized, AuthProblemCodes.TwoFactorChallengeInvalid,
            "The sign-in attempt expired. Enter your email and password again.", "Sign-in expired");

    public static ApiProblem TwoFactorAlreadyEnabled() =>
        new(StatusCodes.Status409Conflict, AuthProblemCodes.TwoFactorAlreadyEnabled,
            "Two-factor authentication is already on. Turn it off first to enrol another authenticator.", "Two-factor already enabled");

    public static ApiProblem TwoFactorNotEnabled(string detail) =>
        new(StatusCodes.Status409Conflict, AuthProblemCodes.TwoFactorNotEnabled, detail, "Two-factor not enabled");

    public static ApiProblem TwoFactorNoEnrolment() =>
        new(StatusCodes.Status409Conflict, AuthProblemCodes.TwoFactorNoEnrolment,
            "No enrolment is in progress. Start one first.", "No two-factor enrolment");

    /// <summary>422 for a single body field, e.g. <c>Field("/password", "too_short", "...")</c>.</summary>
    public static ApiProblem Field(string pointer, string code, string message) =>
        ApiProblems.Validation([FieldError.AtPointer(pointer, code, message)]);

    /// <summary>The <c>401</c> that a handler's failure maps to; <paramref name="code"/> refines <c>auth.unauthenticated</c>.</summary>
    public static ApiProblem Unauthenticated(string code) => ApiProblems.Unauthenticated(code);
}
