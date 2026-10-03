using System.Text.Json.Serialization;
using Aethera.Domain;

namespace Aethera.Api.Features.Auth;

// ---- auth ------------------------------------------------------------------------------------------------------------------------

/// <summary>Whether first-run setup is still open. True only while no user exists.</summary>
public sealed record SetupStatusResponse(bool SetupRequired);

/// <summary>First-run setup: creates the organization and its first Owner.</summary>
public sealed record SetupRequest(string Email, string Password, string DisplayName, string OrganizationName);

public sealed record LoginRequest(string Email, string Password, bool RememberMe = false);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>The token for the <c>X-CSRF-Token</c> header of unsafe requests made with the session cookie.</summary>
public sealed record CsrfTokenResponse(string Token);

public sealed record UserSummary(Guid Id, string Email, string DisplayName);

public sealed record OrganizationSummary(Guid Id, string Name, string Slug);

/// <summary>Who the caller is (session or API token): the user, the organization and the user's role in it.</summary>
public sealed record MeResponse(UserSummary User, OrganizationSummary Organization, OrganizationRole Role);

// ---- users -----------------------------------------------------------------------------------------------------------------------

public sealed record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    OrganizationRole Role,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Creates a user in the caller's organization with an initial password.</summary>
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, OrganizationRole Role);

/// <summary>
/// JSON Merge Patch: omitted members are unchanged. <c>isActive: false</c> deactivates (signs the user out everywhere);
/// <c>password</c> sets a new password and signs the user out everywhere.
/// </summary>
public sealed record UpdateUserRequest(string? Email = null, string? DisplayName = null, bool? IsActive = null, string? Password = null);

public sealed record SetUserRoleRequest(OrganizationRole Role);

// ---- api tokens ------------------------------------------------------------------------------------------------------------------

/// <summary>An API token as listed. The secret is never included; it only appears once, in <see cref="CreatedApiTokenResponse"/>.</summary>
public sealed record ApiTokenResponse(
    Guid Id,
    Guid UserId,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    string? LastUsedIp,
    DateTimeOffset? RevokedAt);

/// <summary>The response of <c>POST /api-tokens</c>: the token's fields plus its plaintext <see cref="Token"/>, shown exactly once.</summary>
public sealed record CreatedApiTokenResponse(
    Guid Id,
    Guid UserId,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    string Token);

/// <summary>
/// Creates a token for the caller. Omit <c>expiresAt</c> for the default (90 days); a date is accepted up to one year ahead;
/// an explicit <c>null</c> means "never expires" and is allowed for Admin and Owner only.
/// </summary>
public sealed class CreateApiTokenRequest
{
    private DateTimeOffset? _expiresAt;

    public string Name { get; set; } = "";

    public List<string> Scopes { get; set; } = [];

    /// <summary>Absent = default lifetime; <c>null</c> = never; a timestamp = that moment.</summary>
    public DateTimeOffset? ExpiresAt
    {
        get => _expiresAt;
        set
        {
            _expiresAt = value;
            ExpiresAtSpecified = true; // the JSON deserializer only calls the setter for members that are present, even when null
        }
    }

    /// <summary>True when the request had an <c>expiresAt</c> member (possibly null).</summary>
    [JsonIgnore]
    public bool ExpiresAtSpecified { get; private set; }
}
