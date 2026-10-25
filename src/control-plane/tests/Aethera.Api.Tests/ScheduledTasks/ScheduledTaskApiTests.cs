using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Features.ScheduledTasks;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Tests.ScheduledTasks;

/// <summary>Pure rules: no database needed.</summary>
public sealed class ScheduledTaskRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("* * * * *", "UTC")]
    [InlineData("*/5 * * * *", "UTC")]
    [InlineData("0 3 * * *", "UTC")]
    [InlineData("0 0 * * 0", null)]
    [InlineData("@daily", "UTC")]
    public void Valid_schedules_pass(string cron, string? tz) => Assert.Null(ScheduledTaskRules.CheckSchedule(cron, tz, Now));

    [Theory]
    [InlineData("", "schedule.invalid")]
    [InlineData("* * * *", "schedule.invalid")]
    [InlineData("61 * * * *", "schedule.invalid")]
    [InlineData("not a cron", "schedule.invalid")]
    [InlineData("* * * * * *", "schedule.invalid")] // seconds field: more often than every minute
    [InlineData("*/1 * * * * *", "schedule.invalid")]
    public void Invalid_or_sub_minute_schedules_are_refused(string cron, string code) =>
        Assert.Equal(code, ScheduledTaskRules.CheckSchedule(cron, "UTC", Now)?.Code);

    [Fact]
    public void Unknown_time_zone_is_reported_on_the_time_zone_field()
    {
        var problem = ScheduledTaskRules.CheckSchedule("* * * * *", "Mars/Olympus", Now);
        Assert.Equal(("/timeZone", "time_zone.unknown"), (problem!.Value.Pointer, problem.Value.Code));
    }

    [Fact]
    public void Compose_service_names_follow_the_docker_service_pattern()
    {
        Assert.True(ScheduledTaskRules.IsValidComposeService("web"));
        Assert.True(ScheduledTaskRules.IsValidComposeService("api_1.worker-2"));
        Assert.False(ScheduledTaskRules.IsValidComposeService("-web"));
        Assert.False(ScheduledTaskRules.IsValidComposeService("we b"));
        Assert.False(ScheduledTaskRules.IsValidComposeService(new string('a', 201)));
    }
}

public sealed class ScheduledTaskApiTests(ScheduledTasksFixture fixture) : IClassFixture<ScheduledTasksFixture>
{
    private static async Task<IReadOnlyList<(string? Location, string Code)>> ErrorsAsync(HttpResponseMessage response) => await response.ValidationErrorsAsync();

