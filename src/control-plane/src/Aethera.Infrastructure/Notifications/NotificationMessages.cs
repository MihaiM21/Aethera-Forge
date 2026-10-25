using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aethera.Domain;

namespace Aethera.Infrastructure.Notifications;

/// <summary>Why a message is sent: the alert opened, a reminder while it stays open, it resolved, a plain event, or a test.</summary>
public static class NotificationKinds
{
    public const string Opened = "opened";
    public const string Reminder = "reminder";
    public const string Resolved = "resolved";
    public const string Event = "event";
    public const string Test = "test";
}

/// <summary>
/// The <c>notification.send</c> job payload: one event for one channel. References and display text only, never a secret value.
/// </summary>
public sealed class NotificationPayload
{
    public Guid OrganizationId { get; set; }
    public Guid ChannelId { get; set; }
    public Guid? AlertId { get; set; }

    /// <summary>The event type (<see cref="NotificationEvents"/>).</summary>
    public string Type { get; set; } = "";

    /// <summary>One of <see cref="NotificationKinds"/>.</summary>
    public string Kind { get; set; } = NotificationKinds.Event;

    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;
    public string Title { get; set; } = "";
    public string? Message { get; set; }
    public string? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string>? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A message ready to send: the email forms and the webhook body.</summary>
public sealed record RenderedNotification(string Subject, string Text, string Html, string WebhookJson);

/// <summary>Turns a <see cref="NotificationPayload"/> into mail and webhook bodies. Pure; shared by every channel and by the tests.</summary>
public static partial class NotificationMessageBuilder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    [GeneratedRegex("password|passwd|secret|token|api[-_]?key|credential|private[-_]?key|authorization", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKey();

    public static string SubjectOf(NotificationPayload p)
    {
        var suffix = p.Kind switch
        {
            NotificationKinds.Resolved => " — resolved",
            NotificationKinds.Reminder => " — still open",
            NotificationKinds.Test => " — test",
            _ => "",
        };
        // Header values cannot hold line breaks.
        return ("[Aethera] " + p.Title + suffix).ReplaceLineEndings(" ");
    }

    /// <summary>The absolute link to the resource in the UI, or null when the instance's public URL or the path is unknown.</summary>
    public static string? LinkOf(NotificationPayload p, string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(publicUrl) || string.IsNullOrWhiteSpace(p.Path)) return null;
        return publicUrl.TrimEnd('/') + (p.Path.StartsWith('/') ? p.Path : "/" + p.Path);
    }

    /// <summary>Details with values of sensitive-looking keys masked (a safety net: callers must not pass secrets).</summary>
    public static IReadOnlyDictionary<string, string> SafeDetails(IReadOnlyDictionary<string, string>? details)
    {
        var result = new Dictionary<string, string>();
        if (details is null) return result;
        foreach (var (key, value) in details)
            result[key] = SensitiveKey().IsMatch(key) ? "********" : value;
        return result;
    }

    public static RenderedNotification Render(NotificationPayload p, string? publicUrl)
    {
        var link = LinkOf(p, publicUrl);
        var details = SafeDetails(p.Details);
        var resourceName = details.TryGetValue("resource", out var name) ? name : null;
        var resource = resourceName ?? (p.ResourceType is null ? null : p.ResourceId is { } id ? $"{p.ResourceType} {id}" : p.ResourceType);
        var shown = details.Where(d => d.Key != "resource").ToList();
        var when = p.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        var status = p.Kind switch
        {
            NotificationKinds.Resolved => "Resolved",
            NotificationKinds.Reminder => "Still open",
            NotificationKinds.Test => "Test",
            _ => p.Severity.ToString(),
        };

        var text = new StringBuilder();
        text.AppendLine(p.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(p.Message)) text.AppendLine(p.Message).AppendLine();
        text.AppendLine($"Status:   {status}");
        text.AppendLine($"Event:    {p.Type}");
        if (resource is not null) text.AppendLine($"Resource: {resource}");
        text.AppendLine($"Time:     {when}");
        foreach (var (key, value) in shown) text.AppendLine($"{key}: {value}");
        if (link is not null) text.AppendLine().AppendLine($"Open in Aethera: {link}");
        text.AppendLine().AppendLine("Sent by Aethera. You receive this because a notification channel subscribes to this event.");

        var accent = p.Kind == NotificationKinds.Resolved ? "#3f8f2b" : p.Severity switch
        {
            AlertSeverity.Critical => "#c0392b",
            AlertSeverity.Warning => "#b7791f",
            _ => "#4a5568",
        };
        var html = new StringBuilder();
        html.Append("<!doctype html><html><body style=\"margin:0;padding:24px;background:#f4f5f7;font-family:-apple-system,Segoe UI,Helvetica,Arial,sans-serif;color:#1a1d21\">");
        html.Append("<div style=\"max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #d8dce1\">");
        html.Append($"<div style=\"padding:6px 20px;background:{accent};color:#ffffff;font:12px 'Courier New',monospace;letter-spacing:.08em;text-transform:uppercase\">{E(status)} &middot; {E(p.Type)}</div>");
        html.Append("<div style=\"padding:20px\">");
        html.Append($"<h1 style=\"margin:0 0 12px;font-size:18px;line-height:1.3\">{E(p.Title)}</h1>");
        if (!string.IsNullOrWhiteSpace(p.Message)) html.Append($"<p style=\"margin:0 0 16px;font-size:14px;line-height:1.5\">{E(p.Message)}</p>");
        html.Append("<table style=\"border-collapse:collapse;font-size:13px\">");
        if (resource is not null) Row(html, "Resource", resource);
        Row(html, "Time", when);
        foreach (var (key, value) in shown) Row(html, key, value);
        html.Append("</table>");
        if (link is not null)
            html.Append($"<p style=\"margin:20px 0 0\"><a href=\"{E(link)}\" style=\"display:inline-block;padding:8px 14px;background:#1a1d21;color:#ffffff;text-decoration:none;font-size:13px\">Open in Aethera</a></p>");
        html.Append("</div><div style=\"padding:12px 20px;border-top:1px solid #d8dce1;font-size:12px;color:#6b7280\">Sent by Aethera. You receive this because a notification channel subscribes to this event.</div>");
        html.Append("</div></body></html>");

        var body = new
        {
            @event = p.Type,
            kind = p.Kind,
            severity = p.Severity.ToString().ToLowerInvariant(),
            title = p.Title,
            message = p.Message,
            resource = p.ResourceType is null && resourceName is null ? null : new { type = p.ResourceType, id = p.ResourceId, name = resourceName },
            path = p.Path,
            url = link,
            details = shown.ToDictionary(d => d.Key, d => d.Value),
            alertId = p.AlertId,
            occurredAt = p.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        return new RenderedNotification(SubjectOf(p), text.ToString(), html.ToString(), JsonSerializer.Serialize(body, Json));
    }

    private static void Row(StringBuilder html, string label, string value) =>
        html.Append($"<tr><td style=\"padding:3px 16px 3px 0;color:#6b7280;vertical-align:top\">{E(label)}</td><td style=\"padding:3px 0\">{E(value)}</td></tr>");

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
