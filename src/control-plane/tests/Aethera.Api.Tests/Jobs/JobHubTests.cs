using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Jobs;

/// <summary>/hubs/logs and /hubs/jobs over the in-memory server (in-process fan-out: no Redis configured).</summary>
public sealed class JobHubTests(JobsApiFixture fixture) : IClassFixture<JobsApiFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private HttpClient Admin() => fixture.Client(OrganizationRole.Admin);

    private static string[] Lines(string prefix, int count) => Enumerable.Range(0, count).Select(i => $"{prefix}-{i}").ToArray();

    [RequiresDatabaseFact]
    public async Task LogsHub_ReplaysStoredChunks_ThenGoesLive_WithoutDuplicatesOrGaps()
    {
        var admin = Admin();
        var job = await JobsApiFixture.PostEchoAsync(admin, new { lines = Lines("row", 12), delayMs = 120 });
        var id = job.Str("id");
        var streamId = $"job:{id}";

        // Wait until some lines are stored: the subscription then has a replay AND a live part.
        await Eventually.UntilAsync(async () =>
            (await (await admin.GetAsync($"/api/v1/jobs/{id}/logs")).ReadJsonAsync())["items"]!.AsArray().Count >= 3, Wait);

        await using var connection = fixture.ConnectHub("/hubs/logs", Admin());
        var collector = new LogCollector(connection, streamId);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", streamId, (long?)null);

        Assert.Equal("succeeded", await collector.WaitForEndAsync(Wait));

        var lines = collector.Lines;
        var sequences = lines.Select(l => l.Sequence).ToList();
        Assert.Equal(sequences.Distinct().Count(), sequences.Count); // de-duplicated by sequence
        Assert.Equal(sequences.Order(), sequences);                   // in order
        Assert.Equal(Enumerable.Range((int)sequences[0], sequences.Count).Select(i => (long)i), sequences); // no gaps
        Assert.Equal(1, sequences[0]);
        for (var i = 0; i < 12; i++) Assert.Contains($"row-{i}\n", collector.Text);

        // The same text the REST endpoint serves.
        var rest = await (await admin.GetAsync($"/api/v1/jobs/{id}/logs?limit=1000")).ReadJsonAsync();
        Assert.Equal(string.Concat(rest["items"]!.AsArray().Select(i => i!.Str("text"))), collector.Text);
    }

    [RequiresDatabaseFact]
    public async Task LogsHub_LateSubscriber_GetsTheWholeStoredLog_ThenTheEnd()
    {
        var admin = Admin();
        var id = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Lines("old", 5), delayMs = 40 })).Str("id");
        await JobsApiFixture.WaitForJobAsync(admin, id);
        var streamId = $"job:{id}";

        await using var connection = fixture.ConnectHub("/hubs/logs", Admin());
        var collector = new LogCollector(connection, streamId);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", streamId, (long?)null);
        Assert.Equal("succeeded", await collector.WaitForEndAsync(Wait));
        for (var i = 0; i < 5; i++) Assert.Contains($"old-{i}\n", collector.Text);

        // fromSequence resumes: only chunks from that sequence on
        var all = collector.Lines;
        var from = all[^1].Sequence;
        await using var resumed = fixture.ConnectHub("/hubs/logs", Admin());
        var tail = new LogCollector(resumed, streamId);
        await resumed.StartAsync();
        await resumed.InvokeAsync("Subscribe", streamId, (long?)from);
        await tail.WaitForEndAsync(Wait);
        Assert.Equal([from], tail.Lines.Select(l => l.Sequence));
    }

    [RequiresDatabaseFact]
    public async Task LogsHub_FollowsAFailedAndACancelledJobToTheirEnd()
    {
        var admin = Admin();
        var failed = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Lines("f", 3), delayMs = 50, failAt = 2, retryable = false })).Str("id");
        await using var connection = fixture.ConnectHub("/hubs/logs", Admin());
        var collector = new LogCollector(connection, $"job:{failed}");
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", $"job:{failed}", (long?)null);
        Assert.Equal("failed", await collector.WaitForEndAsync(Wait));
        Assert.Contains("Failed (echo.failed)", collector.Text);

        var slow = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Lines("s", 400), delayMs = 50 })).Str("id");
        var cancelCollector = new LogCollector(connection, $"job:{slow}");
        await connection.InvokeAsync("Subscribe", $"job:{slow}", (long?)null);
        await Eventually.UntilAsync(() => Task.FromResult(cancelCollector.Lines.Count >= 2), Wait);
        await fixture.Client(OrganizationRole.Developer).PostAsync($"/api/v1/jobs/{slow}/cancel", null);
        Assert.Equal("cancelled", await cancelCollector.WaitForEndAsync(Wait));
        Assert.Contains("Cancelled", cancelCollector.Text);
    }

    [RequiresDatabaseFact]
    public async Task LogsHub_Unsubscribe_StopsTheFlow()
    {
        var admin = Admin();
        var id = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Lines("u", 60), delayMs = 40 })).Str("id");
        var streamId = $"job:{id}";
        await using var connection = fixture.ConnectHub("/hubs/logs", Admin());
        var collector = new LogCollector(connection, streamId);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", streamId, (long?)null);
        await Eventually.UntilAsync(() => Task.FromResult(collector.Lines.Count >= 2), Wait);

        await connection.InvokeAsync("Unsubscribe", streamId);
        await Task.Delay(300);
        var frozen = collector.Lines.Count;
        await Task.Delay(500);

        Assert.Equal(frozen, collector.Lines.Count);
        await fixture.Client(OrganizationRole.Developer).PostAsync($"/api/v1/jobs/{id}/cancel", null);
    }

    [RequiresDatabaseFact]
    public async Task LogsHub_RefusesStreamsOfOtherOrganizationsAndUnknownStreams()
    {
        var id = (await JobsApiFixture.PostEchoAsync(Admin(), new { lines = new[] { "secret" } })).Str("id");

        await using var stranger = fixture.ConnectHub("/hubs/logs", fixture.Client(OrganizationRole.Owner, fixture.Other));
        await stranger.StartAsync();
        foreach (var stream in new[] { $"job:{id}", $"job:{Guid.NewGuid()}", "build:123", "nonsense", "job:not-a-guid" })
        {
            var error = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync("Subscribe", stream, (long?)null));
            Assert.Contains("log_stream.not_found", error.Message);
        }

        var tooLong = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync("Subscribe", new string('x', 500), (long?)null));
        Assert.Contains("validation.invalid_parameter", tooLong.Message);
    }

    [RequiresDatabaseFact]
    public async Task Hubs_RequireAuthenticationAndTheReadScope()
    {
        await using var anonymous = fixture.ConnectHub("/hubs/logs", fixture.Factory.CreateAnonymousClient());
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());

        await using var anonymousJobs = fixture.ConnectHub("/hubs/jobs", fixture.Factory.CreateAnonymousClient());
        await Assert.ThrowsAnyAsync<Exception>(() => anonymousJobs.StartAsync());

        await using var wrongScope = fixture.ConnectHub("/hubs/logs", fixture.Client(OrganizationRole.Admin, fixture.Owner, "deploy"));
        await Assert.ThrowsAnyAsync<Exception>(() => wrongScope.StartAsync());

        await using var readToken = fixture.ConnectHub("/hubs/logs", fixture.Client(OrganizationRole.Viewer, fixture.Owner, "read"));
        await readToken.StartAsync();
        Assert.Equal(HubConnectionState.Connected, readToken.State);
    }

    [RequiresDatabaseFact]
    public async Task JobsHub_PushesStatusChangesAndProgress_OfASubscribedJob()
    {
        var admin = Admin();
        await using var connection = fixture.ConnectHub("/hubs/jobs", Admin());
        var updates = new System.Collections.Concurrent.ConcurrentQueue<System.Text.Json.Nodes.JsonObject>();
        var progress = new System.Collections.Concurrent.ConcurrentQueue<(Guid Id, int Percent)>();
        connection.On<System.Text.Json.Nodes.JsonObject>("JobUpdated", job => updates.Enqueue(job));
        connection.On<Guid, int, string?>("JobProgress", (jobId, percent, _) => progress.Enqueue((jobId, percent)));
        await connection.StartAsync();

        // Queue it behind a lock so we can subscribe before it starts.
        var key = "app:" + Guid.NewGuid();
        var holder = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Lines("h", 400), delayMs = 25, lockKey = key })).Str("id");
        await JobsApiFixture.WaitForJobAsync(admin, holder, "running");
        var job = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Lines("j", 4), delayMs = 30, lockKey = key })).Str("id");

        await connection.InvokeAsync("Subscribe", Guid.Parse(job));
        await Eventually.UntilAsync(() => Task.FromResult(updates.Any(u => u.Str("id") == job)), Wait); // the snapshot
        Assert.Equal("queued", updates.First(u => u.Str("id") == job).Str("status"));

        await fixture.Client(OrganizationRole.Developer).PostAsync($"/api/v1/jobs/{holder}/cancel", null);
        await Eventually.UntilAsync(() => Task.FromResult(updates.Any(u => u.Str("id") == job && u.Str("status") == "succeeded")), Wait);

        var statuses = updates.Where(u => u.Str("id") == job).Select(u => u.Str("status")).ToList();
        Assert.Contains("running", statuses);
        Assert.Equal("succeeded", statuses[^1]);
        Assert.DoesNotContain(updates, u => u.Str("id") == holder); // only the subscribed job
        Assert.Equal([25, 50, 75, 100], progress.Where(p => p.Id == Guid.Parse(job)).Select(p => p.Percent).Order());
        var final = updates.Last(u => u.Str("id") == job);
        Assert.Equal(4, final["result"]!["lines"]!.GetValue<int>()); // the same shape GET /jobs/{id} returns
        await JobsApiFixture.WaitForJobAsync(admin, holder, "cancelled");
    }

    [RequiresDatabaseFact]
    public async Task JobsHub_SubscribeAll_And_SubscribeResource_AreScopedToTheOrganization()
    {
        var admin = Admin();
        var resource = new JobResource("application", Guid.NewGuid());

        await using var all = fixture.ConnectHub("/hubs/jobs", Admin());
        await using var byResource = fixture.ConnectHub("/hubs/jobs", Admin());
        await using var stranger = fixture.ConnectHub("/hubs/jobs", fixture.Client(OrganizationRole.Owner, fixture.Other));
        var allSeen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var resourceSeen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var strangerSeen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        all.On<System.Text.Json.Nodes.JsonObject>("JobUpdated", job => allSeen.Enqueue(job.Str("id")));
        byResource.On<System.Text.Json.Nodes.JsonObject>("JobUpdated", job => resourceSeen.Enqueue(job.Str("id")));
        stranger.On<System.Text.Json.Nodes.JsonObject>("JobUpdated", job => strangerSeen.Enqueue(job.Str("id")));
        foreach (var c in new[] { all, byResource, stranger }) await c.StartAsync();
        await all.InvokeAsync("SubscribeAll");
        await stranger.InvokeAsync("SubscribeAll");
        await byResource.InvokeAsync("SubscribeResource", "application", resource.Id);

        var viaApi = (await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "api" } })).Str("id");
        Job onResource;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            // created by a member of the caller's organization, acting on the resource
            onResource = await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(
                new JobRequest(EchoJobHandler.JobType, new EchoPayload { Lines = ["res"] }) { Resource = resource });
        }

        await Eventually.UntilAsync(() => Task.FromResult(allSeen.Contains(viaApi) && allSeen.Contains(onResource.Id.ToString())), Wait);
        await Eventually.UntilAsync(() => Task.FromResult(resourceSeen.Contains(onResource.Id.ToString())), Wait);
        await JobsApiFixture.WaitForJobAsync(admin, viaApi);
        await Task.Delay(300);

        Assert.DoesNotContain(viaApi, resourceSeen);
        Assert.DoesNotContain(viaApi, strangerSeen);       // another organization's job never reaches the stranger
        Assert.Contains(onResource.Id.ToString(), strangerSeen); // a system job (no creator) is visible to every organization
    }

    [RequiresDatabaseFact]
    public async Task JobsHub_Subscribe_ToAnotherOrganizationsJob_IsRefused()
    {
        var id = (await JobsApiFixture.PostEchoAsync(Admin(), new { lines = new[] { "x" } })).Str("id");
        await using var stranger = fixture.ConnectHub("/hubs/jobs", fixture.Client(OrganizationRole.Owner, fixture.Other));
        await stranger.StartAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync("Subscribe", Guid.Parse(id)));
        Assert.Contains("job.not_found", error.Message);
        var unknown = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync("Subscribe", Guid.NewGuid()));
        Assert.Contains("job.not_found", unknown.Message);
    }

    [RequiresDatabaseFact]
    public async Task LogsHub_LostLiveMessages_CostLatency_NeverLines()
    {
        // A stream that is not produced by a worker: we play the ingestor by hand (rows first, then the bus message, as it commits).
        Job job;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            job = await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(
                new JobRequest("t.never-runs") { RunAfter = DateTimeOffset.UtcNow.AddHours(1) });
        }

        var streamId = $"job:{job.Id}";
        var bus = fixture.Factory.Services.GetRequiredService<ILiveBus>();
        Task Publish(long sequence) => bus.PublishAsync(LiveChannels.Logs(streamId),
            new LogBusMessage(streamId, new LogLine(sequence, DateTimeOffset.UtcNow, LogStream.Stdout, LogSource.Job, $"line {sequence}\n"), null).ToJson());

        await using var connection = fixture.ConnectHub("/hubs/logs", Admin());
        var collector = new LogCollector(connection, streamId);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", streamId, (long?)null);

        await InsertChunkAsync(streamId, 1);
        await InsertChunkAsync(streamId, 2);   // stored, but its message is lost
        await InsertChunkAsync(streamId, 3);
        await Publish(1);
        await Publish(3);                      // the hub sees the hole and fills it from Postgres
        await Eventually.UntilAsync(() => Task.FromResult(collector.Lines.Count >= 3), Wait);
        Assert.Equal([1L, 2L, 3L], collector.Lines.Select(l => l.Sequence));

        await InsertChunkAsync(streamId, 4);   // stored, and nothing at all was published: the safety-net poll finds it
        await Eventually.UntilAsync(() => Task.FromResult(collector.Lines.Count >= 4), Wait);

        await InsertChunkAsync(streamId, 5);   // the last chunk's message is lost, then the end of the stream arrives
        await bus.PublishAsync(LiveChannels.Logs(streamId), new LogBusMessage(streamId, null, "succeeded").ToJson());
        Assert.Equal("succeeded", await collector.WaitForEndAsync(Wait));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], collector.Lines.Select(l => l.Sequence)); // line 5 arrived before the end, not never
        Assert.Equal("line 1\nline 2\nline 3\nline 4\nline 5\n", collector.Text);
    }

    [RequiresDatabaseFact]
    public async Task LogsHub_KeepsWorking_WhenRedisIsConfiguredButDown()
    {
        using var factory = JobsApiFixture.Configure(fixture.Factory, b =>
            b.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,connectTimeout=200,syncTimeout=200"));
        var admin = factory.CreateClientAs(OrganizationRole.Admin, null, fixture.Owner);

        var id = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Lines("down", 6), delayMs = 100 })).Str("id");
        var streamId = $"job:{id}";
        await using var connection = fixture.ConnectHub("/hubs/logs", admin, factory);
        var collector = new LogCollector(connection, streamId);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", streamId, (long?)null);

        // No live feed at all: the lines and the end are found in Postgres.
        var reason = await collector.WaitForEndAsync(Wait);
        Assert.True(reason == "succeeded", (await JobsApiFixture.GetJobAsync(admin, id)).ToJsonString());
        for (var i = 0; i < 6; i++) Assert.Contains($"down-{i}\n", collector.Text);
        var sequences = collector.Lines.Select(l => l.Sequence).ToList();
        Assert.Equal(sequences.Distinct().Count(), sequences.Count);
    }

    private async Task InsertChunkAsync(string streamId, long sequence)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "INSERT INTO log_chunks (stream_id, sequence, ts, source, stream, data) VALUES ($1, $2, now(), 5, 1, $3)", connection);
        command.Parameters.AddWithValue(streamId);
        command.Parameters.AddWithValue(sequence);
        command.Parameters.AddWithValue($"line {sequence}\n");
        await command.ExecuteNonQueryAsync();
    }
}
