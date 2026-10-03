namespace Aethera.Domain;

/// <summary>
/// An encrypted value as stored (<c>secret_versions</c>). Contains no plaintext.
/// </summary>
/// <param name="Ciphertext">Encrypted bytes including the authentication tag.</param>
/// <param name="Nonce">Unique per encryption.</param>
/// <param name="KeyVersion">Version of the master key in use (<c>master_key_version</c>), so keys can be rotated.</param>
public sealed record ProtectedValue(byte[] Ciphertext, byte[] Nonce, int KeyVersion)
{
    /// <summary>Envelope encryption: the per-value data key, wrapped by the master key. Null for schemes without a data key.</summary>
    public byte[]? WrappedDataKey { get; init; }

    /// <summary>Nonce used to wrap <see cref="WrappedDataKey"/>.</summary>
    public byte[]? WrappedDataKeyNonce { get; init; }
}

/// <summary>Authenticated encryption of secret values at rest (AES-256-GCM envelope encryption in the default implementation).</summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> with the current master key.</summary>
    /// <param name="plaintext">The value. The caller owns and should clear the buffer.</param>
    /// <param name="associatedData">
    /// Context bound into the authentication tag (for example <c>secret:&lt;id&gt;:v&lt;version&gt;</c>) so a ciphertext
    /// cannot be moved to another record. The same string must be passed to <see cref="Unprotect"/>. Not secret, not stored here.
    /// </param>
    ProtectedValue Protect(ReadOnlySpan<byte> plaintext, string associatedData);

    /// <summary>
    /// Decrypts a value, using the master key version recorded in <paramref name="value"/>.
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The data, key or <paramref name="associatedData"/> do not match.</exception>
    byte[] Unprotect(ProtectedValue value, string associatedData);
}
