using Aethera.Api.Http.Errors;

namespace Aethera.Api.Http.Pagination;

/// <summary>One field of <c>?sort=</c>.</summary>
public sealed record SortField(string Name, bool Descending);

/// <summary>
/// The sort order of a list endpoint: a parsed <c>?sort=-createdAt,name</c>. The allowlist is per resource and contains only indexed
/// properties. Keyset queries must append <c>id</c> as the last tiebreaker themselves.
/// </summary>
public sealed class SortSpec
{
    private SortSpec(IReadOnlyList<SortField> fields) => Fields = fields;

    public IReadOnlyList<SortField> Fields { get; }

    /// <summary>Normalised text (<c>-createdAt,name</c>), used as part of the cursor context.</summary>
    public string Canonical => string.Join(',', Fields.Select(f => (f.Descending ? "-" : "") + f.Name));

    /// <summary>
    /// Parses <paramref name="sort"/> against <paramref name="allowed"/> (property names, matched case-insensitively and returned in
    /// their canonical casing). Null or empty gives <paramref name="defaultSort"/>.
    /// </summary>
    /// <exception cref="ApiProblemException">400 <c>validation.invalid_parameter</c> for an unknown field, a repeated field or empty entries.</exception>
    public static SortSpec Parse(string? sort, IReadOnlyCollection<string> allowed, string defaultSort)
    {
        var text = string.IsNullOrWhiteSpace(sort) ? defaultSort : sort;
        var fields = new List<SortField>();
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var descending = raw.StartsWith('-');
            var name = descending || raw.StartsWith('+') ? raw[1..] : raw;
            var canonical = allowed.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
                throw new ApiProblemException(ApiProblems.InvalidParameter(
                    "sort", $"Unknown sort field '{name}'. Allowed: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.", "invalid_sort"));
            if (fields.Any(f => f.Name == canonical))
                throw new ApiProblemException(ApiProblems.InvalidParameter("sort", $"Sort field '{canonical}' is repeated.", "invalid_sort"));
            fields.Add(new SortField(canonical, descending));
        }

        return new SortSpec(fields);
    }
}
