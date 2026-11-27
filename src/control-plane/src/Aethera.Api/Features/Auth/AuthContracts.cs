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

/// <summary>
/// Who the caller is (session or API token): the user, the organization and the user's role in it. <c>twoFactorEnabled</c> says whether the user
/// has a confirmed authenticator; <c>twoFactorEnrolmentRequired</c> is true for a browser session that may only enrol one, because the organization
/// requires two-factor authentication (ADR 0013; always false for API tokens).
/// </summary>
public sealed record MeResponse(
    UserSummary User,
    OrganizationSummary Organization,
    OrganizationRole Role,
    bool TwoFactorEnabled = false,
    bool TwoFactorEnrolmentRequired = false);

// ---- users -----------------------------------------------------------------------------------------------------------------------

public sealed record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    OrganizationRole Role,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool TwoFactorEnabled = false);

/// <summary>Creates a user in the caller's organization with an initial password.</summary>
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, OrganizationRole Role);

/// <summary>
/// JSON Merge Patch: omitted members are unchanged. <c>isActive: false</c> deactivates (signs the user out everywhere);
/// <c>password</c> sets a new password and signs the user out everywhere.
/// </summary>
public sealed record UpdateUserRequest(string? Email = null, string? DisplayName = null, bool? IsActive = null, string? Password = null);

public sealed record SetUserRoleRequest(OrganizationRole Role);

// ---- two-factor authentication (ADR 0013) ---------------------------------------------------------------------------------------

/// <summary>The caller's two-factor state.</summary>
/// <param name="Enabled">A confirmed authenticator exists; sign-in asks for a code.</param>
/// <param name="EnabledAt">When it was confirmed.</param>
/// <param name="EnrolmentPending">An enrolment was started and not confirmed yet.</param>
/// <param name="RecoveryCodesRemaining">Unused recovery codes.</param>
/// <param name="RequiredByOrganization">The organization requires two-factor authentication for every member.</param>
public sealed record TwoFactorStatusResponse(
    bool Enabled, DateTimeOffset? EnabledAt, bool EnrolmentPending, int RecoveryCodesRemaining, bool RequiredByOrganization);

/// <summary>Starts (or restarts) enrolling an authenticator; the current password confirms the request.</summary>
public sealed record StartTwoFactorEnrolmentRequest(string Password);

/// <summary>
/// The new authenticator secret, shown once: <c>otpauthUri</c> is what the QR code encodes, <c>secret</c> (base32) is for typing it in.
/// Nothing changes at sign-in until a code from it is confirmed.
/// </summary>
public sealed record TwoFactorEnrolmentResponse(string Secret, string OtpauthUri, string Issuer, string AccountName, int Digits, int Period);

/// <summary>A 6-digit code from the new authenticator.</summary>
public sealed record ConfirmTwoFactorEnrolmentRequest(string Code);

/// <summary>A new set of one-time recovery codes, shown once. Each signs in once when the authenticator is not at hand.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>Turns two-factor authentication off: the current password and a code from the authenticator, or a recovery code.</summary>
public sealed record DisableTwoFactorRequest(string Password, string? Code = null, string? RecoveryCode = null);

/// <summary>Replaces the recovery codes: the current password and a code from the authenticator.</summary>
public sealed record RegenerateRecoveryCodesRequest(string Password, string Code);

/// <summary>
/// The second step of a sign-in: the <c>challenge</c> from the <c>auth.two_factor_required</c> answer of <c>POST /auth/login</c>, and either a
/// 6-digit <c>code</c> or a <c>recoveryCode</c>.
/// </summary>
public sealed record TwoFactorLoginRequest(string Challenge, string? Code = null, string? RecoveryCode = null);

/// <summary>Whether every member of the organization must use two-factor authentication.</summary>
public sealed record TwoFactorPolicyRequest(bool Required);

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
