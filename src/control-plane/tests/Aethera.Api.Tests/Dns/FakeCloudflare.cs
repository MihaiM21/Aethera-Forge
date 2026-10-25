using System.Net;
using System.Text;
using System.Text.Json;
using Aethera.Api.Tests.Resources;
using Aethera.Infrastructure.DnsProviders;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.DnsProviders;

/// <summary>What a fake Cloudflare token may do.</summary>
public sealed record FakeToken(bool UserVerify = true, bool ListZones = true, bool ReadDns = true);

public sealed record FakeRecord(string ZoneId, string Type, string Name, string Content, bool Proxied);

/// <summary>A programmable stand-in for the Cloudflare v4 API (paging, name filter, bearer tokens with different permissions).</summary>
public sealed class FakeCloudflareHandler : HttpMessageHandler
{
    public const string BaseAddress = "https://cloudflare.test/client/v4/";

    public Dictionary<string, FakeToken> Tokens { get; } = new(StringComparer.Ordinal);

    public List<(string Id, string Name)> Zones { get; } = [];

    public List<FakeRecord> Records { get; } = [];

    /// <summary>Every request seen (path and query) and the bearer token it carried.</summary>
    public List<(string Url, string? Token)> Requests { get; } = [];

    /// <summary>When set, every request throws it (network failure).</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, every request answers with this status and a non-JSON body (a proxy error page).</summary>
    public HttpStatusCode? RawStatus { get; set; }

    public void Reset()
    {
        Tokens.Clear();
        Zones.Clear();
        Records.Clear();
        Requests.Clear();
        Failure = null;
        RawStatus = null;
    }

    public int UrlCount(string fragment) => Requests.Count(r => r.Url.Contains(fragment, StringComparison.Ordinal));

    public FakeCloudflareHandler WithZone(string name, string? id = null)
    {
        Zones.Add((id ?? "zone-" + name, name));
        return this;
    }

    public FakeCloudflareHandler WithRecord(string zoneName, string type, string name, string content, bool proxied = false)
    {
        Records.Add(new FakeRecord(Zones.First(z => z.Name == zoneName).Id, type, name, content, proxied));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null;
        Requests.Add((request.RequestUri!.PathAndQuery, token));
        if (Failure is not null) throw Failure;
        if (RawStatus is { } raw)
            return Task.FromResult(new HttpResponseMessage(raw) { Content = new StringContent("<html>bad gateway</html>", Encoding.UTF8, "text/html") });

        if (token is null || !Tokens.TryGetValue(token, out var policy))
            return Task.FromResult(Error(HttpStatusCode.Unauthorized, "Invalid API Token"));

        var path = request.RequestUri.AbsolutePath["/client/v4/".Length..];
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        if (path == "user/tokens/verify")
            return Task.FromResult(policy.UserVerify ? Ok(new { status = "active" }) : Error(HttpStatusCode.Unauthorized, "Invalid API Token"));
        if (path == "zones")
        {
            if (!policy.ListZones) return Task.FromResult(Error(HttpStatusCode.Forbidden, "Authentication error"));
            var zones = Zones.Where(z => query["name"] is not { } n || n == z.Name).Select(z => new { id = z.Id, name = z.Name });
            return Task.FromResult(Paged(zones, query));
        }

        if (path.StartsWith("zones/", StringComparison.Ordinal) && path.EndsWith("/dns_records", StringComparison.Ordinal))
        {
            if (!policy.ReadDns) return Task.FromResult(Error(HttpStatusCode.Forbidden, "Authentication error"));
            var zoneId = path["zones/".Length..^"/dns_records".Length];
            var records = Records.Where(r => r.ZoneId == zoneId && (query["name"] is not { } n || n == r.Name))
                .Select(r => new { type = r.Type, name = r.Name, content = r.Content, proxied = r.Proxied });
            return Task.FromResult(Paged(records, query));
        }

        return Task.FromResult(Error(HttpStatusCode.NotFound, "Route not found"));
    }

    private static HttpResponseMessage Paged<T>(IEnumerable<T> items, System.Collections.Specialized.NameValueCollection query)
    {
        var all = items.ToList();
        var perPage = int.Parse(query["per_page"] ?? "20");
        var page = int.Parse(query["page"] ?? "1");
        var totalPages = Math.Max(1, (all.Count + perPage - 1) / perPage);
        return Json(HttpStatusCode.OK, new
        {
            success = true, errors = Array.Empty<object>(), result = all.Skip((page - 1) * perPage).Take(perPage).ToList(),
            result_info = new { page, per_page = perPage, total_pages = totalPages, count = Math.Min(perPage, Math.Max(0, all.Count - (page - 1) * perPage)), total_count = all.Count },
        });
    }

    private static HttpResponseMessage Ok(object result) => Json(HttpStatusCode.OK, new { success = true, errors = Array.Empty<object>(), result });

    private static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        Json(status, new { success = false, errors = new[] { new { code = 1000, message } }, result = (object?)null });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public CloudflareClient CreateClient() => new(new HttpClient(this) { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(10) });
}

/// <summary>An API factory whose Cloudflare client talks to <see cref="Cloudflare"/> and whose resolver is <see cref="Dns"/>.</summary>
public sealed class DnsFixture : IDisposable
{
    public DnsFixture()
    {
        Base = new AetheraApiFactory();
        Cloudflare = new FakeCloudflareHandler();
        Dns = new FakeDnsResolver();
        Factory = Base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<Aethera.Api.Features.Resources.DomainNames.IDnsResolver>(Dns);
            services.AddSingleton<ICloudflareClient>(Cloudflare.CreateClient());
        }));
    }

    public AetheraApiFactory Base { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeCloudflareHandler Cloudflare { get; }

    public FakeDnsResolver Dns { get; }

    /// <summary>A new organization; the Cloudflare fake starts empty (the tests of the collection run one after the other).</summary>
    public async Task<Tenant> NewTenantAsync()
    {
        Cloudflare.Reset();
        return new Tenant(Factory, await Factory.SeedIdentityAsync(Aethera.Domain.OrganizationRole.Owner));
    }

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class DnsCollection : ICollectionFixture<DnsFixture>
{
    public const string Name = "dns";
}
