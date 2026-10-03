namespace Aethera.Api.Http.Pagination;

/// <summary>Query parameters of every list endpoint: <c>?limit=&amp;cursor=</c> (ADR 0003 section 2).</summary>
/// <param name="Limit">1 to <see cref="MaxLimit"/>, default <see cref="DefaultLimit"/>.</param>
/// <param name="Cursor">Opaque value from a previous <see cref="Page{T}.NextCursor"/>.</param>
/// <remarks>
/// Bind with <c>([AsParameters] PageRequest page)</c> and call <see cref="Validated"/> first; it throws an
/// <c>ApiProblemException</c> (400 <c>validation.invalid_parameter</c>) for an out-of-range limit.
/// </remarks>
public sealed record PageRequest(int Limit = PageRequest.DefaultLimit, string? Cursor = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public PageRequest Validated()
    {
        if (Limit is < 1 or > MaxLimit)
            throw new Errors.ApiProblemException(Errors.ApiProblems.InvalidParameter("limit", $"Must be between 1 and {MaxLimit}.", "range"));
        return string.IsNullOrEmpty(Cursor) ? this with { Cursor = null } : this;
    }
}

/// <summary>A page of a collection. <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    /// <summary>
    /// Builds a page from a query that fetched <c>limit + 1</c> rows: when the extra row is present there is a next page and
    /// <paramref name="cursorOf"/> encodes the position of the last row that is returned.
    /// </summary>
    public static Page<T> FromOverfetch(IReadOnlyList<T> fetched, int limit, Func<T, string> cursorOf)
    {
        if (fetched.Count <= limit) return new Page<T>(fetched, null);
        var items = fetched.Take(limit).ToList();
        return new Page<T>(items, cursorOf(items[^1]));
    }
}
