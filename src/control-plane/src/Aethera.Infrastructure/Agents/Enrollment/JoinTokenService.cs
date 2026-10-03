using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Agents.Enrollment;

/// <summary>
/// Join tokens of ADR 0002: 256 random bits, base64url, prefix <c>aeth_join_</c>. Only the SHA-256 hash is stored. A token is consumed
/// by one atomic statement, so concurrent enrolments with the same token cannot both succeed.
/// </summary>
public sealed class JoinTokenService
{
    public const string Prefix = "aeth_join_";

    /// <summary>Longest token string accepted before hashing (prefix + 43 characters of base64url).</summary>
    public const int MaxLength = 128;

    public static string Generate() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool LooksLikeToken(string? token) =>
        token is { Length: > 0 and <= MaxLength } && token.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Creates the token and adds its record to <paramref name="db"/> (the caller saves). The plaintext is returned once and never stored.
    /// </summary>
    public static (string Plaintext, JoinToken Record) Issue(AetheraDbContext db, Guid serverId, DateTimeOffset now, TimeSpan? ttl = null, Guid? createdByUserId = null)
    {
        var plaintext = Generate();
        var record = JoinToken.Issue(serverId, Hash(plaintext), now, ttl, createdByUserId);
        db.JoinTokens.Add(record);
        return (plaintext, record);
    }

    /// <summary>
    /// <c>UPDATE ... WHERE used_at IS NULL AND expires_at &gt; now RETURNING server_id</c>: the server the token belongs to, or null when the
    /// token is unknown, expired, revoked, already used or its server was deleted (the three cases are indistinguishable by design).
    /// </summary>
    public static async Task<Guid?> TryConsumeAsync(AetheraDbContext db, string token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!LooksLikeToken(token)) return null;
        var hash = Hash(token);
        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE join_tokens AS t SET used_at = {now}, updated_at = {now}
            WHERE t.token_hash = {hash} AND t.used_at IS NULL AND t.revoked_at IS NULL AND t.expires_at > {now}
              AND EXISTS (SELECT 1 FROM servers AS s WHERE s.id = t.server_id AND s.deleted_at IS NULL)
            RETURNING t.server_id AS "Value"
            """).ToListAsync(cancellationToken);
        return ids.Count == 1 ? ids[0] : null;
    }
}
