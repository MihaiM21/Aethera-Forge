using System.Security.Cryptography;

namespace Aethera.Infrastructure.Backups;

/// <summary>What <see cref="Aeb1Encryptor.EncryptAsync"/> produced.</summary>
/// <param name="RawBytes">Plaintext bytes read.</param>
/// <param name="StoredBytes">Bytes written (header, chunks and tags).</param>
/// <param name="StoredSha256">SHA-256 (lower-case hex) of the bytes written.</param>
public sealed record Aeb1Result(long RawBytes, long StoredBytes, string StoredSha256);

/// <summary>Streaming AEB1 encryption (format: <see cref="Aeb1"/>). Holds at most two chunks in memory, whatever the stream's length.</summary>
public static class Aeb1Encryptor
{
    /// <param name="salt">Random when null (always, outside tests).</param>
    public static async Task<Aeb1Result> EncryptAsync(
        Stream plaintext, Stream ciphertext, ReadOnlyMemory<byte> streamKey, string keyId, ReadOnlyMemory<byte>? salt = null, CancellationToken cancellationToken = default)
    {
        if (streamKey.Length != Aeb1.KeyLength) throw new ArgumentException($"The stream key must be {Aeb1.KeyLength} bytes.", nameof(streamKey));
        var saltBytes = salt?.ToArray() ?? RandomNumberGenerator.GetBytes(Aeb1.SaltLength);
        var header = Aeb1.BuildHeader(keyId, saltBytes);
        var key = Aeb1.DeriveChunkKey(streamKey.Span, saltBytes);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long raw = 0, stored = 0;
        try
        {
            using var aes = new AesGcm(key, Aeb1.TagLength);
            await ciphertext.WriteAsync(header, cancellationToken);
            sha.AppendData(header);
            stored += header.Length;

            // One chunk of look-ahead decides which chunk is the last one.
            var current = new byte[Aeb1.ChunkSize];
            var next = new byte[Aeb1.ChunkSize];
            var sealedChunk = new byte[Aeb1.ChunkSize + Aeb1.TagLength];
            var nonce = new byte[Aeb1.NonceLength];
            var currentLength = await FillAsync(plaintext, current, cancellationToken);
            raw += currentLength;
            ulong counter = 0;
            while (true)
            {
                var nextLength = currentLength == Aeb1.ChunkSize ? await FillAsync(plaintext, next, cancellationToken) : 0;
                var final = nextLength == 0;
                raw += nextLength;

                Aeb1.WriteNonce(nonce, counter++, final);
                aes.Encrypt(nonce, current.AsSpan(0, currentLength), sealedChunk.AsSpan(0, currentLength), sealedChunk.AsSpan(currentLength, Aeb1.TagLength), header);
                var size = currentLength + Aeb1.TagLength;
                await ciphertext.WriteAsync(sealedChunk.AsMemory(0, size), cancellationToken);
                sha.AppendData(sealedChunk.AsSpan(0, size));
                stored += size;
                if (final) break;

                (current, next) = (next, current);
                currentLength = nextLength;
            }

            await ciphertext.FlushAsync(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new Aeb1Result(raw, stored, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends.</summary>
    internal static async Task<int> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }

        return total;
    }
}
