using Aethera.Api.Security;
using Aethera.Domain;

namespace Aethera.Api.Features.Auth;

/// <summary>
/// The most an API token may be allowed to do, by the role of its owner. Scopes only ever narrow a token below its owner's role
/// (ADR 0003: effective permission = token scopes AND role), so a token is never created with, and never acts with, a scope the owner's
/// role does not carry:
/// <list type="table">
/// <listheader><term>Role</term><description>Scopes a token of that owner may carry</description></listheader>
/// <item><term>Viewer</term><description><c>read</c></description></item>
/// <item><term>Developer</term><description><c>read</c>, <c>write</c>, <c>deploy</c></description></item>
/// <item><term>Admin, Owner</term><description>everything: <c>read</c>, <c>write</c>, <c>deploy</c>, <c>secrets:read</c>, <c>secrets:write</c>,
/// <c>servers:write</c>, <c>admin</c> and the wildcard <c>*</c></description></item>
/// </list>
/// The ceiling is checked when a token is created and again on every request (the role may have been lowered since), where a lowered
/// owner's wildcard is replaced by the role's own scopes and anything above the role is dropped.
/// </summary>
public static class ScopeCeiling
{
    private static readonly string[] ViewerScopes = [Scopes.Read];
    private static readonly string[] DeveloperScopes = [Scopes.Read, Scopes.Write, Scopes.Deploy];
    private static readonly string[] AdminScopes = [.. Scopes.Known, Scopes.All];

    public static IReadOnlyList<string> AllowedFor(OrganizationRole role) => role switch
    {
        OrganizationRole.Viewer => ViewerScopes,
        OrganizationRole.Developer => DeveloperScopes,
        _ => AdminScopes,
    };

    public static bool IsAllowed(OrganizationRole role, string scope) => AllowedFor(role).Contains(scope);

    /// <summary>The scopes a token currently acts with: its own, cut down to what <paramref name="role"/> allows.</summary>
    public static IReadOnlyList<string> Effective(OrganizationRole role, IEnumerable<string> tokenScopes)
    {
        var allowed = AllowedFor(role);
        var scopes = tokenScopes.Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Contains(Scopes.All, StringComparer.Ordinal) && !allowed.Contains(Scopes.All))
        {
            scopes.Remove(Scopes.All);
            scopes.AddRange(allowed);
        }

        return scopes.Where(allowed.Contains).Distinct(StringComparer.Ordinal).ToList();
    }
}
