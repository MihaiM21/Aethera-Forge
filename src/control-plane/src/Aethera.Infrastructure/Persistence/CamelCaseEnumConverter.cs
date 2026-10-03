using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aethera.Infrastructure.Persistence;

/// <summary>
/// Stores enums as camelCase strings (<c>InProgress</c> -> <c>inProgress</c>). This matches the API's JSON enum casing
/// (ADR 0003) and the literals in the job-claim SQL of ADR 0004 (<c>status = 'queued'</c>).
/// </summary>
public sealed class CamelCaseEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(v => EnumNames<TEnum>.ToDb(v), s => EnumNames<TEnum>.FromDb(s))
    where TEnum : struct, Enum;

internal static class EnumNames<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToName = Enum.GetValues<TEnum>().ToDictionary(v => v, v => CamelCase(v.ToString()));
    private static readonly Dictionary<string, TEnum> FromName = ToName.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string ToDb(TEnum value) => ToName.TryGetValue(value, out var name) ? name : CamelCase(value.ToString());

    public static TEnum FromDb(string value) =>
        FromName.TryGetValue(value, out var v) ? v : Enum.Parse<TEnum>(value, ignoreCase: true);

    private static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
