using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Notifications;

/// <summary>Outcome of one delivery attempt. <see cref="Error"/> is safe to show and store (no secret values).</summary>
public sealed record DeliveryResult(bool Ok, string? Error = null, bool Retryable = false)
{
    public static DeliveryResult Success { get; } = new(true);
}

/// <summary>Reads the managed secrets of notification settings (SMTP password, Resend key, webhook secret). The only place here that sees plaintext.</summary>
public sealed class NotificationSecretReader(AetheraDbContext db, ISecretProtector protector)
{
    public async Task<string?> ReadAsync(Guid? secretId, CancellationToken cancellationToken)
    {
        if (secretId is not { } id) return null;
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (secret is null) return null;
        var row = await db.SecretVersions.AsNoTracking().FirstOrDefaultAsync(v => v.SecretId == id && v.Version == secret.CurrentVersion, cancellationToken);
        if (row is null) return null;
        try
        {
            var value = new ProtectedValue(row.Ciphertext, row.Nonce, row.MasterKeyVersion) { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce };
            return Encoding.UTF8.GetString(protector.Unprotect(value, $"secret:{id}:v{row.Version}"));
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("A notification secret could not be decrypted. Check the configured master key.");
        }
    }
}

/// <summary>
/// Delivers one rendered notification to one channel: resolves the email recipients or the webhook secret, calls the transport and turns
/// every failure into a <see cref="DeliveryResult"/> with a message that is safe to store. Shared by the <c>notification.send</c> job and the
/// "send test" endpoints.
/// </summary>
public sealed class NotificationDispatcher(
    AetheraDbContext db, NotificationSecretReader secrets, IEmailSender email, WebhookSender webhook,
    IOptions<NotificationOptions> options, ILogger<NotificationDispatcher> logger)
{
    /// <summary>The addresses an email channel sends to: administrators and owners (when <c>SendToMembers</c>) plus the channel's own list, de-duplicated.</summary>
    public async Task<IReadOnlyList<string>> RecipientsAsync(NotificationChannel channel, CancellationToken cancellationToken)
    {
        var addresses = new List<string>();
        if (channel.SendToMembers)
        {
            addresses.AddRange(await db.OrganizationMembers.AsNoTracking()
                .Where(m => m.OrganizationId == channel.OrganizationId && (m.Role == OrganizationRole.Admin || m.Role == OrganizationRole.Owner) && m.User.IsActive)
                .OrderBy(m => m.CreatedAt).Select(m => m.User.Email).ToListAsync(cancellationToken));
        }

        addresses.AddRange(channel.EmailRecipients);
        return addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The decrypted transport configuration of the organization; null when email is not set up (or, with <paramref name="requireEnabled"/>, not enabled).</summary>
    public async Task<EmailTransportConfig?> LoadEmailConfigAsync(Guid organizationId, bool requireEnabled, CancellationToken cancellationToken)
    {
        var settings = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken);
        if (settings is null || (requireEnabled && !settings.Enabled)) return null;
        return new EmailTransportConfig(
            settings.Transport, settings.FromAddress, settings.FromName, settings.SmtpHost, settings.SmtpPort ?? DefaultPort(settings.SmtpEncryption), settings.SmtpEncryption,
            settings.SmtpUsername,
            settings.Transport == EmailTransport.Smtp ? await secrets.ReadAsync(settings.SmtpPasswordSecretId, cancellationToken) : null,
            settings.Transport == EmailTransport.Resend ? await secrets.ReadAsync(settings.ResendApiKeySecretId, cancellationToken) : null);
    }

    public static int DefaultPort(SmtpEncryption encryption) => encryption switch
    {
        SmtpEncryption.Tls => 465,
        SmtpEncryption.StartTls => 587,
        _ => 25,
    };

    /// <summary>Sends <paramref name="message"/> with the organization's email settings.</summary>
    public async Task<DeliveryResult> SendEmailAsync(Guid organizationId, EmailMessage message, bool requireEnabled, CancellationToken cancellationToken)
    {
        try
        {
            var config = await LoadEmailConfigAsync(organizationId, requireEnabled, cancellationToken);
            if (config is null)
                return new DeliveryResult(false, requireEnabled ? "Email is not configured or not enabled (Settings > Notifications)." : "Email is not configured (Settings > Notifications).");
            await email.SendAsync(config, message, cancellationToken);
            return DeliveryResult.Success;
        }
        catch (EmailSendException ex)
        {
            return new DeliveryResult(false, ex.Message, ex.Retryable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sending email for organization {Organization} failed", organizationId);
            return new DeliveryResult(false, "Sending the email failed unexpectedly; see the server log.", Retryable: true);
        }
    }

    /// <summary>Delivers <paramref name="payload"/> to <paramref name="channel"/>.</summary>
    public async Task<DeliveryResult> DeliverAsync(NotificationChannel channel, NotificationPayload payload, Guid deliveryId, CancellationToken cancellationToken)
    {
        var rendered = NotificationMessageBuilder.Render(payload, await Deployments.ProxySettingsStore.PublicUrlAsync(db, options.Value.PublicUrl, cancellationToken));
        if (channel.Kind == NotificationChannelKind.Email)
        {
            var recipients = await RecipientsAsync(channel, cancellationToken);
            if (recipients.Count == 0) return new DeliveryResult(false, "The channel has no recipients (no administrators and no extra addresses).");
            return await SendEmailAsync(channel.OrganizationId, new EmailMessage(rendered.Subject, rendered.Text, rendered.Html, recipients), requireEnabled: true, cancellationToken);
        }

        string? secret = null;
        try
        {
            secret = await secrets.ReadAsync(channel.WebhookSecretId, cancellationToken);
            if (secret is null || channel.WebhookUrl is null) return new DeliveryResult(false, "The webhook has no URL or signing secret.");
            await webhook.SendAsync(channel.WebhookUrl, payload.Type, deliveryId, rendered.WebhookJson, secret, cancellationToken);
            return DeliveryResult.Success;
        }
        catch (WebhookSendException ex)
        {
            return new DeliveryResult(false, secret is null ? ex.Message : ex.Message.Replace(secret, "********", StringComparison.Ordinal), ex.Retryable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delivering to webhook channel {Channel} failed", channel.Id);
            return new DeliveryResult(false, "Delivering the webhook failed unexpectedly; see the server log.", Retryable: true);
        }
    }
}
