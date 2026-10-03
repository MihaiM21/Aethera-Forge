using System.Reflection;

namespace Aethera.Api.Http.Errors;

/// <summary>
/// The stable, machine-readable <c>code</c> values of error bodies (ADR 0003 section 3). Codes are never repurposed.
/// Resource-specific codes (<c>application.not_found</c>) are built with <see cref="NotFound"/> / <see cref="AlreadyExists"/>
/// or declared next to the endpoint that raises them.
/// </summary>
public static class ProblemCodes
{
    public const string RequestMalformed = "request.malformed";
    public const string ValidationFailed = "validation.failed";
    public const string InvalidParameter = "validation.invalid_parameter";
    public const string InvalidCursor = "pagination.invalid_cursor";
    public const string Unauthenticated = "auth.unauthenticated";
    public const string Forbidden = "auth.forbidden";
    public const string InsufficientScope = "auth.insufficient_scope";
    public const string RouteNotFound = "route.not_found";
    public const string ResourceConflict = "resource.conflict";
    public const string ConcurrencyConflict = "concurrency.conflict";
    public const string PreconditionFailed = "precondition.failed";
    public const string ConfirmationRequired = "confirmation.required";
    public const string DomainRuleViolation = "domain.rule_violation";
    public const string UnsupportedMediaType = "request.unsupported_media_type";
    public const string NotAcceptable = "request.not_acceptable";
    public const string MethodNotAllowed = "request.method_not_allowed";
    public const string RequestTooLarge = "request.too_large";
    public const string RateLimited = "rate_limited";
    public const string ServiceUnavailable = "service.unavailable";
    public const string InternalError = "internal.error";

    /// <summary><c>&lt;resource&gt;.not_found</c></summary>
    public static string NotFound(string resource) => $"{resource}.not_found";

    /// <summary><c>&lt;resource&gt;.already_exists</c></summary>
    public static string AlreadyExists(string resource) => $"{resource}.already_exists";

    /// <summary>The fixed codes above, for the OpenAPI document.</summary>
    public static IReadOnlyList<string> Known { get; } = typeof(ProblemCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .Order(StringComparer.Ordinal)
        .ToList();

    private static readonly Dictionary<string, string> Titles = new()
    {
        [RequestMalformed] = "Malformed request",
        [ValidationFailed] = "Validation failed",
        [InvalidParameter] = "Invalid parameter",
        [InvalidCursor] = "Invalid cursor",
        [Unauthenticated] = "Authentication required",
        [Forbidden] = "Forbidden",
        [InsufficientScope] = "Insufficient token scope",
        [RouteNotFound] = "Not found",
        [ResourceConflict] = "Conflict",
        [ConcurrencyConflict] = "Concurrent modification",
        [PreconditionFailed] = "Precondition failed",
        [ConfirmationRequired] = "Confirmation required",
        [DomainRuleViolation] = "Rule violation",
        [UnsupportedMediaType] = "Unsupported media type",
        [NotAcceptable] = "Not acceptable",
        [MethodNotAllowed] = "Method not allowed",
        [RequestTooLarge] = "Request too large",
        [RateLimited] = "Too many requests",
        [ServiceUnavailable] = "Service unavailable",
        [InternalError] = "Internal error",
    };

    /// <summary>Short, fixed English title of a code. <c>x.not_found</c> becomes "X not found"; unknown codes get a generic title.</summary>
    public static string TitleOf(string code)
    {
        if (Titles.TryGetValue(code, out var title)) return title;
        var dot = code.IndexOf('.');
        if (dot <= 0 || dot == code.Length - 1) return "Error";
        var subject = code[..dot].Replace('_', ' ');
        var reason = code[(dot + 1)..].Replace('_', ' ');
        return char.ToUpperInvariant(subject[0]) + subject[1..] + " " + reason;
    }

    /// <summary>The code used when the framework produced an error (no endpoint-chosen code) for an HTTP status.</summary>
    public static string ForStatus(int status) => status switch
    {
        400 => RequestMalformed,
        401 => Unauthenticated,
        403 => Forbidden,
        404 => RouteNotFound,
        405 => MethodNotAllowed,
        406 => NotAcceptable,
        409 => ResourceConflict,
        412 => PreconditionFailed,
        413 => RequestTooLarge,
        415 => UnsupportedMediaType,
        422 => ValidationFailed,
        428 => ConfirmationRequired,
        429 => RateLimited,
        503 => ServiceUnavailable,
        >= 500 => InternalError,
        _ => RequestMalformed,
    };
}
