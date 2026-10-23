using Aethera.Api.Features.Resources.Secrets;
using Aethera.Domain;
using Aethera.Infrastructure;
using Aethera.Infrastructure.Auth;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aethera.Api.HostCommands;

public enum PasswordResetStatus
{
    Reset,
    UserNotFound,
    PasswordRejected,
}

/// <param name="Password">Set only when the password was generated: it is printed once and never stored in plain text.</param>
public sealed record PasswordResetResult(PasswordResetStatus Status, string? Password = null, string? Problem = null);

/// <summary>
/// Sets a new password for a user, from the host. Whoever can run it can already read <c>.env</c> and the database, so it is not an API
/// endpoint: it is the way back in after the last owner forgot their password (<c>sudo aethera reset-password &lt;email&gt;</c>).
/// Like a password reset by an administrator it clears the lockout and the failed-login counter and revokes every session of the user, and it
/// records <c>users.password_reset</c> with the system as the actor. A deactivated account stays deactivated.
/// </summary>
public sealed class PasswordResetService(AetheraDbContext db, PasswordService passwords, SessionStore sessions, IClock clock)
{
    public const string ActorLabel = "host: aethera reset-password";

    /// <summary>Resets the password of the user with this email. <paramref name="newPassword"/> null generates a strong one.</summary>
    public async Task<PasswordResetResult> ResetAsync(string email, string? newPassword, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email ?? "");
        var membership = await db.OrganizationMembers.Include(m => m.User)
            .Where(m => m.User.NormalizedEmail == normalized && m.User.DeletedAt == null)
            .OrderBy(m => m.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (membership is null) return new PasswordResetResult(PasswordResetStatus.UserNotFound);

        var user = membership.User;
        var generated = newPassword is null;
        var password = newPassword ?? SecretVault.GeneratePassword(24);
        var problem = PasswordPolicy.Check(password, user.Email) switch
        {
            PasswordProblem.TooShort => $"The password must have at least {PasswordPolicy.MinLength} characters.",
            PasswordProblem.TooLong => $"The password must have at most {PasswordPolicy.MaxLength} characters.",
            PasswordProblem.SameAsEmail => "The password must not be the email address.",
            _ => null,
        };
        if (problem is not null) return new PasswordResetResult(PasswordResetStatus.PasswordRejected, Problem: problem);

        user.PasswordHash = passwords.Hash(user, password);
        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        db.AuditEvents.Add(new AuditEvent
        {
            OrganizationId = membership.OrganizationId,
            ActorType = AuditActorType.System,
            ActorLabel = ActorLabel,
            Action = "users.password_reset",
            ResourceType = "user",
            ResourceId = user.Id,
            ResourceName = user.Email,
            MetadataJson = "{\"source\":\"host\",\"generated\":" + (generated ? "true" : "false") + "}",
            RequestId = "host-reset-password",
            OccurredAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        await sessions.RevokeAllAsync(user.Id, cancellationToken: cancellationToken);
        return new PasswordResetResult(PasswordResetStatus.Reset, generated ? password : null);
    }
}

/// <summary>
/// <c>dotnet Aethera.Api.dll reset-password &lt;email&gt; [--stdin]</c>: handled before the web host exists (nothing listens, no hosted
/// service runs). Without <c>--stdin</c> a random password is generated and printed once; with it the new password is the first line of
/// standard input. Exit code 0 on success, 1 for an unknown user or a rejected password, 2 for bad usage. Uses the container's own
/// configuration (<c>ConnectionStrings__Aethera</c>), so <c>aethera reset-password</c> runs it inside the control-plane container.
/// </summary>
public static class ResetPasswordCommand
{
    public const string Verb = "reset-password";

    /// <summary>Null when <paramref name="args"/> is not this command (start the web host); otherwise the exit code.</summary>
    public static async Task<int?> TryRunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, Func<IServiceProvider>? services = null)
    {
        if (args.Length == 0 || !string.Equals(args[0], Verb, StringComparison.Ordinal)) return null;

        string? email = null;
        var fromStdin = false;
        foreach (var arg in args.Skip(1))
        {
            if (arg == "--stdin") fromStdin = true;
            else if (!arg.StartsWith('-') && email is null) email = arg;
            else
            {
                await error.WriteLineAsync($"reset-password: unexpected argument '{arg}'");
                email = null;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            await error.WriteLineAsync("usage: reset-password <email> [--stdin]");
            return 2;
        }

        string? password = null;
        if (fromStdin)
        {
            password = (await input.ReadLineAsync())?.TrimEnd('\r', '\n');
            if (string.IsNullOrEmpty(password))
            {
                await error.WriteLineAsync("reset-password: no password on standard input");
                return 2;
            }
        }

        var provider = services?.Invoke() ?? BuildHost();
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var service = ActivatorUtilities.CreateInstance<PasswordResetService>(scope.ServiceProvider);
            var result = await service.ResetAsync(email, password, CancellationToken.None);
            switch (result.Status)
            {
                case PasswordResetStatus.Reset:
                    await error.WriteLineAsync($"Password of {email} changed; its lockout is cleared and all its sessions are revoked.");
                    if (result.Password is not null)
                    {
                        await error.WriteLineAsync("The new password (shown once, change it in Settings after signing in):");
                        await output.WriteLineAsync(result.Password);
                    }

                    return 0;
                case PasswordResetStatus.UserNotFound:
                    await error.WriteLineAsync($"reset-password: no user with the email {email}");
                    return 1;
                default:
                    await error.WriteLineAsync($"reset-password: {result.Problem}");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException or DbUpdateException)
        {
            // The message of these never contains the password; the connection string is not echoed either.
            await error.WriteLineAsync($"reset-password: could not reach or update the database ({ex.GetType().Name}: {ex.Message.Split((char)10)[0]}). Is the stack running?");
            return 1;
        }
        finally
        {
            if (services is null && provider is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
    }

    private static IServiceProvider BuildHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddAetheraPersistence(builder.Configuration);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<PasswordService>();
        builder.Services.AddOptions<AuthOptions>().Configure<IConfiguration>((options, root) => root.GetSection(AuthOptions.SectionName).Bind(options));
        builder.Services.AddScoped<SessionStore>();
        return builder.Build().Services; // built, never run: no listener, no hosted service
    }
}
