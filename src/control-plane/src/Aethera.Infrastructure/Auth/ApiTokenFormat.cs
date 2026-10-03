using System.Security.Cryptography;
using System.Text;

namespace Aethera.Infrastructure.Auth;

/// <summary>A freshly generated API token. <see cref="Plaintext"/> is shown to the user once and never stored.</summary>
/// <param name="Plaintext"><c>aeth_</c> + 40 base62 characters.</param>
/// <param name="Prefix">The first 8 characters after <c>aeth_</c>: the public lookup key and what the UI shows to recognise a token.</param>
/// <param name="SecretHash">SHA-256 of the 40-character secret part.</param>
public sealed record GeneratedApiToken(string Plaintext, string Prefix, byte[] SecretHash);

/// <summary>A syntactically valid presented token, split into its lookup key and secret.</summary>
public readonly record struct ParsedApiToken(string Prefix, string Secret);

/// <summary>
/// The API token wire format (ADR 0003): <c>aeth_</c> followed by 40 base62 characters, about 238 random bits (40 x log2 62), drawn
/// from the operating system CSPRNG without modulo bias. The constant marker lets secret scanners find leaked tokens.
/// Only the SHA-256 of the 40-character secret and its 8-character prefix are stored. SHA-256 (not a slow password hash) is right
/// here because the input is already high-entropy, so there is nothing to brute-force.
/// </summary>
public static class ApiTokenFormat
{
    public const string Marker = "aeth_";
    public const int SecretLength = 40;
    public const int PrefixLength = 8;
    public const int TotalLength = 5 + SecretLength;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static GeneratedApiToken Generate()
    {
        var secret = string.Create(SecretLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
        return new GeneratedApiToken(Marker + secret, secret[..PrefixLength], Hash(secret));
    }

    /// <summary>
    /// Checks the shape only (marker, exact length, base62 alphabet) so garbage is rejected without touching the database.
    /// Says nothing about whether the token exists.
    /// </summary>
    public static bool TryParse(string? value, out ParsedApiToken token)
    {
        token = default;
        if (value is null || value.Length != TotalLength || !value.StartsWith(Marker, StringComparison.Ordinal)) return false;

        var secret = value.AsSpan(Marker.Length);
        foreach (var c in secret)
        {
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        }

        token = new ParsedApiToken(new string(secret[..PrefixLength]), new string(secret));
        return true;
    }

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    /// <summary>Constant-time comparison of the presented secret with the stored hash.</summary>
    public static bool Matches(byte[] storedHash, string presentedSecret) =>
        CryptographicOperations.FixedTimeEquals(storedHash, Hash(presentedSecret));
}
