using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Tests.ScheduledTasks;

/// <summary>The <c>scheduled_task.run</c> job end to end: API run-now, real workers, a fake agent transport.</summary>
public sealed class ScheduledTaskJobTests(ScheduledTasksFixture fixture) : IClassFixture<ScheduledTasksFixture>
{
    private static readonly ContainerExecOutcome Ok = new(0, "hello\n", false, TimeSpan.FromSeconds(2));

    private async Task<(Tenant Tenant, string AppId, string EnvId)> DeployedStackAsync(DesiredState state = DesiredState.Running)
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, envId, _, appId) = await tenant.CreateStackAsync();
        await fixture.Factory.DeployedAsync(appId, state);
        fixture.Transport.Commands.Clear();
        return (tenant, appId, envId);
    }

    private async Task<JsonNode> RunAsync(Tenant tenant, string taskId)
    {
        var accepted = await tenant.Developer.RunNowAsync(taskId);
        return await tenant.Developer.WaitForRunAsync(accepted["run"]!.Id());
    }

    private async Task<JsonNode> JobOfAsync(Tenant tenant, JsonNode run) => await tenant.Developer.GetJsonAsync($"/api/v1/jobs/{run["jobId"]!.GetValue<string>()}");

    private IReadOnlyList<NotificationEvent> EventsOf(string taskId) =>
        fixture.Notifier.Events.Where(e => e.AlertKey == $"task:{taskId}").ToList();

    [RequiresDatabaseFact]
    public async Task Success_runs_sh_c_in_the_workloads_container_and_records_the_outcome()
    {
        var (t, appId, envId) = await DeployedStackAsync();
        var task = await t.CreateTaskAsync(appId, extra: new { command = "php artisan schedule:run && echo done", timeoutSeconds = 120 });
        fixture.Transport.Containers(TaskTestHelpers.Container("c-old", ContainerRunState.Exited), TaskTestHelpers.Container("c-live")).Exec(_ => Ok);

        var before = DateTimeOffset.UtcNow;
        var run = await RunAsync(t, task.Id());

        Assert.Equal("succeeded", run["status"]!.GetValue<string>());
        Assert.Equal(0, run["exitCode"]!.GetValue<int>());
        Assert.Equal("hello\n", run["outputTail"]!.GetValue<string>());
        Assert.False(run["outputTruncated"]!.GetValue<bool>());
        Assert.NotNull(run["startedAt"]);
        Assert.NotNull(run["finishedAt"]);
        Assert.Equal("succeeded", (await JobOfAsync(t, run))["status"]!.GetValue<string>());

        var list = fixture.Transport.Received<ContainerListCommand>().Single();
        Assert.False(list.All);
        Assert.Equal([$"aethera.workload={appId}"], list.LabelFilters);
        var exec = Assert.Single(fixture.Transport.Received<ContainerExecCommand>());
        Assert.Equal("c-live", exec.Container); // the running one, not the exited one
        Assert.Equal(["sh", "-c", "php artisan schedule:run && echo done"], exec.Argv);
        Assert.Equal(ScheduledTaskTestExpect.TailBytes, exec.OutputTailBytes);
        var options = fixture.Transport.Commands.Single(c => c.Command is ContainerExecCommand).Options;
        Assert.Equal($"task:{run.Id()}", options.LogStreamId);
        Assert.NotNull(options.JobId);
        Assert.InRange(options.Deadline!.Value, before.AddSeconds(110), DateTimeOffset.UtcNow.AddSeconds(125));

        // The task remembers when it last ran, and the notifier heard about the success (it resolves an open failure alert).
        var shown = await t.Viewer.GetJsonAsync($"/api/v1/scheduled-tasks/{task.Id()}");
        Assert.NotNull(shown["lastRunAt"]);
        Assert.Equal(run.Id(), shown["lastRun"]!["id"]!.GetValue<string>());
        var published = Assert.Single(EventsOf(task.Id()));
        Assert.Equal(NotificationEvents.TaskSucceeded, published.Type);
        Assert.True(published.Resolves);
        Assert.Equal(envId, published.EnvironmentId.ToString());
        Assert.Equal(Guid.Parse(task.Id()), published.ResourceId);
        Assert.Equal($"/applications/{appId}#tasks", published.Path);
        Assert.Equal(t.Identity.OrganizationId, published.OrganizationId);
    }

    [RequiresDatabaseFact]
    public async Task A_non_zero_exit_fails_the_run_and_the_job_and_raises_the_alert()
    {
        var (t, appId, envId) = await DeployedStackAsync();
        var task = await t.CreateTaskAsync(appId);
        fixture.Transport.Containers(TaskTestHelpers.Container("c1")).Exec(_ => new ContainerExecOutcome(3, "boom: the cache is gone\n", true, TimeSpan.FromSeconds(1)));

        var run = await RunAsync(t, task.Id());

        Assert.Equal("failed", run["status"]!.GetValue<string>());
        Assert.Equal(3, run["exitCode"]!.GetValue<int>());
        Assert.Equal("boom: the cache is gone\n", run["outputTail"]!.GetValue<string>());
        Assert.True(run["outputTruncated"]!.GetValue<bool>());
        Assert.Contains("code 3", run["error"]!.GetValue<string>());
        var job = await JobOfAsync(t, run);
        Assert.Equal("failed", job["status"]!.GetValue<string>());
        Assert.Equal("scheduled_task.failed", job["error"]!["code"]!.GetValue<string>());

        var published = Assert.Single(EventsOf(task.Id()));
        Assert.Equal(NotificationEvents.TaskFailed, published.Type);
        Assert.False(published.Resolves);
        Assert.Equal(envId, published.EnvironmentId.ToString());
        Assert.NotEqual(AlertSeverity.Info, published.Severity);
        Assert.DoesNotContain("cache is gone", (published.Message ?? "") + published.Title + string.Join("", published.Details!.Values)); // never the output
        Assert.Equal("3", published.Details!["exitCode"]);
    }

    [RequiresDatabaseFact]
    public async Task A_success_after_a_failure_resolves_the_alert_key()
    {
        var (t, appId, _) = await DeployedStackAsync();
        var task = await t.CreateTaskAsync(appId);
        fixture.Transport.Containers(TaskTestHelpers.Container("c1")).Exec(_ => new ContainerExecOutcome(1, "", false, TimeSpan.Zero));
        await RunAsync(t, task.Id());
        fixture.Transport.Exec(_ => Ok);
        await RunAsync(t, task.Id());

        var events = EventsOf(task.Id());
        Assert.Equal([NotificationEvents.TaskFailed, NotificationEvents.TaskSucceeded], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal($"task:{task.Id()}", e.AlertKey));
        Assert.Equal([false, true], events.Select(e => e.Resolves));
    }

    [RequiresDatabaseFact]
    public async Task A_command_that_exceeds_its_timeout_fails_the_run()
    {
        var (t, appId, _) = await DeployedStackAsync();
        var task = await t.CreateTaskAsync(appId, extra: new { timeoutSeconds = 90 });
        fixture.Transport.Containers(TaskTestHelpers.Container("c1"))
            .Fail<ContainerExecCommand, ContainerExecOutcome>(CommandErrorCode.Timeout, "deadline exceeded");

        var run = await RunAsync(t, task.Id());

        Assert.Equal("failed", run["status"]!.GetValue<string>());
        Assert.Contains("did not finish within 90 s", run["error"]!.GetValue<string>());
        Assert.Null(run["exitCode"]);
        Assert.Equal(NotificationEvents.TaskFailed, Assert.Single(EventsOf(task.Id())).Type);
    }

    [RequiresDatabaseFact]
    public async Task An_agent_without_the_exec_capability_fails_with_an_update_hint()
    {
        var (t, appId, _) = await DeployedStackAsync();
        var task = await t.CreateTaskAsync(appId);
        fixture.Transport.Containers(TaskTestHelpers.Container("c1"))
            .Throw<ContainerExecCommand>(new ServerTransportException(TransportErrors.Unsupported, "The agent on this server does not support 'container.exec'. Update the agent."));

        var run = await RunAsync(t, task.Id());

        Assert.Equal("failed", run["status"]!.GetValue<string>());
        Assert.Contains("update the agent on this server", run["error"]!.GetValue<string>());
        Assert.Equal(NotificationEvents.TaskFailed, Assert.Single(EventsOf(task.Id())).Type);
    }

    [RequiresDatabaseFact]
    public async Task An_unreachable_server_fails_the_run()
    {
        var (t, appId, _) = await DeployedStackAsync();
        var task = await t.CreateTaskAsync(appId);
        fixture.Transport.Throw<ContainerListCommand>(new ServerTransportException(TransportErrors.AgentUnavailable, "No agent session."));

        var run = await RunAsync(t, task.Id());

        Assert.Equal("failed", run["status"]!.GetValue<string>());
        Assert.Contains("could not be reached", run["error"]!.GetValue<string>());
        Assert.Empty(fixture.Transport.Received<ContainerExecCommand>());
    }

    [RequiresDatabaseFact]
    public async Task A_stopped_workload_skips_the_run_without_touching_the_server()
    {
        var (t, appId, _) = await DeployedStackAsync(DesiredState.Stopped);
        var task = await t.CreateTaskAsync(appId);

        var run = await RunAsync(t, task.Id());

        Assert.Equal("skipped", run["status"]!.GetValue<string>());
        Assert.Contains("stopped", run["error"]!.GetValue<string>());
        Assert.Empty(fixture.Transport.Commands);
        Assert.Empty(EventsOf(task.Id()));
        Assert.Equal("succeeded", (await JobOfAsync(t, run))["status"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_workload_that_was_never_deployed_or_has_no_running_container_skips_the_run()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        fixture.Transport.Commands.Clear();
        var neverDeployed = await tenant.CreateTaskAsync(appId);
        var first = await RunAsync(tenant, neverDeployed.Id());
        Assert.Equal("skipped", first["status"]!.GetValue<string>());
        Assert.Contains("not been deployed", first["error"]!.GetValue<string>());

        await fixture.Factory.DeployedAsync(appId);
        fixture.Transport.Containers(TaskTestHelpers.Container("c1", ContainerRunState.Exited));
        var second = await RunAsync(tenant, neverDeployed.Id());
        Assert.Equal("skipped", second["status"]!.GetValue<string>());
        Assert.Contains("no running container", second["error"]!.GetValue<string>());

        fixture.Transport.Containers();
        Assert.Equal("skipped", (await RunAsync(tenant, neverDeployed.Id()))["status"]!.GetValue<string>());
        Assert.Empty(fixture.Transport.Received<ContainerExecCommand>());
        Assert.Empty(EventsOf(neverDeployed.Id()));
    }

    [RequiresDatabaseFact]
    public async Task Compose_applications_run_in_the_container_of_the_named_service_of_their_project()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var compose = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = "Shop Stack", environmentId = envId, serverId, sourceKind = "compose", compose = new { inlineContent = "services:\n  web:\n    image: nginx\n  worker:\n    image: busybox\n" },
        });
        await fixture.Factory.DeployedAsync(compose.Id());
        var task = await tenant.CreateTaskAsync(compose.Id(), extra: new { composeService = "worker", command = "./reindex" });
        var slug = compose["slug"]!.GetValue<string>();
        fixture.Transport.Commands.Clear();
        fixture.Transport.Containers(TaskTestHelpers.Container("worker-1")).Exec(_ => Ok);

        var run = await RunAsync(tenant, task.Id());

        Assert.Equal("succeeded", run["status"]!.GetValue<string>());
        var list = fixture.Transport.Received<ContainerListCommand>().Single();
        Assert.Equal([$"com.docker.compose.project={slug}-{compose.Id().Replace("-", "")[..8]}", "com.docker.compose.service=worker"], list.LabelFilters);
        Assert.Equal("worker-1", fixture.Transport.Received<ContainerExecCommand>().Single().Container);
    }

    [RequiresDatabaseFact]
    public async Task A_compose_service_without_a_running_container_is_skipped_with_its_name()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var compose = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = "Stack", environmentId = envId, serverId, sourceKind = "compose", compose = new { inlineContent = "services:\n  web:\n    image: nginx\n" },
        });
        await fixture.Factory.DeployedAsync(compose.Id());
        var task = await tenant.CreateTaskAsync(compose.Id(), extra: new { composeService = "web" });
        fixture.Transport.Containers();

        var run = await RunAsync(tenant, task.Id());

        Assert.Equal("skipped", run["status"]!.GetValue<string>());
        Assert.Contains("\"web\"", run["error"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task The_workloads_runtime_variables_reach_the_container_as_typed_variables_and_secrets_stay_secret()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, _) = await tenant.CreateStackAsync();
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "db", environmentId = envId, serverId, templateKey = "postgres" });
        await tenant.Developer.CreateAsync($"/api/v1/services/{service.Id()}/env-vars", new { key = "APP_MODE", value = "production" });
        await tenant.Developer.CreateAsync($"/api/v1/services/{service.Id()}/env-vars", new { key = "BUILD_ONLY", value = "x", isBuildTime = true, isRuntime = false });
        await fixture.Factory.DeployedAsync(service.Id());
        var task = await tenant.CreateTaskAsync(service.Id(), "services", new { command = "pg_dump -U \"$POSTGRES_USER\" app > /tmp/x" });
        string? password = null;
        fixture.Transport.Commands.Clear();
        fixture.Transport.Containers(TaskTestHelpers.Container("pg-1")).Exec(c =>
        {
            password = c.Env!.Single(e => e.Name == "POSTGRES_PASSWORD").Secret!.Value;
            // A careless command that prints its environment: the stored output must not keep the value.
            return new ContainerExecOutcome(0, $"POSTGRES_PASSWORD={password}\nAPP_MODE=production\n", false, TimeSpan.Zero);
        });

        var run = await RunAsync(tenant, task.Id());

        Assert.Equal("succeeded", run["status"]!.GetValue<string>());
        var exec = fixture.Transport.Received<ContainerExecCommand>().Single();
        Assert.NotNull(password);
        Assert.True(password!.Length >= 12);
        Assert.Equal(["sh", "-c", "pg_dump -U \"$POSTGRES_USER\" app > /tmp/x"], exec.Argv);
        Assert.DoesNotContain(exec.Argv, a => a.Contains(password));
        var plain = exec.Env!.Single(e => e.Name == "APP_MODE");
        Assert.Equal("production", plain.Plain);
        Assert.Null(plain.Secret);
        Assert.Null(exec.Env!.Single(e => e.Name == "POSTGRES_PASSWORD").Plain);
        Assert.DoesNotContain(exec.Env!, e => e.Name == "BUILD_ONLY");

        // Nothing the control plane stores or returns contains the value.
        var stored = await fixture.Factory.WithDbAsync(async db => (
            await db.ScheduledTaskRuns.AsNoTracking().Where(r => r.Id == Guid.Parse(run.Id())).Select(r => r.OutputTail + "|" + r.Error).SingleAsync(),
            string.Join("\n", await db.LogChunks.AsNoTracking().Select(c => c.Data).ToListAsync()),
            string.Join("\n", (await db.Jobs.AsNoTracking().Select(j => new { j.PayloadJson, j.ErrorJson, j.ResultJson }).ToListAsync()).Select(j => j.PayloadJson + j.ErrorJson + j.ResultJson))));
        Assert.Contains("POSTGRES_PASSWORD=********", stored.Item1);
        Assert.DoesNotContain(password, stored.Item1);
        Assert.DoesNotContain(password, stored.Item2);
        Assert.DoesNotContain(password, stored.Item3);
        Assert.DoesNotContain(password, run.ToJsonString());
        Assert.DoesNotContain(password, (await tenant.Viewer.GetJsonAsync($"/api/v1/jobs/{run["jobId"]!.GetValue<string>()}/logs")).ToJsonString());
        foreach (var audit in await tenant.AuditAsync()) Assert.DoesNotContain(password, audit.MetadataJson);
        Assert.All(fixture.Notifier.Events, e => Assert.DoesNotContain(password, e.Title + e.Message + string.Join("", e.Details?.Values ?? [])));
    }

    [RequiresDatabaseFact]
    public async Task A_run_whose_task_was_deleted_meanwhile_is_skipped()
    {
        var (t, appId, _) = await DeployedStackAsync();
        var task = await t.CreateTaskAsync(appId);
        // A pending run with a job, then the task goes away before the job starts: simulate by creating the run directly.
        var run = new ScheduledTaskRun { TaskId = Guid.Parse(task.Id()), WorkloadId = Guid.Parse(appId) };
        await fixture.Factory.WithDbAsync(async db =>
        {
            db.ScheduledTaskRuns.Add(run);
            await db.SaveChangesAsync();
            await db.ScheduledTasks.Where(x => x.Id == run.TaskId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow));
        });
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<Aethera.Domain.IJobQueue>();
        var job = await queue.EnqueueAsync(new JobRequest("scheduled_task.run", new { runId = run.Id }) { OrganizationId = t.Identity.OrganizationId });

        await Eventually.WaitAsync(async () => (await fixture.Factory.WithDbAsync(db => db.ScheduledTaskRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id))).Status == ScheduledTaskRunStatus.Skipped);
        Assert.Empty(fixture.Transport.Received<ContainerExecCommand>());
        _ = job;
    }

    [RequiresDatabaseFact]
    public async Task Finishing_a_run_prunes_the_history_to_the_newest_hundred()
    {
        var (t, appId, _) = await DeployedStackAsync();
        var task = await t.CreateTaskAsync(appId);
        var taskId = Guid.Parse(task.Id());
        await fixture.Factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < 104; i++)
                db.ScheduledTaskRuns.Add(new ScheduledTaskRun { TaskId = taskId, WorkloadId = Guid.Parse(appId), CreatedAt = DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(i), Status = ScheduledTaskRunStatus.Succeeded });
            await db.SaveChangesAsync();
        });
        fixture.Transport.Containers(TaskTestHelpers.Container("c1")).Exec(_ => Ok);

        var run = await RunAsync(t, task.Id());

        var count = await fixture.Factory.WithDbAsync(db => db.ScheduledTaskRuns.CountAsync(r => r.TaskId == taskId));
        Assert.Equal(100, count);
        Assert.Equal(run.Id(), (await t.Viewer.GetJsonAsync($"/api/v1/scheduled-tasks/{taskId}/runs?limit=1"))["items"]![0]!.Id());
    }
}

internal static class ScheduledTaskTestExpect
{
    public const int TailBytes = 16384;
}

internal static class Eventually
{
    public static Task WaitAsync(Func<Task<bool>> probe) => Jobs.Eventually.UntilAsync(probe, TimeSpan.FromSeconds(20), "the condition");
}
