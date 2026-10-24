using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Aethera.Infrastructure.Dns;

public sealed class CloudflareOptions
{
    public const string Section = "Aethera:Dns";

    /// <summary>Base address of the Cloudflare API (with a trailing slash). Overridden in tests.</summary>
    public string CloudflareApiBase { get; set; } = "https://api.cloudflare.com/client/v4/";
}

public sealed record CloudflareZone(string Id, string Name);

/// <summary>A DNS record of a zone. <c>Content</c> is the address of an A/AAAA record or the target of a CNAME.</summary>
public sealed record CloudflareDnsRecord(string Type, string Name, string Content, bool Proxied);

/// <summary>Why a Cloudflare call failed.</summary>
public enum CloudflareFailure
{
    Other = 0,
    InvalidToken = 1,
    PermissionDenied = 2,
    Unreachable = 3,
}

/// <summary>A failed Cloudflare call. The message is safe to show (it never contains the token).</summary>
public sealed class CloudflareException(CloudflareFailure failure, string message) : Exception(message)
{
    public CloudflareFailure Failure { get; } = failure;
}

/// <summary>The few Cloudflare API calls Aethera needs. The token is passed per call: one client serves every provider.</summary>
public interface ICloudflareClient
{
    /// <summary><c>GET /user/tokens/verify</c>: whether the token is active. Does not throw for a rejected token.</summary>
    Task<bool> VerifyTokenAsync(string token, CancellationToken ct);

    /// <summary>The zones the token can see (all pages, or only the zone called <paramref name="name"/>).</summary>
    Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(string token, string? name, CancellationToken ct);

    /// <summary>The A, AAAA and CNAME records of a zone, optionally only those whose name is exactly <paramref name="name"/>.</summary>
    Task<IReadOnlyList<CloudflareDnsRecord>> ListDnsRecordsAsync(string token, string zoneId, string? name, CancellationToken ct);
}

/// <summary>Typed <see cref="HttpClient"/> over the Cloudflare v4 API (bearer token; base address and 10 s timeout come from the registration).</summary>
public sealed class CloudflareClient(HttpClient http) : ICloudflareClient
{
    public const int PageSize = 50;
    private const int MaxPages = 100;

    public async Task<bool> VerifyTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync(token, "user/tokens/verify", ct);
            return doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("status", out var status)
                && status.GetString() == "active";
        }
        catch (CloudflareException ex) when (ex.Failure is CloudflareFailure.InvalidToken or CloudflareFailure.PermissionDenied)
        {
            // Account-owned tokens are not verifiable through the user endpoint; the caller falls back to listing zones.
            return false;
        }
    }

    public async Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(string token, string? name, CancellationToken ct)
    {
        var zones = new List<CloudflareZone>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var query = $"zones?per_page={PageSize}&page={page}" + (name is null ? "" : "&name=" + Uri.EscapeDataString(name));
            using var doc = await GetAsync(token, query, ct);
            foreach (var item in Results(doc))
                zones.Add(new CloudflareZone(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!.ToLowerInvariant()));
            if (!HasMore(doc, page)) break;
        }

        return zones;
    }

    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListDnsRecordsAsync(string token, string zoneId, string? name, CancellationToken ct)
    {
        var records = new List<CloudflareDnsRecord>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var query = $"zones/{Uri.EscapeDataString(zoneId)}/dns_records?per_page=100&page={page}" + (name is null ? "" : "&name=" + Uri.EscapeDataString(name));
            using var doc = await GetAsync(token, query, ct);
            foreach (var item in Results(doc))
            {
                var type = item.GetProperty("type").GetString()!;
                if (type is not ("A" or "AAAA" or "CNAME")) continue;
                records.Add(new CloudflareDnsRecord(type, item.GetProperty("name").GetString()!.ToLowerInvariant(), item.GetProperty("content").GetString() ?? "",
                    item.TryGetProperty("proxied", out var proxied) && proxied.ValueKind == JsonValueKind.True));
            }

            if (!HasMore(doc, page)) break;
        }

        return records;
    }

    private static IEnumerable<JsonElement> Results(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array ? result.EnumerateArray() : [];

    private static bool HasMore(JsonDocument doc, int page) =>
        doc.RootElement.TryGetProperty("result_info", out var info) && info.TryGetProperty("total_pages", out var total) && total.TryGetInt32(out var pages)
        && page < pages;

    private async Task<JsonDocument> GetAsync(string token, string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new CloudflareException(CloudflareFailure.Unreachable, "Cloudflare could not be reached.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudflareException(CloudflareFailure.Unreachable, "Cloudflare did not answer in time.");
        }

        using (response)
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException)
            {
                // Not JSON (a proxy error page): the status code decides below.
            }

            var success = doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            if (response.IsSuccessStatusCode && success) return doc!;

            var message = doc is null ? null : FirstError(doc);
            doc?.Dispose();
            var failure = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => CloudflareFailure.InvalidToken,
                HttpStatusCode.Forbidden => CloudflareFailure.PermissionDenied,
                _ when message is not null && message.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("token", StringComparison.OrdinalIgnoreCase) => CloudflareFailure.InvalidToken,
                _ => CloudflareFailure.Other,
            };
            throw new CloudflareException(failure, message ?? $"Cloudflare answered with HTTP {(int)response.StatusCode}.");
        }
    }

    private static string? FirstError(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var error in errors.EnumerateArray())
            if (error.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                return text.Length > 300 ? text[..300] : text;
        return null;
    }
}
