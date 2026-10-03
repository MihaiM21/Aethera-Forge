using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Auth;

public enum ApiTokenStatus
{
    Valid,

    /// <summary>Unknown prefix, wrong secret, or an owner that no longer exists / is not a member / is deactivated. Deliberately indistinguishable.</summary>
    NotFound,
    Revoked,
    Expired,
}

/// <summary>Result of authenticating a presented API token. Everything but <see cref="Status"/> is set only when it is <see cref="ApiTokenStatus.Valid"/>.</summary>
public sealed record ApiTokenResolution(
    ApiTokenStatus Status,
    Guid TokenId = default,
    Guid UserId = default,
    Guid OrganizationId = default,
    OrganizationRole Role = OrganizationRole.Viewer,
    IReadOnlyList<string>? Scopes = null)
{
    public bool IsValid => Status == ApiTokenStatus.Valid;
}

/// <summary>
/// Authenticates <c>Authorization: Bearer aeth_...</c> credentials: finds the row by the 8-character public prefix (unique index), then
/// compares SHA-256 hashes in constant time. Revoked and expired are only reported to a caller that proved it knows the secret, so the
/// distinct error codes cannot be used to probe for tokens.
/// </summary>
public sealed class ApiTokenStore(AetheraDbContext db, IClock clock, IOptionsMonitor<AuthOptions> options)
{
    public async Task<ApiTokenResolution> ResolveAsync(ParsedApiToken token, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var row = await (
                from t in db.ApiTokens.AsNoTracking()
                where t.Prefix == token.Prefix
                join u in db.Users on t.CreatedByUserId equals u.Id // the soft-delete filter hides deleted owners
                join m in db.OrganizationMembers on new { u.Id, t.OrganizationId } equals new { Id = m.UserId, m.OrganizationId }
                select new
                {
                    t.Id,
                    t.SecretHash,
                    t.Scopes,
                    t.ExpiresAt,
                    t.RevokedAt,
                    t.LastUsedAt,
                    t.OrganizationId,
                    UserId = u.Id,
                    u.IsActive,
                    m.Role,
                })
            .FirstOrDefaultAsync(cancellationToken);

        // Hash even when nothing was found, so a miss does not return measurably faster than a wrong secret.
        var hash = ApiTokenFormat.Hash(token.Secret);
        if (row is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(row.SecretHash, hash))
            return new ApiTokenResolution(ApiTokenStatus.NotFound);

        if (row.RevokedAt is not null) return new ApiTokenResolution(ApiTokenStatus.Revoked);
        if (row.ExpiresAt is { } expiresAt && expiresAt <= now) return new ApiTokenResolution(ApiTokenStatus.Expired);
        if (!row.IsActive) return new ApiTokenResolution(ApiTokenStatus.NotFound);

        if (row.LastUsedAt is not { } last || now - last >= options.CurrentValue.TouchInterval)
        {
            var ip = ipAddress is { Length: > 45 } ? ipAddress[..45] : ipAddress;
            var previous = row.LastUsedAt;
            // Conditional on the value we read: of several concurrent requests only one writes.
            await db.ApiTokens
                .Where(t => t.Id == row.Id && t.LastUsedAt == previous)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastUsedAt, now).SetProperty(t => t.LastUsedIp, ip), cancellationToken);
        }

        return new ApiTokenResolution(ApiTokenStatus.Valid, row.Id, row.UserId, row.OrganizationId, row.Role, row.Scopes);
    }
}
