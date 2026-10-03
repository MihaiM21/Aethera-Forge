using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Http;
using Aethera.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Resources;

/// <summary>One API factory (and, when AETHERA_TEST_DB is set, one database) shared by the resource tests. Each test makes its own tenant.</summary>
public sealed class ResourcesFixture : IDisposable
{
    public ResourcesFixture()
    {
        Base = new AetheraApiFactory();
        Dns = new FakeDnsResolver();
        Factory = Base.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<Aethera.Api.Features.Resources.DomainNames.IDnsResolver>(Dns)));
    }

    public AetheraApiFactory Base { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeDnsResolver Dns { get; }

    public async Task<Tenant> NewTenantAsync(OrganizationRole seededRole = OrganizationRole.Owner) =>
        new(Factory, await Factory.SeedIdentityAsync(seededRole));

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class ResourcesCollection : ICollectionFixture<ResourcesFixture>
{
    public const string Name = "resources";
}

public sealed class FakeDnsResolver : Aethera.Api.Features.Resources.DomainNames.IDnsResolver
{
    private readonly Dictionary<string, System.Net.IPAddress[]> _records = new(StringComparer.OrdinalIgnoreCase);

    public Exception? Failure { get; set; }

    public void Set(string hostname, params string[] addresses) => _records[hostname] = addresses.Select(System.Net.IPAddress.Parse).ToArray();

    public Task<IReadOnlyList<System.Net.IPAddress>> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        if (Failure is not null) throw Failure;
        return Task.FromResult<IReadOnlyList<System.Net.IPAddress>>(_records.TryGetValue(hostname, out var found) ? found : []);
    }
}

/// <summary>An organization with clients for each role, and shortcuts to create resources through the API.</summary>
public sealed class Tenant(WebApplicationFactory<Program> factory, SeededIdentity identity)
{
    public SeededIdentity Identity { get; } = identity;

    public WebApplicationFactory<Program> Factory { get; } = factory;

    public HttpClient Owner => As(OrganizationRole.Owner);

    public HttpClient Admin => As(OrganizationRole.Admin);

    public HttpClient Developer => As(OrganizationRole.Developer);

    public HttpClient Viewer => As(OrganizationRole.Viewer);

    public HttpClient As(OrganizationRole role) => Factory.CreateClientAs(role, identity: Identity);

    /// <summary>An API token of a user with <paramref name="role"/> carrying exactly <paramref name="scopes"/>.</summary>
    public HttpClient Token(OrganizationRole role, params string[] scopes) => Factory.CreateClientAs(role, scopes, Identity);

