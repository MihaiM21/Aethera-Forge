namespace Aethera.Domain;

public enum SecretScope
{
    Organization = 0,
    Project = 1,
    Environment = 2,
    Workload = 3,
}

/// <summary>
/// Metadata of a secret. The encrypted payloads live in <see cref="SecretVersion"/> rows (ADR 0004: deployments pin
/// a secret id + version, old versions are kept while a rollback point references them). There is deliberately no
/// plaintext property anywhere.
/// </summary>
public class Secret : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }
    public string? Description { get; set; }

    // At most one of these is set; none = organization scope (enforced by a check constraint).
    public Guid? ProjectId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public Guid? WorkloadId { get; set; }

    /// <summary>Highest version number; 0 until the first version is added.</summary>
    public int CurrentVersion { get; private set; }

    public DateTimeOffset? RotatedAt { get; private set; }
    public List<SecretVersion> Versions { get; set; } = [];

    public SecretScope Scope =>
        WorkloadId is not null ? SecretScope.Workload
        : EnvironmentId is not null ? SecretScope.Environment
        : ProjectId is not null ? SecretScope.Project
        : SecretScope.Organization;

    /// <summary>Appends an encrypted version and makes it current.</summary>
    public SecretVersion AddVersion(byte[] ciphertext, byte[] nonce, byte[] wrappedDataKey, byte[] wrappedDataKeyNonce,
        int masterKeyVersion, DateTimeOffset now)
    {
        var version = new SecretVersion
        {
            SecretId = Id, Version = ++CurrentVersion, Ciphertext = ciphertext, Nonce = nonce,
            WrappedDataKey = wrappedDataKey, WrappedDataKeyNonce = wrappedDataKeyNonce,
            MasterKeyVersion = masterKeyVersion, CreatedAt = now,
        };
        if (CurrentVersion > 1) RotatedAt = now;
        UpdatedAt = now;
        Versions.Add(version);
        return version;
    }
}

/// <summary>
/// One encrypted value (AES-256-GCM envelope encryption, implemented elsewhere). <see cref="Ciphertext"/> includes the GCM
/// tag; the per-version data key is wrapped with the master key identified by <see cref="MasterKeyVersion"/>.
/// Immutable once written.
/// </summary>
public class SecretVersion
{
    public Guid SecretId { get; set; }
    public Secret Secret { get; set; } = null!;
    public int Version { get; set; }
    public required byte[] Ciphertext { get; set; }
    public required byte[] Nonce { get; set; }
    public required byte[] WrappedDataKey { get; set; }
    public required byte[] WrappedDataKeyNonce { get; set; }
    public int MasterKeyVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>External container registry credentials (Docker Hub, GHCR, GitLab, private).</summary>
public class Registry : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }
    public required string Url { get; set; }
    public string? Username { get; set; }
    public Guid? PasswordSecretId { get; set; }
    public Secret? PasswordSecret { get; set; }
}
