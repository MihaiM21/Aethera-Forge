using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Aethera.Infrastructure.Backups;

/// <summary>
/// The AEB1 container format of encrypted backups (ADR 0010). Go (the agent) and .NET (the control plane, <c>backup-decrypt</c>) implement the
/// same bytes; a shared test vector keeps them in step.
/// <code>
/// header = "AEB1" | key_id length (1 byte) | key_id (ASCII) | salt (32 random bytes)
/// key    = HKDF-SHA256(ikm = stream key, salt = salt, info = "aethera/backup/aeb1", 32 bytes)
/// chunk  = AES-256-GCM(key, nonce = 11-byte big-endian counter | final flag (0x00 / 0x01), plaintext of 65536 bytes, aad = header) | 16-byte tag
/// </code>
/// The last chunk carries the final flag and may be shorter than the others; the plaintext of an empty stream is one empty final chunk. A
/// stream that ends on a chunk boundary does not get an extra empty chunk: its last full chunk is the final one. A truncated object fails to
/// decrypt (its last chunk was not sealed as final) instead of restoring half a database.
/// </summary>
public static class Aeb1
{
    public const int SaltLength = 32;
    public const int KeyLength = 32;
    public const int ChunkSize = 64 * 1024;
    public const int TagLength = 16;
    public const int NonceLength = 12;
    public const int MaxKeyIdLength = 255;

    private static readonly byte[] MagicBytes = "AEB1"u8.ToArray();
    private static readonly byte[] HkdfInfo = "aethera/backup/aeb1"u8.ToArray();

    public static ReadOnlySpan<byte> Magic => MagicBytes;

    /// <summary>The header bytes; they are also the associated data of every chunk.</summary>
    public static byte[] BuildHeader(string keyId, ReadOnlySpan<byte> salt)
    {
        var id = Encoding.ASCII.GetBytes(keyId);
        if (id.Length is 0 or > MaxKeyIdLength || Encoding.ASCII.GetString(id) != keyId)
            throw new ArgumentException($"The key id must be 1-{MaxKeyIdLength} ASCII characters.", nameof(keyId));
        if (salt.Length != SaltLength) throw new ArgumentException($"The salt must be {SaltLength} bytes.", nameof(salt));

        var header = new byte[MagicBytes.Length + 1 + id.Length + SaltLength];
        MagicBytes.CopyTo(header, 0);
        header[MagicBytes.Length] = (byte)id.Length;
        id.CopyTo(header, MagicBytes.Length + 1);
        salt.CopyTo(header.AsSpan(MagicBytes.Length + 1 + id.Length));
        return header;
    }

    public static byte[] DeriveChunkKey(ReadOnlySpan<byte> streamKey, ReadOnlySpan<byte> salt)
    {
        var key = new byte[KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, streamKey, key, salt, HkdfInfo);
        return key;
    }

    /// <summary>11-byte big-endian counter (the value sits in the last 8 of those bytes) followed by the final flag.</summary>
    public static void WriteNonce(Span<byte> nonce, ulong counter, bool final)
    {
        nonce[..NonceLength].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(3, 8), counter);
        nonce[11] = final ? (byte)1 : (byte)0;
    }
}

/// <summary>The parsed start of an AEB1 object.</summary>
public sealed record Aeb1Header(string KeyId, byte[] Salt, byte[] Bytes);

/// <summary>An AEB1 object that cannot be read: wrong format, wrong key, or damaged or truncated data. The message never contains key material.</summary>
public sealed class Aeb1Exception(string message, Exception? inner = null) : Exception(message, inner);
