using System.Globalization;
using System.Security.Cryptography;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Crypto;

namespace Aethera.Infrastructure.Backups;

/// <summary>The key an AEB1 object is encrypted with and its id (<c>mk&lt;master key version&gt;-&lt;8 hex&gt;</c>).</summary>
public sealed class BackupStreamKey(byte[] key, string keyId, int masterKeyVersion)
{
    public byte[] Key { get; } = key;

    public string KeyId { get; } = keyId;

    public int MasterKeyVersion { get; } = masterKeyVersion;

    /// <summary>What the agent needs to encrypt or decrypt (the key base64, never logged: <see cref="SecretValue"/>).</summary>
    public BackupEncryptionSpec ToSpec() => new(new SecretValue(Convert.ToBase64String(Key)), KeyId);
}

/// <summary>
/// Derives backup stream keys from the master keyring (ADR 0010): <c>HKDF-SHA256(ikm = master key v, info = "aethera/backup/v1")</c>. New backups use the
/// current master key; old ones name their version in the key id, so a rotated keyring still reads them as long as the old version is kept.
/// </summary>
public static class BackupKeys
{
    public const string Info = "aethera/backup/v1";

    private static readonly byte[] InfoBytes = System.Text.Encoding.ASCII.GetBytes(Info);

    public static BackupStreamKey Derive(ReadOnlySpan<byte> masterKey, int masterKeyVersion)
    {
        var key = new byte[Aeb1.KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, key, [], InfoBytes);
        return new BackupStreamKey(key, KeyIdOf(masterKeyVersion, key), masterKeyVersion);
    }

    public static string KeyIdOf(int masterKeyVersion, ReadOnlySpan<byte> streamKey) =>
        $"mk{masterKeyVersion.ToString(CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(SHA256.HashData(streamKey))[..8]}";

    /// <summary>The key for new backups.</summary>
    public static BackupStreamKey Current(MasterKeyring keyring)
    {
        keyring.TryGet(keyring.CurrentVersion, out var master);
        return Derive(master, keyring.CurrentVersion);
    }

    /// <summary>The key a stored object was encrypted with, from the key id in its header (or on its run). False when the master key version is gone or does not match.</summary>
    public static bool TryForKeyId(MasterKeyring keyring, string keyId, out BackupStreamKey? key)
    {
        key = null;
        if (!TryParseVersion(keyId, out var version) || !keyring.TryGet(version, out var master)) return false;
        var derived = Derive(master, version);
        if (derived.KeyId != keyId)
        {
            CryptographicOperations.ZeroMemory(derived.Key);
            return false;
        }

        key = derived;
        return true;
    }

    /// <summary>The master key version of a key id, e.g. 3 for <c>mk3-1a2b3c4d</c>.</summary>
    public static bool TryParseVersion(string? keyId, out int version)
    {
        version = 0;
        if (keyId is null || !keyId.StartsWith("mk", StringComparison.Ordinal)) return false;
        var dash = keyId.IndexOf('-');
        return dash > 2 && int.TryParse(keyId.AsSpan(2, dash - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1;
    }
}
