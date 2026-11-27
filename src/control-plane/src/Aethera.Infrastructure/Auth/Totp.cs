using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aethera.Infrastructure.Auth;

/// <summary>
/// Time-based one-time passwords (RFC 6238 over RFC 4226 HOTP) with the parameters every authenticator app understands: HMAC-SHA1, 6 digits,
/// 30-second steps. A code is accepted for the current step and one step either side (clock drift), and only for a step later than the last one
/// accepted, so a code cannot be replayed (ADR 0013).
/// </summary>
public static class Totp
{
    public const int SecretLength = 20; // 160 bits, the RFC 4226 recommendation
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    /// <summary>Steps either side of the current one that are still accepted.</summary>
    public const int AllowedDrift = 1;

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    /// <summary>The code for <paramref name="step"/> (zero-padded, 6 digits).</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Strips spaces and dashes (apps show <c>123 456</c>); null when what is left is not exactly 6 digits.</summary>
    public static string? Normalize(string? code)
    {
        if (code is null) return null;
        var digits = new StringBuilder(Digits);
        foreach (var c in code)
        {
            if (c is ' ' or '-') continue;
            if (c is < '0' or > '9' || digits.Length == Digits) return null;
            digits.Append(c);
        }

        return digits.Length == Digits ? digits.ToString() : null;
    }

    /// <summary>
    /// Finds the step <paramref name="code"/> belongs to within the drift window around <paramref name="now"/>, refusing steps at or before
    /// <paramref name="lastUsedStep"/>. Compares in constant time per candidate.
    /// </summary>
    public static bool TryMatch(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long? lastUsedStep, out long matchedStep)
    {
        matchedStep = 0;
        if (Normalize(code) is not { } normalized) return false;

        var current = StepAt(now);
        var found = false;
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            var candidate = Code(secret, step);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(normalized))
                && (lastUsedStep is null || step > lastUsedStep) && !found)
            {
                matchedStep = step;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// The <c>otpauth://</c> URI that authenticator apps read from the QR code (Key Uri Format). The label is <c>issuer:account</c>.
    /// </summary>
    public static string ProvisioningUri(string issuer, string account, ReadOnlySpan<byte> secret)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }
}

/// <summary>RFC 4648 base32 (upper case, no padding), the encoding of TOTP secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    /// <summary>Decodes, ignoring case, spaces and padding. Throws <see cref="FormatException"/> for other characters.</summary>
    public static byte[] Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '=' or '-') continue;
            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0) throw new FormatException("Not a base32 string.");
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. output];
    }
}

/// <summary>
/// One-time recovery codes: 10 per set, 80 random bits each, written as four groups of four base32 characters in lower case
/// (<c>k3xq-7mfa-2pdn-w4hv</c>). Only <see cref="Hash"/> of the normalised code is stored.
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const int Bytes = 10; // 80 bits -> 16 base32 characters

    public static IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(Count);
        for (var i = 0; i < Count; i++)
        {
            var text = Base32.Encode(RandomNumberGenerator.GetBytes(Bytes)).ToLowerInvariant();
            codes.Add($"{text[..4]}-{text[4..8]}-{text[8..12]}-{text[12..16]}");
        }

        return codes;
    }

    /// <summary>Lower case without dashes and spaces; null when it cannot be a recovery code (wrong length or characters).</summary>
    public static string? Normalize(string? code)
    {
        if (code is null) return null;
        var text = new StringBuilder(16);
        foreach (var c in code)
        {
            if (c is ' ' or '-') continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is not ((>= 'a' and <= 'z') or (>= '2' and <= '7')) || text.Length == 16) return null;
            text.Append(lower);
        }

        return text.Length == 16 ? text.ToString() : null;
    }

    /// <summary>SHA-256 of the normalised code (32 bytes).</summary>
    public static byte[] Hash(string normalizedCode) => SHA256.HashData(Encoding.ASCII.GetBytes(normalizedCode));
}
