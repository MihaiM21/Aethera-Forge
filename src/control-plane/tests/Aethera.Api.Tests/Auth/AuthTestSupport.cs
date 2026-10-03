using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Http;
using Aethera.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Aethera.Api.Tests.Auth;

/// <summary>A clock tests can move forward, to exercise expiry without sleeping. Starts at the real time.</summary>
public sealed class TestClock : IClock
{
    private long _skewTicks;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _skewTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _skewTicks, by.Ticks);
}

/// <summary>The API with the real authentication handlers, a movable clock and a generous login rate limit (tests share one client IP).</summary>
public class AuthApiFactory : RealAuthApiFactory
{
    public TestClock Clock { get; } = new();

    protected virtual int LoginRateLimit => 10_000;

    protected virtual string EnvironmentName => "Testing";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("Aethera:Auth:LoginRateLimitPermits", LoginRateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }
}

/// <summary>3 login attempts per minute per IP, to reach the limit quickly.</summary>
public sealed class RateLimitedAuthApiFactory : AuthApiFactory
{
    protected override int LoginRateLimit => 3;
}

/// <summary>Production environment: the secure <c>__Host-</c> cookie, even if a test talks plain HTTP.</summary>
public sealed class ProductionAuthApiFactory : AuthApiFactory
{
    protected override string EnvironmentName => "Production";
}

/// <summary>Direct database access for assertions and resets.</summary>
internal static class AuthDb
{
    public static async Task ResetAsync(AetheraApiFactory factory)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE organizations, users, audit_events CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(AetheraApiFactory factory, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)result;
    }

    public static async Task ExecuteAsync(AetheraApiFactory factory, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<string>> AuditActionsAsync(AetheraApiFactory factory)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT action FROM audit_events ORDER BY occurred_at, id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var actions = new List<string>();
        while (await reader.ReadAsync()) actions.Add(reader.GetString(0));
        return actions;
    }

    public static Task<string> AuditMetadataAsync(AetheraApiFactory factory, string action) =>
        ScalarAsync<string>(factory, "SELECT string_agg(metadata::text, ' ') FROM audit_events WHERE action = @a", ("a", action));
}

/// <summary>
/// An HTTP client with an explicit cookie jar (so tests can hold several sessions at once and inspect <c>Set-Cookie</c>) and optional
/// bearer token. Sends <c>X-CSRF-Token</c> automatically on unsafe methods once <see cref="CsrfToken"/> is set.
/// </summary>
internal sealed class ApiClient(HttpClient http, string cookieName = "aethera_session")
{
    public const string Password = "correct horse battery staple";

    public string? SessionCookie { get; set; }

    public string? Bearer { get; set; }

    public string? CsrfToken { get; set; }

    public HttpClient Http => http;

    public string? LastSetCookie { get; private set; }

    public static ApiClient Create(WebApplicationFactory<Program> factory, Uri? baseAddress = null, string cookieName = "aethera_session") =>
        new(factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
            BaseAddress = baseAddress ?? new Uri("http://localhost"),
        }), cookieName);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool csrf = true, string? contentType = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonConventions.CreateOptions());
            request.Content = new StringContent(json, Encoding.UTF8, contentType ?? "application/json");
        }

        if (SessionCookie is not null) request.Headers.Add("Cookie", $"{cookieName}={SessionCookie}");
        if (Bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
        var unsafeMethod = method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;
        if (csrf && unsafeMethod && CsrfToken is not null) request.Headers.Add("X-CSRF-Token", CsrfToken);

        var response = await http.SendAsync(request);
        CaptureCookie(response);
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, bool csrf = true) => SendAsync(HttpMethod.Post, path, body ?? new { }, csrf);

    public Task<HttpResponseMessage> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);

    public Task<HttpResponseMessage> PatchAsync(string path, object body) => SendAsync(HttpMethod.Patch, path, body, contentType: "application/merge-patch+json");

    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path);

    /// <summary>Signs in and, on success, loads the CSRF token. Returns the login response.</summary>
    public async Task<HttpResponseMessage> LoginAsync(string email, string password = Password, bool rememberMe = false)
    {
        SessionCookie = null;
        CsrfToken = null;
        var response = await PostAsync("/api/v1/auth/login", new { email, password, rememberMe });
        if (response.IsSuccessStatusCode) await LoadCsrfAsync();
        return response;
    }

    public async Task LoadCsrfAsync()
    {
        var csrf = await GetAsync("/api/v1/auth/csrf");
        csrf.EnsureSuccessStatusCode();
        CsrfToken = (await csrf.ReadJsonAsync()).Str("token");
    }

    public async Task<JsonNode> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }

    private void CaptureCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var header in values)
        {
            if (!header.StartsWith(cookieName + "=", StringComparison.Ordinal)) continue;
            LastSetCookie = header;
            var value = header[(cookieName.Length + 1)..].Split(';')[0];
            var deleted = value.Length == 0 || header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
            SessionCookie = deleted ? null : value;
        }
    }
}

internal static class AuthTestExtensions
{
    /// <summary>Completes first-run setup (a fresh database is required) and returns an owner client with its session and CSRF token.</summary>
    public static async Task<ApiClient> SetupOwnerAsync(this AetheraApiFactory factory, string email = "owner@example.com", string organization = "Acme Inc")
    {
        var client = ApiClient.Create(factory);
        var response = await client.PostAsync("/api/v1/auth/setup",
            new { email, password = ApiClient.Password, displayName = "Owner", organizationName = organization }, csrf: false);
        await client.ReadAsync(response, HttpStatusCode.Created);
        await client.LoadCsrfAsync();
        return client;
    }

    /// <summary>Creates a user through the API as <paramref name="admin"/> and returns the user id.</summary>
    public static async Task<Guid> CreateUserAsync(this ApiClient admin, string email, OrganizationRole role, string displayName = "Test User")
    {
        var response = await admin.PostAsync("/api/v1/users", new { email, displayName, password = ApiClient.Password, role });
        var body = await admin.ReadAsync(response, HttpStatusCode.Created);
        return Guid.Parse(body.Str("id"));
    }

    /// <summary>Creates a user as <paramref name="admin"/> and returns a signed-in client for it.</summary>
    public static async Task<(ApiClient Client, Guid UserId)> CreateSignedInUserAsync(
        this ApiClient admin, WebApplicationFactory<Program> factory, string email, OrganizationRole role)
    {
        var id = await admin.CreateUserAsync(email, role);
        var client = ApiClient.Create(factory);
        var login = await client.LoginAsync(email);
        await client.ReadAsync(login, HttpStatusCode.OK);
        return (client, id);
    }

    /// <summary>Creates an API token for the client's user and returns (id, plaintext).</summary>
    public static async Task<(Guid Id, string Token)> CreateTokenAsync(this ApiClient client, params string[] scopes)
    {
        var response = await client.PostAsync("/api/v1/api-tokens", new { name = "ci", scopes });
        var body = await client.ReadAsync(response, HttpStatusCode.Created);
        return (Guid.Parse(body.Str("id")), body.Str("token"));
    }

    public static ApiClient WithBearer(this WebApplicationFactory<Program> factory, string token)
    {
        var client = ApiClient.Create(factory);
        client.Bearer = token;
        return client;
    }

    public static byte[] Sha256(string value) => SHA256.HashData(Encoding.ASCII.GetBytes(value));
}
