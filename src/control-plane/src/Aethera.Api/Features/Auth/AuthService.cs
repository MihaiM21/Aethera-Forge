using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Auth;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Auth;

/// <summary>First-run setup, sign-in/out and password change. Failures are thrown as <see cref="ApiProblemException"/>.</summary>
public sealed class AuthService(
    AetheraDbContext db,
    IClock clock,
    IAuditLog audit,
    PasswordService passwords,
    SessionStore sessions,
    SessionCookies cookies,
    IOptionsMonitor<AuthOptions> options,
    TwoFactorService twoFactor,
    TwoFactorChallenge challenges)
{
    // ---- setup -------------------------------------------------------------------------------------------------------------------

    /// <summary>True while no user exists. Soft-deleted users count, so deleting every account never reopens setup.</summary>
    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken) =>
        !await db.Users.IgnoreQueryFilters().AnyAsync(cancellationToken);

    /// <summary>
    /// Creates the default organization, the first user and an Owner membership, and signs the user in. Safe against concurrent calls:
    /// the check and the inserts happen in one transaction that holds a transaction-scoped advisory lock, so exactly one caller wins and
    /// the others see <c>409 auth.setup_completed</c> once the winner commits.
    /// </summary>
    public async Task<MeResponse> SetupAsync(SetupRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        if (!await IsSetupRequiredAsync(cancellationToken)) throw new ApiProblemException(AuthProblems.SetupCompleted());

        var now = clock.UtcNow;
        var user = new User(request.Email, request.DisplayName.Trim());
        user.PasswordHash = passwords.Hash(user, request.Password); // slow on purpose, done before taking the lock
        user.RecordLogin(now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireAsync(db, AuthLocks.FirstRunSetup, cancellationToken);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(cancellationToken)) throw new ApiProblemException(AuthProblems.SetupCompleted());

        var organization = new Organization { Name = request.OrganizationName.Trim(), Slug = await FreeSlugAsync(request.OrganizationName, cancellationToken) };
        var membership = new OrganizationMember { Organization = organization, User = user, Role = OrganizationRole.Owner };
        db.AddRange(organization, user, membership);
        await db.SaveChangesAsync(cancellationToken);

        ActAs(http, user, organization.Id, OrganizationRole.Owner);
        await audit.RecordAsync("auth.setup", "user", user.Id, new { email = user.Email, organization = organization.Name }, cancellationToken);
        var session = await sessions.CreateAsync(user.Id, rememberMe: false, ClientIp(http), UserAgent(http), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        cookies.Write(http, session.CookieValue, session.Persistent, session.ExpiresAt);
        return await MeAsync(user.Id, organization.Id, OrganizationRole.Owner, isSession: true, cancellationToken);
    }

    private async Task<string> FreeSlugAsync(string name, CancellationToken cancellationToken)
    {
        var baseSlug = Slug.FromName(name);
        var slug = baseSlug;
        for (var n = 2; await db.Organizations.AnyAsync(o => o.Slug == slug, cancellationToken); n++)
        {
            var suffix = "-" + n;
            slug = (baseSlug.Length + suffix.Length > Slug.MaxLength ? baseSlug[..(Slug.MaxLength - suffix.Length)] : baseSlug) + suffix;
        }

        return slug;
    }

    // ---- sign in / out -----------------------------------------------------------------------------------------------------------

    public async Task<MeResponse> LoginAsync(LoginRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var now = clock.UtcNow;
        var email = request.Email.Trim();

        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == User.NormalizeEmail(email), cancellationToken);
        if (user is null || !user.IsActive || user.PasswordHash is null)
        {
            // Same work and same answer as a wrong password, so neither timing nor the response tells which emails exist.
            passwords.Verify(null, request.Password);
            await RecordLoginFailedAsync(email, "invalid_credentials", cancellationToken);
            throw new ApiProblemException(AuthProblems.InvalidCredentials());
        }

        if (user.IsLockedOut(now))
        {
            await RecordLoginFailedAsync(email, "locked_out", cancellationToken);
            throw new ApiProblemException(AuthProblems.LockedOut(user.LockoutEndAt!.Value - now));
        }

        var check = passwords.Verify(user, request.Password);
        if (check == PasswordCheck.Failed)
        {
            await LoginCounters.RecordFailureAsync(db, user, now, settings, cancellationToken);
            await RecordLoginFailedAsync(email, user.IsLockedOut(now) ? "locked_out" : "invalid_credentials", cancellationToken);
            throw new ApiProblemException(user.IsLockedOut(now)
                ? AuthProblems.LockedOut(user.LockoutEndAt!.Value - now)
                : AuthProblems.InvalidCredentials());
        }

        var membership = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserId == user.Id).OrderBy(m => m.CreatedAt)
            .Select(m => new { m.OrganizationId, m.Role })
            .FirstOrDefaultAsync(cancellationToken);
        if (membership is null)
        {
            await RecordLoginFailedAsync(email, "no_membership", cancellationToken);
            throw new ApiProblemException(AuthProblems.InvalidCredentials());
        }

        var upgradedHash = check == PasswordCheck.SuccessRehashNeeded ? passwords.Hash(user, request.Password) : null;

        if (await twoFactor.IsEnabledAsync(user.Id, cancellationToken))
        {
            // ADR 0013: the password was right, but there is no session until the second step. The failure counters are NOT cleared here, or a
            // known password could be replayed to reset the lockout between code guesses; only a correct code clears them.
            if (upgradedHash is not null) await LoginCounters.StoreUpgradedHashAsync(db, user, upgradedHash, cancellationToken);
            await audit.RecordAsync("auth.login.two_factor_challenged", "user", user.Id, new { email = user.Email }, cancellationToken);
            throw new ApiProblemException(AuthProblems.TwoFactorRequired(
                challenges.Issue(user.Id, user.PasswordHash, request.RememberMe, now), TwoFactorChallenge.Lifetime));
        }

        await LoginCounters.RecordSuccessAsync(db, user, now, upgradedHash, cancellationToken);
        return await SignInAsync(user, membership.OrganizationId, membership.Role, request.RememberMe, http, TwoFactorMethod.None, cancellationToken);
    }

    /// <summary>
    /// The second step of a sign-in with two-factor authentication: the challenge from <see cref="LoginAsync"/> and a code or a recovery code.
    /// A wrong code counts towards the account lockout like a wrong password; the endpoint is rate limited per IP as well.
    /// </summary>
    public async Task<MeResponse> LoginWithTwoFactorAsync(TwoFactorLoginRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (challenges.Read(request.Challenge, now, out var stamp) is not { } ticket)
            throw new ApiProblemException(AuthProblems.TwoFactorChallengeInvalid());

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ticket.UserId, cancellationToken);
        if (user is null || !user.IsActive || user.PasswordHash is null || !TwoFactorChallenge.MatchesPassword(stamp, user.PasswordHash))
            throw new ApiProblemException(AuthProblems.TwoFactorChallengeInvalid());

        if (user.IsLockedOut(now))
        {
            await RecordLoginFailedAsync(user.Email, "locked_out", cancellationToken);
            throw new ApiProblemException(AuthProblems.LockedOut(user.LockoutEndAt!.Value - now));
        }

        var membership = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserId == user.Id).OrderBy(m => m.CreatedAt)
            .Select(m => new { m.OrganizationId, m.Role })
            .FirstOrDefaultAsync(cancellationToken);
        if (membership is null) throw new ApiProblemException(AuthProblems.TwoFactorChallengeInvalid());

        // Checks the lockout again, verifies and counts a failure under the per-user lock (parallel guesses cannot outrun the lockout).
        var method = await twoFactor.TryConsumeAsync(user, request.Code, request.RecoveryCode, cancellationToken);
        if (method is TwoFactorMethod.None or TwoFactorMethod.Locked)
        {
            await RecordLoginFailedAsync(user.Email, user.IsLockedOut(now) ? "locked_out" : "invalid_two_factor_code", cancellationToken);
            throw new ApiProblemException(user.IsLockedOut(now)
                ? AuthProblems.LockedOut(user.LockoutEndAt!.Value - now)
                : AuthProblems.TwoFactorInvalidCode());
        }

        await LoginCounters.RecordSuccessAsync(db, user, now, null, cancellationToken);
        return await SignInAsync(user, membership.OrganizationId, membership.Role, ticket.RememberMe, http, method, cancellationToken);
    }

    private async Task<MeResponse> SignInAsync(
        User user, Guid organizationId, OrganizationRole role, bool rememberMe, HttpContext http, TwoFactorMethod method, CancellationToken cancellationToken)
    {
        // Signing in again from a browser that still holds a live session replaces it (no two sessions behind one cookie jar).
        if (AuthClaims.ReadSessionId(http.User) is { } previousSession) await sessions.RevokeAsync(previousSession, cancellationToken);

        var session = await sessions.CreateAsync(user.Id, rememberMe, ClientIp(http), UserAgent(http), cancellationToken);
        ActAs(http, user, organizationId, role);
        await audit.RecordAsync("auth.login.succeeded", "user", user.Id,
            method == TwoFactorMethod.None
                ? new { email = user.Email, rememberMe }
                : new { email = user.Email, rememberMe, twoFactor = TwoFactorService.MethodName(method) }, cancellationToken);

        cookies.Write(http, session.CookieValue, session.Persistent, session.ExpiresAt);
        return await MeAsync(user.Id, organizationId, role, isSession: true, cancellationToken);
    }

    /// <summary>Ends the caller's session. For an API token there is no session, so there is nothing to end.</summary>
    public async Task LogoutAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (AuthClaims.ReadSessionId(http.User) is not { } sessionId) return;

        await sessions.RevokeAsync(sessionId, cancellationToken);
        cookies.Delete(http);
        await audit.RecordAsync("auth.logout", "user", AetheraPrincipal.ReadGuid(http.User, AetheraClaimTypes.UserId), null, cancellationToken);
    }

    // ---- who am i ----------------------------------------------------------------------------------------------------------------

    public async Task<MeResponse> MeAsync(ICurrentActor actor, CancellationToken cancellationToken)
    {
        if (actor.UserId is not { } userId || actor.OrganizationId is not { } organizationId || actor.Role is not { } role)
            throw new ApiProblemException(ApiProblems.Unauthenticated());
        return await MeAsync(userId, organizationId, role, isSession: actor.ApiTokenId is null, cancellationToken);
    }

    private async Task<MeResponse> MeAsync(Guid userId, Guid organizationId, OrganizationRole role, bool isSession, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new UserSummary(u.Id, u.Email, u.DisplayName)).FirstOrDefaultAsync(cancellationToken);
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId)
            .Select(o => new { Summary = new OrganizationSummary(o.Id, o.Name, o.Slug), o.RequireTwoFactor }).FirstOrDefaultAsync(cancellationToken);
        if (user is null || organization is null) throw new ApiProblemException(ApiProblems.Unauthenticated());
        var enabled = await twoFactor.IsEnabledAsync(userId, cancellationToken);
        // API tokens are not affected by the organization's two-factor requirement (ADR 0013).
        return new MeResponse(user, organization.Summary, role, enabled, isSession && organization.RequireTwoFactor && !enabled);
    }

    // ---- password ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Changes the caller's password. Needs a browser session (an API token cannot change credentials). A wrong current password counts as
    /// a failed sign-in towards the lockout, because a stolen session must not be able to guess it freely. On success every <em>other</em>
    /// session of the user is revoked.
    /// </summary>
    public async Task ChangePasswordAsync(ChangePasswordRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        if (AuthClaims.ReadSessionId(http.User) is not { } sessionId || AetheraPrincipal.ReadGuid(http.User, AetheraClaimTypes.UserId) is not { } userId)
            throw new ApiProblemException(ApiProblems.Forbidden("Changing the password requires a browser session, not an API token."));

        var settings = options.CurrentValue;
        var now = clock.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new ApiProblemException(ApiProblems.Unauthenticated());

        if (user.IsLockedOut(now)) throw new ApiProblemException(AuthProblems.LockedOut(user.LockoutEndAt!.Value - now));

        if (passwords.Verify(user, request.CurrentPassword) == PasswordCheck.Failed)
        {
            await LoginCounters.RecordFailureAsync(db, user, now, settings, cancellationToken);
            await audit.RecordAsync("auth.password.change_failed", "user", user.Id, null, cancellationToken);
            throw new ApiProblemException(user.IsLockedOut(now)
                ? AuthProblems.LockedOut(user.LockoutEndAt!.Value - now)
                : AuthProblems.Field("/currentPassword", "incorrect", "The current password is not correct."));
        }

        if (PasswordPolicy.Check(request.NewPassword, user.Email) == PasswordProblem.SameAsEmail)
            throw new ApiProblemException(AuthProblems.Field("/newPassword", "password.same_as_email", "Must not be the same as the email address."));

        user.PasswordHash = passwords.Hash(user, request.NewPassword);
        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        await db.SaveChangesAsync(cancellationToken);

        var revoked = await sessions.RevokeAllAsync(user.Id, exceptSessionId: sessionId, cancellationToken);
        await audit.RecordAsync("auth.password.changed", "user", user.Id, new { revokedSessions = revoked }, cancellationToken);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------

    private Task RecordLoginFailedAsync(string email, string reason, CancellationToken cancellationToken) =>
        audit.RecordAsync("auth.login.failed", "user", null,
            new { email = email.Length > AuthRules.MaxEmailLength ? email[..AuthRules.MaxEmailLength] : email, reason }, cancellationToken);

    /// <summary>Makes the new principal the request's actor, so the audit event carries the user, not "anonymous".</summary>
    private static void ActAs(HttpContext http, User user, Guid organizationId, OrganizationRole role) =>
        http.User = AetheraPrincipal.Create(AetheraAuthSchemes.Session, user.Id, organizationId, role);

    private static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static string? UserAgent(HttpContext http) =>
        http.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;
}

