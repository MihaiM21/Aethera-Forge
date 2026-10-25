using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aethera.Domain;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Aethera.Infrastructure.Notifications;

/// <summary>A message to deliver by email.</summary>
public sealed record EmailMessage(string Subject, string Text, string Html, IReadOnlyList<string> To);

/// <summary>Everything a transport needs, with secrets already decrypted (kept in memory for one send only).</summary>
public sealed record EmailTransportConfig(
    EmailTransport Transport, string FromAddress, string? FromName,
    string? SmtpHost, int SmtpPort, SmtpEncryption Encryption, string? SmtpUsername, string? SmtpPassword, string? ResendApiKey)
{
    /// <summary>The secret values of this configuration, to scrub from any error text.</summary>
    public IEnumerable<string> SecretValues => new[] { SmtpPassword, ResendApiKey }.OfType<string>().Where(s => s.Length >= 3);

    public MailboxAddress From => new(FromName ?? "", FromAddress);
}

/// <summary>A send that failed. <see cref="Message"/> is safe to show: it never contains a configured secret.</summary>
public sealed class EmailSendException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True for a failure that may pass (connection refused, timeout, 4xx, 5xx from the HTTP API); false for a refusal (bad credentials, rejected sender).</summary>
    public bool Retryable { get; } = retryable;
}

/// <summary>Sends one email through the configured transport (SMTP or Resend). Replaced by a fake in tests.</summary>
public interface IEmailSender
{
    /// <exception cref="EmailSendException">The message was not accepted.</exception>
    Task SendAsync(EmailTransportConfig config, EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Picks the transport of the configuration.</summary>
public sealed class RoutingEmailSender(SmtpEmailSender smtp, ResendEmailSender resend) : IEmailSender
{
    public Task SendAsync(EmailTransportConfig config, EmailMessage message, CancellationToken cancellationToken) =>
        config.Transport == EmailTransport.Resend ? resend.SendAsync(config, message, cancellationToken) : smtp.SendAsync(config, message, cancellationToken);
}

/// <summary>SMTP through MailKit: STARTTLS, implicit TLS or none, optional authentication.</summary>
public sealed class SmtpEmailSender(IOptions<NotificationOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailTransportConfig config, EmailMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.SmtpHost)) throw new EmailSendException("No SMTP host is configured.", retryable: false);

        MimeMessage mime;
        try
        {
            mime = new MimeMessage();
            mime.From.Add(config.From);
            foreach (var to in message.To) mime.To.Add(MailboxAddress.Parse(to));
            mime.Subject = message.Subject;
            mime.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();
        }
        catch (ParseException ex)
        {
            throw new EmailSendException("An email address is not valid: " + ex.Message, retryable: false, ex);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.SmtpTimeoutSeconds));
        using var client = new SmtpClient { Timeout = (int)timeout.TotalMilliseconds };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout * 2);
        try
        {
            var security = config.Encryption switch
            {
                SmtpEncryption.None => SecureSocketOptions.None,
                SmtpEncryption.Tls => SecureSocketOptions.SslOnConnect,
                _ => SecureSocketOptions.StartTls,
            };
            await client.ConnectAsync(config.SmtpHost, config.SmtpPort, security, deadline.Token);
            if (!string.IsNullOrEmpty(config.SmtpUsername))
                await client.AuthenticateAsync(config.SmtpUsername, config.SmtpPassword ?? "", deadline.Token);
            await client.SendAsync(mime, deadline.Token);
            await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new EmailSendException($"The SMTP server {config.SmtpHost}:{config.SmtpPort} did not answer in time.", retryable: true, ex);
        }
        catch (Exception ex) when (ex is not EmailSendException)
        {
            throw Translate(ex, config);
        }
    }

    private static EmailSendException Translate(Exception ex, EmailTransportConfig config)
    {
        var target = $"{config.SmtpHost}:{config.SmtpPort}";
        var (text, retryable) = ex switch
        {
            AuthenticationException => ($"The SMTP server {target} rejected the credentials: {ex.Message}", false),
            SslHandshakeException => ($"The TLS handshake with {target} failed: {ex.Message}", false),
            SmtpCommandException command => ($"The SMTP server {target} answered: {command.Message}", (int)command.StatusCode is >= 400 and < 500),
            SmtpProtocolException => ($"The SMTP server {target} broke the protocol: {ex.Message}", true),
            SocketException => ($"Could not connect to the SMTP server {target}: {ex.Message}", true),
            IOException => ($"The connection to the SMTP server {target} failed: {ex.Message}", true),
            ServiceNotConnectedException or ServiceNotAuthenticatedException => ($"The SMTP session with {target} ended unexpectedly: {ex.Message}", true),
            _ => ($"Sending through {target} failed: {ex.Message}", true),
        };
        return new EmailSendException(Scrub(text, config), retryable, ex);
    }

    internal static string Scrub(string text, EmailTransportConfig config)
    {
        foreach (var secret in config.SecretValues) text = text.Replace(secret, "********", StringComparison.Ordinal);
        return text.Length <= 900 ? text : text[..900];
    }
}

/// <summary>Resend's HTTPS API (<c>POST https://api.resend.com/emails</c>), authenticated with an API key.</summary>
public sealed class ResendEmailSender(HttpClient http) : IEmailSender
{
    public const string Endpoint = "https://api.resend.com/emails";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SendAsync(EmailTransportConfig config, EmailMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.ResendApiKey)) throw new EmailSendException("No Resend API key is configured.", retryable: false);

        var from = string.IsNullOrWhiteSpace(config.FromName) ? config.FromAddress : $"{config.FromName.Replace("\"", "").Replace("<", "").Replace(">", "")} <{config.FromAddress}>";
        var body = JsonSerializer.Serialize(new { from, to = message.To, subject = message.Subject, html = message.Html, text = message.Text }, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ResendApiKey);
        request.Headers.UserAgent.ParseAdd("Aethera-Notifications/1");

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return;

            var detail = await ErrorTextAsync(response, cancellationToken);
            var status = (int)response.StatusCode;
            var retryable = status is 408 or 429 or >= 500;
            throw new EmailSendException(SmtpEmailSender.Scrub($"Resend answered {status}: {detail}", config), retryable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new EmailSendException(SmtpEmailSender.Scrub("Could not reach the Resend API: " + ex.Message, config), retryable: true, ex);
        }
    }

    private static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = "";
        try { text = await response.Content.ReadAsStringAsync(cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { /* body unreadable: the status says enough */ }
        if (string.IsNullOrWhiteSpace(text)) return response.ReasonPhrase ?? "no details";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? text;
        }
        catch (JsonException) { /* not JSON: show the start of it */ }
        return text.Length <= 300 ? text : text[..300];
    }
}
