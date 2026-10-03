using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;

namespace Aethera.Api.Http;

/// <summary>JSON conventions of ADR 0003: camelCase properties (dictionary keys verbatim), camelCase enum strings, nulls kept, RFC 3339 UTC timestamps with a <c>Z</c>.</summary>
public static class JsonConventions
{
    public static IServiceCollection AddAetheraJson(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(options => Configure(options.SerializerOptions));

    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null; // keys are data (NODE_ENV, X-Custom): kept verbatim, never camel-cased
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never; // declared properties are always present; "no value" is null
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new UtcDateTimeOffsetJsonConverter());
        options.Converters.Add(new UtcDateTimeJsonConverter());
    }

    /// <summary>Options for tests and tools that must serialize exactly like the API.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }
}

/// <summary>Writes <c>2026-10-03T14:07:31.482Z</c> (millisecond precision, UTC); reads any RFC 3339 value and normalises it to UTC.</summary>
public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    internal const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            return value;
        throw new JsonException("Expected an RFC 3339 timestamp such as 2026-10-03T14:07:31.482Z.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}

/// <summary>Same for <see cref="DateTime"/> (treated as UTC when unspecified).</summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            return value.UtcDateTime;
        throw new JsonException("Expected an RFC 3339 timestamp such as 2026-10-03T14:07:31.482Z.");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        writer.WriteStringValue(utc.ToString(UtcDateTimeOffsetJsonConverter.Format, CultureInfo.InvariantCulture));
    }
}
