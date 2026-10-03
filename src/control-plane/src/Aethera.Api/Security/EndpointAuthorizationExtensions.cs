using Microsoft.AspNetCore.Authorization;

namespace Aethera.Api.Security;

/// <summary>
/// Endpoint conventions for authorization. Every endpoint in the <c>/api/v1</c> group already requires an authenticated caller;
/// add a role and, for token callers, a scope:
/// <code>
/// group.MapPost("/", Create).WithName("createProject").RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Write);
/// </code>
/// Anonymous endpoints must say <c>.AllowAnonymous()</c> explicitly.
/// </summary>
public static class EndpointAuthorizationExtensions
{
    /// <summary>Requires at least the given role (one of the <see cref="AetheraPolicies"/> names).</summary>
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, string policy) where TBuilder : IEndpointConventionBuilder
    {
        _ = AetheraPolicies.MinimumRole(policy); // fail at startup on a typo
        return builder.RequireAuthorization(policy);
    }

    /// <summary>
    /// Requires an API token to carry <paramref name="scope"/> (see <see cref="Scopes"/>). <b>Has no effect on browser sessions</b>,
    /// which are bound by role only. The scope appears in the OpenAPI document as <c>x-required-scope</c>.
    /// </summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(scope)).Build();
        builder.Add(endpoint => endpoint.Metadata.Add(new RequiredScopeMetadata(scope)));
        return builder.RequireAuthorization(policy);
    }
}
