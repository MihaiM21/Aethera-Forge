using Aethera.Api.Http.Errors;

namespace Aethera.Api.Features.Auth;

/// <summary>ADR 0003 section 2: unknown query parameters are rejected with <c>400</c>, not ignored, so typos are caught.</summary>
internal static class QueryGuard
{
    public static void RejectUnknown(HttpContext http, params string[] allowed)
    {
        foreach (var name in http.Request.Query.Keys)
        {
            if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ApiProblemException(ApiProblems.InvalidParameter(
                    name, $"Unknown query parameter '{name}'. Allowed: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.", "unknown_parameter"));
        }
    }

    /// <summary>Splits a comma-separated filter (<c>?role=admin,owner</c>) into trimmed non-empty values.</summary>
    public static string[] Split(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
