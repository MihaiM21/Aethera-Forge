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

    /// <summary>422 for a single body field, e.g. <c>Field("/password", "too_short", "...")</c>.</summary>
    public static ApiProblem Field(string pointer, string code, string message) =>
        ApiProblems.Validation([FieldError.AtPointer(pointer, code, message)]);

    /// <summary>The <c>401</c> that a handler's failure maps to; <paramref name="code"/> refines <c>auth.unauthenticated</c>.</summary>
    public static ApiProblem Unauthenticated(string code) => ApiProblems.Unauthenticated(code);
}
