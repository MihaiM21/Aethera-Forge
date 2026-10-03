using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;

namespace Aethera.Infrastructure.Crypto;

/// <summary>
/// AES-256-GCM envelope encryption (<see cref="ISecretProtector"/>).
/// <list type="number">
/// <item>Every value gets a fresh random 256-bit data key and a random 96-bit nonce; the value is encrypted with the data key and
/// <c>associatedData</c> (<c>secret:{id}:v{version}</c>), so the ciphertext only authenticates for that exact record.</item>
/// <item>The data key is wrapped (encrypted) with the current master key under another random nonce. The additional authenticated data of the
/// wrap binds the master key version and the record: <c>aethera:dek:v{keyVersion}|{associatedData}</c>.</item>
/// <item>Stored: <c>Ciphertext</c> = encrypted bytes || 16-byte tag, <c>Nonce</c>, <c>WrappedDataKey</c> = wrapped key || tag (48 bytes),
/// <c>WrappedDataKeyNonce</c>, and the master key version, so old values stay readable while new ones use a rotated key.</item>
/// </list>
/// Plaintext buffers and data keys are zeroed after use. Exceptions are <see cref="CryptographicException"/> with fixed messages: they never
/// contain key material, plaintext or the associated data.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector, IDisposable
{
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int DataKeyLength = 32;

    private readonly MasterKeyring _keyring;
    private readonly bool _ownsKeyring;

    public AesGcmSecretProtector(MasterKeyring keyring, bool ownsKeyring = false)
    {
        _keyring = keyring;
        _ownsKeyring = ownsKeyring;
    }

    /// <summary>The master key version that <see cref="Protect"/> uses.</summary>
    public int CurrentKeyVersion => _keyring.CurrentVersion;

    public ProtectedValue Protect(ReadOnlySpan<byte> plaintext, string associatedData)
    {
        ArgumentException.ThrowIfNullOrEmpty(associatedData);
        var version = _keyring.CurrentVersion;
        if (!_keyring.TryGet(version, out var masterKey)) throw new CryptographicException("The current master key is not available.");

        var dataKey = RandomNumberGenerator.GetBytes(DataKeyLength);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[plaintext.Length + TagLength];
            using (var aes = new AesGcm(dataKey, TagLength))
                aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), Encoding.UTF8.GetBytes(associatedData));

            var wrapNonce = RandomNumberGenerator.GetBytes(NonceLength);
            var wrapped = new byte[DataKeyLength + TagLength];
            using (var aes = new AesGcm(masterKey, TagLength))
                aes.Encrypt(wrapNonce, dataKey, wrapped.AsSpan(0, DataKeyLength), wrapped.AsSpan(DataKeyLength), WrapAssociatedData(version, associatedData));

            return new ProtectedValue(ciphertext, nonce, version) { WrappedDataKey = wrapped, WrappedDataKeyNonce = wrapNonce };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public byte[] Unprotect(ProtectedValue value, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrEmpty(associatedData);

        if (!_keyring.TryGet(value.KeyVersion, out var masterKey))
            throw new CryptographicException($"Master key version {value.KeyVersion} is not available.");
        if (value.WrappedDataKey is not { Length: DataKeyLength + TagLength } wrapped
            || value.WrappedDataKeyNonce is not { Length: NonceLength } wrapNonce
            || value.Nonce is not { Length: NonceLength } nonce
            || value.Ciphertext is not { Length: >= TagLength } ciphertext)
            throw new CryptographicException(Failure);

        var dataKey = new byte[DataKeyLength];
        try
        {
            try
            {
                using var unwrap = new AesGcm(masterKey, TagLength);
                unwrap.Decrypt(wrapNonce, wrapped.AsSpan(0, DataKeyLength), wrapped.AsSpan(DataKeyLength), dataKey,
                    WrapAssociatedData(value.KeyVersion, associatedData));

                var plaintext = new byte[ciphertext.Length - TagLength];
                try
                {
                    using var aes = new AesGcm(dataKey, TagLength);
                    aes.Decrypt(nonce, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), plaintext,
                        Encoding.UTF8.GetBytes(associatedData));
                    return plaintext;
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    throw;
                }
            }
            catch (CryptographicException)
            {
                throw new CryptographicException(Failure); // replace the framework message: it is generic, ours is stable
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public void Dispose()
    {
        if (_ownsKeyring) _keyring.Dispose();
    }

    private const string Failure = "The value could not be decrypted: the data, the master key version or the associated data do not match.";

    private static byte[] WrapAssociatedData(int keyVersion, string associatedData) =>
        Encoding.UTF8.GetBytes($"aethera:dek:v{keyVersion}|{associatedData}");
}
