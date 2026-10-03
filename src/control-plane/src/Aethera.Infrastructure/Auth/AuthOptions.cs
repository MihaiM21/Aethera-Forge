namespace Aethera.Infrastructure.Auth;

/// <summary>
/// Tunables of authentication, bound from the <c>Aethera:Auth</c> configuration section (environment variables use <c>__</c>,
/// e.g. <c>Aethera__Auth__LoginRateLimitPermits=20</c>). Timespans are written <c>hh:mm:ss</c> or <c>d.hh:mm:ss</c>.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Aethera:Auth";

    /// <summary>Idle timeout of a normal browser session: every request (at most once per <see cref="TouchInterval"/>) pushes expiry this far out.</summary>
    public TimeSpan SessionSlidingExpiration { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Idle timeout of a "remember me" session (persistent cookie).</summary>
    public TimeSpan RememberMeSlidingExpiration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Hard limit counted from login, whatever the activity. Applies to both kinds of session.</summary>
    public TimeSpan SessionAbsoluteLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Minimum time between two writes of <c>last_seen_at</c> (sessions) / <c>last_used_at</c> (API tokens).</summary>
    public TimeSpan TouchInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Wrong passwords in a row before the account locks (see <c>User.RecordFailedLogin</c>).</summary>
    public int MaxFailedLogins { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Login attempts allowed per client IP and <see cref="LoginRateLimitWindow"/> before <c>429 rate_limited</c>.</summary>
    public int LoginRateLimitPermits { get; set; } = 10;

    public TimeSpan LoginRateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Self-hosted installs often start on plain HTTP (LAN address, no certificate yet). The secure <c>__Host-</c> cookie would never be
    /// sent back by a browser there, so this switch makes plain-HTTP requests use the non-secure <c>aethera_session</c> cookie, as
    /// <c>Development</c> and <c>Testing</c> do. Leave it off in production behind HTTPS.
    /// </summary>
    public bool AllowInsecureCookies { get; set; }

    /// <summary>Lifetime given to an API token when the request does not say (ADR 0003: 90 days).</summary>
    public int ApiTokenDefaultLifetimeDays { get; set; } = 90;

    /// <summary>Longest expiry a token may be created with (a token that never expires is separate, Admin and above only).</summary>
    public int ApiTokenMaxLifetimeDays { get; set; } = 365;
}
