using System.Text.RegularExpressions;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Notifications;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Notifications;

/// <summary>
/// The organization's email settings. Passwords and API keys are write-only managed secrets: <c>hasSmtpPassword</c> / <c>hasResendApiKey</c> only
/// say whether one is stored. Before the first save <c>configured</c> is false and the other fields are the defaults.
/// </summary>
public sealed record EmailSettingsResponse(
    bool Configured, bool Enabled, EmailTransport Transport, string? SmtpHost, int? SmtpPort, SmtpEncryption SmtpEncryption, string? SmtpUsername,
    bool HasSmtpPassword, bool HasResendApiKey, string? FromAddress, string? FromName, DateTimeOffset? LastTestedAt, string? LastTestError, DateTimeOffset? UpdatedAt);

/// <summary>
/// Replaces the settings. <c>smtpPassword</c> and <c>resendApiKey</c>: absent or null keeps the stored secret, a value replaces it, an empty
/// string removes it. Setting one needs the <c>secrets:write</c> scope (the <c>write</c> scope never covers a credential).
/// </summary>
public sealed record UpdateEmailSettingsRequest
{
    public bool Enabled { get; init; }
    public string? Transport { get; init; }
    public string? SmtpHost { get; init; }
    public int? SmtpPort { get; init; }
    public string? SmtpEncryption { get; init; }
    public string? SmtpUsername { get; init; }
    public string? SmtpPassword { get; init; }
    public string? ResendApiKey { get; init; }
    public string? FromAddress { get; init; }
    public string? FromName { get; init; }
}

/// <summary>Result of a test: <c>ok</c> false carries the error text of the SMTP server or Resend (never a secret).</summary>
public sealed record EmailTestResponse(bool Ok, string? Error, string SentTo, DateTimeOffset TestedAt);

public static partial class NotificationRules
{
    [GeneratedRegex(@"\A[^@\s<>"",;()\[\]\\]{1,64}@[^@\s<>"",;()\[\]\\]{1,255}\.[^@\s<>"",;()\[\]\\.]{2,63}\z")]
    private static partial Regex EmailPattern();

    public static bool IsEmail(string? value) => value is { Length: <= 320 } && EmailPattern().IsMatch(value);
}

public sealed class UpdateEmailSettingsValidator : AbstractValidator<UpdateEmailSettingsRequest>
{
    public UpdateEmailSettingsValidator()
    {
        RuleFor(x => x.Transport).NotEmpty().MustBeEnum<UpdateEmailSettingsRequest, EmailTransport>();
        RuleFor(x => x.FromAddress).NotEmpty().Must(NotificationRules.IsEmail).WithErrorCode("email").WithMessage("Must be an email address such as aethera@example.com.")
            .When(x => !string.IsNullOrEmpty(x.FromAddress));
        RuleFor(x => x.FromAddress).NotEmpty();
        RuleFor(x => x.FromName).MaximumLength(200).Must(n => n is null || !n.Any(char.IsControl)).WithErrorCode("pattern").WithMessage("Must not contain control characters.");
        RuleFor(x => x.SmtpHost).MaximumLength(253).Must(h => h is null || !h.Any(c => char.IsWhiteSpace(c) || c is '/' or '@')).WithErrorCode("pattern")
            .WithMessage("Must be a host name or address.");
        RuleFor(x => x.SmtpHost).NotEmpty().When(x => x.Enabled && EnumText.Parse<EmailTransport>(x.Transport) == EmailTransport.Smtp)
            .WithMessage("An SMTP host is required to enable SMTP.");
        RuleFor(x => x.SmtpPort).InclusiveBetween(1, 65535).When(x => x.SmtpPort is not null);
        RuleFor(x => x.SmtpEncryption).MustBeEnum<UpdateEmailSettingsRequest, SmtpEncryption>();
        RuleFor(x => x.SmtpUsername).MaximumLength(320);
        RuleFor(x => x.SmtpPassword).MaximumLength(SecretRules.MaxValueLength);
        RuleFor(x => x.ResendApiKey).MaximumLength(SecretRules.MaxValueLength);
    }
}

