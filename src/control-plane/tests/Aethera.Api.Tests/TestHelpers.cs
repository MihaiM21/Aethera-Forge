using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aethera.Api.Tests;

internal static class TestHelpers
{
    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body: " + text);
    }

    public static string Str(this JsonNode? node, string name) => node?[name]?.GetValue<string>() ?? "";

    public static void AssertProblem(this HttpResponseMessage response, JsonNode body, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, body["status"]?.GetValue<int>());
        Assert.Equal(code, body.Str("code"));
        Assert.Equal("urn:aethera:problem:" + code, body.Str("type"));
        Assert.False(string.IsNullOrWhiteSpace(body.Str("title")));
        Assert.False(string.IsNullOrWhiteSpace(body.Str("traceId")));
        Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), body.Str("traceId"));
    }
}
