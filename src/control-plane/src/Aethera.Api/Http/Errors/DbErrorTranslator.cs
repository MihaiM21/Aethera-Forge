using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aethera.Api.Http.Errors;

/// <summary>
/// The one place where database failures become API problems: stale rows (<c>xmin</c> concurrency token) and unique-constraint
/// violations. Endpoints should still pre-check for friendly resource-specific codes (<c>domain.already_exists</c>); this is the
/// safety net for races.
/// </summary>
public static class DbErrorTranslator
{
    public const string UniqueViolation = PostgresErrorCodes.UniqueViolation; // 23505

    /// <summary>Returns the problem for a known database failure, or null when <paramref name="exception"/> is not one.</summary>
    public static ApiProblem? Translate(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => new ApiProblem(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict,
            "The resource was modified by someone else. Fetch it again and retry."),
        DbUpdateException { InnerException: PostgresException { SqlState: UniqueViolation } } => new ApiProblem(
            StatusCodes.Status409Conflict, ProblemCodes.ResourceConflict, "A resource with the same unique value already exists."),
        _ => null,
    };
}
