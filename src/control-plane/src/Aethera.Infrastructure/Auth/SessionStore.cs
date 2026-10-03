using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Auth;

/// <summary>A freshly created session: the cookie to send and when it lapses.</summary>
public sealed record IssuedSession(Guid SessionId, string CookieValue, DateTimeOffset ExpiresAt, bool Persistent);

public enum SessionStatus
{
    Valid,

    /// <summary>No such session, a deleted user, or a user without any organization membership.</summary>
    NotFound,
    Revoked,
    Expired,

    /// <summary>The account was deactivated after the session was created.</summary>
    UserInactive,
}

/// <summary>Result of resolving a session cookie.</summary>
/// <param name="Refreshed">True when this call extended the session (sliding expiry); a persistent cookie should then be re-issued with the new expiry.</param>
public sealed record SessionResolution(
    SessionStatus Status,
    Guid SessionId = default,
    Guid UserId = default,
    Guid OrganizationId = default,
    OrganizationRole Role = OrganizationRole.Viewer,
    DateTimeOffset ExpiresAt = default,
    bool Persistent = false,
    bool Refreshed = false)
{
    public bool IsValid => Status == SessionStatus.Valid;
}

/// <summary>
/// Server-side browser sessions (<c>user_sessions</c>). The cookie carries a 256-bit secret; the table keeps only its SHA-256 hash, so a
/// database leak cannot be replayed as logins. Expiry is sliding (12 h idle, 7 days for "remember me") and bounded by an absolute
/// limit (30 days from creation). Activity is written at most once per <see cref="AuthOptions.TouchInterval"/>.
/// </summary>
public sealed class SessionStore(AetheraDbContext db, IClock clock, IOptionsMonitor<AuthOptions> options)
{
    public async Task<IssuedSession> CreateAsync(
        Guid userId, bool rememberMe, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;
        var now = clock.UtcNow;
        var secret = SessionSecret.Generate(rememberMe);
        var session = new UserSession
        {
            UserId = userId,
            SecretHash = secret.Hash,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now,
            ExpiresAt = Min(now + Window(settings, rememberMe), now + settings.SessionAbsoluteLifetime),
            IpAddress = Truncate(ipAddress, 45),
            UserAgent = Truncate(userAgent, 512),
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new IssuedSession(session.Id, secret.CookieValue, session.ExpiresAt, rememberMe);
    }

    /// <summary>Finds the session for a cookie, checks it and (at most once per touch interval) slides its expiry.</summary>
    public async Task<SessionResolution> ResolveAsync(ParsedSessionSecret secret, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;
        var now = clock.UtcNow;

        var row = await (
                from s in db.UserSessions.AsNoTracking()
                where s.SecretHash == secret.Hash
                join u in db.Users on s.UserId equals u.Id // the soft-delete filter hides deleted users
                join m in db.OrganizationMembers on u.Id equals m.UserId
                orderby m.CreatedAt
                select new
                {
                    s.Id,
                    s.UserId,
                    s.CreatedAt,
                    s.ExpiresAt,
                    s.LastSeenAt,
                    s.RevokedAt,
                    u.IsActive,
                    m.OrganizationId,
                    m.Role,
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null) return new SessionResolution(SessionStatus.NotFound);
        if (row.RevokedAt is not null) return new SessionResolution(SessionStatus.Revoked);

        var absoluteEnd = row.CreatedAt + settings.SessionAbsoluteLifetime;
        if (row.ExpiresAt <= now || absoluteEnd <= now) return new SessionResolution(SessionStatus.Expired);
        if (!row.IsActive) return new SessionResolution(SessionStatus.UserInactive);

        var expiresAt = row.ExpiresAt;
        var refreshed = false;
        if (now - row.LastSeenAt >= settings.TouchInterval)
        {
            var newExpiry = Min(now + Window(settings, secret.Persistent), absoluteEnd);
            var previousSeen = row.LastSeenAt;
            // Conditional on the previous value: of several concurrent requests only one writes.
            var updated = await db.UserSessions
                .Where(s => s.Id == row.Id && s.RevokedAt == null && s.LastSeenAt == previousSeen)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.LastSeenAt, now)
                    .SetProperty(s => s.ExpiresAt, newExpiry)
                    .SetProperty(s => s.UpdatedAt, now), cancellationToken);
            if (updated > 0)
            {
                expiresAt = newExpiry;
                refreshed = true;
            }
        }

        return new SessionResolution(
            SessionStatus.Valid, row.Id, row.UserId, row.OrganizationId, row.Role, expiresAt, secret.Persistent, refreshed);
    }

    /// <summary>Revokes one session (logout). Revoking a revoked or unknown session is a no-op.</summary>
    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        await db.UserSessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.UpdatedAt, now), cancellationToken);
    }

    /// <summary>
    /// Revokes every live session of a user, optionally keeping one (the one that just changed the password). Returns how many were revoked.
    /// </summary>
    public async Task<int> RevokeAllAsync(Guid userId, Guid? exceptSessionId = null, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        return await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && (exceptSessionId == null || s.Id != exceptSessionId))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.UpdatedAt, now), cancellationToken);
    }

    private static TimeSpan Window(AuthOptions settings, bool persistent) =>
        persistent ? settings.RememberMeSlidingExpiration : settings.SessionSlidingExpiration;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
