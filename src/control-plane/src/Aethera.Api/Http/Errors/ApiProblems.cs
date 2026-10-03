namespace Aethera.Api.Http.Errors;

/// <summary>
/// Factory for the common error responses (ADR 0003 section 3). Return the value from an endpoint, or throw it with
/// <c>throw new ApiProblemException(ApiProblems.NotFound(...))</c> / <see cref="Throw"/> from deeper code.
/// </summary>
public static class ApiProblems
{
    /// <summary>404 <c>&lt;resource&gt;.not_found</c>. Also use it when the caller may not know the resource exists.</summary>
    /// <param name="resource">Singular snake-case name: <c>application</c>, <c>env_var</c>.</param>
    /// <param name="id">The id that was looked up, echoed in <c>detail</c>.</param>
    public static ApiProblem NotFound(string resource, object? id = null) =>
        new(StatusCodes.Status404NotFound, ProblemCodes.NotFound(resource),
            id is null ? $"No {Humanize(resource)} was found." : $"No {Humanize(resource)} with id {id} exists.");

    /// <summary>409 with a caller-chosen code: <c>deployment.already_running</c>, <c>domain.already_exists</c>, <c>job.already_finished</c>.</summary>
    public static ApiProblem Conflict(string code, string? detail = null) => new(StatusCodes.Status409Conflict, code, detail);

    /// <summary>409 <c>&lt;resource&gt;.already_exists</c> (unique constraint).</summary>
    public static ApiProblem AlreadyExists(string resource, string? detail = null) =>
        Conflict(ProblemCodes.AlreadyExists(resource), detail ?? $"A {Humanize(resource)} with the same identity already exists.");

    /// <summary>422 <c>validation.failed</c> with the field-level <c>errors</c> array.</summary>
    public static ApiProblem Validation(IEnumerable<FieldError> errors)
    {
        var list = errors.ToList();
        var detail = list.Count == 1 ? "1 field is invalid." : $"{list.Count} fields are invalid.";
        return new ApiProblem(StatusCodes.Status422UnprocessableEntity, ProblemCodes.ValidationFailed, detail)
            .WithExtension("errors", list);
    }

    /// <summary>400 <c>validation.invalid_parameter</c> for an unparsable or out-of-range query parameter, unknown sort/filter field.</summary>
    public static ApiProblem InvalidParameter(string parameter, string message, string code = "invalid") =>
        new ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidParameter, message)
            .WithExtension("errors", new[] { FieldError.ForParameter(parameter, code, message) });

    /// <summary>400 <c>pagination.invalid_cursor</c>.</summary>
    public static ApiProblem InvalidCursor() =>
        new(StatusCodes.Status400BadRequest, ProblemCodes.InvalidCursor,
            "The cursor is not valid for this request. Start again without a cursor.");

    /// <summary>400 <c>request.malformed</c>: invalid JSON, bad parameter syntax.</summary>
    public static ApiProblem Malformed(string? detail = null) =>
        new(StatusCodes.Status400BadRequest, ProblemCodes.RequestMalformed, detail ?? "The request could not be parsed.");

    /// <summary>428 <c>confirmation.required</c>. <paramref name="expected"/> is the exact value to pass (resource name or slug).</summary>
    public static ApiProblem ConfirmationRequired(string expected) =>
        new ApiProblem(StatusCodes.Status428PreconditionRequired, ProblemCodes.ConfirmationRequired,
            $"This action is destructive. Repeat the request with ?confirm={Uri.EscapeDataString(expected)} to proceed.")
            .WithExtension("expectedConfirmation", expected);

    /// <summary>412 <c>precondition.failed</c>: the <c>If-Match</c> ETag does not match.</summary>
    public static ApiProblem PreconditionFailed() =>
        new(StatusCodes.Status412PreconditionFailed, ProblemCodes.PreconditionFailed,
            "The resource changed since you read it. Fetch it again and retry.");

    /// <summary>401 <c>auth.unauthenticated</c>. Never says whether an account or token exists.</summary>
    public static ApiProblem Unauthenticated(string code = ProblemCodes.Unauthenticated) =>
        new ApiProblem(StatusCodes.Status401Unauthorized, code, "Authentication is required or the credentials are not valid.")
            .WithHeader("WWW-Authenticate", "Bearer");

    /// <summary>403 <c>auth.forbidden</c>: authenticated, but the role or ownership does not allow it.</summary>
    public static ApiProblem Forbidden(string? detail = null) =>
        new(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, detail ?? "You do not have permission to perform this action.");

    /// <summary>403 <c>auth.insufficient_scope</c> with the <c>requiredScope</c> extension.</summary>
    public static ApiProblem InsufficientScope(string requiredScope) =>
        new ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.InsufficientScope,
            $"This API token lacks the '{requiredScope}' scope.")
            .WithExtension("requiredScope", requiredScope);

    /// <summary>429 <c>rate_limited</c> with <c>Retry-After</c> (seconds).</summary>
    public static ApiProblem RateLimited(int retryAfterSeconds) =>
        new ApiProblem(StatusCodes.Status429TooManyRequests, ProblemCodes.RateLimited, "Too many requests. Try again later.")
            .WithHeader("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>500 <c>internal.error</c>. The body carries the trace id only; details stay in the logs.</summary>
    public static ApiProblem Internal() =>
        new(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError, "An unexpected error occurred.");

    private static string Humanize(string resource) => resource.Replace('_', ' ');
}
