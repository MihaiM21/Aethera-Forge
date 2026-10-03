using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aethera.Infrastructure.Auditing;

/// <summary>
/// Removes sensitive values from audit metadata. Any object member whose name contains <c>password</c>, <c>secret</c>,
/// <c>token</c>, <c>key</c> or <c>value</c> (case-insensitive) has its value replaced by <see cref="Placeholder"/>, at any depth,
/// including inside arrays. Names are matched, not values, so this is a safety net: callers must not put secrets in metadata.
/// </summary>
public static partial class Redaction
{
    public const string Placeholder = "[redacted]";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [GeneratedRegex("password|secret|token|key|value", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();

    /// <summary>True when a property name is treated as sensitive.</summary>
    public static bool IsSensitiveName(string name) => SensitiveName().IsMatch(name);

    /// <summary>Returns a redacted JSON copy of <paramref name="metadata"/>; <c>null</c> for null input.</summary>
    public static JsonNode? Redact(object? metadata)
    {
        if (metadata is null) return null;
        var node = metadata as JsonNode is { } given ? given.DeepClone() : JsonSerializer.SerializeToNode(metadata, Json);
        return RedactNode(node);
    }

    /// <summary>
    /// Serializes redacted metadata for the <c>metadata</c> jsonb column. Always a JSON object: null becomes <c>{}</c>, a
    /// non-object root (string, number, array) is stored under <c>data</c>, unserializable input under <c>error</c>.
    /// </summary>
    public static string ToJsonObject(object? metadata)
    {
        try
        {
            var node = Redact(metadata);
            return node switch
            {
                null => "{}",
                JsonObject obj => obj.ToJsonString(),
                _ => new JsonObject { ["data"] = node }.ToJsonString(),
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new JsonObject { ["error"] = "metadata could not be serialized" }.ToJsonString();
        }
    }

    /// <summary>Redacts in place (the tree is always a private copy).</summary>
    private static JsonNode? RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IsSensitiveName(name)) obj[name] = JsonValue.Create(Placeholder);
                    else RedactNode(obj[name]);
                }

                break;
            case JsonArray array:
                foreach (var item in array) RedactNode(item);
                break;
        }

        return node;
    }
}
