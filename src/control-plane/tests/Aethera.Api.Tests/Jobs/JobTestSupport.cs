using System.Collections.Concurrent;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aethera.Api.Tests.Jobs;

/// <summary>A throw-away migrated database plus helpers to look at it directly.</summary>
internal sealed class JobDatabase : IAsyncDisposable
{
    private readonly TestDatabase _database;
    private readonly ServiceProvider _services;

    private JobDatabase(TestDatabase database)
    {
        _database = database;
        _services = TestDatabaseFixture.BuildServices(database.ConnectionString);
    }

    public string ConnectionString => _database.ConnectionString;

    public static async Task<JobDatabase> CreateAsync() =>
        new(await TestDatabase.CreateAsync() ?? throw new InvalidOperationException("AETHERA_TEST_DB is not set."));

    public async Task<Job> GetJobAsync(Guid id)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AetheraDbContext>().Jobs.AsNoTracking().FirstAsync(j => j.Id == id);
    }

    public async Task<List<Job>> GetJobsAsync(string? type = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<AetheraDbContext>().Jobs.AsNoTracking();
        return await (type is null ? jobs : jobs.Where(j => j.Type == type)).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToListAsync();
    }

    public async Task<List<LogChunk>> GetChunksAsync(string streamId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AetheraDbContext>().LogChunks.AsNoTracking()
            .Where(c => c.StreamId == streamId).OrderBy(c => c.Sequence).ToListAsync();
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    /// <summary>Waits for a job to reach one of the statuses.</summary>
    public async Task<Job> WaitForStatusAsync(Guid id, TimeSpan? timeout = null, params JobStatus[] statuses) =>
        await Eventually.UntilAsync(async () =>
        {
            var job = await GetJobAsync(id);
            return statuses.Contains(job.Status) ? job : null;
        }, timeout, $"job {id} to become {string.Join("/", statuses)}");

    public async Task<Job> WaitForTerminalAsync(Guid id, TimeSpan? timeout = null) =>
        await Eventually.UntilAsync(async () =>
        {
            var job = await GetJobAsync(id);
            return job.IsTerminal ? job : null;
        }, timeout, $"job {id} to finish");

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }
}

internal static class Eventually
{
    public static async Task<T> UntilAsync<T>(Func<Task<T?>> probe, TimeSpan? timeout = null, string what = "condition") where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var value = await probe();
            if (value is not null) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(25);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> probe, TimeSpan? timeout = null, string what = "condition") =>
        await UntilAsync(async () => await probe() ? new object() : null, timeout, what);
}

/// <summary>A generic host running the job system (no HTTP) against a database. Several can run against one database.</summary>
internal sealed class JobTestHost : IAsyncDisposable
{
    private JobTestHost(IHost host) => Host = host;

    public IHost Host { get; }

    public JobWorkerHost Worker => Host.Services.GetRequiredService<JobWorkerHost>();

    /// <summary>Short leases and polls so the tests are quick.</summary>
    public static Dictionary<string, string?> FastSettings(string connectionString, int workers = 4) => new()
    {
        ["ConnectionStrings:Aethera"] = connectionString,
        ["Aethera:Jobs:WorkerCount"] = workers.ToString(),
        ["Aethera:Jobs:LeaseSeconds"] = "2",
        ["Aethera:Jobs:HeartbeatSeconds"] = "0.2",
        ["Aethera:Jobs:PollSeconds"] = "0.25",
        ["Aethera:Jobs:ReaperSeconds"] = "0.3",
        ["Aethera:Jobs:BaseBackoffSeconds"] = "0.2",
        ["Aethera:Jobs:MaxBackoffSeconds"] = "1",
        ["Aethera:Jobs:LockContentionDelaySeconds"] = "0.2",
        ["Aethera:Jobs:LogFlushMilliseconds"] = "20",
    };

    public static async Task<JobTestHost> StartAsync(
        string connectionString,
        Action<IServiceCollection>? configure = null,
        Action<Dictionary<string, string?>>? settings = null,
        bool start = true)
    {
        var values = FastSettings(connectionString);
        settings?.Invoke(values);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddAetheraPersistence(builder.Configuration).AddAetheraJobSystem(builder.Configuration);
        configure?.Invoke(builder.Services);

        var host = builder.Build();
        var testHost = new JobTestHost(host);
        if (start) await host.StartAsync();
        return testHost;
    }

    public Task StartAsync() => Host.StartAsync();

    public async Task<Job> EnqueueAsync(JobRequest request)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(request);
    }

    public async Task<Job> EnqueueEchoAsync(
        string[]? lines = null, int delayMs = 0, int? failAt = null, bool retryable = true, int maxAttempts = 1, string? lockKey = null, int priority = 0) =>
        await EnqueueAsync(new JobRequest(EchoJobHandler.JobType, new EchoPayload { Lines = lines ?? ["hello"], DelayMs = delayMs, FailAt = failAt, Retryable = retryable })
        {
            MaxAttempts = maxAttempts,
            LockKey = lockKey,
            Priority = priority,
        });

    public async ValueTask DisposeAsync()
    {
        try { await Host.StopAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception) { /* already stopped */ }
        Host.Dispose();
    }
}

/// <summary>A handler built from a delegate, for tests.</summary>
internal sealed class DelegateHandler(string type, Func<JobContext, CancellationToken, Task> run, bool resumable = true) : IJobHandler
{
    public string Type => type;

    public bool Resumable => resumable;

    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => run(context, cancellationToken);
}

/// <summary>Records executions across hosts: who ran what, and how many ran at the same time.</summary>
internal sealed class ExecutionLog
{
    private readonly ConcurrentQueue<(Guid JobId, string Host, int Attempt, DateTime At)> _starts = new();
    private int _concurrent;
    private int _maxConcurrent;
    private readonly ConcurrentDictionary<string, int> _concurrentByKey = new();
    private readonly ConcurrentDictionary<string, int> _maxByKey = new();

    public IReadOnlyList<(Guid JobId, string Host, int Attempt, DateTime At)> Starts => [.. _starts];

    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public int MaxConcurrentFor(string key) => _maxByKey.GetValueOrDefault(key);

    public async Task RunAsync(JobContext context, string host, TimeSpan duration, string? key = null)
    {
        _starts.Enqueue((context.JobId, host, context.Attempt, DateTime.UtcNow));
        var now = Interlocked.Increment(ref _concurrent);
        InterlockedMax(ref _maxConcurrent, now);
        var perKey = key is null ? 0 : _concurrentByKey.AddOrUpdate(key, 1, (_, v) => v + 1);
        if (key is not null) _maxByKey.AddOrUpdate(key, perKey, (_, v) => Math.Max(v, perKey));
        try
        {
            await Task.Delay(duration, context.CancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
            if (key is not null) _concurrentByKey.AddOrUpdate(key, 0, (_, v) => v - 1);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
