namespace Aethera.Domain;

/// <summary>
/// One-time token an agent presents to enroll (ADR 0002): 256 random bits shown once, only the SHA-256 hash is stored,
/// single use, bound to a pre-created pending server. Default TTL 1 hour, maximum 24 hours.
/// </summary>
public class JoinToken : MutableEntity
{
    public const int HashLength = 32; // SHA-256
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);

    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? CreatedByUserId { get; set; }

    public static JoinToken Issue(Guid serverId, byte[] tokenHash, DateTimeOffset now, TimeSpan? ttl = null, Guid? createdByUserId = null)
    {
        var lifetime = ttl ?? DefaultTtl;
        if (lifetime <= TimeSpan.Zero || lifetime > MaxTtl)
            throw new DomainRuleException($"Join token TTL must be between 1 second and {MaxTtl.TotalHours:0} hours.");
        if (tokenHash.Length != HashLength)
            throw new DomainRuleException("Join token hash must be a SHA-256 digest.");
        return new JoinToken
        {
            ServerId = serverId, TokenHash = tokenHash, ExpiresAt = now + lifetime, CreatedByUserId = createdByUserId,
            CreatedAt = now, UpdatedAt = now,
        };
    }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && RevokedAt is null && ExpiresAt > now;

    /// <summary>In-memory mirror of the atomic <c>UPDATE ... WHERE used_at IS NULL</c> the gateway performs.</summary>
    public void Consume(DateTimeOffset now)
    {
        if (!IsUsable(now)) throw new DomainRuleException("Join token is expired, revoked or already used.");
        UsedAt = now;
        UpdatedAt = now;
    }

    public void Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        UpdatedAt = now;
    }
}

/// <summary>
/// The internal CA (ECDSA P-256 root). The private key is stored encrypted with the master key (same AES-256-GCM
/// envelope as secrets); at most one authority is active.
/// </summary>
public class CertificateAuthority : MutableEntity
{
    public required string Name { get; set; }
    public required string Subject { get; set; }

    /// <summary>PEM of the CA certificate (public).</summary>
    public required string CertificatePem { get; set; }

    /// <summary>SHA-256 of the DER certificate, hex; the pin agents receive as <c>--ca-sha256</c>.</summary>
    public required string FingerprintSha256 { get; set; }

    public string KeyAlgorithm { get; set; } = "ECDSA-P256";
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }

    public required byte[] PrivateKeyCiphertext { get; set; }
    public required byte[] PrivateKeyNonce { get; set; }
    public required byte[] WrappedDataKey { get; set; }
    public required byte[] WrappedDataKeyNonce { get; set; }
    public int MasterKeyVersion { get; set; } = 1;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>
/// Record of every client certificate the CA issued to an agent. Revocation is a lookup of the serial here (no CRL/OCSP):
/// the gateway rejects a handshake whose serial has <see cref="RevokedAt"/> set.
/// </summary>
public class AgentCertificate : MutableEntity
{
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public Guid CertificateAuthorityId { get; set; }
    public CertificateAuthority CertificateAuthority { get; set; } = null!;

    /// <summary>Hex serial number, unique across all certificates.</summary>
    public required string Serial { get; set; }

    public required string FingerprintSha256 { get; set; }

    /// <summary>SAN URI, <c>spiffe://aethera/server/&lt;server_id&gt;</c>.</summary>
    public required string SubjectUri { get; set; }

    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }

    public bool IsValid(DateTimeOffset now) => RevokedAt is null && NotBefore <= now && NotAfter > now;

    public void Revoke(string reason, DateTimeOffset now)
    {
        RevokedAt ??= now;
        RevokedReason ??= reason;
        UpdatedAt = now;
    }
}
