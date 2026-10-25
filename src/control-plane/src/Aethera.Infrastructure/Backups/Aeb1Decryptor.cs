using System.Security.Cryptography;
using System.Text;

namespace Aethera.Infrastructure.Backups;

/// <summary>Streaming AEB1 decryption (format: <see cref="Aeb1"/>).</summary>
public static class Aeb1Decryptor
{
    /// <summary>Reads and checks the header of an AEB1 object, leaving <paramref name="input"/> at the first chunk.</summary>
    /// <exception cref="Aeb1Exception">Not an AEB1 object.</exception>
    public static async Task<Aeb1Header> ReadHeaderAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var fixedPart = new byte[5];
        if (await ReadExactAsync(input, fixedPart, cancellationToken) != fixedPart.Length || !fixedPart.AsSpan(0, 4).SequenceEqual(Aeb1.Magic))
            throw new Aeb1Exception("This is not an Aethera backup (AEB1) file.");
        var idLength = fixedPart[4];
        if (idLength == 0) throw new Aeb1Exception("The backup header is damaged.");

        var rest = new byte[idLength + Aeb1.SaltLength];
        if (await ReadExactAsync(input, rest, cancellationToken) != rest.Length) throw new Aeb1Exception("The backup file is truncated: its header is incomplete.");
        var idBytes = rest.AsSpan(0, idLength);
        foreach (var b in idBytes)
            if (b is < 0x20 or > 0x7e) throw new Aeb1Exception("The backup header is damaged.");

        var header = new byte[fixedPart.Length + rest.Length];
        fixedPart.CopyTo(header, 0);
        rest.CopyTo(header, fixedPart.Length);
        return new Aeb1Header(Encoding.ASCII.GetString(idBytes), rest.AsSpan(idLength).ToArray(), header);
    }

    /// <summary>
    /// Decrypts <paramref name="input"/> into <paramref name="output"/>. <paramref name="resolveKey"/> returns the 32-byte stream key of the key
    /// id named in the header (null = unknown key). Plaintext is written chunk by chunk, so a damaged or truncated object fails after the
    /// chunks before the damage were written: delete the output on failure.
    /// </summary>
    /// <returns>Plaintext bytes written.</returns>
    /// <exception cref="Aeb1Exception">Wrong format or key, or damaged or truncated data.</exception>
    public static async Task<long> DecryptAsync(
        Stream input, Stream output, Func<string, byte[]?> resolveKey, CancellationToken cancellationToken = default)
    {
        var header = await ReadHeaderAsync(input, cancellationToken);
        var streamKey = resolveKey(header.KeyId)
            ?? throw new Aeb1Exception($"No key is available for key id '{header.KeyId}'. Backups are encrypted with a key derived from the master key (AETHERA_MASTER_KEY) that was configured when they were made.");
        if (streamKey.Length != Aeb1.KeyLength) throw new Aeb1Exception("The stream key has the wrong length.");

        var key = Aeb1.DeriveChunkKey(streamKey, header.Salt);
        try
        {
            using var aes = new AesGcm(key, Aeb1.TagLength);
            var sealedSize = Aeb1.ChunkSize + Aeb1.TagLength;
            var current = new byte[sealedSize];
            var next = new byte[sealedSize];
            var plain = new byte[Aeb1.ChunkSize];
            var nonce = new byte[Aeb1.NonceLength];
            var currentLength = await Aeb1Encryptor.FillAsync(input, current, cancellationToken);
            long written = 0;
            ulong counter = 0;
            while (true)
            {
                if (currentLength < Aeb1.TagLength) throw new Aeb1Exception("The backup file is truncated or damaged.");
                var nextLength = currentLength == sealedSize ? await Aeb1Encryptor.FillAsync(input, next, cancellationToken) : 0;
                var final = nextLength == 0;

                var plainLength = currentLength - Aeb1.TagLength;
                Aeb1.WriteNonce(nonce, counter++, final);
                try
                {
                    aes.Decrypt(nonce, current.AsSpan(0, plainLength), current.AsSpan(plainLength, Aeb1.TagLength), plain.AsSpan(0, plainLength), header.Bytes);
                }
                catch (CryptographicException ex)
                {
                    throw new Aeb1Exception(
                        "The backup could not be decrypted: the key is wrong, or the file is damaged or truncated.", ex);
                }

                await output.WriteAsync(plain.AsMemory(0, plainLength), cancellationToken);
                written += plainLength;
                if (final) break;

                (current, next) = (next, current);
                currentLength = nextLength;
            }

            await output.FlushAsync(cancellationToken);
            return written;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        await Aeb1Encryptor.FillAsync(stream, buffer, cancellationToken);
}
