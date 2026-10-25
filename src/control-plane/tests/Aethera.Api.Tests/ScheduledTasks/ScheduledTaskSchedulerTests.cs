using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Scheduling;
using Aethera.Infrastructure.ScheduledTasks;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.ScheduledTasks;

/// <summary>The scheduler against a real database; job workers are off, so queued runs stay pending and can be inspected.</summary>
public sealed class ScheduledTaskSchedulerTests(IdleScheduledTasksFixture fixture) : IClassFixture<IdleScheduledTasksFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private ScheduledTaskScheduler Scheduler => fixture.Factory.Services.GetRequiredService<ScheduledTaskScheduler>();

    private async Task<(Tenant Tenant, string AppId)> StackAsync()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        return (tenant, appId);
    }

    private Task SetDueAsync(string taskId, DateTimeOffset? nextRunAt) =>
        fixture.Factory.WithDbAsync(db => db.ScheduledTasks.Where(t => t.Id == Guid.Parse(taskId)).ExecuteUpdateAsync(s => s.SetProperty(t => t.NextRunAt, nextRunAt)));

    private Task<List<ScheduledTaskRun>> RunsAsync(string taskId) =>
        fixture.Factory.WithDbAsync(db => db.ScheduledTaskRuns.AsNoTracking().Where(r => r.TaskId == Guid.Parse(taskId)).OrderBy(r => r.CreatedAt).ToListAsync());

    private Task<ScheduledTask> TaskAsync(string taskId) =>
        fixture.Factory.WithDbAsync(db => db.ScheduledTasks.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == Guid.Parse(taskId)));

    [RequiresDatabaseFact]
    public async Task Due_tasks_are_enqueued_and_rescheduled_after_now_and_not_due_tasks_are_left_alone()
    {
        var (t, appId) = await StackAsync();
        var due = await t.CreateTaskAsync(appId, extra: new { name = "due", schedule = "*/15 * * * *" });
        var later = await t.CreateTaskAsync(appId, extra: new { name = "later" });
        var off = await t.CreateTaskAsync(appId, extra: new { name = "off", enabled = false });
        await SetDueAsync(due.Id(), Now.AddMinutes(-1));
        await SetDueAsync(later.Id(), Now.AddHours(1));
        await SetDueAsync(off.Id(), Now.AddMinutes(-1)); // disabled: never claimed even when "due"

        var result = await Scheduler.TickAsync(Now, CancellationToken.None);
        Assert.True(result.Queued >= 1);

        var dueRuns = await RunsAsync(due.Id());
        var run = Assert.Single(dueRuns);
        Assert.Equal(ScheduledTaskTrigger.Scheduled, run.Trigger);
        Assert.Equal(ScheduledTaskRunStatus.Pending, run.Status);
        Assert.NotNull(run.JobId);
        var job = await fixture.Factory.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == run.JobId));
        Assert.Equal("scheduled_task.run", job.Type);
        Assert.Equal(("scheduled_task", Guid.Parse(due.Id())), (job.ResourceType, job.ResourceId));

        var after = await TaskAsync(due.Id());
        Assert.Equal(CronSchedule.Parse("*/15 * * * *").Next(Now), after.NextRunAt);
        Assert.True(after.NextRunAt > Now);

        Assert.Empty(await RunsAsync(later.Id()));
        Assert.Empty(await RunsAsync(off.Id()));
        Assert.Equal(Now.AddHours(1), (await TaskAsync(later.Id())).NextRunAt!.Value, TimeSpan.FromMilliseconds(1));

        // Nothing is due any more: a second pass does not enqueue it again.
        await Scheduler.TickAsync(Now, CancellationToken.None);
        Assert.Single(await RunsAsync(due.Id()));
    }

    [RequiresDatabaseFact]
    public async Task Concurrent_passes_never_enqueue_a_task_twice()
    {
        var (t, appId) = await StackAsync();
        var tasks = new List<string>();
        for (var i = 0; i < 12; i++) tasks.Add((await t.CreateTaskAsync(appId, extra: new { name = "t" + i })).Id());
        foreach (var id in tasks) await SetDueAsync(id, Now.AddSeconds(-30));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Scheduler.TickAsync(Now, CancellationToken.None)));

        foreach (var id in tasks) Assert.Single(await RunsAsync(id));
    }

    [RequiresDatabaseFact]
    public async Task A_long_outage_gives_one_catch_up_run_not_one_per_missed_occurrence()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId, extra: new { schedule = "0 * * * *" });
        await SetDueAsync(task.Id(), Now.AddDays(-3));

        await Scheduler.TickAsync(Now, CancellationToken.None);
        await Scheduler.TickAsync(Now.AddSeconds(30), CancellationToken.None);

        Assert.Single(await RunsAsync(task.Id()));
        var next = (await TaskAsync(task.Id())).NextRunAt!.Value;
        Assert.True(next > Now);
        Assert.True(next <= Now.AddHours(1));
    }

    [RequiresDatabaseFact]
    public async Task A_run_whose_predecessor_is_still_running_is_recorded_as_skipped_without_a_job()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId, extra: new { schedule = "* * * * *" });
        var taskId = Guid.Parse(task.Id());
        await fixture.Factory.WithDbAsync(async db =>
        {
            db.ScheduledTaskRuns.Add(new ScheduledTaskRun { TaskId = taskId, WorkloadId = Guid.Parse(appId), Status = ScheduledTaskRunStatus.Running, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2) });
            await db.SaveChangesAsync();
        });
        await SetDueAsync(task.Id(), Now.AddSeconds(-5));

        var result = await Scheduler.TickAsync(Now, CancellationToken.None);
        Assert.True(result.Skipped >= 1);

        var runs = await RunsAsync(task.Id());
        Assert.Equal(2, runs.Count);
        var skipped = runs.Single(r => r.Status == ScheduledTaskRunStatus.Skipped);
        Assert.Null(skipped.JobId);
        Assert.Equal(ScheduledTaskTrigger.Scheduled, skipped.Trigger);
        Assert.Contains("still running", skipped.Error);
        Assert.NotNull(skipped.FinishedAt);
        Assert.True((await TaskAsync(task.Id())).NextRunAt > Now); // the schedule moved on regardless
    }

    [RequiresDatabaseFact]
    public async Task A_stale_running_run_does_not_block_forever()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId, extra: new { schedule = "* * * * *", timeoutSeconds = 60 });
        await fixture.Factory.WithDbAsync(async db =>
        {
            // Older than the timeout plus the margin: its job is long gone.
            db.ScheduledTaskRuns.Add(new ScheduledTaskRun { TaskId = Guid.Parse(task.Id()), WorkloadId = Guid.Parse(appId), Status = ScheduledTaskRunStatus.Running, CreatedAt = DateTimeOffset.UtcNow.AddHours(-3) });
            await db.SaveChangesAsync();
        });
        await SetDueAsync(task.Id(), Now.AddSeconds(-5));

        await Scheduler.TickAsync(Now, CancellationToken.None);

        var runs = await RunsAsync(task.Id());
        Assert.Contains(runs, r => r.Status == ScheduledTaskRunStatus.Pending && r.JobId is not null);
        Assert.DoesNotContain(runs, r => r.Status == ScheduledTaskRunStatus.Skipped);
    }

    [RequiresDatabaseFact]
    public async Task Runs_whose_job_ended_without_finishing_them_are_failed()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId);
        var run = await t.Developer.RunNowAsync(task.Id());
        var runId = Guid.Parse(run["run"]!.Id());
        var jobId = Guid.Parse(run["job"]!.Id());
        // The job engine reaped the job (worker lost) while the run was still marked running.
        await fixture.Factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"UPDATE jobs SET status = 'failed', finished_at = now() WHERE id = {jobId}");
            await db.ScheduledTaskRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ScheduledTaskRunStatus.Running));
        });

        var result = await Scheduler.TickAsync(Now, CancellationToken.None);
        Assert.True(result.Reaped >= 1);
        var reaped = (await RunsAsync(task.Id())).Single();
        Assert.Equal(ScheduledTaskRunStatus.Failed, reaped.Status);
        Assert.NotNull(reaped.FinishedAt);

        // ... and the next manual run is allowed again.
        await t.Developer.RunNowAsync(task.Id());
    }

    [RequiresDatabaseFact]
    public async Task Tasks_of_deleted_workloads_and_deleted_tasks_are_not_scheduled()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, envId, _, appId) = await tenant.CreateStackAsync();
        var gone = await tenant.CreateApplicationAsync(envId, serverId);
        var tGone = await tenant.CreateTaskAsync(gone.Id());
        var tDeleted = await tenant.CreateTaskAsync(appId);
        await tenant.Developer.DeleteAsync($"/api/v1/scheduled-tasks/{tDeleted.Id()}");
        await SetDueAsync(tGone.Id(), Now.AddMinutes(-1));
        await fixture.Factory.WithDbAsync(db => db.Workloads.Where(w => w.Id == Guid.Parse(gone.Id()))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.DeletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)));

        await Scheduler.TickAsync(Now, CancellationToken.None);

        Assert.Empty(await RunsAsync(tGone.Id()));
        Assert.Empty(await RunsAsync(tDeleted.Id()));
    }

    [RequiresDatabaseFact]
    public async Task Enabled_tasks_without_a_next_run_are_healed()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId, extra: new { schedule = "0 4 * * *" });
        await SetDueAsync(task.Id(), null);

        var result = await Scheduler.TickAsync(Now, CancellationToken.None);

        Assert.True(result.Healed >= 1);
        Assert.Equal(CronSchedule.Parse("0 4 * * *").Next(Now), (await TaskAsync(task.Id())).NextRunAt);
    }

    [RequiresDatabaseFact]
    public async Task Newest_hundred_runs_are_kept_per_task_with_their_output()
    {
        var (t, appId) = await StackAsync();
        var task = await t.CreateTaskAsync(appId);
        var other = await t.CreateTaskAsync(appId);
        var taskId = Guid.Parse(task.Id());
        var start = DateTimeOffset.UtcNow.AddHours(-5);
        var oldest = new List<Guid>();
        await fixture.Factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < 107; i++)
            {
                var run = new ScheduledTaskRun { TaskId = taskId, WorkloadId = Guid.Parse(appId), CreatedAt = start.AddMinutes(i), Status = ScheduledTaskRunStatus.Succeeded, OutputTail = i.ToString() };
                if (i < 7) oldest.Add(run.Id);
                db.ScheduledTaskRuns.Add(run);
                db.LogChunks.Add(new LogChunk { StreamId = $"task:{run.Id}", Sequence = 1, Data = "x" });
            }

            db.ScheduledTaskRuns.Add(new ScheduledTaskRun { TaskId = Guid.Parse(other.Id()), WorkloadId = Guid.Parse(appId), Status = ScheduledTaskRunStatus.Succeeded });
            await db.SaveChangesAsync();
        });

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ScheduledTaskService>().PruneRunsAsync(taskId);

        var kept = await RunsAsync(task.Id());
        Assert.Equal(100, kept.Count);
        Assert.Equal("7", kept[0].OutputTail);
        Assert.Equal("106", kept[^1].OutputTail);
        Assert.Single(await RunsAsync(other.Id()));
        var chunks = await fixture.Factory.WithDbAsync(db => db.LogChunks.AsNoTracking().Where(c => c.StreamId.StartsWith("task:")).Select(c => c.StreamId).ToListAsync());
        Assert.DoesNotContain(chunks, s => oldest.Any(id => s == $"task:{id}"));
        Assert.Contains(chunks, s => s == $"task:{kept[0].Id}");
    }
}
