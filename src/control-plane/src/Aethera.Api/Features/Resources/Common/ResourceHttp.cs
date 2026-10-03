using System.Globalization;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Net.Http.Headers;

namespace Aethera.Api.Features.Resources;

/// <summary>Problem codes raised by the resource endpoints (in addition to the shared ones in <see cref="ProblemCodes"/>).</summary>
public static class ResourceProblemCodes
{
    public const string NotImplemented = "not_implemented";
    public const string ProjectNotEmpty = "project.not_empty";
    public const string EnvironmentNotEmpty = "environment.not_empty";
    public const string ServerInUse = "server.in_use";
    public const string SecretInUse = "secret.in_use";
    public const string SecretManaged = "secret.managed";
    public const string SecretBindingForbidden = "secret.binding_forbidden";
    public const string RegistryInUse = "registry.in_use";
    public const string DomainInvalidHost = "domain.invalid_host";
    public const string Mismatch = "mismatch";
}

/// <summary>Shared endpoint plumbing of the resource modules: tenant, role/scope shortcuts, ETags, query parameter checks.</summary>
public static class ResourceHttp
{
    /// <summary>The caller's organization. Every query of a resource endpoint is filtered by it; other organizations' rows are 404.</summary>
    public static Guid Org(this ICurrentActor actor) =>
        actor.OrganizationId ?? throw new ApiProblemException(ApiProblems.Forbidden("The caller does not belong to an organization."));

    /// <summary>True for an Administrator or the Owner (for an API token: its owning user's role). The trust-model checks use it (ADR 0006).</summary>
    public static bool IsAdmin(this ICurrentActor actor) => actor.Role is { } role && role >= OrganizationRole.Admin;

    /// <summary>Viewer role and the <c>read</c> scope.</summary>
    public static TBuilder RequireRead<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Read);

    /// <summary>Developer role and the <c>write</c> scope.</summary>
    public static TBuilder RequireWrite<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Write);

    /// <summary>Admin role and the given scope (<c>write</c> by default).</summary>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder, string scope = Scopes.Write) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRole(AetheraPolicies.Admin).RequireScope(scope);

    /// <summary>Weak-free strong ETag from the row version (<c>xmin</c>).</summary>
    public static string ETagOf(uint rowVersion) => "\"" + rowVersion.ToString(CultureInfo.InvariantCulture) + "\"";

    public static void SetETag(this HttpContext http, uint rowVersion) =>
        http.Response.Headers.ETag = ETagOf(rowVersion);

    /// <summary>Honours <c>If-Match</c> (optional in v1): <c>412 precondition.failed</c> when none of the listed ETags matches the current row.</summary>
    public static void CheckIfMatch(this HttpContext http, uint currentRowVersion)
    {
        var header = http.Request.Headers.IfMatch;
        if (header.Count == 0) return;
        var current = ETagOf(currentRowVersion);
        foreach (var value in header)
        {
            foreach (var candidate in (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (candidate == "*" || candidate == current) return;
            }
        }

        throw new ApiProblemException(ApiProblems.PreconditionFailed());
    }

    /// <summary>Rejects query parameters the endpoint does not know (400), so typos are caught instead of ignored (ADR 0003 section 2).</summary>
    public static void RejectUnknownQuery(this HttpContext http, params string[] allowed)
    {
        foreach (var name in http.Request.Query.Keys)
        {
            if (name is "limit" or "cursor" or "sort") continue;
            if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ApiProblemException(ApiProblems.InvalidParameter(name, $"Unknown query parameter '{name}'.", "unknown_parameter"));
        }
    }

    /// <summary>Parses a comma-separated enum filter (<c>?status=running,failed</c>); null when absent.</summary>
    public static IReadOnlyList<TEnum>? ParseEnumFilter<TEnum>(string? raw, string parameter) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var values = new List<TEnum>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Enum.TryParse<TEnum>(part, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                var allowed = string.Join(", ", Enum.GetNames<TEnum>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]));
                throw new ApiProblemException(ApiProblems.InvalidParameter(parameter, $"'{part}' is not valid. Allowed: {allowed}.", "invalid_enum"));
            }

            values.Add(value);
        }

        return values;
    }

    /// <summary>Case-insensitive substring pattern for <c>?q=</c> (LIKE metacharacters escaped), or null.</summary>
    public static string? LikePattern(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return null;
        return "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }

    /// <summary>Absolute path of a resource (for <c>Location</c>).</summary>
    public static string Path(string collection, Guid id) => $"/api/v1/{collection}/{id}";
}