    private async Task<(Tenant Tenant, string AppId)> StackAsync()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        return (tenant, appId);
    }

    private static object Body(object? name = null, object? command = null, object? schedule = null, object? extra = null)
    {
        var body = new JsonObject
        {
            ["name"] = name is null ? Tenant.Unique("task") : JsonValue.Create(name.ToString()), ["command"] = command is null ? "echo hi" : JsonValue.Create(command.ToString()),
            ["schedule"] = schedule is null ? "0 * * * *" : JsonValue.Create(schedule.ToString()),
        };
        if (extra is not null)
            foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(extra, Json.Options)!.AsObject()) body[key] = value?.DeepClone();
        return body;
    }

    // ---- create + validation ---------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Create_applies_defaults_and_returns_the_next_three_runs()
    {
        var (t, appId) = await StackAsync();
        var task = await t.Developer.CreateAsync($"/api/v1/applications/{appId}/scheduled-tasks", Body(name: "  Nightly cleanup ", schedule: "*/10 * * * *"));

        Assert.Equal("Nightly cleanup", task["name"]!.GetValue<string>());
        Assert.Equal("UTC", task["timeZone"]!.GetValue<string>());
        Assert.Equal(3600, task["timeoutSeconds"]!.GetValue<int>());
        Assert.True(task["enabled"]!.GetValue<bool>());
        Assert.Equal("application", task["workloadKind"]!.GetValue<string>());
        Assert.Null(task["composeService"]);
        Assert.Null(task["lastRun"]);
        var next = task["nextRuns"]!.AsArray().Select(n => n!.GetValue<DateTimeOffset>()).ToList();
        Assert.Equal(3, next.Count);
        Assert.All(next, n => Assert.Equal(0, n.Minute % 10));
        Assert.Equal(next[1] - next[0], TimeSpan.FromMinutes(10));
        Assert.Equal(next[0], task["nextRunAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(task["id"]!.GetValue<string>(), (await t.Viewer.GetJsonAsync($"/api/v1/scheduled-tasks/{task.Id()}")).Id());
    }

    [RequiresDatabaseFact]
    public async Task Create_disabled_has_no_next_run_but_still_previews_the_schedule()
    {
        var (t, appId) = await StackAsync();
        var task = await t.Developer.CreateAsync($"/api/v1/applications/{appId}/scheduled-tasks", Body(extra: new { enabled = false, timeZone = TestZones.Bucharest, timeoutSeconds = 30 }));
        Assert.False(task["enabled"]!.GetValue<bool>());
        Assert.Null(task["nextRunAt"]);
        Assert.Equal(3, task["nextRuns"]!.AsArray().Count);
        Assert.Equal(TestZones.Bucharest, task["timeZone"]!.GetValue<string>());
        Assert.Equal(30, task["timeoutSeconds"]!.GetValue<int>());
    }

    [RequiresDatabaseFact]
    public async Task Create_on_a_service_works_and_lists_under_the_service()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "db", environmentId = envId, serverId, templateKey = "postgres" });
        var task = await tenant.Developer.CreateAsync($"/api/v1/services/{service.Id()}/scheduled-tasks", Body(name: "vacuum"));
        Assert.Equal("service", task["workloadKind"]!.GetValue<string>());

        var onService = await tenant.Viewer.GetJsonAsync($"/api/v1/services/{service.Id()}/scheduled-tasks");
        Assert.Equal(task.Id(), Assert.Single(onService["items"]!.AsArray())!.Id());
        Assert.Empty((await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/scheduled-tasks"))["items"]!.AsArray());

        // An application id under /services (or the other way round) is not found.
        (await tenant.Viewer.GetAsync($"/api/v1/services/{appId}/scheduled-tasks")).StatusCode.Should404();
        (await tenant.Developer.PostAsync($"/api/v1/applications/{service.Id()}/scheduled-tasks", Body())).StatusCode.Should404();
    }

    [RequiresDatabaseFact]
    public async Task Create_validates_every_field()
    {
        var (t, appId) = await StackAsync();
        var url = $"/api/v1/applications/{appId}/scheduled-tasks";

        Assert.Contains(("/name", "required"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(name: " "))));
        Assert.Contains(("/name", "too_long"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(name: new string('n', 201)))));
        Assert.Contains(("/command", "required"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(command: "   "))));
        Assert.Contains(("/command", "too_long"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(command: new string('c', 4001)))));
        Assert.Contains(("/command", "pattern"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(command: "echo\0x"))));
        Assert.Contains(("/schedule", "schedule.invalid"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(schedule: "every day"))));
        Assert.Contains(("/schedule", "schedule.invalid"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(schedule: "*/5 * * * * *"))));
        Assert.Contains(("/timeZone", "time_zone.unknown"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(extra: new { timeZone = "Nowhere/City" }))));
        Assert.Contains(("/timeoutSeconds", "range"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(extra: new { timeoutSeconds = 0 }))));
        Assert.Contains(("/timeoutSeconds", "range"), await ErrorsAsync(await t.Developer.PostAsync(url, Body(extra: new { timeoutSeconds = 86401 }))));
        Assert.Contains(("/schedule", "required"), await ErrorsAsync(await t.Developer.PostAsync(url, new { name = "x", command = "y" })));

        // The limits themselves are fine.
        await t.Developer.CreateAsync(url, Body(command: new string('c', 4000), name: new string('n', 200), extra: new { timeoutSeconds = 86400 }));
        await t.Developer.CreateAsync(url, Body(extra: new { timeoutSeconds = 1 }));
    }

    [RequiresDatabaseFact]
    public async Task Compose_service_is_required_for_compose_applications_and_forbidden_otherwise()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        var compose = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = "stack", environmentId = envId, serverId, sourceKind = "compose", compose = new { inlineContent = "services:\n  web:\n    image: nginx\n" },
        });
        var composeUrl = $"/api/v1/applications/{compose.Id()}/scheduled-tasks";

        Assert.Contains(("/composeService", "required"), await ErrorsAsync(await tenant.Developer.PostAsync(composeUrl, Body())));
        Assert.Contains(("/composeService", "pattern"), await ErrorsAsync(await tenant.Developer.PostAsync(composeUrl, Body(extra: new { composeService = "bad name" }))));
        var task = await tenant.Developer.CreateAsync(composeUrl, Body(extra: new { composeService = "web" }));
        Assert.Equal("web", task["composeService"]!.GetValue<string>());

        Assert.Contains(("/composeService", "not_allowed"), await ErrorsAsync(await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/scheduled-tasks", Body(extra: new { composeService = "web" }))));

        // Patch: cannot be cleared on a compose application, can be changed.
        (await tenant.Developer.PatchAsync($"/api/v1/scheduled-tasks/{task.Id()}", new { composeService = (string?)null })).StatusCode.Should422();
        var patched = await tenant.Developer.PatchOkAsync($"/api/v1/scheduled-tasks/{task.Id()}", new { composeService = "worker" });
        Assert.Equal("worker", patched["composeService"]!.GetValue<string>());
        var plain = await tenant.CreateTaskAsync(appId);
        Assert.Contains(("/composeService", "not_allowed"), await ErrorsAsync(await tenant.Developer.PatchAsync($"/api/v1/scheduled-tasks/{plain.Id()}", new { composeService = "web" })));
    }

    [RequiresDatabaseFact]
    public async Task Name_is_unique_per_workload_among_live_tasks()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        var other = await tenant.CreateApplicationAsync(envId, serverId);
        var url = $"/api/v1/applications/{appId}/scheduled-tasks";
        var first = await tenant.Developer.CreateAsync(url, Body(name: "backup"));

        await (await tenant.Developer.PostAsync(url, Body(name: "backup"))).AssertProblemAsync(409, "scheduled_task.already_exists");
        await tenant.Developer.CreateAsync($"/api/v1/applications/{other.Id()}/scheduled-tasks", Body(name: "backup")); // another workload: fine

        var second = await tenant.Developer.CreateAsync(url, Body(name: "other"));
        await (await tenant.Developer.PatchAsync($"/api/v1/scheduled-tasks/{second.Id()}", new { name = "backup" })).AssertProblemAsync(409, "scheduled_task.already_exists");

        // A deleted task frees its name.
        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/scheduled-tasks/{first.Id()}")).StatusCode);
        await tenant.Developer.CreateAsync(url, Body(name: "backup"));
    }

    // ---- read, patch, delete ---------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Patch_recomputes_the_next_run_when_the_schedule_or_enabled_changes()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId, extra: new { schedule = "0 3 * * *" });
        var url = $"/api/v1/scheduled-tasks/{task.Id()}";
        Assert.Equal(3, task["nextRunAt"]!.GetValue<DateTimeOffset>().Hour);

        var daily = await t.Developer.PatchOkAsync(url, new { schedule = "30 14 * * *", timeZone = "UTC" });
        Assert.Equal((14, 30), (daily["nextRunAt"]!.GetValue<DateTimeOffset>().Hour, daily["nextRunAt"]!.GetValue<DateTimeOffset>().Minute));

        var disabled = await t.Developer.PatchOkAsync(url, new { enabled = false });
        Assert.Null(disabled["nextRunAt"]);
        Assert.Equal(3, disabled["nextRuns"]!.AsArray().Count);

        var enabled = await t.Developer.PatchOkAsync(url, new { enabled = true, command = "echo changed", timeoutSeconds = 120 });
        Assert.NotNull(enabled["nextRunAt"]);
        Assert.Equal("echo changed", enabled["command"]!.GetValue<string>());
        Assert.Equal(120, enabled["timeoutSeconds"]!.GetValue<int>());

        var zoned = await t.Developer.PatchOkAsync(url, new { timeZone = TestZones.Tokyo });
        Assert.Equal(TestZones.Tokyo, zoned["timeZone"]!.GetValue<string>());
        Assert.Equal(TimeSpan.FromHours(9), TimeZoneInfo.FindSystemTimeZoneById(TestZones.Tokyo).GetUtcOffset(DateTimeOffset.UtcNow));
        Assert.Equal(5, (zoned["nextRunAt"]!.GetValue<DateTimeOffset>().Hour)); // 14:30 in Tokyo is 05:30 UTC

        // An unrelated edit does not move the next run.
        var renamed = await t.Developer.PatchOkAsync(url, new { name = "renamed" });
        Assert.Equal(zoned["nextRunAt"]!.GetValue<DateTimeOffset>(), renamed["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [RequiresDatabaseFact]
    public async Task Patch_validates_and_rejects_nulls_for_required_fields()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId);
        var url = $"/api/v1/scheduled-tasks/{task.Id()}";
        Assert.Contains(("/schedule", "schedule.invalid"), await ErrorsAsync(await t.Developer.PatchAsync(url, new { schedule = "nope" })));
        Assert.Contains(("/timeZone", "time_zone.unknown"), await ErrorsAsync(await t.Developer.PatchAsync(url, new { timeZone = "No/Where" })));
        Assert.Contains(("/timeoutSeconds", "range"), await ErrorsAsync(await t.Developer.PatchAsync(url, new { timeoutSeconds = 100000 })));
        Assert.Contains(("/command", "too_long"), await ErrorsAsync(await t.Developer.PatchAsync(url, new { command = new string('x', 4001) })));
        Assert.Contains(("/name", "required"), await ErrorsAsync(await t.Developer.PatchAsync(url, new { name = (string?)null })));
        Assert.Contains(("/schedule", "required"), await ErrorsAsync(await t.Developer.PatchAsync(url, new { schedule = (string?)null })));
    }

    [RequiresDatabaseFact]
    public async Task Preview_returns_three_occurrences_or_the_validation_error()
    {
        var (t, _) = await StackAsync();
        var preview = await t.Viewer.GetJsonAsync("/api/v1/scheduled-tasks/preview?schedule=0%203%20*%20*%20*&timeZone=" + Uri.EscapeDataString(TestZones.Bucharest));
        Assert.Equal(3, preview["nextRuns"]!.AsArray().Count);
        Assert.Equal(TestZones.Bucharest, preview["timeZone"]!.GetValue<string>());
        var first = preview["nextRuns"]![0]!.GetValue<DateTimeOffset>();
        Assert.Equal(TimeSpan.FromDays(1), preview["nextRuns"]![1]!.GetValue<DateTimeOffset>() - first);

        var bad = await t.Viewer.GetAsync("/api/v1/scheduled-tasks/preview?schedule=nope");
        await bad.AssertProblemAsync(422, "validation.failed");
        await (await t.Viewer.GetAsync("/api/v1/scheduled-tasks/preview")).AssertProblemAsync(422, "validation.failed");
    }

    [RequiresDatabaseFact]
    public async Task Delete_is_soft_hides_the_task_and_frees_the_runs_list()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId);
        var id = Guid.Parse(task.Id());
        Assert.Equal(HttpStatusCode.NoContent, (await t.Developer.DeleteAsync($"/api/v1/scheduled-tasks/{id}")).StatusCode);

        (await t.Viewer.GetAsync($"/api/v1/scheduled-tasks/{id}")).StatusCode.Should404();
        (await t.Developer.DeleteAsync($"/api/v1/scheduled-tasks/{id}")).StatusCode.Should404();
        (await t.Developer.PostAsync($"/api/v1/scheduled-tasks/{id}/run", new { })).StatusCode.Should404();
        Assert.Empty((await t.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/scheduled-tasks"))["items"]!.AsArray());
        var row = await fixture.Factory.WithDbAsync(db => db.ScheduledTasks.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == id));
        Assert.NotNull(row.DeletedAt);
        Assert.Null(row.NextRunAt);
    }

    [RequiresDatabaseFact]
    public async Task List_filters_sorts_and_pages()
    {
        var (t, appId) = await StackAsync();
        foreach (var name in new[] { "bravo", "alpha", "charlie" }) await t.CreateTaskAsync(appId, extra: new { name, command = "echo " + name });
        var off = await t.CreateTaskAsync(appId, extra: new { name = "delta", enabled = false });
        var url = $"/api/v1/applications/{appId}/scheduled-tasks";

        var all = await t.Viewer.GetJsonAsync(url);
        Assert.Equal(["alpha", "bravo", "charlie", "delta"], all["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        var page = await t.Viewer.GetJsonAsync(url + "?limit=2");
        Assert.Equal(2, page["items"]!.AsArray().Count);
        var second = await t.Viewer.GetJsonAsync(url + "?limit=2&cursor=" + Uri.EscapeDataString(page["nextCursor"]!.GetValue<string>()));
        Assert.Equal(["charlie", "delta"], second["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal([off.Id()], (await t.Viewer.GetJsonAsync(url + "?enabled=false"))["items"]!.AsArray().Select(i => i!.Id()));
        Assert.Single((await t.Viewer.GetJsonAsync(url + "?q=echo%20bravo"))["items"]!.AsArray());
        (await t.Viewer.GetAsync(url + "?bogus=1")).StatusCode.Should400();
    }

    // ---- roles, scopes, tenancy ------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Roles_and_token_scopes_are_enforced()
    {
        var (t, appId) = await StackAsync();
        var url = $"/api/v1/applications/{appId}/scheduled-tasks";
        var task = await t.CreateTaskAsync(appId);
        var taskUrl = $"/api/v1/scheduled-tasks/{task.Id()}";

        // Viewers read, they do not change anything or run anything.
        Assert.Equal(HttpStatusCode.OK, (await t.Viewer.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Viewer.GetAsync(taskUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Viewer.GetAsync(taskUrl + "/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsync(url, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PatchAsync(taskUrl, new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.DeleteAsync(taskUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsync(taskUrl + "/run", new { })).StatusCode);

        // Tokens: write for the definition, deploy for running.
        var readOnly = t.Token(OrganizationRole.Developer, "read");
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync(url, Body())).StatusCode);
        var writer = t.Token(OrganizationRole.Developer, "write");
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsync(url, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writer.PatchAsync(taskUrl, new { name = "renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsync(taskUrl + "/run", new { })).StatusCode);
        var deployer = t.Token(OrganizationRole.Developer, "deploy");
        Assert.Equal(HttpStatusCode.Forbidden, (await deployer.PostAsync(url, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deployer.DeleteAsync(taskUrl)).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Other_organizations_see_nothing()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId);
        var other = await fixture.NewTenantAsync();
        var url = $"/api/v1/scheduled-tasks/{task.Id()}";

        foreach (var response in new[]
        {
            await other.Owner.GetAsync(url), await other.Owner.PatchAsync(url, new { name = "x" }), await other.Owner.DeleteAsync(url),
            await other.Owner.PostAsync(url + "/run", new { }), await other.Owner.GetAsync(url + "/runs"),
            await other.Owner.GetAsync($"/api/v1/applications/{appId}/scheduled-tasks"), await other.Owner.PostAsync($"/api/v1/applications/{appId}/scheduled-tasks", Body()),
        })
            await response.AssertProblemAsync(404, response.RequestMessage!.RequestUri!.AbsolutePath.Contains("/applications/") ? "application.not_found" : "scheduled_task.not_found");

        // Still there for its owner, untouched.
        Assert.Equal(task["name"]!.GetValue<string>(), (await t.Owner.GetJsonAsync(url))["name"]!.GetValue<string>());
    }

    // ---- run now, runs ---------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Run_now_answers_202_with_the_run_and_its_job_and_refuses_overlap()
    {
        var (t, appId) = await StackAsync(); // not deployed: the job will skip the run, which is all this test needs
        var task = await t.CreateTaskAsync(appId);
        var accepted = await t.Developer.RunNowAsync(task.Id());

        var run = accepted["run"]!;
        Assert.Equal("manual", run["trigger"]!.GetValue<string>());
        Assert.Equal(task.Id(), run["taskId"]!.GetValue<string>());
        Assert.Equal(accepted["job"]!["id"]!.GetValue<string>(), run["jobId"]!.GetValue<string>());
        Assert.Equal("scheduled_task.run", accepted["job"]!["type"]!.GetValue<string>());
        Assert.Equal($"task:{run.Id()}", run["logStreamId"]!.GetValue<string>());
        var finished = await t.Developer.WaitForRunAsync(run.Id());
        Assert.Equal("skipped", finished["status"]!.GetValue<string>());

        // A run that is still going blocks a manual one.
        await fixture.Factory.WithDbAsync(async db =>
        {
            db.ScheduledTaskRuns.Add(new ScheduledTaskRun { TaskId = Guid.Parse(task.Id()), WorkloadId = Guid.Parse(appId), Status = ScheduledTaskRunStatus.Running, StartedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        await (await t.Developer.PostAsync($"/api/v1/scheduled-tasks/{task.Id()}/run", new { })).AssertProblemAsync(409, "scheduled_task.already_running");

        var audits = await t.AuditAsync(task.Id(), "scheduled_task.run_requested");
        Assert.Single(audits);
    }

    [RequiresDatabaseFact]
    public async Task Runs_are_listed_newest_first_filtered_and_paged_and_carry_the_last_run_on_the_task()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId);
        var taskId = Guid.Parse(task.Id());
        var now = DateTimeOffset.UtcNow;
        await fixture.Factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < 5; i++)
                db.ScheduledTaskRuns.Add(new ScheduledTaskRun
                {
                    TaskId = taskId, WorkloadId = Guid.Parse(appId), CreatedAt = now.AddMinutes(-10 + i), StartedAt = now.AddMinutes(-10 + i), FinishedAt = now.AddMinutes(-10 + i).AddSeconds(2),
                    Status = i % 2 == 0 ? ScheduledTaskRunStatus.Succeeded : ScheduledTaskRunStatus.Failed, ExitCode = i % 2 == 0 ? 0 : 1, OutputTail = "line " + i,
                });
            await db.SaveChangesAsync();
        });

        var url = $"/api/v1/scheduled-tasks/{taskId}/runs";
        var page = await t.Viewer.GetJsonAsync(url + "?limit=2");
        Assert.Equal(["line 4", "line 3"], page["items"]!.AsArray().Select(i => i!["outputTail"]!.GetValue<string>()));
        var next = await t.Viewer.GetJsonAsync(url + "?limit=2&cursor=" + Uri.EscapeDataString(page["nextCursor"]!.GetValue<string>()));
        Assert.Equal(["line 2", "line 1"], next["items"]!.AsArray().Select(i => i!["outputTail"]!.GetValue<string>()));
        var failed = await t.Viewer.GetJsonAsync(url + "?status=failed");
        Assert.Equal(2, failed["items"]!.AsArray().Count);
        Assert.All(failed["items"]!.AsArray(), i => Assert.Equal(2000, i!["durationMs"]!.GetValue<int>()));
        (await t.Viewer.GetAsync(url + "?status=bogus")).StatusCode.Should400();

        var summary = (await t.Viewer.GetJsonAsync($"/api/v1/scheduled-tasks/{taskId}"))["lastRun"]!;
        Assert.Equal("succeeded", summary["status"]!.GetValue<string>());
        Assert.Equal(0, summary["exitCode"]!.GetValue<int>());
        var single = await t.Viewer.GetJsonAsync($"/api/v1/scheduled-task-runs/{(await t.Viewer.GetJsonAsync(url + "?limit=1"))["items"]![0]!.Id()}");
        Assert.Equal("line 4", single["outputTail"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Run_logs_are_read_from_the_runs_stream_and_hidden_from_other_organizations()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId);
        var run = new ScheduledTaskRun { TaskId = Guid.Parse(task.Id()), WorkloadId = Guid.Parse(appId), Status = ScheduledTaskRunStatus.Succeeded, FinishedAt = DateTimeOffset.UtcNow };
        await fixture.Factory.WithDbAsync(async db =>
        {
            db.ScheduledTaskRuns.Add(run);
            db.LogChunks.AddRange(
                new LogChunk { StreamId = $"task:{run.Id}", Sequence = 1, Data = "first\n", Source = LogSource.Deploy },
                new LogChunk { StreamId = $"task:{run.Id}", Sequence = 2, Data = "second\n", Source = LogSource.Deploy });
            await db.SaveChangesAsync();
        });

        var page = await t.Viewer.GetJsonAsync($"/api/v1/scheduled-task-runs/{run.Id}/logs");
        Assert.Equal(["first\n", "second\n"], page["items"]!.AsArray().Select(i => i!["text"]!.GetValue<string>()));
        Assert.True(page["ended"]!.GetValue<bool>());
        Assert.Equal(3, page["nextSequence"]!.GetValue<int>());
        Assert.Single((await t.Viewer.GetJsonAsync($"/api/v1/scheduled-task-runs/{run.Id}/logs?fromSequence=2"))["items"]!.AsArray());
        Assert.Equal("first\nsecond\n", await (await t.Viewer.GetAsync($"/api/v1/scheduled-task-runs/{run.Id}/logs?download=true")).Content.ReadAsStringAsync());

        var other = await fixture.NewTenantAsync();
        await (await other.Owner.GetAsync($"/api/v1/scheduled-task-runs/{run.Id}/logs")).AssertProblemAsync(404, "scheduled_task_run.not_found");
        await (await other.Owner.GetAsync($"/api/v1/scheduled-task-runs/{run.Id}")).AssertProblemAsync(404, "scheduled_task_run.not_found");
    }

    // ---- audit -----------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Create_update_delete_are_audited_without_environment_values()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "db", environmentId = envId, serverId, templateKey = "postgres" });
        var task = await tenant.CreateTaskAsync(service.Id(), "services", new { command = "psql -c 'vacuum'" });
        await tenant.Developer.PatchOkAsync($"/api/v1/scheduled-tasks/{task.Id()}", new { command = "psql -c 'analyze'", enabled = false });
        await tenant.Developer.DeleteAsync($"/api/v1/scheduled-tasks/{task.Id()}");

        var events = await tenant.AuditAsync(task.Id());
        Assert.Equal(["scheduled_task.created", "scheduled_task.deleted", "scheduled_task.updated"], events.Select(e => e.Action).Order());
        var created = events.Single(e => e.Action == "scheduled_task.created");
        Assert.Contains("psql -c 'vacuum'", created.MetadataJson.Replace("\\u0027", "'"));
        var updated = events.Single(e => e.Action == "scheduled_task.updated");
        Assert.Contains("command", updated.MetadataJson);
        Assert.Contains("enabled", updated.MetadataJson);

        // The service's generated secret never shows up in the audit trail of its tasks.
        var secrets = await fixture.Factory.WithDbAsync(db => db.EnvironmentVariables.AsNoTracking().Where(v => v.WorkloadId == Guid.Parse(service.Id())).Select(v => v.Key).ToListAsync());
        Assert.Contains("POSTGRES_PASSWORD", secrets);
        Assert.All(events, e => Assert.DoesNotContain("POSTGRES_PASSWORD", e.MetadataJson));
    }
}

internal static class StatusAssertions
{
    public static void Should404(this HttpStatusCode code) => Assert.Equal(HttpStatusCode.NotFound, code);

    public static void Should400(this HttpStatusCode code) => Assert.Equal(HttpStatusCode.BadRequest, code);

    public static void Should422(this HttpStatusCode code) => Assert.Equal(HttpStatusCode.UnprocessableEntity, code);
}