internal static class EmailEndpoints
{
    private const string SmtpPasswordSecret = "notifications/smtp-password";
    private const string ResendKeySecret = "notifications/resend-api-key";

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/notifications/email").WithTags("Notifications");
        group.MapGet("/", Get).WithName("getEmailSettings").RequireAdmin(Scopes.Read);
        group.MapPut("/", Update).WithName("updateEmailSettings").Validate<UpdateEmailSettingsRequest>().RequireAdmin();
        group.MapPost("/test", Test).WithName("testEmailSettings").RequireAdmin();
    }

    private static async Task<Ok<EmailSettingsResponse>> Get(HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var settings = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == actor.Org(), ct);
        if (settings is not null) http.SetETag(settings.RowVersion);
        return TypedResults.Ok(ToResponse(settings));
    }

    private static async Task<Ok<EmailSettingsResponse>> Update(
        UpdateEmailSettingsRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        var transport = EnumText.Parse<EmailTransport>(request.Transport)!.Value;
        var encryption = EnumText.Parse<SmtpEncryption>(request.SmtpEncryption) ?? SmtpEncryption.StartTls;
        if (request.SmtpPassword is not null || request.ResendApiKey is not null) ManagedSecrets.RequireTokenScope(actor);

        var settings = await db.EmailSettings.FirstOrDefaultAsync(s => s.OrganizationId == org, ct);
        if (settings is not null) http.CheckIfMatch(settings.RowVersion);
        var created = settings is null;
        settings ??= new EmailSettings { OrganizationId = org, FromAddress = request.FromAddress!.Trim() };
        if (created) db.EmailSettings.Add(settings);

        settings.Enabled = request.Enabled;
        settings.Transport = transport;
        settings.SmtpHost = Blank(request.SmtpHost);
        settings.SmtpPort = request.SmtpPort;
        settings.SmtpEncryption = encryption;
        settings.SmtpUsername = Blank(request.SmtpUsername);
        settings.FromAddress = request.FromAddress!.Trim();
        settings.FromName = Blank(request.FromName);

        var changed = new List<string> { "settings" };
        settings.SmtpPasswordSecretId = await ApplySecretAsync(db, vault, clock, org, settings.SmtpPasswordSecretId, request.SmtpPassword, SmtpPasswordSecret,
            "The SMTP password of the notification email settings.", changed, "smtpPassword", ct);
        settings.ResendApiKeySecretId = await ApplySecretAsync(db, vault, clock, org, settings.ResendApiKeySecretId, request.ResendApiKey, ResendKeySecret,
            "The Resend API key of the notification email settings.", changed, "resendApiKey", ct);

        if (request.Enabled && transport == EmailTransport.Resend && settings.ResendApiKeySecretId is null)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/resendApiKey", "required", "A Resend API key is required to enable Resend.")]));

        await audit.RecordAsync("notifications.email_updated", "email_settings", settings.Id,
            new { transport = transport.ToString().ToLowerInvariant(), enabled = settings.Enabled, host = settings.SmtpHost, changed }, ct);
        http.SetETag(settings.RowVersion);
        return TypedResults.Ok(ToResponse(settings));
    }

    private static async Task<Ok<EmailTestResponse>> Test(AetheraDbContext db, ICurrentActor actor, NotificationDispatcher dispatcher, IOptions<NotificationOptions> options, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        var settings = await db.EmailSettings.FirstOrDefaultAsync(s => s.OrganizationId == org, ct)
            ?? throw new ApiProblemException(ApiProblems.Conflict("notifications.email_not_configured", "Save the email settings first."));
        var email = actor.UserId is { } userId ? await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct) : null;
        if (email is null)
            throw new ApiProblemException(ApiProblems.Conflict("notifications.no_test_recipient", "The caller has no email address to send the test to."));

        var now = clock.UtcNow;
        var rendered = NotificationMessageBuilder.Render(new NotificationPayload
        {
            OrganizationId = org, Type = "test", Kind = NotificationKinds.Test, Severity = AlertSeverity.Info, Title = "Test email",
            Message = "If you can read this, Aethera can send email through the settings you saved.", OccurredAt = now, Path = "/settings",
        }, await Infrastructure.Deployments.ProxySettingsStore.PublicUrlAsync(db, options.Value.PublicUrl, ct));
        var result = await dispatcher.SendEmailAsync(org, new EmailMessage(rendered.Subject, rendered.Text, rendered.Html, [email]), requireEnabled: false, ct);

        settings.LastTestedAt = now;
        settings.LastTestError = result.Ok ? null : Cut(result.Error ?? "The test email could not be sent.", 1000);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new EmailTestResponse(result.Ok, result.Ok ? null : settings.LastTestError, email, now));
    }

    /// <summary>Null keeps the secret, a value stores a new version (creating the secret on first use), an empty string removes it.</summary>
    private static async Task<Guid?> ApplySecretAsync(
        AetheraDbContext db, SecretVault vault, IClock clock, Guid org, Guid? current, string? value, string name, string description, List<string> changed, string field, CancellationToken ct)
    {
        if (value is null) return current;
        changed.Add(field);
        if (value.Length == 0)
        {
            if (current is { } existing) (await db.Secrets.FirstAsync(s => s.Id == existing, ct)).MarkDeleted(clock.UtcNow);
            return null;
        }

        if (current is { } id)
        {
            vault.AddVersion(await db.Secrets.FirstAsync(s => s.Id == id, ct), value);
            return id;
        }

        // Someone may have made an ordinary secret with this name: the managed one then carries a suffix.
        var taken = await db.SecretsOf(org).AnyAsync(s => s.Name == name && s.ProjectId == null && s.EnvironmentId == null && s.WorkloadId == null, ct);
        return vault.Create(org, taken ? $"{name}-{Guid.NewGuid().ToString("N")[..6]}" : name, description, value, purpose: SecretPurpose.NotificationCredential).Id;
    }

    internal static EmailSettingsResponse ToResponse(EmailSettings? s) => s is null
        ? new EmailSettingsResponse(false, false, EmailTransport.Smtp, null, null, SmtpEncryption.StartTls, null, false, false, null, "Aethera", null, null, null)
        : new EmailSettingsResponse(true, s.Enabled, s.Transport, s.SmtpHost, s.SmtpPort, s.SmtpEncryption, s.SmtpUsername, s.SmtpPasswordSecretId is not null,
            s.ResendApiKeySecretId is not null, s.FromAddress, s.FromName, s.LastTestedAt, s.LastTestError, s.UpdatedAt);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
