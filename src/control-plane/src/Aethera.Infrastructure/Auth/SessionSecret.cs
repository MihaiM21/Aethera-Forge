using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Aethera.Infrastructure.Auth;

/// <summary>A newly minted session cookie value and the hash that is stored for it.</summary>
public sealed record GeneratedSessionSecret(string CookieValue, byte[] Hash, bool Persistent);

/// <summary>A cookie value that has the right shape.</summary>
public readonly record struct ParsedSessionSecret(bool Persistent, byte[] Hash);

/// <summary>
/// The browser session cookie value: a one-letter lifetime marker (<c>p</c> persistent "remember me", <c>s</c> browser-session) followed
/// by 32 random bytes (256 bits) in base64url, 44 characters in all. The server stores only SHA-256 of the whole value, so the marker
/// is bound to the secret (changing it changes the hash and the lookup fails) and the table holds nothing that logs in.
/// The marker exists because <c>user_sessions</c> has no "remember me" column: it tells the sliding-expiry logic which window to use.
/// </summary>
public static class SessionSecret
{
    public const int SecretBytes = 32;
    public const int CookieValueLength = 1 + 43;

    private const char PersistentMarker = 'p';
    private const char TransientMarker = 's';

    public static GeneratedSessionSecret Generate(bool persistent)
    {
        var value = (persistent ? PersistentMarker : TransientMarker) + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        return new GeneratedSessionSecret(value, Hash(value), persistent);
    }

    /// <summary>Validates shape only (marker, length, base64url alphabet), so malformed cookies never reach the database.</summary>
    public static bool TryParse(string? cookieValue, out ParsedSessionSecret secret)
    {
        secret = default;
        if (cookieValue is null || cookieValue.Length != CookieValueLength) return false;
        if (cookieValue[0] is not (PersistentMarker or TransientMarker)) return false;
        foreach (var c in cookieValue.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) return false;
        }

        secret = new ParsedSessionSecret(cookieValue[0] == PersistentMarker, Hash(cookieValue));
        return true;
    }

    public static byte[] Hash(string cookieValue) => SHA256.HashData(Encoding.ASCII.GetBytes(cookieValue));

    /// <summary>
    /// The CSRF token of a session: <c>HMAC-SHA256(key = stored session hash, message = "aethera:csrf:v1")</c> in base64url. It needs no
    /// storage and no extra server key, is different for every session, and can be recomputed from the session cookie alone. A cross-site
    /// attacker cannot compute it (it needs the cookie secret) and cannot read it (<c>GET /auth/csrf</c> is not readable cross-origin).
    /// </summary>
    public static string CsrfToken(ReadOnlySpan<byte> sessionHash) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(sessionHash, CsrfLabel));

    /// <summary>Constant-time check of a presented CSRF token against the session's.</summary>
    public static bool IsValidCsrfToken(ReadOnlySpan<byte> sessionHash, string? presented)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length > 64) return false;
        var expected = Encoding.ASCII.GetBytes(CsrfToken(sessionHash));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(presented));
    }

    private static readonly byte[] CsrfLabel = "aethera:csrf:v1"u8.ToArray();
}
