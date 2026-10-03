using System.Security.Claims;
using Aethera.Domain;

namespace Aethera.Api.Security;

/// <summary>
/// The claim types of an authenticated Aethera principal. WP1.1's authentication handlers issue exactly these claims and
/// <c>HttpCurrentActor</c>, the role/scope authorization handler and the test auth handler read exactly these.
/// </summary>
public static class AetheraClaimTypes
{
    /// <summary>The user's id (UUID). Present for sessions and tokens (the token's owner).</summary>
    public const string UserId = "aethera:user_id";

    /// <summary>The API token's id (UUID). Present only when <see cref="AuthMethod"/> is <see cref="AetheraAuthMethods.Token"/>.</summary>
    public const string TokenId = "aethera:token_id";

    /// <summary>The organization id (UUID) the principal acts in.</summary>
    public const string OrganizationId = "aethera:org_id";

    /// <summary>The role in that organization: <c>viewer</c>, <c>developer</c>, <c>admin</c> or <c>owner</c> (case-insensitive when read).</summary>
    public const string Role = "aethera:role";

    /// <summary>One claim per granted token scope (multi-valued). Never issued for sessions.</summary>
    public const string Scope = "aethera:scope";

    /// <summary><see cref="AetheraAuthMethods.Session"/> or <see cref="AetheraAuthMethods.Token"/>.</summary>
    public const string AuthMethod = "aethera:auth_method";
}

/// <summary>Values of the <see cref="AetheraClaimTypes.AuthMethod"/> claim.</summary>
public static class AetheraAuthMethods
{
    public const string Session = "session";
    public const string Token = "token";
}

/// <summary>Authentication scheme names.</summary>
public static class AetheraAuthSchemes
{
    /// <summary>Policy scheme and default scheme: forwards to <see cref="Session"/> or <see cref="Token"/> by request.</summary>
    public const string Default = "Aethera";

    /// <summary>Cookie session handler (WP1.1).</summary>
    public const string Session = "Aethera.Session";

    /// <summary>Bearer API token handler (WP1.1).</summary>
    public const string Token = "Aethera.Token";

    /// <summary>Used by <c>Aethera.Testing</c> only.</summary>
    public const string Test = "Aethera.Test";
}

/// <summary>Builds and reads principals with the claim types above so issuers and readers cannot drift apart.</summary>
public static class AetheraPrincipal
{
    /// <summary>
    /// Creates a principal. A token principal is created by passing <paramref name="tokenId"/> (and its <paramref name="scopes"/>);
    /// a session principal has neither. <paramref name="role"/> is the owning user's role in the organization.
    /// </summary>
    public static ClaimsPrincipal Create(
        string authenticationType,
        Guid userId,
        Guid organizationId,
        OrganizationRole role,
        Guid? tokenId = null,
        IEnumerable<string>? scopes = null)
    {
        var claims = new List<Claim>
        {
            new(AetheraClaimTypes.UserId, userId.ToString()),
            new(AetheraClaimTypes.OrganizationId, organizationId.ToString()),
            new(AetheraClaimTypes.Role, RoleClaimValue(role)),
            new(AetheraClaimTypes.AuthMethod, tokenId is null ? AetheraAuthMethods.Session : AetheraAuthMethods.Token),
        };
        if (tokenId is { } id)
        {
            claims.Add(new Claim(AetheraClaimTypes.TokenId, id.ToString()));
            claims.AddRange((scopes ?? []).Distinct(StringComparer.Ordinal).Select(s => new Claim(AetheraClaimTypes.Scope, s)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    /// <summary>Canonical claim value of a role: the camelCase enum name (<c>owner</c>).</summary>
    public static string RoleClaimValue(OrganizationRole role) => role.ToString().ToLowerInvariant();

    public static OrganizationRole? ReadRole(ClaimsPrincipal principal) =>
        Enum.TryParse<OrganizationRole>(principal.FindFirstValue(AetheraClaimTypes.Role), ignoreCase: true, out var role)
        && Enum.IsDefined(role)
            ? role
            : null;

    public static Guid? ReadGuid(ClaimsPrincipal principal, string claimType) =>
        Guid.TryParse(principal.FindFirstValue(claimType), out var id) ? id : null;

    public static bool IsTokenAuthenticated(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && string.Equals(principal.FindFirstValue(AetheraClaimTypes.AuthMethod), AetheraAuthMethods.Token, StringComparison.Ordinal);

    public static IReadOnlySet<string> ReadScopes(ClaimsPrincipal principal) =>
        principal.FindAll(AetheraClaimTypes.Scope).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
}
