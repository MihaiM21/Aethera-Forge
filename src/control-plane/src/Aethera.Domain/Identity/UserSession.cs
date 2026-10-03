namespace Aethera.Domain;

/// <summary>Server-side record behind a login cookie so sessions can be listed and revoked.</summary>
public class UserSession : MutableEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 of the random cookie value (the id is not the secret).</summary>
    public required byte[] SecretHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        UpdatedAt = now;
    }
}
