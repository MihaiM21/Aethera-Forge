using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Http;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Jobs;

/// <summary>REST endpoints of the job system with real workers behind them.</summary>
public sealed class JobsApiTests(JobsApiFixture fixture) : IClassFixture<JobsApiFixture>
{
    private HttpClient Admin() => fixture.Client(OrganizationRole.Admin);

    private HttpClient Viewer() => fixture.Client(OrganizationRole.Viewer);

    [RequiresDatabaseFact]
    public async Task Echo_RunsThroughTheApi_AndTheLogCanBeReadPagedOrDownloaded()
    {
        var admin = Admin();
        var queued = await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "one", "two", "three", "four" }, delayMs = 80 });
        var id = queued.Str("id");

        Assert.Equal("system.echo", queued.Str("type"));
        Assert.Contains(queued.Str("status"), new[] { "queued", "running" });
        Assert.Equal($"/api/v1/jobs/{id}", queued["links"]!.Str("self"));
        Assert.Equal($"/api/v1/jobs/{id}/logs", queued["links"]!.Str("logs"));

        var done = await JobsApiFixture.WaitForJobAsync(admin, id);
        Assert.Equal("succeeded", done.Str("status"));
        Assert.Equal(4, done["result"]!["lines"]!.GetValue<int>());
        Assert.Null(done["error"]);
        Assert.Null(done["queuePosition"]);
        Assert.False(done["cancelRequested"]!.GetValue<bool>());
        Assert.Equal(1, done["attempt"]!.GetValue<int>());
        Assert.NotNull(done["startedAt"]);
        Assert.NotNull(done["finishedAt"]);
        Assert.Equal(fixture.Owner.UserId.ToString(), done.Str("createdBy"));

        // JSON pages, by sequence
        var all = await (await Viewer().GetAsync($"/api/v1/jobs/{id}/logs")).ReadJsonAsync();
        var items = all["items"]!.AsArray();
        Assert.True(items.Count >= 3, "lines were 80 ms apart, so they are separate chunks");
        Assert.True(all["ended"]!.GetValue<bool>());
        Assert.False(all["hasMore"]!.GetValue<bool>());
        Assert.Equal(items[^1]!["sequence"]!.GetValue<long>() + 1, all["nextSequence"]!.GetValue<long>());
        Assert.Equal("stdout", items[0]!.Str("stream"));
        Assert.Equal("job", items[0]!.Str("source"));
        var text = string.Concat(items.Select(i => i!.Str("text")));
        Assert.True(text.IndexOf("one", StringComparison.Ordinal) < text.IndexOf("four", StringComparison.Ordinal));

        var paged = new List<long>();
        long next = 0;
        for (var guard = 0; guard < 50; guard++)
        {
            var page = await (await Viewer().GetAsync($"/api/v1/jobs/{id}/logs?fromSequence={next}&limit=2")).ReadJsonAsync();
            paged.AddRange(page["items"]!.AsArray().Select(i => i!["sequence"]!.GetValue<long>()));
            next = page["nextSequence"]!.GetValue<long>();
            if (!page["hasMore"]!.GetValue<bool>()) break;
        }

        Assert.Equal(items.Select(i => i!["sequence"]!.GetValue<long>()), paged);

        var tail = await (await Viewer().GetAsync($"/api/v1/jobs/{id}/logs?fromSequence=9999")).ReadJsonAsync();
        Assert.Empty(tail["items"]!.AsArray());
        Assert.Equal(9999, tail["nextSequence"]!.GetValue<long>());

        // text download
        var download = await Viewer().GetAsync($"/api/v1/jobs/{id}/logs?download=true");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"job-{id}.log", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var body = await download.Content.ReadAsStringAsync();
        Assert.Equal(text, body);
        Assert.Contains("[aethera] Started system.echo", body);
    }

    [RequiresDatabaseFact]
    public async Task Logs_RejectBadParameters_AndUnknownJobs()
    {
        var viewer = Viewer();
        var id = (await JobsApiFixture.PostEchoAsync(Admin(), new { lines = new[] { "x" } })).Str("id");

        foreach (var query in new[] { "limit=0", "limit=1001", "fromSequence=-1", "bogus=1" })
        {
            var response = await viewer.GetAsync($"/api/v1/jobs/{id}/logs?{query}");
            response.AssertProblem(await response.ReadJsonAsync(), 400, "validation.invalid_parameter");
        }

        var missing = await viewer.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}/logs");
        missing.AssertProblem(await missing.ReadJsonAsync(), 404, "job.not_found");
    }

    [RequiresDatabaseFact]
    public async Task List_FiltersByStatusTypeResourceAndTime_AndPaginatesByKeyset()
    {
        var admin = Admin();
        var viewer = Viewer();
        var marker = DateTimeOffset.UtcNow;
        await Task.Delay(20);

        // Five echo jobs (two fail for good), plus a system job on a resource and a job of another type.
        var ok = new List<string>();
        for (var i = 0; i < 3; i++) ok.Add((await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { $"ok {i}" } })).Str("id"));
        var failed = new List<string>();
        for (var i = 0; i < 2; i++) failed.Add((await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "x" }, failAt = 0, retryable = false })).Str("id"));
        var resource = new JobResource("application", Guid.NewGuid());
        Job systemJob;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            systemJob = await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(
                new JobRequest("t.unhandled", new { a = 1 }) { Resource = resource, MaxAttempts = 2 });
        }

        foreach (var id in ok.Concat(failed).Append(systemJob.Id.ToString())) await JobsApiFixture.WaitForJobAsync(viewer, id);
        var since = Uri.EscapeDataString(marker.ToString("O"));

        async Task<JsonNode> List(string query) => await (await viewer.GetAsync($"/api/v1/jobs?createdAfter={since}&{query}")).ReadJsonAsync();
        static string[] Ids(JsonNode page) => page["items"]!.AsArray().Select(i => i!.Str("id")).ToArray();

        Assert.Equal(6, Ids(await List("")).Length);
        Assert.Equal(ok.Order(), Ids(await List("status=succeeded")).Order());
        Assert.Equal(failed.Append(systemJob.Id.ToString()).Order(), Ids(await List("status=failed")).Order());
        Assert.Equal(6, Ids(await List("status=succeeded,failed")).Length);
        Assert.Empty(Ids(await List("status=queued,running")));
        Assert.Equal(5, Ids(await List("type=system.echo")).Length);
        Assert.Equal([systemJob.Id.ToString()], Ids(await List("type=t.unhandled")));
        Assert.Equal([systemJob.Id.ToString()], Ids(await List($"resourceType=application&resourceId={resource.Id}")));
        Assert.Empty(Ids(await List($"resourceId={Guid.NewGuid()}")));
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        Assert.Empty(Ids(await (await viewer.GetAsync($"/api/v1/jobs?createdAfter={future}")).ReadJsonAsync()));
        Assert.Empty(Ids(await List($"createdBefore={since}")));
        Assert.Equal(6, Ids(await List($"startedAfter={since}&finishedAfter={since}")).Length);

        // keyset paging, newest first, no duplicates and no gaps
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await List($"limit=2{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}");
            seen.AddRange(Ids(page));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null && pages < 20);

        Assert.Equal(3, pages);
        Assert.Equal(6, seen.Distinct().Count());
        var newestFirst = (await List("limit=200"))["items"]!.AsArray().Select(i => DateTimeOffset.Parse(i!.Str("createdAt"))).ToList();
        Assert.Equal(newestFirst.OrderByDescending(t => t), newestFirst);
        Assert.Equal(Ids(await List("limit=200")), seen);

        var ascending = Ids(await List("sort=createdAt&limit=200"));
        Assert.Equal(seen.AsEnumerable().Reverse(), ascending);
        var ascendingSeen = new List<string>();
        cursor = null;
        do
        {
            var page = await List($"sort=createdAt&limit=4{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}");
            ascendingSeen.AddRange(Ids(page));
            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        Assert.Equal(ascending, ascendingSeen);
    }

    [RequiresDatabaseFact]
    public async Task List_RejectsBadParameters()
    {
        var viewer = Viewer();
        var first = await (await viewer.GetAsync("/api/v1/jobs?limit=1")).ReadJsonAsync();
        var cursor = first["nextCursor"]?.GetValue<string>();

        foreach (var query in new[] { "limit=0", "limit=201", "status=bogus", "sort=name", "unknown=1", "sort=createdAt,createdAt" })
        {
            var response = await viewer.GetAsync($"/api/v1/jobs?{query}");
            response.AssertProblem(await response.ReadJsonAsync(), 400, "validation.invalid_parameter");
        }

        var tampered = await viewer.GetAsync("/api/v1/jobs?cursor=not-a-cursor");
        tampered.AssertProblem(await tampered.ReadJsonAsync(), 400, "pagination.invalid_cursor");
        if (cursor is not null)
        {
            var otherFilter = await viewer.GetAsync($"/api/v1/jobs?status=failed&limit=1&cursor={Uri.EscapeDataString(cursor)}");
            otherFilter.AssertProblem(await otherFilter.ReadJsonAsync(), 400, "pagination.invalid_cursor");
        }
    }

    [RequiresDatabaseFact]
    public async Task Jobs_AreScopedToTheCallersOrganization_ButSystemJobsAreShared()
    {
        var mine = (await JobsApiFixture.PostEchoAsync(Admin(), new { lines = new[] { "mine" } })).Str("id");
        await JobsApiFixture.WaitForJobAsync(Admin(), mine);
        Job systemJob;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            systemJob = await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest(EchoJobHandler.JobType, new EchoPayload { Lines = ["system"] }));

        var stranger = fixture.Client(OrganizationRole.Owner, fixture.Other);
        foreach (var (method, path) in new[]
        {
            ("GET", $"/api/v1/jobs/{mine}"),
            ("GET", $"/api/v1/jobs/{mine}/logs"),
            ("POST", $"/api/v1/jobs/{mine}/cancel"),
            ("POST", $"/api/v1/jobs/{mine}/retry"),
        })
        {
            var response = await stranger.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            response.AssertProblem(await response.ReadJsonAsync(), 404, "job.not_found");
        }

        var list = await (await stranger.GetAsync("/api/v1/jobs?limit=200")).ReadJsonAsync();
        var ids = list["items"]!.AsArray().Select(i => i!.Str("id")).ToList();
        Assert.DoesNotContain(mine, ids);
        Assert.Contains(systemJob.Id.ToString(), ids);
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync($"/api/v1/jobs/{systemJob.Id}")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task GetJob_Unknown_Is404()
    {
        var response = await Viewer().GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");
        response.AssertProblem(await response.ReadJsonAsync(), 404, "job.not_found");
    }

    [RequiresDatabaseFact]
    public async Task Cancel_Queued_IsImmediate_Running_StopsWithinAHeartbeat_Finished_Is409()
    {
        var admin = Admin();
        var developer = fixture.Client(OrganizationRole.Developer);
        var key = "app:" + Guid.NewGuid();

        // A holds the lock key and keeps running; B waits behind it.
        var a = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Enumerable.Repeat("tick", 600).ToArray(), delayMs = 50, lockKey = key })).Str("id");
        await JobsApiFixture.WaitForJobAsync(admin, a, "running");
        var b = await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "never" }, lockKey = key });
        Assert.Equal("queued", b.Str("status"));
        Assert.Equal(1, b["queuePosition"]!.GetValue<int>());

        // queued: cancelled at once
        var cancelB = await developer.PostAsync($"/api/v1/jobs/{b.Str("id")}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancelB.StatusCode);
        var cancelledB = await cancelB.ReadJsonAsync();
        Assert.Equal("cancelled", cancelledB.Str("status"));
        Assert.NotNull(cancelledB["finishedAt"]);

        // running: flagged, then it stops (NOTIFY wakes the owner at once; the heartbeat is the fallback)
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var cancelA = await developer.PostAsync($"/api/v1/jobs/{a}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancelA.StatusCode);
        var flagged = await cancelA.ReadJsonAsync();
        Assert.True(flagged.Str("status") is "running" or "cancelled");
        var stopped = await JobsApiFixture.WaitForJobAsync(admin, a, "cancelled");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
        Assert.True(stopped["cancelRequested"]!.GetValue<bool>());
        Assert.NotNull(stopped["finishedAt"]);

        // B never ran
        Assert.Equal("cancelled", (await JobsApiFixture.GetJobAsync(admin, b.Str("id"))).Str("status"));
        Assert.Null((await JobsApiFixture.GetJobAsync(admin, b.Str("id")))["startedAt"]);

        // finished: 409
        var again = await developer.PostAsync($"/api/v1/jobs/{a}/cancel", null);
        again.AssertProblem(await again.ReadJsonAsync(), 409, "job.already_finished");

        var unknown = await developer.PostAsync($"/api/v1/jobs/{Guid.NewGuid()}/cancel", null);
        unknown.AssertProblem(await unknown.ReadJsonAsync(), 404, "job.not_found");
    }

    [RequiresDatabaseFact]
    public async Task Retry_CreatesALinkedJob_OnlyFromFailedOrCancelled()
    {
        var admin = Admin();
        var developer = fixture.Client(OrganizationRole.Developer);

        var failed = (await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "a", "b" }, failAt = 1, retryable = false, maxAttempts = 2, lockKey = "app:retry-" + Guid.NewGuid() })).Str("id");
        var failedJob = await JobsApiFixture.WaitForJobAsync(admin, failed);
        Assert.Equal("failed", failedJob.Str("status"));
        Assert.Equal("echo.failed", failedJob["error"]!.Str("code"));
        Assert.False(failedJob["error"]!["retryable"]!.GetValue<bool>());

        var retry = await developer.PostAsync($"/api/v1/jobs/{failed}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var created = await retry.ReadJsonAsync();
        Assert.NotEqual(failed, created.Str("id"));
        Assert.Equal(failed, created.Str("parentJobId"));
        Assert.Equal(1, created["retryNo"]!.GetValue<int>());
        Assert.Equal(2, created["maxAttempts"]!.GetValue<int>());
        Assert.Equal("system.echo", created.Str("type"));
        Assert.Equal($"/api/v1/jobs/{created.Str("id")}", retry.Headers.Location?.ToString());
        var retried = await JobsApiFixture.WaitForJobAsync(admin, created.Str("id"));
        Assert.Equal("failed", retried.Str("status")); // same payload, same failure: the point is a new, linked job
        Assert.Equal("failed", (await JobsApiFixture.GetJobAsync(admin, failed)).Str("status")); // history is immutable

        // a cancelled job can be retried too; a succeeded one cannot
        var ok = (await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "fine" } })).Str("id");
        await JobsApiFixture.WaitForJobAsync(admin, ok);
        var notRetryable = await developer.PostAsync($"/api/v1/jobs/{ok}/retry", null);
        notRetryable.AssertProblem(await notRetryable.ReadJsonAsync(), 409, "job.not_retryable");

        var slow = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Enumerable.Repeat("t", 300).ToArray(), delayMs = 50 })).Str("id");
        await JobsApiFixture.WaitForJobAsync(admin, slow, "running");
        var running = await developer.PostAsync($"/api/v1/jobs/{slow}/retry", null);
        running.AssertProblem(await running.ReadJsonAsync(), 409, "job.not_retryable");
        await developer.PostAsync($"/api/v1/jobs/{slow}/cancel", null);
        await JobsApiFixture.WaitForJobAsync(admin, slow, "cancelled");
        var afterCancel = await developer.PostAsync($"/api/v1/jobs/{slow}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, afterCancel.StatusCode);
        var again = (await afterCancel.ReadJsonAsync()).Str("id");
        await developer.PostAsync($"/api/v1/jobs/{again}/cancel", null);
        await JobsApiFixture.WaitForJobAsync(admin, again, "cancelled");
    }

    [RequiresDatabaseFact]
    public async Task QueuePosition_CountsTheJobsAhead()
    {
        var admin = Admin();
        var key = "app:" + Guid.NewGuid();
        var holder = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Enumerable.Repeat("t", 400).ToArray(), delayMs = 50, lockKey = key })).Str("id");
        await JobsApiFixture.WaitForJobAsync(admin, holder, "running");
        var first = await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "1" }, lockKey = key });
        var second = await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "2" }, lockKey = key });

        Assert.True(first["queuePosition"]!.GetValue<int>() < second["queuePosition"]!.GetValue<int>());
        var listed = await (await Viewer().GetAsync("/api/v1/jobs?status=queued&limit=200")).ReadJsonAsync();
        var byId = listed["items"]!.AsArray().ToDictionary(i => i!.Str("id"), i => i!["queuePosition"]!.GetValue<int>());
        Assert.Equal(first["queuePosition"]!.GetValue<int>(), byId[first.Str("id")]);
        Assert.Equal(second["queuePosition"]!.GetValue<int>(), byId[second.Str("id")]);

        var developer = fixture.Client(OrganizationRole.Developer);
        foreach (var id in new[] { first.Str("id"), second.Str("id"), holder }) await developer.PostAsync($"/api/v1/jobs/{id}/cancel", null);
        await JobsApiFixture.WaitForJobAsync(admin, holder, "cancelled");
    }

    [RequiresDatabaseFact]
    public async Task EchoEndpoint_ValidatesItsBody()
    {
        var admin = Admin();
        var response = await admin.PostAsJsonAsync("/api/v1/jobs/echo", new { delayMs = 999_999, maxAttempts = 0, failAt = -1 });
        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 422, "validation.failed");
        var pointers = body["errors"]!.AsArray().Select(e => e!.Str("pointer")).ToList();
        Assert.Contains("/delayMs", pointers);
        Assert.Contains("/maxAttempts", pointers);
        Assert.Contains("/failAt", pointers);

        var tooMany = await admin.PostAsJsonAsync("/api/v1/jobs/echo", new { lines = Enumerable.Repeat("x", 1001).ToArray() });
        tooMany.AssertProblem(await tooMany.ReadJsonAsync(), 422, "validation.failed");
    }

    [RequiresDatabaseFact]
    public async Task EchoEndpoint_IsOnlyMappedWhenEnabled()
    {
        using var disabled = JobsApiFixture.Configure(fixture.Factory, b => b.UseSetting("Aethera:Jobs:EnableEchoEndpoint", "false"));
        var response = await disabled.CreateClientAs(OrganizationRole.Admin, null, fixture.Owner)
            .PostAsJsonAsync("/api/v1/jobs/echo", new { lines = new[] { "x" } });
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, response.StatusCode.ToString());

        // Production defaults to off, Development and Testing to on.
        var production = new TestEnvironment("Production");
        var development = new TestEnvironment("Development");
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.False(Aethera.Api.Features.Jobs.JobsEndpoints.EchoEndpointEnabled(config, production));
        Assert.True(Aethera.Api.Features.Jobs.JobsEndpoints.EchoEndpointEnabled(config, development));
        Assert.True(Aethera.Api.Features.Jobs.JobsEndpoints.EchoEndpointEnabled(config, new TestEnvironment("Testing")));
    }

    private sealed class TestEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    // -------------------------------------------------------- role and scope matrix

    [RequiresDatabaseTheory]
    [InlineData("GET", "/jobs", "Viewer", "", 200, null)]
    [InlineData("GET", "/jobs", "Viewer", "read", 200, null)]
    [InlineData("GET", "/jobs", "Viewer", "write", 200, null)]
    [InlineData("GET", "/jobs", "Viewer", "*", 200, null)]
    [InlineData("GET", "/jobs", "Viewer", "deploy", 403, "auth.insufficient_scope")]
    [InlineData("GET", "/jobs", "Viewer", "secrets:read", 403, "auth.insufficient_scope")]
    [InlineData("GET", "/jobs/{job}", "Viewer", "", 200, null)]
    [InlineData("GET", "/jobs/{job}", "Viewer", "deploy", 403, "auth.insufficient_scope")]
    [InlineData("GET", "/jobs/{job}/logs", "Viewer", "", 200, null)]
    [InlineData("GET", "/jobs/{job}/logs", "Viewer", "read", 200, null)]
    [InlineData("GET", "/jobs/{job}/logs", "Viewer", "servers:write", 403, "auth.insufficient_scope")]
    [InlineData("POST", "/jobs/{job}/cancel", "Viewer", "", 403, "auth.forbidden")]
    [InlineData("POST", "/jobs/{job}/cancel", "Developer", "", 409, "job.already_finished")]
    [InlineData("POST", "/jobs/{job}/cancel", "Developer", "deploy", 409, "job.already_finished")]
    [InlineData("POST", "/jobs/{job}/cancel", "Developer", "read", 403, "auth.insufficient_scope")]
    [InlineData("POST", "/jobs/{job}/cancel", "Viewer", "deploy", 403, "auth.forbidden")]
    [InlineData("POST", "/jobs/{job}/cancel", "Owner", "admin", 409, "job.already_finished")]
    [InlineData("POST", "/jobs/{job}/retry", "Viewer", "", 403, "auth.forbidden")]
    [InlineData("POST", "/jobs/{job}/retry", "Developer", "", 409, "job.not_retryable")]
    [InlineData("POST", "/jobs/{job}/retry", "Developer", "write", 403, "auth.insufficient_scope")]
    [InlineData("POST", "/jobs/{job}/retry", "Admin", "deploy", 409, "job.not_retryable")]
    [InlineData("POST", "/jobs/echo", "Developer", "", 403, "auth.forbidden")]
    [InlineData("POST", "/jobs/echo", "Viewer", "", 403, "auth.forbidden")]
    [InlineData("POST", "/jobs/echo", "Admin", "", 202, null)]
    [InlineData("POST", "/jobs/echo", "Owner", "", 202, null)]
    [InlineData("POST", "/jobs/echo", "Admin", "deploy", 202, null)]
    [InlineData("POST", "/jobs/echo", "Admin", "read", 403, "auth.insufficient_scope")]
    public async Task RoleAndScopeMatrix(string method, string path, string role, string scopes, int expectedStatus, string? expectedCode)
    {
        var done = (await JobsApiFixture.PostEchoAsync(Admin(), new { lines = new[] { "matrix" } })).Str("id");
        await JobsApiFixture.WaitForJobAsync(Admin(), done);

        // "" = a browser session (role only); otherwise an API token with exactly these scopes
        var client = fixture.Client(Enum.Parse<OrganizationRole>(role), fixture.Owner, scopes.Length == 0 ? null : scopes.Split(','));
        var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1" + path.Replace("{job}", done));
        if (method == "POST") request.Content = JsonContent.Create(new { lines = new[] { "x" } });
        var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (expectedCode is not null) response.AssertProblem(await response.ReadJsonAsync(), expectedStatus, expectedCode);
        if (expectedCode == "auth.insufficient_scope")
            Assert.NotNull((await response.ReadJsonAsync().ConfigureAwait(false))["requiredScope"]);
    }

    [RequiresDatabaseFact]
    public async Task Anonymous_IsRejectedEverywhere()
    {
        var anonymous = fixture.Factory.CreateAnonymousClient();
        foreach (var (method, path) in new[]
        {
            ("GET", "/api/v1/jobs"),
            ("GET", $"/api/v1/jobs/{Guid.NewGuid()}"),
            ("GET", $"/api/v1/jobs/{Guid.NewGuid()}/logs"),
            ("POST", $"/api/v1/jobs/{Guid.NewGuid()}/cancel"),
            ("POST", $"/api/v1/jobs/{Guid.NewGuid()}/retry"),
            ("POST", "/api/v1/jobs/echo"),
        })
        {
            var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            response.AssertProblem(await response.ReadJsonAsync(), 401, "auth.unauthenticated");
        }
    }
}
