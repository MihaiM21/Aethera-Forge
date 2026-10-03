using Aethera.Api.Http.Errors;

namespace Aethera.Api.Http;

/// <summary>
/// The <c>?confirm=</c> guard of destructive actions (delete application/server/volume/secret, prune...). Without the exact
/// resource name or slug the API answers <c>428 confirmation.required</c> and names the expected value in <c>detail</c>.
/// </summary>
public static class Confirmation
{
    public const string QueryParameter = "confirm";

    public static bool IsConfirmed(string? provided, string expected) =>
        provided is not null && string.Equals(provided.Trim(), expected, StringComparison.Ordinal);

    /// <summary>Throws <see cref="ApiProblemException"/> (428) unless <paramref name="provided"/> equals <paramref name="expected"/>.</summary>
    /// <example><c>Confirmation.Require(confirm, app.Slug);</c> with <c>[FromQuery] string? confirm</c> in the handler.</example>
    public static void Require(string? provided, string expected)
    {
        if (!IsConfirmed(provided, expected)) throw new ApiProblemException(ApiProblems.ConfirmationRequired(expected));
    }
}
