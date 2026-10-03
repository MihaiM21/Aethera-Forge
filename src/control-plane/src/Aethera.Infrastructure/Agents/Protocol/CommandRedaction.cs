using System.Collections;
using System.Security.Cryptography;
using Aethera.Agent.V1;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aethera.Infrastructure.Agents.Protocol;

/// <summary>
/// The shared redactor of ADR 0002 ("Secret-handling rules for code"): log, audit and error code pass protocol messages through it.
/// Secrets are found <b>by message type</b> (every <see cref="SecretValue"/>, however nested), never by field name; compose file bodies
/// are reduced to length and hash because users embed secrets in them; opaque proxy configuration is reduced to its size.
/// </summary>
public static class CommandRedaction
{
    public const string Placeholder = "[redacted]";

    private static readonly string SecretValueName = SecretValue.Descriptor.FullName;

    // Fields that carry sensitive free text rather than SecretValue messages.
    private static readonly HashSet<string> SensitiveBodies =
    [
        $"{ComposeProject.Descriptor.FullName}.compose_file",
        $"{ComposeProject.Descriptor.FullName}.override_file",
        $"{ProxyEnsure.Descriptor.FullName}.config",
    ];

    /// <summary>A JSON rendering of <paramref name="message"/> that is safe to log or audit: no secret value, no compose body.</summary>
    public static string ToLogString(IMessage message)
    {
        var copy = Copy(message);
        var textFixes = new List<(string Encoded, string Readable)>();
        Redact(copy, textFixes);
        var json = JsonFormatter.Default.Format(copy);
        foreach (var (encoded, readable) in textFixes) json = json.Replace(encoded, readable, StringComparison.Ordinal); // bytes are printed as base64: show the summary instead
        return json;
    }

    /// <summary>Every secret value inside <paramref name="message"/>, so the log pipeline can mask them if an agent echoes one back.</summary>
    public static IReadOnlyList<string> CollectSecrets(IMessage message)
    {
        var found = new List<string>();
        Walk(message, secret => found.Add(secret.Value));
        return found;
    }

    /// <summary>
    /// Replaces the body of a sensitive text field by its length and a short hash (enough to tell two versions apart, useless to an attacker).
    /// </summary>
    public static string Summarize(string body) =>
        $"[{body.Length} chars, sha256:{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)))[..12].ToLowerInvariant()}]";

    private static IMessage Copy(IMessage message) => message.Descriptor.Parser.ParseFrom(message.ToByteArray());

    private static void Walk(IMessage message, Action<SecretValue> onSecret)
    {
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (field.IsMap) continue; // no map in the protocol holds messages
            if (field.FieldType != FieldType.Message)
                continue;
            var value = field.Accessor.GetValue(message);
            if (field.IsRepeated)
            {
                foreach (var item in (IList)value)
                    if (item is IMessage child) Visit(child, onSecret);
            }
            else if (value is IMessage single)
            {
                Visit(single, onSecret);
            }
        }
    }

    private static void Visit(IMessage child, Action<SecretValue> onSecret)
    {
        if (child is SecretValue secret) onSecret(secret);
        else Walk(child, onSecret);
    }

    private static void Redact(IMessage message, List<(string Encoded, string Readable)> textFixes)
    {
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (field.IsMap) continue;
            var key = $"{message.Descriptor.FullName}.{field.Name}";
            if (SensitiveBodies.Contains(key))
            {
                var current = field.Accessor.GetValue(message);
                var summary = current switch
                {
                    string text when text.Length > 0 => Summarize(text),
                    ByteString bytes when bytes.Length > 0 => $"[{bytes.Length} bytes]",
                    _ => null,
                };
                if (summary is null) continue;
                if (field.FieldType == FieldType.Bytes)
                {
                    var replacement = ByteString.CopyFromUtf8(summary);
                    textFixes.Add((replacement.ToBase64(), summary));
                    field.Accessor.SetValue(message, replacement);
                }
                else
                {
                    field.Accessor.SetValue(message, summary);
                }
                continue;
            }

            if (field.FieldType != FieldType.Message) continue;
            var value = field.Accessor.GetValue(message);
            if (field.IsRepeated)
            {
                foreach (var item in (IList)value)
                    if (item is IMessage child) RedactChild(child, textFixes);
            }
            else if (value is IMessage single)
            {
                if (single is SecretValue) field.Accessor.SetValue(message, new SecretValue { Value = Placeholder });
                else RedactChild(single, textFixes);
            }
        }
    }

    private static void RedactChild(IMessage child, List<(string Encoded, string Readable)> textFixes)
    {
        if (child is SecretValue secret) secret.Value = Placeholder;
        else Redact(child, textFixes);
    }
}
