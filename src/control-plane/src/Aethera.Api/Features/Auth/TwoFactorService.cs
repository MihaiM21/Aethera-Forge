using System.Security.Cryptography;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Auth;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Auth;

/// <summary>How a second factor was proven.</summary>
public enum TwoFactorMethod
{
    /// <summary>Wrong, reused or missing: counted towards the lockout.</summary>
    None,
    Totp,
    RecoveryCode,

    /// <summary>The account was locked when the code arrived: nothing was checked.</summary>
    Locked,
}

/// <summary>
/// TOTP two-factor authentication for browser sign-ins (ADR 0013): enrolment, verification of codes and recovery codes, and the organization
/// policy. The shared secret only ever leaves the database decrypted inside this class (and once, in the enrolment answer). Settings operations
/// need a browser session: an API token can neither enrol nor turn two-factor authentication off.
/// </summary>
public sealed class TwoFactorService(
    AetheraDbContext db,
    IClock clock,
    IAuditLog audit,
    ISecretProtector protector,
    PasswordService passwords,
    SessionStore sessions,
    IOptionsMonitor<AuthOptions> options)
{
    // ---- status ------------------------------------------------------------------------------------------------------------------

    public Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken) =>
        db.UserTwoFactors.AsNoTracking().AnyAsync(t => t.UserId == userId && t.ConfirmedAt != null, cancellationToken);

    public async Task<TwoFactorStatusResponse> StatusAsync(ICurrentActor actor, CancellationToken cancellationToken)
    {
        if (actor.UserId is not { } userId || actor.OrganizationId is not { } organizationId)
            throw new ApiProblemException(ApiProblems.Unauthenticated());
        var row = await db.UserTwoFactors.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId, cancellationToken);
        var remaining = row?.IsConfirmed == true
            ? await db.UserRecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, cancellationToken)
            : 0;
        var required = await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.RequireTwoFactor).FirstOrDefaultAsync(cancellationToken);
        return new TwoFactorStatusResponse(row?.IsConfirmed == true, row?.ConfirmedAt, row is { IsConfirmed: false }, remaining, required);
    }

    // ---- enrolment ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Generates a new secret and stores it as a pending enrolment (replacing an earlier pending one). Refused while two-factor authentication is on:
    /// it has to be turned off first, so a stolen session cannot silently swap the authenticator.
    /// </summary>
    public async Task<TwoFactorEnrolmentResponse> StartEnrolmentAsync(StartTwoFactorEnrolmentRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        var (user, _) = await SessionUserAsync(http, "Enrolling an authenticator", cancellationToken);
        if (await IsEnabledAsync(user.Id, cancellationToken)) throw new ApiProblemException(AuthProblems.TwoFactorAlreadyEnabled());
        await VerifyPasswordAsync(user, request.Password, cancellationToken);

        var now = clock.UtcNow;
        var secret = Totp.GenerateSecret();
        try
        {
            // Under the per-user lock, re-checked: a confirm that committed meanwhile (another tab) must not get its secret overwritten.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await AuthLocks.AcquireForUserAsync(db, user.Id, cancellationToken);
            var existing = await db.UserTwoFactors.FirstOrDefaultAsync(t => t.UserId == user.Id, cancellationToken);
            if (existing?.IsConfirmed == true) throw new ApiProblemException(AuthProblems.TwoFactorAlreadyEnabled());

            var value = protector.Protect(secret, UserTwoFactor.AssociatedData(user.Id));
            if (existing is null)
            {
                existing = new UserTwoFactor
                {
                    UserId = user.Id, SecretCiphertext = value.Ciphertext, SecretNonce = value.Nonce, WrappedDataKey = value.WrappedDataKey!,
                    WrappedDataKeyNonce = value.WrappedDataKeyNonce!, MasterKeyVersion = value.KeyVersion, CreatedAt = now, UpdatedAt = now,
                };
                db.UserTwoFactors.Add(existing);
            }
            else
            {
                existing.SecretCiphertext = value.Ciphertext;
                existing.SecretNonce = value.Nonce;
                existing.WrappedDataKey = value.WrappedDataKey!;
                existing.WrappedDataKeyNonce = value.WrappedDataKeyNonce!;
                existing.MasterKeyVersion = value.KeyVersion;
                existing.LastUsedStep = null;
                existing.CreatedAt = now;
                existing.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await audit.RecordAsync("auth.two_factor.enrolment_started", "user", user.Id, null, cancellationToken);

            var issuer = string.IsNullOrWhiteSpace(options.CurrentValue.TwoFactorIssuer) ? "Aethera" : options.CurrentValue.TwoFactorIssuer.Trim();
            return new TwoFactorEnrolmentResponse(
                Base32.Encode(secret), Totp.ProvisioningUri(issuer, user.Email, secret), issuer, user.Email, Totp.Digits, Totp.PeriodSeconds);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Confirms the pending enrolment with a code from it: two-factor authentication is on from now, the user gets a fresh set of recovery codes,
    /// and every other session of the user (made without the second factor) is signed out.
    /// </summary>
    public async Task<RecoveryCodesResponse> ConfirmEnrolmentAsync(ConfirmTwoFactorEnrolmentRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        var (user, sessionId) = await SessionUserAsync(http, "Enrolling an authenticator", cancellationToken);
        var now = clock.UtcNow;
        var settings = options.CurrentValue;
        IReadOnlyList<string> codes;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await AuthLocks.AcquireForUserAsync(db, user.Id, cancellationToken);
            await db.Entry(user).ReloadAsync(cancellationToken);
            if (user.IsLockedOut(now)) throw new ApiProblemException(AuthProblems.LockedOut(user.LockoutEndAt!.Value - now));
            var row = await db.UserTwoFactors.FirstOrDefaultAsync(t => t.UserId == user.Id, cancellationToken)
                      ?? throw new ApiProblemException(AuthProblems.TwoFactorNoEnrolment());
            if (row.IsConfirmed) throw new ApiProblemException(AuthProblems.TwoFactorAlreadyEnabled());

            if (!MatchTotp(row, request.Code, now, out var step))
            {
                // Counted like any wrong code (under the same lock): a stolen session must not brute-force a pending enrolment.
                user.RecordFailedLogin(now, Math.Max(1, settings.MaxFailedLogins), settings.LockoutDuration);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await audit.RecordAsync("auth.two_factor.code_failed", "user", user.Id, new { step = "enrolment" }, cancellationToken);
                throw new ApiProblemException(user.IsLockedOut(now)
                    ? AuthProblems.LockedOut(user.LockoutEndAt!.Value - now)
                    : AuthProblems.Field("/code", "incorrect", "The code is not correct. Check the time on your phone and try the next code."));
            }

            row.ConfirmedAt = now;
            row.LastUsedStep = step;
            row.UpdatedAt = now;
            codes = await ReplaceRecoveryCodesAsync(user.Id, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var revoked = await sessions.RevokeAllAsync(user.Id, exceptSessionId: sessionId, cancellationToken);
        await audit.RecordAsync("auth.two_factor.enabled", "user", user.Id, new { revokedSessions = revoked }, cancellationToken);
        return new RecoveryCodesResponse(codes);
    }

    // ---- disable / recovery codes ------------------------------------------------------------------------------------------------

    /// <summary>Turns two-factor authentication off (password and a code or a recovery code). The secret and the recovery codes are deleted.</summary>
    public async Task DisableAsync(DisableTwoFactorRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        var (user, _) = await SessionUserAsync(http, "Turning two-factor authentication off", cancellationToken);
        if (!await IsEnabledAsync(user.Id, cancellationToken))
        {
            // Cancelling a pending enrolment needs no code: nothing protects the account yet.
            var pending = await db.UserTwoFactors.FirstOrDefaultAsync(t => t.UserId == user.Id, cancellationToken);
            if (pending is null) throw new ApiProblemException(AuthProblems.TwoFactorNotEnabled("Two-factor authentication is not on."));
            await VerifyPasswordAsync(user, request.Password, cancellationToken);
            db.UserTwoFactors.Remove(pending);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("auth.two_factor.enrolment_cancelled", "user", user.Id, null, cancellationToken);
            return;
        }

        await VerifyPasswordAsync(user, request.Password, cancellationToken);
        var method = await VerifySecondFactorAsync(user, request.Code, request.RecoveryCode, "/code", cancellationToken);

        await db.UserRecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.UserTwoFactors.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await audit.RecordAsync("auth.two_factor.disabled", "user", user.Id, new { method = MethodName(method) }, cancellationToken);
    }

    /// <summary>Replaces every recovery code of the caller (password and a code from the authenticator).</summary>
    public async Task<RecoveryCodesResponse> RegenerateRecoveryCodesAsync(RegenerateRecoveryCodesRequest request, HttpContext http, CancellationToken cancellationToken)
    {
        var (user, _) = await SessionUserAsync(http, "Creating recovery codes", cancellationToken);
        if (!await IsEnabledAsync(user.Id, cancellationToken))
            throw new ApiProblemException(AuthProblems.TwoFactorNotEnabled("Recovery codes exist only while two-factor authentication is on."));
        await VerifyPasswordAsync(user, request.Password, cancellationToken);
        await VerifySecondFactorAsync(user, request.Code, recoveryCode: null, "/code", cancellationToken);

        var now = clock.UtcNow;
        IReadOnlyList<string> codes;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await AuthLocks.AcquireForUserAsync(db, user.Id, cancellationToken);
            codes = await ReplaceRecoveryCodesAsync(user.Id, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await audit.RecordAsync("auth.two_factor.recovery_codes_regenerated", "user", user.Id, new { count = codes.Count }, cancellationToken);
        return new RecoveryCodesResponse(codes);
    }

    // ---- organization policy -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets whether every member must use two-factor authentication (Owner only, checked by the endpoint). Turning it on needs the caller's own
    /// account to have it, so the owner cannot lock themselves into the enrolment-only state by accident.
    /// </summary>
    public async Task<TwoFactorStatusResponse> SetPolicyAsync(TwoFactorPolicyRequest request, ICurrentActor actor, CancellationToken cancellationToken)
    {
        if (actor.UserId is not { } userId || actor.OrganizationId is not { } organizationId)
            throw new ApiProblemException(ApiProblems.Unauthenticated());
        if (request.Required && !await IsEnabledAsync(userId, cancellationToken))
            throw new ApiProblemException(AuthProblems.TwoFactorNotEnabled(
                "Turn on two-factor authentication for your own account before requiring it for everyone."));

        var organization = await db.Organizations.FirstAsync(o => o.Id == organizationId, cancellationToken);
        if (organization.RequireTwoFactor != request.Required)
        {
            organization.RequireTwoFactor = request.Required;
            await db.SaveChangesAsync(cancellationToken);
            var withoutTwoFactor = await (from m in db.OrganizationMembers
                                          where m.OrganizationId == organizationId
                                          join u in db.Users on m.UserId equals u.Id
                                          where u.IsActive && !db.UserTwoFactors.Any(t => t.UserId == u.Id && t.ConfirmedAt != null)
                                          select u.Id).CountAsync(cancellationToken);
            await audit.RecordAsync("organization.two_factor_policy_changed", "organization", organizationId,
                new { required = request.Required, membersWithoutTwoFactor = withoutTwoFactor }, cancellationToken);
        }

        return await StatusAsync(actor, cancellationToken);
    }

    // ---- verification (sign-in and settings) -------------------------------------------------------------------------------------

    /// <summary>
    /// Checks a TOTP code or a recovery code of a user with a confirmed authenticator and consumes it (the code's time step or the recovery code
    /// cannot be used again). Everything happens under the per-user advisory lock, in one transaction: the lockout is re-read there, a wrong code
    /// is counted there (<see cref="User.RecordFailedLogin"/>), so a burst of parallel guesses cannot get more tries than the lockout allows and
    /// two requests with the same code cannot both pass. Returns <see cref="TwoFactorMethod.Locked"/> when the account is locked (nothing was
    /// checked) and <see cref="TwoFactorMethod.None"/> for a wrong, reused or missing code (counted; <paramref name="user"/> then shows whether
    /// that failure locked the account).
    /// </summary>
    public async Task<TwoFactorMethod> TryConsumeAsync(User user, string? code, string? recoveryCode, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var settings = options.CurrentValue;
        var userId = user.Id;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireForUserAsync(db, userId, cancellationToken);
        await db.Entry(user).ReloadAsync(cancellationToken);
        if (user.IsLockedOut(now)) return TwoFactorMethod.Locked;

        var row = await db.UserTwoFactors.FirstOrDefaultAsync(t => t.UserId == userId && t.ConfirmedAt != null, cancellationToken);
        var method = TwoFactorMethod.None;
        if (row is null)
        {
            // No confirmed authenticator (turned off meanwhile): nothing can match.
        }
        else if (!string.IsNullOrEmpty(code))
        {
            if (MatchTotp(row, code, now, out var step))
            {
                row.LastUsedStep = step;
                row.UpdatedAt = now;
                method = TwoFactorMethod.Totp;
            }
        }
        else if (RecoveryCodes.Normalize(recoveryCode) is { } normalized)
        {
            var hash = RecoveryCodes.Hash(normalized);
            var match = await db.UserRecoveryCodes.FirstOrDefaultAsync(c => c.UserId == userId && c.CodeHash == hash && c.UsedAt == null, cancellationToken);
            if (match is not null)
            {
                match.UsedAt = now;
                method = TwoFactorMethod.RecoveryCode;
            }
        }

        if (method == TwoFactorMethod.None) user.RecordFailedLogin(now, Math.Max(1, settings.MaxFailedLogins), settings.LockoutDuration);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (method == TwoFactorMethod.RecoveryCode)
        {
            var remaining = await db.UserRecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, cancellationToken);
            await audit.RecordAsync("auth.two_factor.recovery_code_used", "user", userId, new { remaining }, cancellationToken);
        }

        return method;
    }

    public static string MethodName(TwoFactorMethod method) => method switch
    {
        TwoFactorMethod.Totp => "totp",
        TwoFactorMethod.RecoveryCode => "recovery_code",
        _ => "none",
    };

    // ---- helpers -----------------------------------------------------------------------------------------------------------------

    /// <summary>A second factor for a settings operation; a wrong one counts towards the lockout like a wrong password.</summary>
    private async Task<TwoFactorMethod> VerifySecondFactorAsync(User user, string? code, string? recoveryCode, string pointer, CancellationToken cancellationToken)
    {
        var method = await TryConsumeAsync(user, code, recoveryCode, cancellationToken);
        if (method is TwoFactorMethod.Totp or TwoFactorMethod.RecoveryCode) return method;

        var now = clock.UtcNow;
        if (method == TwoFactorMethod.None) await audit.RecordAsync("auth.two_factor.code_failed", "user", user.Id, null, cancellationToken);
        throw new ApiProblemException(user.IsLockedOut(now)
            ? AuthProblems.LockedOut(user.LockoutEndAt!.Value - now)
            : AuthProblems.Field(string.IsNullOrEmpty(code) && recoveryCode is not null ? "/recoveryCode" : pointer, "incorrect",
                "The code is not correct, has expired or was already used."));
    }

    /// <summary>The current password, as for a password change: a wrong one counts towards the lockout.</summary>
    private async Task VerifyPasswordAsync(User user, string password, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (user.IsLockedOut(now)) throw new ApiProblemException(AuthProblems.LockedOut(user.LockoutEndAt!.Value - now));
        if (passwords.Verify(user, password) != PasswordCheck.Failed) return;

        await LoginCounters.RecordFailureAsync(db, user, now, options.CurrentValue, cancellationToken);
        await audit.RecordAsync("auth.two_factor.password_failed", "user", user.Id, null, cancellationToken);
        throw new ApiProblemException(user.IsLockedOut(now)
            ? AuthProblems.LockedOut(user.LockoutEndAt!.Value - now)
            : AuthProblems.Field("/password", "incorrect", "The password is not correct."));
    }

    private async Task<(User User, Guid SessionId)> SessionUserAsync(HttpContext http, string what, CancellationToken cancellationToken)
    {
        if (AuthClaims.ReadSessionId(http.User) is not { } sessionId || AetheraPrincipal.ReadGuid(http.User, AetheraClaimTypes.UserId) is not { } userId)
            throw new ApiProblemException(ApiProblems.Forbidden($"{what} requires a browser session, not an API token."));
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw new ApiProblemException(ApiProblems.Unauthenticated());
        return (user, sessionId);
    }

    private bool MatchTotp(UserTwoFactor row, string? code, DateTimeOffset now, out long step)
    {
        var secret = protector.Unprotect(
            new ProtectedValue(row.SecretCiphertext, row.SecretNonce, row.MasterKeyVersion)
                { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce },
            UserTwoFactor.AssociatedData(row.UserId));
        try
        {
            return Totp.TryMatch(secret, code, now, row.LastUsedStep, out step);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Deletes the user's recovery codes and adds a new set (tracked; the caller saves). Returns the plaintext codes, shown once.</summary>
    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.UserRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        var codes = RecoveryCodes.Generate();
        foreach (var code in codes)
            db.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = userId, CodeHash = RecoveryCodes.Hash(RecoveryCodes.Normalize(code)!), CreatedAt = now });
        return codes;
    }
}
