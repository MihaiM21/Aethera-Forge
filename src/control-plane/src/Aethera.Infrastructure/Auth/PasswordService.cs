using Aethera.Domain;
using Microsoft.AspNetCore.Identity;

namespace Aethera.Infrastructure.Auth;

/// <summary>Why a password was refused (<c>null</c> from <see cref="PasswordPolicy.Check"/> = acceptable).</summary>
public enum PasswordProblem
{
    TooShort,
    TooLong,
    SameAsEmail,
}

/// <summary>The password rules (ADR 0003 / WP1.1): 12 to 256 characters and not the account's own email address.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    public static PasswordProblem? Check(string? password, string? email)
    {
        password ??= "";
        if (password.Length < MinLength) return PasswordProblem.TooShort;
        if (password.Length > MaxLength) return PasswordProblem.TooLong;
        if (email is not null && string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
            return PasswordProblem.SameAsEmail;
        return null;
    }
}

/// <summary>
/// Password hashing over <see cref="PasswordHasher{TUser}"/> (versioned PBKDF2-HMAC-SHA512 with a per-hash salt, self-describing format,
/// so the work factor can be raised later and old hashes are upgraded on the next login).
/// </summary>
public sealed class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    // Verified against when the account does not exist, so "unknown email" costs as much as "wrong password".
    private readonly Lazy<string> _decoyHash;

    public PasswordService() =>
        _decoyHash = new Lazy<string>(() => _hasher.HashPassword(new User("decoy@aethera.invalid", "decoy"), Guid.NewGuid().ToString("N")));

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    /// <summary>
    /// Verifies <paramref name="password"/> against the user's hash. A user without a password hash (or no user at all) is always
    /// <see cref="PasswordCheck.Failed"/>, after doing the same amount of hashing work as a real check.
    /// </summary>
    public PasswordCheck Verify(User? user, string password)
    {
        if (user?.PasswordHash is not { Length: > 0 } hash)
        {
            _hasher.VerifyHashedPassword(null!, _decoyHash.Value, password);
            return PasswordCheck.Failed;
        }

        return _hasher.VerifyHashedPassword(user, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };
    }
}

public enum PasswordCheck
{
    Failed,
    Success,

    /// <summary>Correct, but the stored hash uses an outdated format or work factor: store a new one.</summary>
    SuccessRehashNeeded,
}
