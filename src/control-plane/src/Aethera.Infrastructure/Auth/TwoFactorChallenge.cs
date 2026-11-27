using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aethera.Infrastructure.Crypto;

namespace Aethera.Infrastructure.Auth;

/// <summary>What a valid challenge says: who passed the password step, and how the session should be made.</summary>
public sealed record TwoFactorChallengeTicket(Guid UserId, bool RememberMe, DateTimeOffset ExpiresAt);

/// <summary>
/// The token that links the two steps of a sign-in with two-factor authentication (ADR 0013). <c>POST /auth/login</c> returns it after the
/// password was right; <c>POST /auth/login/two-factor</c> takes it back with the code. It is stateless and authenticated:
/// <c>base64url(payload) "." base64url(HMAC-SHA256(key, "v1." payload))</c> where the payload holds the user id, the expiry, "remember me",
/// the master key version and a stamp of the password hash, and the key is <c>HKDF-SHA256(master key, info "aethera/2fa-challenge/v1")</c>.
/// <list type="bullet">
/// <item>It expires after <see cref="Lifetime"/>.</item>
/// <item>A password change or reset invalidates it (the stamp no longer matches).</item>
/// <item>It is not a credential on its own: the code (rate limited, counted towards the account lockout, never accepted twice) is still needed.</item>
/// </list>
/// </summary>
public sealed class TwoFactorChallenge(MasterKeyring keyring)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const string Info = "aethera/2fa-challenge/v1";
    private const string Version = "v1.";
    private const int PayloadLength = 16 + 8 + 1 + 4 + 8;

    public string Issue(Guid userId, string? passwordHash, bool rememberMe, DateTimeOffset now)
    {
        var version = keyring.CurrentVersion;
        var payload = new byte[PayloadLength];
        userId.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), (now + Lifetime).ToUnixTimeSeconds());
        payload[24] = rememberMe ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(25, 4), version);
        PasswordStamp(passwordHash).CopyTo(payload.AsSpan(29, 8));
        return Base64Url(payload) + "." + Base64Url(Mac(version, payload));
    }

    /// <summary>
    /// Checks the token's MAC and expiry. The caller must still compare the password stamp with <see cref="MatchesPassword"/> once it has
    /// loaded the user. Null for anything malformed, forged, expired or made with a master key version that is gone.
    /// </summary>
    public TwoFactorChallengeTicket? Read(string? token, DateTimeOffset now, out byte[] passwordStamp)
    {
        passwordStamp = [];
        if (token is not { Length: > 0 and < 512 }) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return null;

        byte[] payload, mac;
        try
        {
            payload = FromBase64Url(token[..dot]);
            mac = FromBase64Url(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (payload.Length != PayloadLength) return null;
        var version = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(25, 4));
        if (!keyring.TryGet(version, out _)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Mac(version, payload), mac)) return null;

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(16, 8)));
        if (expiresAt <= now) return null;

        passwordStamp = payload.AsSpan(29, 8).ToArray();
        return new TwoFactorChallengeTicket(new Guid(payload.AsSpan(0, 16)), payload[24] == 1, expiresAt);
    }

    /// <summary>True when <paramref name="stamp"/> (from <see cref="Read"/>) was made from the user's current password hash.</summary>
    public static bool MatchesPassword(byte[] stamp, string? passwordHash) =>
        CryptographicOperations.FixedTimeEquals(stamp, PasswordStamp(passwordHash));

    private static byte[] PasswordStamp(string? passwordHash) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("aethera:2fa-challenge:" + (passwordHash ?? "")))[..8];

    private byte[] Mac(int version, byte[] payload)
    {
        keyring.TryGet(version, out var master);
        var key = new byte[32];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, master, key, [], Encoding.ASCII.GetBytes(Info));
            var message = new byte[Version.Length + payload.Length];
            Encoding.ASCII.GetBytes(Version, message);
            payload.CopyTo(message, Version.Length);
            return HMACSHA256.HashData(key, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }
}
