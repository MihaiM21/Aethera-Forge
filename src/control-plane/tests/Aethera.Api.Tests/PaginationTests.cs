using System.Net;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Domain;

namespace Aethera.Api.Tests;

[Collection(TestApiCollection.Name)]
public sealed class PaginationTests(TestApiFixture fixture)
{
    private static readonly byte[] Key = "0123456789abcdef0123456789abcdef"u8.ToArray();
    private static readonly KeysetPosition Position = new(["2026-10-03T14:07:31.4820000Z", null], Guid.Parse("0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55"));

    private HttpClient Client() => fixture.Factory.CreateClientAs(OrganizationRole.Viewer);

    [Fact]
    public void Cursor_RoundTrips_AndIsUrlSafe()
    {
        var codec = new KeysetCursor(Key);
        var cursor = codec.Encode(Position, "-createdAt");

        Assert.Matches("^[A-Za-z0-9_.-]+$", cursor);
        var decoded = codec.Decode(cursor, "-createdAt");
        Assert.Equal(Position.SortValues, decoded.SortValues);
        Assert.Equal(Position.Id, decoded.Id);
    }

    [Fact]
    public void Cursor_WithOtherSortOrFilters_IsRejected()
    {
        var codec = new KeysetCursor(Key);
        var cursor = codec.Encode(Position, "-createdAt|status=running");

        Assert.False(codec.TryDecode(cursor, "name|status=running", out _));
        Assert.False(codec.TryDecode(cursor, "-createdAt|status=failed", out _));
    }

    [Fact]
    public void Cursor_FromAnotherKey_IsRejected()
    {
        var cursor = new KeysetCursor(Key).Encode(Position, "x");
        Assert.False(new KeysetCursor("ffffffffffffffffffffffffffffffff"u8.ToArray()).TryDecode(cursor, "x", out _));
    }

    [Fact]
    public void Cursor_TamperedPayload_IsRejected()
    {
        var codec = new KeysetCursor(Key);
        var cursor = codec.Encode(Position, "x");
        var dot = cursor.IndexOf('.');
        var forged = System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes("{\"V\":[\"1900-01-01\"],\"I\":\"00000000-0000-0000-0000-000000000000\",\"C\":\"x\"}"));

        Assert.False(codec.TryDecode(forged + cursor[dot..], "x", out _));
        Assert.False(codec.TryDecode(cursor[..^2] + "AA", "x", out _));
        Assert.Throws<ApiProblemException>(() => codec.Decode(cursor[..^2] + "AA", "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData(".")]
    [InlineData("a.b")]
    [InlineData("%%%.%%%")]
    public void Cursor_Garbage_IsRejectedWithoutThrowingOtherExceptions(string cursor)
    {
        var codec = new KeysetCursor(Key);
        Assert.False(codec.TryDecode(cursor, "x", out _));
        Assert.Equal(ProblemCodes.InvalidCursor, Assert.Throws<ApiProblemException>(() => codec.Decode(cursor, "x")).Problem.Code);
    }

    [Fact]
    public async Task Endpoint_TamperedCursor_Is400InvalidCursor()
    {
        var first = await (await Client().GetAsync("/api/v1/_test/page?limit=2")).ReadJsonAsync();
        var cursor = first.Str("nextCursor");
        Assert.NotEmpty(cursor);

        var ok = await Client().GetAsync($"/api/v1/_test/page?limit=2&cursor={cursor}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var tampered = await Client().GetAsync($"/api/v1/_test/page?limit=2&cursor={cursor[..^3]}AAA");
        tampered.AssertProblem(await tampered.ReadJsonAsync(), 400, "pagination.invalid_cursor");

        var otherSort = await Client().GetAsync($"/api/v1/_test/page?limit=2&cursor={cursor}&sort=name");
        otherSort.AssertProblem(await otherSort.ReadJsonAsync(), 400, "pagination.invalid_cursor");
    }

    [Fact]
    public async Task Endpoint_DefaultsToLimit50_AndReturnsTheItemsNextCursorShape()
    {
        var body = await (await Client().GetAsync("/api/v1/_test/page")).ReadJsonAsync();
        Assert.Equal(50, body["items"]!.AsArray().Count);
        Assert.NotNull(body["nextCursor"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("-5")]
    public async Task Endpoint_LimitOutOfRange_Is400InvalidParameter(string limit)
    {
        var response = await Client().GetAsync($"/api/v1/_test/page?limit={limit}");
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 400, "validation.invalid_parameter");
        Assert.Equal("limit", body["errors"]![0]!.Str("parameter"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("200")]
    public async Task Endpoint_LimitBounds_AreInclusive(string limit) =>
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync($"/api/v1/_test/page?limit={limit}")).StatusCode);

    [Fact]
    public async Task Endpoint_NonNumericLimit_Is400()
    {
        var response = await Client().GetAsync("/api/v1/_test/page?limit=abc");
        response.AssertProblem(await response.ReadJsonAsync(), 400, "request.malformed");
    }

    [Fact]
    public void Sort_ParsesAllowlistedFieldsCaseInsensitively()
    {
        var sort = SortSpec.Parse("-CREATEDAT, name", ["createdAt", "name"], "-createdAt");
        Assert.Equal([new SortField("createdAt", true), new SortField("name", false)], sort.Fields);
        Assert.Equal("-createdAt,name", sort.Canonical);
    }

    [Fact]
    public void Sort_UsesTheDefaultWhenMissing() =>
        Assert.Equal("-createdAt", SortSpec.Parse(null, ["createdAt"], "-createdAt").Canonical);

    [Theory]
    [InlineData("password")]
    [InlineData("name,name")]
    [InlineData("name,,createdAt")]
    [InlineData("-")]
    public void Sort_RejectsUnknownRepeatedOrEmptyFields(string sort)
    {
        var ex = Assert.Throws<ApiProblemException>(() => SortSpec.Parse(sort, ["createdAt", "name"], "-createdAt"));
        Assert.Equal(400, ex.Problem.Status);
        Assert.Equal(ProblemCodes.InvalidParameter, ex.Problem.Code);
    }

    [Fact]
    public async Task Endpoint_UnknownSortField_Is400()
    {
        var response = await Client().GetAsync("/api/v1/_test/page?sort=passwordHash");
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 400, "validation.invalid_parameter");
        Assert.Equal("sort", body["errors"]![0]!.Str("parameter"));
    }

    [Fact]
    public void Page_FromOverfetch_TrimsAndSetsTheCursorOnlyWhenThereIsMore()
    {
        var more = Page<int>.FromOverfetch([1, 2, 3], 2, i => "after" + i);
        Assert.Equal([1, 2], more.Items);
        Assert.Equal("after2", more.NextCursor);

        var last = Page<int>.FromOverfetch([1, 2], 2, i => "after" + i);
        Assert.Equal([1, 2], last.Items);
        Assert.Null(last.NextCursor);
    }
}
