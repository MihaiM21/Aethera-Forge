namespace Aethera.Domain;

/// <summary>
/// A user's TOTP authenticator (RFC 6238, table <c>user_two_factors</c>, one row per user, ADR 0013). The shared secret is stored only encrypted
/// with the master key (envelope encryption, like <see cref="SecretVersion"/>); the associated data is <c>user-2fa:{userId}</c>.
/// A row with <see cref="ConfirmedAt"/> null is an enrolment in progress: it is not used at sign-in until a code from it was confirmed.
/// </summary>
public class UserTwoFactor
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public required byte[] SecretCiphertext { get; set; }
    public required byte[] SecretNonce { get; set; }
    public required byte[] WrappedDataKey { get; set; }
    public required byte[] WrappedDataKeyNonce { get; set; }
    public int MasterKeyVersion { get; set; } = 1;

    /// <summary>When the first code was confirmed; null while the enrolment is pending. Only a confirmed authenticator is "2FA enabled".</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>The 30-second time step of the last accepted code. A code of this step or an earlier one is refused (no replay).</summary>
    public long? LastUsedStep { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsConfirmed => ConfirmedAt is not null;

    /// <summary>The associated data the secret is encrypted with: binds the ciphertext to this user.</summary>
    public static string AssociatedData(Guid userId) => $"user-2fa:{userId}";
}

/// <summary>
/// A one-time recovery code (table <c>user_recovery_codes</c>): lets a user sign in without the authenticator. Only the SHA-256 of the normalised
/// code is stored (the codes carry 80 random bits, so a plain hash is enough). Used codes stay, marked, until the set is regenerated.
/// </summary>
public class UserRecoveryCode : Entity
{
    public const int HashLength = 32;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public required byte[] CodeHash { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
