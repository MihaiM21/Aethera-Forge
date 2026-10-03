using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aethera.Api.Http.Errors;

namespace Aethera.Api.Http.Pagination;

/// <summary>Where a keyset page ended: the sort-column values of the last row, then its id (the final tiebreaker).</summary>
/// <param name="SortValues">One entry per sort field, invariant-formatted (see <see cref="KeysetCursor.Format(DateTimeOffset)"/>); null for SQL NULL.</param>
public sealed record KeysetPosition(IReadOnlyList<string?> SortValues, Guid Id);

/// <summary>
/// Encodes and decodes opaque list cursors: URL-safe base64 of <c>{v: sort values, i: id, c: context}</c>, a dot, and an
/// HMAC-SHA256 over the payload. Clients must treat the result as opaque; a modified or foreign cursor, or one produced for
/// a different <c>context</c> (sort and filters), is rejected as <c>400 pagination.invalid_cursor</c>.
/// </summary>
/// <remarks>
/// The key comes from <c>Aethera:Pagination:CursorKey</c>. If it is not configured a random per-process key is used, so cursors
/// stop working after a restart (clients simply start the list again).
/// </remarks>
public sealed class KeysetCursor
{
    private readonly byte[] _key;

    public KeysetCursor(byte[]? key = null) => _key = key is { Length: >= 16 } ? key : RandomNumberGenerator.GetBytes(32);

    /// <param name="position">Keyset position of the last returned row.</param>
    /// <param name="context">
    /// What the cursor is valid for: the canonical sort (<c>SortSpec.Canonical</c>) plus any filters, e.g.
    /// <c>"-createdAt,id|status=running"</c>. Pass the same string to <see cref="Decode"/>.
    /// </param>
    public string Encode(KeysetPosition position, string context)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(position.SortValues, position.Id, ContextHash(context)));
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(Sign(payload));
    }

    /// <summary>Decodes a cursor produced by <see cref="Encode"/> for the same <paramref name="context"/>.</summary>
    /// <exception cref="ApiProblemException">400 <c>pagination.invalid_cursor</c></exception>
    public KeysetPosition Decode(string cursor, string context) =>
        TryDecode(cursor, context, out var position) ? position : throw new ApiProblemException(ApiProblems.InvalidCursor());

    public bool TryDecode(string cursor, string context, out KeysetPosition position)
    {
        position = default!;
        var dot = cursor.IndexOf('.');
        if (dot <= 0 || dot == cursor.Length - 1 || cursor.Length > 4096) return false;

        try
        {
            var payload = Base64Url.DecodeFromChars(cursor.AsSpan(0, dot));
            var signature = Base64Url.DecodeFromChars(cursor.AsSpan(dot + 1));
            if (!CryptographicOperations.FixedTimeEquals(Sign(payload), signature)) return false;

            var parsed = JsonSerializer.Deserialize<Payload>(payload);
            if (parsed is null || parsed.V is null || parsed.C != ContextHash(context)) return false;
            position = new KeysetPosition(parsed.V, parsed.I);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static string Format(Guid value) => value.ToString("D");

    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private byte[] Sign(ReadOnlySpan<byte> payload) => HMACSHA256.HashData(_key, payload);

    private static string ContextHash(string context) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(context))[..12]);

    private sealed record Payload(IReadOnlyList<string?> V, Guid I, string C);
}
