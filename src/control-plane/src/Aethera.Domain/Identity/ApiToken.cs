namespace Aethera.Domain;

/// <summary>
/// Personal/automation token. The plaintext is shown once as <c>{Prefix}_{secret}</c>; only the public
/// <see cref="Prefix"/> (lookup key, e.g. <c>aeth_ab12cd34</c>) and the SHA-256 of the secret part are stored.
/// </summary>
public class ApiToken : MutableEntity
{
    public const int SecretHashLength = 32; // SHA-256

    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required byte[] SecretHash { get; set; }
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && !IsExpired(now);

    /// <summary>True when the token carries the scope, or the wildcard scope <c>*</c>.</summary>
    public bool HasScope(string scope) => Scopes.Contains("*") || Scopes.Contains(scope);

    public void Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        UpdatedAt = now;
    }

    public void RecordUse(DateTimeOffset now, string? ip)
    {
        LastUsedAt = now;
        LastUsedIp = ip;
    }
}
