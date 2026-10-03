using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Http;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Aethera.Api.Tests.Jobs;

/// <summary>
/// The API with a real database, real workers (short leases and polls) and two organizations. One database per fixture, so test classes
/// stay independent; tests within a class use their own jobs.
/// </summary>
public sealed class JobsApiFixture : IAsyncLifetime
{
    private AetheraApiFactory? _root;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString => _root!.ConnectionString;

    public SeededIdentity Owner { get; private set; } = null!;
    public SeededIdentity Other { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!TestDatabase.IsConfigured) return;
        _root = new AetheraApiFactory();
        Factory = Configure(_root);
        // The role of a test caller comes from the header (CreateClientAs); the identity supplies the organization and user rows.
        // Seeded straight into the database: the factory's own host (and its workers) must not start before a test asks for it.
        await using var services = TestDatabaseFixture.BuildServices(ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Aethera.Infrastructure.Persistence.AetheraDbContext>();
        Owner = await TestSeeder.SeedIdentityAsync(db, OrganizationRole.Owner);
        Other = await TestSeeder.SeedIdentityAsync(db, OrganizationRole.Admin);
    }

    public Task DisposeAsync()
    {
        Factory?.Dispose();
        _root?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The standard test configuration: fast workers, an extra handler that fails or blocks on demand.</summary>
    public static WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> factory, Action<IWebHostBuilder>? more = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["Aethera:Jobs:WorkerCount"] = "4",
                ["Aethera:Jobs:LeaseSeconds"] = "2",
                ["Aethera:Jobs:HeartbeatSeconds"] = "0.25",
                ["Aethera:Jobs:PollSeconds"] = "0.25",
                ["Aethera:Jobs:ReaperSeconds"] = "0.3",
                ["Aethera:Jobs:BaseBackoffSeconds"] = "0.2",
                ["Aethera:Jobs:MaxBackoffSeconds"] = "1",
                ["Aethera:Jobs:LogFlushMilliseconds"] = "20",
            })
                builder.UseSetting(key, value);
            more?.Invoke(builder);
        });

    public HttpClient Client(OrganizationRole role, SeededIdentity? identity = null, params string[]? scopes) =>
        Factory.CreateClientAs(role, scopes is { Length: > 0 } ? scopes : null, identity ?? Owner);

    public HttpClient AdminClient() => Client(OrganizationRole.Admin);

    // ----------------------------------------------------------------- helpers

    public static async Task<JsonNode> PostEchoAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/jobs/echo", body, JsonConventions.CreateOptions());
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return await response.ReadJsonAsync();
    }

    public static async Task<JsonNode> GetJobAsync(HttpClient client, string id) =>
        await (await client.GetAsync($"/api/v1/jobs/{id}")).ReadJsonAsync();

    public static async Task<JsonNode> WaitForJobAsync(HttpClient client, string id, params string[] statuses)
    {
        var wanted = statuses.Length == 0 ? new[] { "succeeded", "failed", "cancelled" } : statuses;
        return await Eventually.UntilAsync(async () =>
        {
            var job = await GetJobAsync(client, id);
            return wanted.Contains(job.Str("status")) ? job : null;
        }, TimeSpan.FromSeconds(30), $"job {id} to become {string.Join("/", wanted)}");
    }

    /// <summary>A SignalR client against the in-memory server, authenticated like <paramref name="client"/> (long polling: no sockets needed).</summary>
    public HubConnection ConnectHub(string path, HttpClient client, WebApplicationFactory<Program>? factory = null)
    {
        var server = (factory ?? Factory).Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                foreach (var header in client.DefaultRequestHeaders)
                    options.Headers[header.Key] = string.Join(',', header.Value);
            })
            .AddJsonProtocol(options => JsonConventions.Configure(options.PayloadSerializerOptions))
            .Build();
        return connection;
    }
}

/// <summary>Collects what a <c>/hubs/logs</c> subscription delivers.</summary>
internal sealed class LogCollector
{
    private readonly object _gate = new();
    private readonly List<LogLine> _lines = [];
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LogCollector(HubConnection connection, string streamId)
    {
        connection.On<string, LogLine[]>("LogLines", (id, lines) =>
        {
            if (id != streamId) return;
            lock (_gate) _lines.AddRange(lines);
        });
        connection.On<string, string>("LogStreamEnded", (id, reason) =>
        {
            if (id == streamId) _ended.TrySetResult(reason);
        });
    }

    public IReadOnlyList<LogLine> Lines
    {
        get
        {
            lock (_gate) return [.. _lines];
        }
    }

    public string Text => string.Concat(Lines.Select(l => l.Text));

    public Task<string> Ended => _ended.Task;

    public Task<string> WaitForEndAsync(TimeSpan? timeout = null) => _ended.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30));
}

/// <summary>A logger provider that remembers what was logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, System.Collections.Concurrent.ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, category, formatter(state, exception)));
    }
}