/// <summary>The per-account counters behind the lockout, shared by password sign-in, the two-factor step and the two-factor settings.</summary>
internal static class LoginCounters
{
    /// <summary>
    /// Counts a failed attempt on the account (<see cref="User.RecordFailedLogin"/>). Parallel attempts on one account queue on an advisory
    /// lock and each re-reads the counters under it, so a burst of simultaneous guesses is counted exactly and cannot dodge the lockout.
    /// </summary>
    public static async Task RecordFailureAsync(AetheraDbContext db, User user, DateTimeOffset now, AuthOptions settings, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireForUserAsync(db, user.Id, cancellationToken);
        await db.Entry(user).ReloadAsync(cancellationToken);
        user.RecordFailedLogin(now, Math.Max(1, settings.MaxFailedLogins), settings.LockoutDuration);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records a successful sign-in (<see cref="User.RecordLogin"/>: clears the failure counters) and stores an upgraded password hash when
    /// the stored one is outdated. Takes the same per-user lock as <see cref="RecordFailureAsync"/>, so it never loses a race with it.
    /// </summary>
    public static async Task RecordSuccessAsync(AetheraDbContext db, User user, DateTimeOffset now, string? upgradedHash, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireForUserAsync(db, user.Id, cancellationToken);
        await db.Entry(user).ReloadAsync(cancellationToken);
        if (upgradedHash is not null) user.PasswordHash = upgradedHash;
        user.RecordLogin(now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Stores an upgraded password hash without touching the failure counters (the password step of a two-factor sign-in).</summary>
    public static async Task StoreUpgradedHashAsync(AetheraDbContext db, User user, string upgradedHash, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireForUserAsync(db, user.Id, cancellationToken);
        await db.Entry(user).ReloadAsync(cancellationToken);
        user.PasswordHash = upgradedHash;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
