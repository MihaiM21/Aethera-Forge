using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace Aethera.Testing;

/// <summary>Checks of the endpoint conventions that every work package must keep (ADR 0003).</summary>
public static partial class ApiConventions
{
    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex CamelCaseName();

    /// <summary>
    /// Every <c>/api/v1</c> endpoint needs a unique camelCase endpoint name (<c>.WithName("listProjects")</c>), which becomes its OpenAPI
    /// <c>operationId</c> and the function name in the generated TypeScript client. Returns one message per violation.
    /// </summary>
    public static IReadOnlyList<string> FindOperationIdProblems(EndpointDataSource endpoints)
    {
        var problems = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? "";
            if (!route.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) continue;

            var methods = string.Join(',', endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["?"]);
            var label = $"{methods} {route}";
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
            if (string.IsNullOrEmpty(name))
                problems.Add($"{label} has no endpoint name; add .WithName(\"verbNoun\").");
            else if (!CamelCaseName().IsMatch(name))
                problems.Add($"{label} is named '{name}'; operation ids are camelCase (listProjects).");
            else if (seen.TryGetValue(name, out var first))
                problems.Add($"{label} reuses the name '{name}' of {first}; names must be unique.");
            else
                seen[name] = label;
        }

        return problems;
    }
}
