using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Aethera.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aethera.Api.Tests.Jobs;

/// <summary>/metrics (content, token) and /ready (redis) with real workers behind them.</summary>
public sealed partial class JobObservabilityTests(JobsApiFixture fixture) : IClassFixture<JobsApiFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private HttpClient Admin() => fixture.Client(OrganizationRole.Admin);

    private static async Task<string> ScrapeAsync(HttpClient client, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("version=0.0.4", response.Content.Headers.ContentType?.ToString());
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Value of the sample <c>name{labels}</c> (labels as written in the exposition), or null when it is absent.</summary>
    private static double? Sample(string text, string name, string labels = "")
    {
        var series = labels.Length == 0 ? Regex.Escape(name) : Regex.Escape(name + "{" + labels + "}");
        var match = Regex.Match(text, $"^{series}(?: \\d+)? (?<v>[^\\s]+)$", RegexOptions.Multiline);
        return match.Success ? double.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture) : null;
    }

    [RequiresDatabaseFact]
    public async Task Metrics_ExposeJobRequestAndWorkerSeries()
    {
        var admin = Admin();
        var anonymous = fixture.Factory.CreateAnonymousClient(); // open when no token is configured
        var before = await ScrapeAsync(anonymous);
        var baseSucceeded = Sample(before, "aethera_jobs_finished_total", "type=\"system.echo\",status=\"succeeded\"") ?? 0;
        var baseFailed = Sample(before, "aethera_jobs_finished_total", "type=\"system.echo\",status=\"failed\"") ?? 0;

        var ids = new List<string>();
        for (var i = 0; i < 3; i++) ids.Add((await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { $"ok {i}" } })).Str("id"));
        ids.Add((await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "x" }, failAt = 0, retryable = false })).Str("id"));
        ids.Add((await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "x" }, failAt = 0, retryable = true, maxAttempts = 2 })).Str("id"));
        foreach (var id in ids) await JobsApiFixture.WaitForJobAsync(admin, id);

        var text = await ScrapeAsync(anonymous);
        Assert.Equal(baseSucceeded + 3, Sample(text, "aethera_jobs_finished_total", "type=\"system.echo\",status=\"succeeded\""));
        Assert.Equal(baseFailed + 2, Sample(text, "aethera_jobs_finished_total", "type=\"system.echo\",status=\"failed\""));
        Assert.True(Sample(text, "aethera_job_failures_total", "type=\"system.echo\",code=\"echo.failed\"") >= 3);
        Assert.True(Sample(text, "aethera_job_retries_total", "type=\"system.echo\"") >= 1);
        Assert.True(Sample(text, "aethera_job_duration_seconds_count", "type=\"system.echo\",status=\"succeeded\"") >= 3);
        Assert.True(Sample(text, "aethera_job_duration_seconds_count", "type=\"system.echo\",status=\"retried\"") >= 1);
        Assert.Contains("aethera_job_duration_seconds_bucket{type=\"system.echo\",status=\"succeeded\",le=\"0.05\"}", text);
        Assert.Contains("aethera_job_duration_seconds_sum{type=\"system.echo\",status=\"succeeded\"}", text);
        Assert.Equal(4, Sample(text, "aethera_job_workers"));
        Assert.True(Sample(text, "aethera_job_claims_total") >= 5);
        Assert.True(Sample(text, "aethera_log_chunks_written_total") >= 5);
        Assert.Contains("# TYPE aethera_job_duration_seconds histogram", text);
        Assert.Contains("# TYPE aethera_jobs_queued gauge", text);

        // HTTP request metrics come from the pipeline middleware
        Assert.Contains("# TYPE http_requests_received_total counter", text);
        Assert.Contains("http_request_duration_seconds_bucket", text);
        Assert.Matches("http_requests_received_total\\{[^}]*code=\"202\"[^}]*\\} \\d+", text);
    }

    [RequiresDatabaseFact]
    public async Task Metrics_QueueGauges_CountQueuedAndRunningJobsByType()
    {
        var admin = Admin();
        var key = "app:" + Guid.NewGuid();
        var running = (await JobsApiFixture.PostEchoAsync(admin, new { lines = Enumerable.Repeat("t", 400).ToArray(), delayMs = 50, lockKey = key })).Str("id");
        await JobsApiFixture.WaitForJobAsync(admin, running, "running");
        var queued = (await JobsApiFixture.PostEchoAsync(admin, new { lines = new[] { "later" }, lockKey = key })).Str("id");

        var anonymous = fixture.Factory.CreateAnonymousClient();
        var text = await ScrapeAsync(anonymous);
        Assert.True(Sample(text, "aethera_jobs_running", "type=\"system.echo\"") >= 1);
        Assert.True(Sample(text, "aethera_jobs_queued", "type=\"system.echo\"") >= 1);

        var developer = fixture.Client(OrganizationRole.Developer);
        await developer.PostAsync($"/api/v1/jobs/{queued}/cancel", null);
        await developer.PostAsync($"/api/v1/jobs/{running}/cancel", null);
        await JobsApiFixture.WaitForJobAsync(admin, running, "cancelled");
        await Eventually.UntilAsync(async () => await ScrapeAsync(anonymous) is var t && (Sample(t, "aethera_jobs_running", "type=\"system.echo\"") ?? 0) == 0, Wait);
    }

    [RequiresDatabaseFact]
    public async Task Metrics_CountLeaseReaps()
    {
        var queued = await EnqueueGhostAsync();
        await JobsApiFixture.WaitForJobAsync(Admin(), queued.ToString(), "succeeded");

        var text = await ScrapeAsync(fixture.Factory.CreateAnonymousClient());
        Assert.True(Sample(text, "aethera_job_lease_reaps_total", "outcome=\"requeued\"") >= 1);
    }

    private async Task<Guid> EnqueueGhostAsync()
    {
        Guid id;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(
                new JobRequest(Aethera.Infrastructure.Jobs.EchoJobHandler.JobType, new Aethera.Infrastructure.Jobs.EchoPayload { Lines = ["ghost"] })
                {
                    MaxAttempts = 3,
                    RunAfter = DateTimeOffset.UtcNow.AddMinutes(10), // keep the workers away until we have made it a ghost
                });
            id = job.Id;
        }

        await using var connection = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "UPDATE jobs SET status = 'running', attempt = 1, locked_by = 'ghost', run_after = now(), lease_expires_at = now() - interval '1 minute' WHERE id = $1", connection);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    [RequiresDatabaseFact]
    public async Task Metrics_AreProtectedByTheTokenWhenOneIsConfigured()
    {
        using var protectedFactory = JobsApiFixture.Configure(fixture.Factory, b => b.UseSetting("AETHERA_METRICS_TOKEN", "s3cret-metrics-token"));
        var client = protectedFactory.CreateClient();

        var missing = await client.GetAsync("/metrics");
        missing.AssertProblem(await missing.ReadJsonAsync(), 401, "auth.unauthenticated");
        Assert.Contains("Bearer", missing.Headers.WwwAuthenticate.ToString());

        foreach (var wrong in new[] { "wrong", "s3cret-metrics-toke", "s3cret-metrics-token-x", "" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {wrong}");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var basic = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        basic.Headers.TryAddWithoutValidation("Authorization", "Basic czNjcmV0LW1ldHJpY3MtdG9rZW4=");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(basic)).StatusCode);

        var text = await ScrapeAsync(client, "s3cret-metrics-token");
        Assert.Contains("aethera_jobs_queued", text);
        Assert.DoesNotContain("s3cret-metrics-token", text);

        // The documented fallback key works too, and the endpoint is not part of the public API document.
        using var alt = JobsApiFixture.Configure(fixture.Factory, b => b.UseSetting("Aethera:Metrics:Token", "other-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await alt.CreateClient().GetAsync("/metrics")).StatusCode);
        Assert.Contains("aethera_job_workers", await ScrapeAsync(alt.CreateClient(), "other-token"));
        var openApi = await fixture.Factory.CreateClient().GetStringAsync("/api/openapi/v1.json");
        Assert.DoesNotContain("/metrics", openApi);
    }

    [RequiresDatabaseFact]
    public async Task Ready_ReportsRedisAsOk_WhenItIsNotConfigured_AndTheWarningIsLogged()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = JobsApiFixture.Configure(fixture.Factory, b => b.ConfigureLogging(l => l.AddProvider(logs)));
        var response = await factory.CreateClient().GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("ready", body.Str("status"));
        Assert.Equal("ok", body["checks"]!.Str("database"));
        Assert.Equal("ok", body["checks"]!.Str("redis"));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Category == "Aethera.Jobs" && e.Message.Contains("Redis is not configured"));
    }

    [RequiresDatabaseFact]
    public async Task Ready_ReportsRedisUnavailable_WhenConfiguredButUnreachable()
    {
        using var factory = JobsApiFixture.Configure(fixture.Factory, b =>
            b.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,connectTimeout=300,syncTimeout=300,password=very-secret-redis-pw"));
        var response = await factory.CreateClient().GetAsync("/ready");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"redis\":\"unavailable\"", text);
        Assert.Contains("\"database\":\"ok\"", text);
        Assert.DoesNotContain("very-secret-redis-pw", text);

        // The job system keeps working on Postgres alone while Redis is down: publishing is best effort.
        var client = factory.CreateClientAs(OrganizationRole.Admin, null, fixture.Owner);
        var id = (await JobsApiFixture.PostEchoAsync(client, new { lines = new[] { "still works" } })).Str("id");
        Assert.Equal("succeeded", (await JobsApiFixture.WaitForJobAsync(client, id)).Str("status"));
    }
}
