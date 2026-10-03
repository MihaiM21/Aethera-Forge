namespace Aethera.Domain;

public class User : SoftDeletableEntity
{
    private User() { }

    public User(string email, string displayName)
    {
        SetEmail(email);
        DisplayName = displayName;
    }

    public string Email { get; private set; } = null!;

    /// <summary>Trimmed + lower-cased email; the unique login key (among non-deleted users).</summary>
    public string NormalizedEmail { get; private set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>Hash produced by the password hasher; null for accounts that cannot use a password (future SSO).</summary>
    public string? PasswordHash { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndAt { get; set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public void SetEmail(string email)
    {
        var trimmed = email.Trim();
        if (trimmed.Length == 0 || !trimmed.Contains('@'))
            throw new DomainRuleException("Email address is invalid.");
        Email = trimmed;
        NormalizedEmail = NormalizeEmail(trimmed);
    }

    public bool IsLockedOut(DateTimeOffset now) => LockoutEndAt is { } end && end > now;

    public void RecordLogin(DateTimeOffset now)
    {
        LastLoginAt = now;
        FailedLoginCount = 0;
        LockoutEndAt = null;
    }

    public void RecordFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= maxAttempts)
            LockoutEndAt = now + lockoutDuration;
    }
}
