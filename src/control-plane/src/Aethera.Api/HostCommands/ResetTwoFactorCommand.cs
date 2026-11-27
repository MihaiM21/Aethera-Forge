using Aethera.Domain;
using Aethera.Infrastructure.Auth;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.HostCommands;

public enum TwoFactorResetStatus
{
    Reset,

    /// <summary>The user exists but had no authenticator and no pending enrolment: nothing to clear.</summary>
    NotEnabled,
    UserNotFound,
}

/// <summary>
/// Turns two-factor authentication off for a user, from the host (ADR 0013): the way back in after someone lost both the authenticator and
/// the recovery codes (<c>sudo aethera reset-2fa &lt;email&gt;</c>). Deletes the secret and the recovery codes, clears the lockout, revokes every
/// session of the user and records <c>users.two_factor_reset</c> with the system as the actor. The password is unchanged. If the organization
/// requires two-factor authentication, the user enrols a new authenticator at the next sign-in.
/// </summary>
public sealed class TwoFactorResetService(AetheraDbContext db, SessionStore sessions, IClock clock)
{
    public const string ActorLabel = "host: aethera reset-2fa";

    public async Task<TwoFactorResetStatus> ResetAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email ?? "");
        var membership = await db.OrganizationMembers.Include(m => m.User)
            .Where(m => m.User.NormalizedEmail == normalized && m.User.DeletedAt == null)
            .OrderBy(m => m.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (membership is null) return TwoFactorResetStatus.UserNotFound;

        var user = membership.User;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireForUserAsync(db, user.Id, cancellationToken);
        var hadTwoFactor = await db.UserTwoFactors.AnyAsync(t => t.UserId == user.Id, cancellationToken);
        var codes = await db.UserRecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.UserTwoFactors.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        if (!hadTwoFactor && codes == 0) return TwoFactorResetStatus.NotEnabled;

        await db.Entry(user).ReloadAsync(cancellationToken);
        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        db.AuditEvents.Add(new AuditEvent
        {
            OrganizationId = membership.OrganizationId,
            ActorType = AuditActorType.System,
            ActorLabel = ActorLabel,
            Action = "users.two_factor_reset",
            ResourceType = "user",
            ResourceId = user.Id,
            ResourceName = user.Email,
            MetadataJson = "{\"source\":\"host\"}",
            RequestId = "host-reset-2fa",
            OccurredAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await sessions.RevokeAllAsync(user.Id, cancellationToken: cancellationToken);
        return TwoFactorResetStatus.Reset;
    }
}

/// <summary>
/// <c>dotnet Aethera.Api.dll reset-2fa &lt;email&gt;</c>: like <see cref="ResetPasswordCommand"/>, handled before the web host exists. Exit code 0
/// when two-factor authentication was cleared or was not on, 1 for an unknown user or an unreachable database, 2 for bad usage.
/// </summary>
public static class ResetTwoFactorCommand
{
    public const string Verb = "reset-2fa";

    /// <summary>Null when <paramref name="args"/> is not this command; otherwise the exit code.</summary>
    public static async Task<int?> TryRunAsync(string[] args, TextWriter error, Func<IServiceProvider>? services = null)
    {
        if (args.Length == 0 || !string.Equals(args[0], Verb, StringComparison.Ordinal)) return null;
        if (args.Length != 2 || args[1].StartsWith('-') || string.IsNullOrWhiteSpace(args[1]))
        {
            await error.WriteLineAsync("usage: reset-2fa <email>");
            return 2;
        }

        var email = args[1];
        var provider = services?.Invoke() ?? ResetPasswordCommand.BuildHost();
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var service = ActivatorUtilities.CreateInstance<TwoFactorResetService>(scope.ServiceProvider);
            switch (await service.ResetAsync(email, CancellationToken.None))
            {
                case TwoFactorResetStatus.Reset:
                    await error.WriteLineAsync(
                        $"Two-factor authentication of {email} is off; its recovery codes are deleted, its lockout is cleared and all its sessions are revoked.");
                    return 0;
                case TwoFactorResetStatus.NotEnabled:
                    await error.WriteLineAsync($"{email} had no two-factor authentication; nothing changed.");
                    return 0;
                default:
                    await error.WriteLineAsync($"reset-2fa: no user with the email {email}");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException or DbUpdateException)
        {
            await error.WriteLineAsync($"reset-2fa: could not reach or update the database ({ex.GetType().Name}: {ex.Message.Split((char)10)[0]}). Is the stack running?");
            return 1;
        }
        finally
        {
            if (services is null && provider is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
    }
}