    /// <summary>Audit events of this organization, optionally for one resource and/or action.</summary>
    public async Task<List<AuditEvent>> AuditAsync(string? resourceId = null, string? action = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Aethera.Infrastructure.Persistence.AetheraDbContext>();
        var rid = resourceId is null ? (Guid?)null : Guid.Parse(resourceId);
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.AuditEvents.Where(e => e.OrganizationId == Identity.OrganizationId && (rid == null || e.ResourceId == rid) && (action == null || e.Action == action)));
    }

    // ---- creation shortcuts (as Admin; failing loudly if the API refuses) -----------------------------------------------------------

    public static string Unique(string prefix = "x") => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    public async Task<JsonNode> CreateServerAsync(string? name = null, string? publicIp = null)
    {
        var body = new { name = name ?? Unique("srv"), host = Unique("host") + ".example.com", publicIp, roles = new[] { "master", "worker" } };
        return await Admin.CreateAsync("/api/v1/servers", body);
    }

    public async Task<JsonNode> CreateProjectAsync(string? name = null) =>
        await Developer.CreateAsync("/api/v1/projects", new { name = name ?? Unique("proj") });

    public async Task<(JsonNode Project, string EnvironmentId)> CreateProjectWithEnvironmentAsync()
    {
        var project = await CreateProjectAsync();
        return (project, project["environments"]![0]!["id"]!.GetValue<string>());
    }

    public async Task<JsonNode> CreateApplicationAsync(string environmentId, string serverId, string? name = null, object? extra = null)
    {
        var body = new JsonObject
        {
            ["name"] = name ?? Unique("app"), ["environmentId"] = environmentId, ["serverId"] = serverId, ["sourceKind"] = "dockerImage",
            ["image"] = new JsonObject { ["image"] = "nginx", ["tag"] = "1.27" },
        };
        if (extra is not null)
            foreach (var (key, value) in JsonSerializer.SerializeToNode(extra, Json.Options)!.AsObject()) body[key] = value?.DeepClone();
        return await Developer.CreateAsync("/api/v1/applications", body);
    }

    public async Task<JsonNode> CreateSecretAsync(string? name = null, string value = "s3cr3t-value", object? scope = null)
    {
        var body = new JsonObject { ["name"] = name ?? Unique("secret"), ["value"] = value };
        if (scope is not null)
            foreach (var (key, v) in JsonSerializer.SerializeToNode(scope, Json.Options)!.AsObject()) body[key] = v?.DeepClone();
        // Organization-wide secrets are Administrator-only to write (ADR 0006); narrower scopes are the Developer's.
        return await (scope is null ? Admin : Developer).CreateAsync("/api/v1/secrets", body);
    }

    /// <summary>A server, a project with its production environment and a docker-image application: the usual starting point.</summary>
    public async Task<(string ServerId, string EnvironmentId, string ProjectId, string ApplicationId)> CreateStackAsync()
    {
        var server = await CreateServerAsync(publicIp: "203.0.113.10");
        var (project, environmentId) = await CreateProjectWithEnvironmentAsync();
        var app = await CreateApplicationAsync(environmentId, server.Id());
        return (server.Id(), environmentId, project.Id(), app.Id());
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    // Requests keep dictionary keys exactly as written (the API's camelCase policy would turn NODE_ENV into node_ENV).
    private static JsonSerializerOptions CreateOptions()
    {
        var options = JsonConventions.CreateOptions();
        options.DictionaryKeyPolicy = null;
        return options;
    }

    public static string Id(this JsonNode node) => node["id"]!.GetValue<string>();

    public static async Task<JsonNode> ReadAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body (" + (int)response.StatusCode + ").");
    }

    public static StringContent Body(object? body, string mediaType = "application/json") =>
        new(body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body, Options), Encoding.UTF8, mediaType);

    public static Task<HttpResponseMessage> PostAsync(this HttpClient client, string url, object? body) => client.PostAsync(url, Body(body));

    public static Task<HttpResponseMessage> PatchAsync(this HttpClient client, string url, object? body, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = Body(body, "application/merge-patch+json") };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }

    public static async Task<JsonNode> GetJsonAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var node = await response.ReadAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {node.ToJsonString()}");
        return node;
    }

    /// <summary>POST expecting 201; returns the body.</summary>
    public static async Task<JsonNode> CreateAsync(this HttpClient client, string url, object? body)
    {
        var response = await client.PostAsync(url, body);
        var node = await response.ReadAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"POST {url} -> {(int)response.StatusCode}: {node.ToJsonString()}");
        Assert.NotNull(response.Headers.Location);
        return node;
    }

    public static async Task<JsonNode> PatchOkAsync(this HttpClient client, string url, object? body)
    {
        var response = await client.PatchAsync(url, body);
        var node = await response.ReadAsync();
        Assert.True(response.IsSuccessStatusCode, $"PATCH {url} -> {(int)response.StatusCode}: {node.ToJsonString()}");
        return node;
    }

    public static async Task AssertProblemAsync(this HttpResponseMessage response, int status, string code)
    {
        var node = await response.ReadAsync();
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, node["code"]?.GetValue<string>());
    }

    /// <summary>The first validation error's pointer/parameter and code.</summary>
    public static async Task<IReadOnlyList<(string? Location, string Code)>> ValidationErrorsAsync(this HttpResponseMessage response)
    {
        await response.AssertProblemAsync(422, "validation.failed");
        var body = await response.ReadAsync();
        return body["errors"]!.AsArray()
            .Select(e => (e!["pointer"]?.GetValue<string>() ?? e["parameter"]?.GetValue<string>(), e["code"]!.GetValue<string>())).ToList();
    }
}
