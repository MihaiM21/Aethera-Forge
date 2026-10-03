using Aethera.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Aethera.Api.Security;

/// <summary>Minimum-role policies. The order is Viewer &lt; Developer &lt; Admin &lt; Owner; a higher role passes lower policies.</summary>
public static class AetheraPolicies
{
    public const string Viewer = "Viewer";
    public const string Developer = "Developer";
    public const string Admin = "Admin";
    public const string Owner = "Owner";

    public static OrganizationRole MinimumRole(string policy) => policy switch
    {
        Viewer => OrganizationRole.Viewer,
        Developer => OrganizationRole.Developer,
        Admin => OrganizationRole.Admin,
        Owner => OrganizationRole.Owner,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Not an Aethera role policy."),
    };

    internal static void Register(AuthorizationOptions options)
    {
        foreach (var policy in new[] { Viewer, Developer, Admin, Owner })
            options.AddPolicy(policy, p => p.RequireAuthenticatedUser().AddRequirements(new MinimumRoleRequirement(MinimumRole(policy))));
    }
}

/// <summary>The caller's role must be at least <see cref="Minimum"/>.</summary>
public sealed record MinimumRoleRequirement(OrganizationRole Minimum) : IAuthorizationRequirement;

/// <summary>
/// An API token must carry <see cref="Scope"/>. Applies <b>only to token-authenticated principals</b>; browser sessions
/// satisfy it unconditionally (they are bound by role only).
/// </summary>
public sealed record ScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>Endpoint metadata written by <c>RequireScope</c>; read by the OpenAPI transformer (<c>x-required-scope</c>).</summary>
public sealed record RequiredScopeMetadata(string Scope);
