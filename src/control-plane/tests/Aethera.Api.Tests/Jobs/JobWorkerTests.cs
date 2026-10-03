using Aethera.Domain;
using Aethera.Infrastructure.Jobs;

namespace Aethera.Api.Tests.Jobs;

/// <summary>The queue and worker host against a real Postgres: generic hosts without HTTP, short leases and polls.</summary>
public sealed class JobWorkerTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    /// <summary>jsonb normalises whitespace; compare the compact form.</summary>
    private static string Compact(string? json) => System.Text.Json.Nodes.JsonNode.Parse(json ?? "null")?.ToJsonString() ?? "null";

    [RequiresDatabaseFact]
    public async Task Echo_RunsEndToEnd_AndLogsAreStoredInOrder()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString);

        var queued = await host.EnqueueEchoAsync(["alpha", "beta", "gamma"], delayMs: 20);
        Assert.Equal(JobStatus.Queued, queued.Status);

        var job = await db.WaitForTerminalAsync(queued.Id);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.LockedBy);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Contains("\"lines\":3", Compact(job.ResultJson));

        var chunks = await db.GetChunksAsync($"job:{job.Id}");
        Assert.NotEmpty(chunks);
        Assert.Equal(Enumerable.Range(1, chunks.Count).Select(i => (long)i), chunks.Select(c => c.Sequence));
        var text = string.Concat(chunks.Select(c => c.Data));
        Assert.True(text.IndexOf("alpha", StringComparison.Ordinal) < text.IndexOf("beta", StringComparison.Ordinal));
        Assert.True(text.IndexOf("beta", StringComparison.Ordinal) < text.IndexOf("gamma", StringComparison.Ordinal));
        Assert.Contains("[aethera] Started system.echo", text);
        Assert.Contains("[aethera] Succeeded", text);
    }

    [RequiresDatabaseFact]
    public async Task Enqueue_WithSameIdempotencyKey_ReturnsTheExistingJob()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, start: false);

        var request = new JobRequest(EchoJobHandler.JobType, new EchoPayload { Lines = ["x"] }) { IdempotencyKey = "delivery-42" };
        var first = await host.EnqueueAsync(request);
        var second = await host.EnqueueAsync(request with { Priority = 5 });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(0, second.Priority);
        Assert.Single(await db.GetJobsAsync());

        // Concurrent producers with one key also produce a single job.
        var racing = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            host.EnqueueAsync(new JobRequest(EchoJobHandler.JobType) { IdempotencyKey = "delivery-race" })));
        Assert.Single(racing.Select(j => j.Id).Distinct());
        Assert.Equal(2, (await db.GetJobsAsync()).Count);
    }

    [RequiresDatabaseFact]
    public async Task Enqueue_StoresTheRequestAndTheCreatorOfRetries()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, start: false);
        var resource = new JobResource("application", Guid.NewGuid());
        var parent = await host.EnqueueAsync(new JobRequest("x.y", new { a = 1 }) { Resource = resource, LockKey = "app:1", MaxAttempts = 3, Priority = 2 });
        var retry = await host.EnqueueAsync(new JobRequest("x.y", new { a = 1 }) { ParentJobId = parent.Id });

        var stored = await db.GetJobAsync(parent.Id);
        Assert.Equal("x.y", stored.Type);
        Assert.Equal("application", stored.ResourceType);
        Assert.Equal(resource.Id, stored.ResourceId);
        Assert.Equal("app:1", stored.LockKey);
        Assert.Equal(3, stored.MaxAttempts);
        Assert.Equal(2, stored.Priority);
        Assert.Equal("{\"a\":1}", stored.PayloadJson.Replace(" ", ""));
        Assert.Equal(parent.Id, (await db.GetJobAsync(retry.Id)).ParentJobId);
        Assert.Equal(1, (await db.GetJobAsync(retry.Id)).RetryNo);
    }

    [RequiresDatabaseFact]
    public async Task Priority_RunsHigherPriorityFirst_ThenOldestFirst()
    {
        await using var db = await JobDatabase.CreateAsync();
        var log = new ExecutionLog();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString,
            services => services.AddSingleton<IJobHandler>(new DelegateHandler("t.priority", (ctx, ct) => log.RunAsync(ctx, "h", TimeSpan.FromMilliseconds(30)))),
            settings => settings["Aethera:Jobs:WorkerCount"] = "1",
            start: false);

        var low1 = await host.EnqueueAsync(new JobRequest("t.priority") { Priority = 0 });
        var low2 = await host.EnqueueAsync(new JobRequest("t.priority") { Priority = 0 });
        var high = await host.EnqueueAsync(new JobRequest("t.priority") { Priority = 10 });
        var mid = await host.EnqueueAsync(new JobRequest("t.priority") { Priority = 5 });

        await host.StartAsync();
        foreach (var job in new[] { low1, low2, high, mid }) await db.WaitForTerminalAsync(job.Id);

        Assert.Equal([high.Id, mid.Id, low1.Id, low2.Id], log.Starts.Select(s => s.JobId));
    }

    [RequiresDatabaseFact]
    public async Task RunAfter_DelaysTheJob()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString);

        var started = DateTimeOffset.UtcNow;
        var job = await host.EnqueueAsync(new JobRequest(EchoJobHandler.JobType, new EchoPayload { Lines = ["later"] }) { RunAfter = started.AddSeconds(1.2) });

        await Task.Delay(500);
        Assert.Equal(JobStatus.Queued, (await db.GetJobAsync(job.Id)).Status);
        var done = await db.WaitForTerminalAsync(job.Id);
        Assert.True(done.StartedAt >= started.AddSeconds(1.1), "ran before run_after");
    }

    [RequiresDatabaseFact]
    public async Task LockKey_SerializesJobs_WhileOtherKeysRunInParallel()
    {
        await using var db = await JobDatabase.CreateAsync();
        var log = new ExecutionLog();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString,
            services => services.AddSingleton<IJobHandler>(new DelegateHandler("t.locked", (ctx, ct) =>
                log.RunAsync(ctx, "h", TimeSpan.FromMilliseconds(80), ctx.Job.LockKey))),
            settings => settings["Aethera:Jobs:WorkerCount"] = "6");

        var jobs = new List<Job>();
        for (var i = 0; i < 4; i++)
            foreach (var key in new[] { "app:a", "app:b", "app:c" })
                jobs.Add(await host.EnqueueAsync(new JobRequest("t.locked") { LockKey = key }));
        foreach (var job in jobs) await db.WaitForStatusAsync(job.Id, Long, JobStatus.Succeeded);

        Assert.Equal(12, log.Starts.Count);
        foreach (var key in new[] { "app:a", "app:b", "app:c" })
            Assert.Equal(1, log.MaxConcurrentFor(key));
        Assert.True(log.MaxConcurrent > 1, "different lock keys should run in parallel");
        Assert.Equal(0, await db.ScalarAsync<long>("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())"));
    }

    [RequiresDatabaseFact]
    public async Task AdvisoryLock_IsHeldOnItsOwnConnection_WhileTheJobRuns()
    {
        await using var db = await JobDatabase.CreateAsync();
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, services =>
            services.AddSingleton<IJobHandler>(new DelegateHandler("t.hold", async (ctx, ct) =>
            {
                running.TrySetResult();
                await release.Task.WaitAsync(ct);
            })));

        var job = await host.EnqueueAsync(new JobRequest("t.hold") { LockKey = "app:hold" });
        await running.Task.WaitAsync(Long);
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted"));

        release.SetResult();
        await db.WaitForStatusAsync(job.Id, Long, JobStatus.Succeeded);
        await Eventually.UntilAsync(async () => await db.ScalarAsync<long>("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted") == 0);
    }

    [RequiresDatabaseFact]
    public async Task TwoHostsOnOneDatabase_NeverRunTheSameJobTwice()
    {
        await using var db = await JobDatabase.CreateAsync();
        var log = new ExecutionLog();
        void Handlers(IServiceCollection services, string name) =>
            services.AddSingleton<IJobHandler>(new DelegateHandler("t.once", (ctx, ct) => log.RunAsync(ctx, name, TimeSpan.FromMilliseconds(15))));

        await using var hostA = await JobTestHost.StartAsync(db.ConnectionString, s => Handlers(s, "A"));
        await using var hostB = await JobTestHost.StartAsync(db.ConnectionString, s => Handlers(s, "B"));

        var jobs = new List<Job>();
        for (var i = 0; i < 50; i++) jobs.Add(await (i % 2 == 0 ? hostA : hostB).EnqueueAsync(new JobRequest("t.once")));
        foreach (var job in jobs) await db.WaitForStatusAsync(job.Id, Long, JobStatus.Succeeded);

        var starts = log.Starts;
        Assert.Equal(50, starts.Count);
        Assert.Equal(50, starts.Select(s => s.JobId).Distinct().Count());
        Assert.All(starts, s => Assert.Equal(1, s.Attempt));
        Assert.Contains(starts, s => s.Host == "A");
        Assert.Contains(starts, s => s.Host == "B");
        Assert.All(await db.GetJobsAsync("t.once"), j => Assert.Equal(1, j.Attempt));
    }

    [RequiresDatabaseFact]
    public async Task TwoHostsWithLockKeys_NeverOverlapAcrossHosts()
    {
        await using var db = await JobDatabase.CreateAsync();
        var log = new ExecutionLog();
        void Handlers(IServiceCollection services, string name) =>
            services.AddSingleton<IJobHandler>(new DelegateHandler("t.keyed", (ctx, ct) => log.RunAsync(ctx, name, TimeSpan.FromMilliseconds(40), ctx.Job.LockKey)));

        await using var hostA = await JobTestHost.StartAsync(db.ConnectionString, s => Handlers(s, "A"));
        await using var hostB = await JobTestHost.StartAsync(db.ConnectionString, s => Handlers(s, "B"));

        var jobs = new List<Job>();
        for (var i = 0; i < 16; i++) jobs.Add(await (i % 2 == 0 ? hostA : hostB).EnqueueAsync(new JobRequest("t.keyed") { LockKey = "app:shared" }));
        foreach (var job in jobs) await db.WaitForStatusAsync(job.Id, Long, JobStatus.Succeeded);

        Assert.Equal(16, log.Starts.Count);
        Assert.Equal(1, log.MaxConcurrentFor("app:shared"));
        Assert.All(await db.GetJobsAsync("t.keyed"), j => Assert.Equal(1, j.Attempt)); // losing the advisory lock race never consumes an attempt
    }

    [RequiresDatabaseFact]
    public async Task Failure_IsRetriedWithBackoff_ThenFailsForGood()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString);

        var queued = await host.EnqueueEchoAsync(["a", "b"], failAt: 1, retryable: true, maxAttempts: 3);
        var job = await db.WaitForTerminalAsync(queued.Id);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempt);
        Assert.Equal(2, job.RetryNo); // bumps after each reported failure that was retried
        Assert.Contains("\"code\":\"echo.failed\"", Compact(job.ErrorJson));
        Assert.Contains("\"retryable\":true", Compact(job.ErrorJson));

        var text = string.Concat((await db.GetChunksAsync($"job:{job.Id}")).Select(c => c.Data));
        Assert.Equal(3, text.Split("Started system.echo").Length - 1);
        Assert.Contains("Retrying with backoff", text);
        Assert.Contains("Failed (echo.failed)", text);
    }

    [Fact]
    public void Backoff_GrowsExponentially_WithJitter_AndIsCapped()
    {
        var options = new JobsOptions { BaseBackoffSeconds = 5, MaxBackoffSeconds = 300 };
        var random = new Random(1);
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var nominal = Math.Min(300, 5 * Math.Pow(2, attempt - 1));
            for (var i = 0; i < 50; i++)
            {
                var seconds = options.Backoff(attempt, random).TotalSeconds;
                Assert.InRange(seconds, nominal * 0.8 - 1e-9, nominal * 1.2 + 1e-9);
            }
        }
    }

    [RequiresDatabaseFact]
    public async Task NonRetryableFailure_FailsAtOnce_EvenWithAttemptsLeft()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString);

        var queued = await host.EnqueueEchoAsync(["a"], failAt: 0, retryable: false, maxAttempts: 5);
        var job = await db.WaitForTerminalAsync(queued.Id);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Contains("\"retryable\":false", Compact(job.ErrorJson));
    }

    [RequiresDatabaseFact]
    public async Task UnknownType_FailsWithUnknownTypeAndNoRetry()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString);

        var queued = await host.EnqueueAsync(new JobRequest("nobody.handles.this") { MaxAttempts = 3 });
        var job = await db.WaitForTerminalAsync(queued.Id);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Contains("\"code\":\"job.unknown_type\"", Compact(job.ErrorJson));
    }

    [RequiresDatabaseFact]
    public async Task UnhandledException_FailsTheJob_WithoutLeakingTheMessage()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, services =>
            services.AddSingleton<IJobHandler>(new DelegateHandler("t.boom", (ctx, ct) =>
                throw new InvalidOperationException("Host=db;Password=hunter2-secret"))));

        var queued = await host.EnqueueAsync(new JobRequest("t.boom") { MaxAttempts = 3 });
        var job = await db.WaitForTerminalAsync(queued.Id);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Contains("job.handler_failed", Compact(job.ErrorJson));
        Assert.DoesNotContain("hunter2", Compact(job.ErrorJson));
        Assert.DoesNotContain("hunter2", string.Concat((await db.GetChunksAsync($"job:{job.Id}")).Select(c => c.Data)));
    }

    [RequiresDatabaseFact]
    public async Task CancelRequestedInTheDatabase_StopsARunningJobWithinAHeartbeat()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString);

        var queued = await host.EnqueueEchoAsync(Enumerable.Repeat("tick", 200).ToArray(), delayMs: 50);
        await db.WaitForStatusAsync(queued.Id, Long, JobStatus.Running);

        // No NOTIFY: only the lease heartbeat (0.2 s here) can discover it.
        var stamp = DateTimeOffset.UtcNow;
        await db.ExecuteAsync($"UPDATE jobs SET cancel_requested_at = now() WHERE id = '{queued.Id}'");
        var job = await db.WaitForTerminalAsync(queued.Id, TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.True(DateTimeOffset.UtcNow - stamp < TimeSpan.FromSeconds(2));
        Assert.Contains("Cancelled", string.Concat((await db.GetChunksAsync($"job:{job.Id}")).Select(c => c.Data)));
    }

    [RequiresDatabaseFact]
    public async Task Heartbeat_KeepsALongJobAlive_AndItRunsOnce()
    {
        await using var db = await JobDatabase.CreateAsync();
        var log = new ExecutionLog();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, services =>
            services.AddSingleton<IJobHandler>(new DelegateHandler("t.long", (ctx, ct) => log.RunAsync(ctx, "h", TimeSpan.FromSeconds(5)))));

        // The lease is 2 s; the job takes 5 s. The reaper (every 0.3 s) must not take it away.
        var queued = await host.EnqueueAsync(new JobRequest("t.long"));
        var job = await db.WaitForTerminalAsync(queued.Id, Long);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Single(log.Starts);
    }

    [RequiresDatabaseFact]
    public async Task ExpiredLease_IsRequeuedByTheReaper_AndRunsAgain()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, start: false);
        var queued = await host.EnqueueEchoAsync(["recovered"], maxAttempts: 3);
        // A worker claimed it and died: running, held by a ghost, lease in the past.
        await db.ExecuteAsync($"UPDATE jobs SET status = 'running', attempt = 1, started_at = now(), locked_by = 'ghost', lease_expires_at = now() - interval '1 minute' WHERE id = '{queued.Id}'");

        await host.StartAsync();
        var job = await db.WaitForTerminalAsync(queued.Id, Long);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, job.Attempt);
        Assert.Equal(0, job.RetryNo); // a crash redelivery is not a reported failure
        var text = string.Concat((await db.GetChunksAsync($"job:{job.Id}")).Select(c => c.Data));
        Assert.Contains("put back in the queue", text);
        Assert.Contains("recovered", text);
    }

    [RequiresDatabaseFact]
    public async Task ExpiredLease_OfANonResumableJobOrOneOutOfAttempts_FailsAsWorkerLost()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, services =>
            services.AddSingleton<IJobHandler>(new DelegateHandler("t.fragile", (ctx, ct) => Task.CompletedTask, resumable: false)), start: false);
        var fragile = await host.EnqueueAsync(new JobRequest("t.fragile") { MaxAttempts = 3 });
        var spent = await host.EnqueueEchoAsync(["x"], maxAttempts: 1);
        await db.ExecuteAsync($"UPDATE jobs SET status = 'running', attempt = 1, locked_by = 'ghost', lease_expires_at = now() - interval '1 minute' WHERE id IN ('{fragile.Id}', '{spent.Id}')");

        await host.StartAsync();
        foreach (var id in new[] { fragile.Id, spent.Id })
        {
            var job = await db.WaitForTerminalAsync(id, Long);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("\"code\":\"job.worker_lost\"", Compact(job.ErrorJson));
        }
    }

    [RequiresDatabaseFact]
    public async Task ExpiredLease_WithAPendingCancel_EndsCancelled()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, start: false);
        var queued = await host.EnqueueEchoAsync(["never"], maxAttempts: 3);
        await db.ExecuteAsync($"UPDATE jobs SET status = 'running', attempt = 1, locked_by = 'ghost', cancel_requested_at = now(), lease_expires_at = now() - interval '1 minute' WHERE id = '{queued.Id}'");

        await host.StartAsync();
        var job = await db.WaitForTerminalAsync(queued.Id, Long);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(1, job.Attempt); // not run again
    }

    [RequiresDatabaseFact]
    public async Task GracefulShutdown_ReleasesRunningJobs_AndAnotherHostFinishesThem()
    {
        await using var db = await JobDatabase.CreateAsync();
        var started = new TaskCompletionSource();
        var log = new ExecutionLog();
        void Handlers(IServiceCollection services, string name) =>
            services.AddSingleton<IJobHandler>(new DelegateHandler("t.shutdown", async (ctx, ct) =>
            {
                started.TrySetResult();
                await log.RunAsync(ctx, name, name == "A" ? TimeSpan.FromMinutes(5) : TimeSpan.FromMilliseconds(50));
            }));

        var hostA = await JobTestHost.StartAsync(db.ConnectionString, s => Handlers(s, "A"));
        var queued = await hostA.EnqueueAsync(new JobRequest("t.shutdown") { MaxAttempts = 2 });
        await started.Task.WaitAsync(Long);
        await db.WaitForStatusAsync(queued.Id, Long, JobStatus.Running);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await hostA.DisposeAsync();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "shutdown should not wait for the lease");

        var released = await db.GetJobAsync(queued.Id);
        Assert.Equal(JobStatus.Queued, released.Status);
        Assert.Equal(0, released.Attempt); // handing the job back does not consume the attempt
        Assert.Null(released.LockedBy);
        Assert.Null(released.LeaseExpiresAt);

        await using var hostB = await JobTestHost.StartAsync(db.ConnectionString, s => Handlers(s, "B"));
        var job = await db.WaitForTerminalAsync(queued.Id, Long);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(["A", "B"], log.Starts.Select(s => s.Host));
    }

    [RequiresDatabaseFact]
    public async Task ProgressAndSecrets_FlowThroughTheContext()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, services =>
            services.AddSingleton<IJobHandler>(new DelegateHandler("t.secret", async (ctx, ct) =>
            {
                ctx.Secrets.Register("s3cr3t-token");
                await ctx.Log.WriteAsync(Aethera.Domain.LogStream.Stdout, "Authorization: Bearer s3cr3t-token", ct);
                await ctx.Log.WriteAsync(Aethera.Domain.LogStream.Stderr, "pull failed for s3cr3t-token@registry", ct);
                throw JobFailedException.Permanent("t.denied", "Denied", "token s3cr3t-token was rejected");
            })));

        var queued = await host.EnqueueAsync(new JobRequest("t.secret"));
        var job = await db.WaitForTerminalAsync(queued.Id);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.DoesNotContain("s3cr3t-token", Compact(job.ErrorJson));
        Assert.Contains("********", Compact(job.ErrorJson));
        var chunks = await db.GetChunksAsync($"job:{job.Id}");
        Assert.DoesNotContain(chunks, c => c.Data.Contains("s3cr3t-token"));
        Assert.Contains(chunks, c => c.Stream == Aethera.Domain.LogStream.Stderr && c.Data.Contains("pull failed for ********@registry"));
        Assert.Contains(chunks, c => c.Data.Contains("Authorization: Bearer ********"));
    }
}
