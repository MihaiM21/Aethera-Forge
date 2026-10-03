using System.Collections.Concurrent;
using System.Net;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Aethera.Api.Tests.Jobs;

/// <summary>
/// Runs when <c>AETHERA_TEST_REDIS</c> names a Redis (e.g. <c>localhost:6390</c> after
/// <c>redis-server --port 6390 --save '' --appendonly no</c>); skipped otherwise. The in-process fallback is covered by
/// <see cref="JobHubTests"/> (no Redis configured there).
/// </summary>
public sealed class RedisFanOutTests(JobsApiFixture fixture) : IClassFixture<JobsApiFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static string Redis => Environment.GetEnvironmentVariable(RequiresRedisFactAttribute.EnvironmentVariable)!;

    private HttpClient Admin(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) =>
        factory.CreateClientAs(OrganizationRole.Admin, null, fixture.Owner);

    [RequiresRedisFact]
    public async Task Chunks_ArePublishedToRedisAfterTheyAreStored_AndReachTheHubLive()
    {
        using var factory = JobsApiFixture.Configure(fixture.Factory, b => b.UseSetting("ConnectionStrings:Redis", Redis));
        var admin = Admin(factory);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(Redis);
        var received = new ConcurrentQueue<string>();
        var channel = await redis.GetSubscriber().SubscribeAsync(RedisChannel.Pattern("logs:job:*"));
        channel.OnMessage(m => received.Enqueue(m.Message.ToString()));

        var ready = await factory.CreateClient().GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ok", (await ready.ReadJsonAsync())["checks"]!.Str("redis"));

        var job = await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "r0", "r1", "r2", "r3", "r4" }, delayMs = 150 });
        var id = job.Str("id");
        var streamId = $"job:{id}";

        // Subscribe at the start: everything that arrives afterwards can only have come through Redis.
        await using var connection = fixture.ConnectHub("/hubs/logs", admin, factory);
        var collector = new LogCollector(connection, streamId);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", streamId, (long?)null);
        Assert.Equal("succeeded", await collector.WaitForEndAsync(Wait));

        for (var i = 0; i < 5; i++) Assert.Contains($"r{i}\n", collector.Text);
        await Eventually.UntilAsync(() => Task.FromResult(received.Any(m => m.Contains("\"eof\":\"succeeded\""))), Wait);

        var messages = received.Where(m => m.Contains($"\"streamId\":\"{streamId}\"")).Select(m => System.Text.Json.Nodes.JsonNode.Parse(m)!).ToList();
        var published = messages.Where(m => m["line"] is not null).Select(m => m["line"]!["sequence"]!.GetValue<long>()).ToList();
        Assert.Equal(collector.Lines.Select(l => l.Sequence), published); // every stored chunk was published, in order
        Assert.Equal("succeeded", messages[^1].Str("eof"));

        // and what was published is already in Postgres (publish happens after commit)
        var rest = await (await admin.GetAsync($"/api/v1/jobs/{id}/logs?limit=1000")).ReadJsonAsync();
        Assert.Equal(published, rest["items"]!.AsArray().Select(i => i!["sequence"]!.GetValue<long>()));
    }

    [RequiresRedisFact]
    public async Task TwoInstances_ShareLiveLogsAndJobEvents_ThroughRedis()
    {
        // Instance A runs the workers; instance B has none and only serves SignalR. Same database, same Redis.
        using var instanceA = JobsApiFixture.Configure(fixture.Factory, b => b.UseSetting("ConnectionStrings:Redis", Redis));
        using var instanceB = JobsApiFixture.Configure(fixture.Factory, b =>
        {
            b.UseSetting("ConnectionStrings:Redis", Redis);
            b.UseSetting("Aethera:Jobs:WorkerCount", "0");
        });
        var adminA = Admin(instanceA);
        var adminB = Admin(instanceB);

        await using var jobs = fixture.ConnectHub("/hubs/jobs", adminB, instanceB);
        var statuses = new ConcurrentQueue<string>();
        jobs.On<System.Text.Json.Nodes.JsonObject>("JobUpdated", job => statuses.Enqueue(job.Str("status")));
        await jobs.StartAsync();
        await jobs.InvokeAsync("SubscribeAll");

        var key = "app:" + Guid.NewGuid();
        var holder = (await JobsApiFixture.PostEchoAsync(adminA, new { lines = Enumerable.Repeat("h", 300).ToArray(), delayMs = 30, lockKey = key })).Str("id");
        await JobsApiFixture.WaitForJobAsync(adminA, holder, "running");
        var job = await JobsApiFixture.PostEchoAsync(adminA, new { lines = new[] { "b0", "b1", "b2", "b3", "b4", "b5" }, delayMs = 150, lockKey = key });
        var streamId = $"job:{job.Str("id")}";

        await using var logs = fixture.ConnectHub("/hubs/logs", adminB, instanceB);
        var collector = new LogCollector(logs, streamId);
        await logs.StartAsync();
        await logs.InvokeAsync("Subscribe", streamId, (long?)null);

        // The job starts only when the holder is gone, i.e. after B subscribed: B has nothing to replay and no workers, so every
        // line it shows travelled A -> Postgres -> Redis -> B.
        await instanceA.CreateClientAs(OrganizationRole.Developer, null, fixture.Owner).PostAsync($"/api/v1/jobs/{holder}/cancel", null);
        Assert.Equal("succeeded", await collector.WaitForEndAsync(Wait));
        for (var i = 0; i < 6; i++) Assert.Contains($"b{i}\n", collector.Text);
        Assert.Equal(collector.Lines.Select(l => l.Sequence).Order(), collector.Lines.Select(l => l.Sequence));

        await Eventually.UntilAsync(() => Task.FromResult(statuses.Contains("succeeded")), Wait);
        Assert.Contains("running", statuses);
        Assert.Contains("cancelled", statuses);
    }
}

/// <summary>Skipped unless <c>AETHERA_TEST_REDIS</c> is set (a Redis connection string such as <c>localhost:6390</c>).</summary>
public sealed class RequiresRedisFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "AETHERA_TEST_REDIS";

    public RequiresRedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"{EnvironmentVariable} is not set; start redis-server (e.g. --port 6390 --save '' --appendonly no) and set it to localhost:6390 to run the Redis fan-out tests.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RequiresDatabaseFactAttribute.EnvironmentVariable)))
            Skip = $"{RequiresDatabaseFactAttribute.EnvironmentVariable} is not set; the Redis fan-out tests also need a database.";
    }
}
