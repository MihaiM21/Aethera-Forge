using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.ScheduledTasks;

/// <summary>
/// <c>scheduled_task.run</c> (ADR 0010): runs the command line of a task as <c>sh -c "..."</c> inside the workload's running container and
/// records the outcome on the run. A stopped workload or a missing container is a <see cref="ScheduledTaskRunStatus.Skipped"/> run (nothing
/// is started on the task's behalf); exit code 0 is <see cref="ScheduledTaskRunStatus.Succeeded"/>, anything else <see cref="ScheduledTaskRunStatus.Failed"/>
/// (and fails the job, so it shows in Jobs). Not resumable: a command that may already have run is never run twice.
/// <para>
/// The command's own output is stored in the run's stream (<c>task:&lt;runId&gt;</c>), not in the job's <c>job:&lt;id&gt;</c> stream: the log
/// pipeline of the job numbers its lines from 1 and so does every agent command, so sharing a stream would silently drop the first lines of one
/// of them. The job's stream carries the handler's own narration. Secret environment values reach the container only through the typed
/// command's secret variants, never the argument vector, and are masked in everything this handler stores or logs.
/// </para>
/// </summary>
public sealed class ScheduledTaskRunJobHandler : IJobHandler
{
    internal const int OutputTailBytes = 16 * 1024;
    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeServiceLabel = "com.docker.compose.service";

    public string Type => ScheduledTaskService.RunJobType;

    public bool Resumable => false;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var payload = context.GetPayload<ScheduledTaskRunPayload>();

        var run = await db.ScheduledTaskRuns.FirstOrDefaultAsync(r => r.Id == payload.RunId, cancellationToken)
                  ?? throw JobFailedException.Permanent("scheduled_task.run_not_found", "The run no longer exists");
        run.JobId ??= context.JobId;
        if (run.Status == ScheduledTaskRunStatus.Running)
        {
            // Only reachable when a job is delivered again after its worker died: the command may have run, so it is not run again.
            await FinishAsync(context, db, run, null, ScheduledTaskRunStatus.Failed, null, null, false, "The run was interrupted: the worker stopped before it finished.", clock);
            throw JobFailedException.Permanent("scheduled_task.interrupted", "The run was interrupted", run.Error);
        }

        if (run.Status != ScheduledTaskRunStatus.Pending) return;

        var task = await db.ScheduledTasks.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == run.TaskId, cancellationToken);
        var workload = task is null || task.IsDeleted ? null : await LoadWorkloadAsync(db, task.WorkloadId, cancellationToken);
        if (task is null || workload is null)
        {
            await FinishAsync(context, db, run, null, ScheduledTaskRunStatus.Skipped, null, null, false, "The task or its workload was deleted.", clock);
            return;
        }

        var started = clock.UtcNow;
        run.Status = ScheduledTaskRunStatus.Running;
        run.StartedAt = started;
        await db.SaveChangesAsync(cancellationToken);
        await db.ScheduledTasks.Where(t => t.Id == task.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastRunAt, (DateTimeOffset?)started), cancellationToken);
        await context.Log.WriteSystemAsync($"Task \"{task.Name}\" on {workload.Name}", cancellationToken);

        var resolver = sp.GetRequiredService<IServerTransportResolver>();
        var baseOptions = new CommandOptions
        {
            IdempotencyKey = context.IdempotencyKey("exec"), JobId = context.JobId, OrganizationId = context.Job.OrganizationId, ActorUserId = context.Job.CreatedBy,
            LogStreamId = ScheduledTaskService.LogStream(run.Id),
        };

        try
        {
            if (workload.DesiredState == DesiredState.Stopped || workload.CurrentDeploymentId is null)
            {
                await SkipAsync(context, db, run, task, clock, workload.DesiredState == DesiredState.Stopped ? "The workload is stopped." : "The workload has not been deployed yet.");
                return;
            }

            // 1. the running container
            DockerContainer? container;
            try
            {
                var list = await resolver.ExecuteAsync<ContainerListCommand, IReadOnlyList<DockerContainer>>(
                    workload.ServerId,
                    new ContainerListCommand(All: false, LabelFilters: Filters(workload, task)),
                    new CommandOptions { IdempotencyKey = context.IdempotencyKey("list"), JobId = context.JobId, OrganizationId = context.Job.OrganizationId, ActorUserId = context.Job.CreatedBy },
                    cancellationToken);
                container = Pick(list.Outcome.Succeeded ? list.Outcome.Result : null, workload);
                if (!list.Outcome.Succeeded)
                {
                    await FailAsync(context, db, run, null, null, false, $"The containers of the workload could not be listed: {list.Outcome.ErrorMessage ?? list.Outcome.ErrorCode.ToString()}", clock);
                    return;
                }
            }
            catch (ServerTransportException ex)
            {
                await FailAsync(context, db, run, null, null, false, TransportMessage(ex), clock);
                return;
            }

            if (container is null)
            {
                await SkipAsync(context, db, run, task, clock,
                    task.ComposeService is { } service ? $"The service \"{service}\" has no running container." : "The workload has no running container.");
                return;
            }

            // 2. its environment (secrets decrypted in memory only and registered for masking)
            IReadOnlyList<EnvVarSpec> env;
            try
            {
                env = await BuildEnvironmentAsync(sp, db, workload, context, cancellationToken);
            }
            catch (CryptographicException)
            {
                await FailAsync(context, db, run, null, null, false, "A secret of the workload could not be decrypted. Check the configured master key.", clock);
                return;
            }

            // 3. exec; no further writes to job:<id> from here on would be needed, the command's output goes to its own stream
            await context.Log.WriteSystemAsync($"Running in {container.Name}: {task.Command}", cancellationToken);
            await context.Log.FlushAsync(cancellationToken);
            var timeout = TimeSpan.FromSeconds(task.TimeoutSeconds);
            ContainerExecOutcome? result;
            string? failure = null;
            try
            {
                var outcome = await resolver.ExecuteAsync<ContainerExecCommand, ContainerExecOutcome>(
                    workload.ServerId,
                    new ContainerExecCommand(container.Id, ["sh", "-c", task.Command], env, OutputTailBytes: OutputTailBytes),
                    new CommandOptions
                    {
                        IdempotencyKey = baseOptions.IdempotencyKey, JobId = baseOptions.JobId, OrganizationId = baseOptions.OrganizationId,
                        ActorUserId = baseOptions.ActorUserId, LogStreamId = baseOptions.LogStreamId, Deadline = clock.UtcNow + timeout,
                    },
                    cancellationToken);
                result = outcome.Outcome.Succeeded ? outcome.Outcome.Result : null;
                if (!outcome.Outcome.Succeeded)
                {
                    failure = outcome.Outcome.Status == CommandStatus.TimedOut || outcome.Outcome.ErrorCode == CommandErrorCode.Timeout
                        ? $"The command did not finish within {FormatTimeout(timeout)} and was stopped."
                        : outcome.Outcome.Status == CommandStatus.Cancelled
                            ? "The command was cancelled."
                            : $"The command could not be run: {outcome.Outcome.ErrorMessage ?? outcome.Outcome.ErrorCode.ToString()}";
                }
            }
            catch (ServerTransportException ex)
            {
                result = null;
                failure = TransportMessage(ex);
            }

            if (result is null)
            {
                await FailAsync(context, db, run, null, null, false, failure ?? "The command failed.", clock);
                return;
            }

            var tail = context.Secrets.Redact(result.OutputTail);
            var status = result.ExitCode == 0 ? ScheduledTaskRunStatus.Succeeded : ScheduledTaskRunStatus.Failed;
            var error = status == ScheduledTaskRunStatus.Failed ? $"The command exited with code {result.ExitCode}." : null;
            await FinishAsync(context, db, run, task, status, result.ExitCode, tail, result.OutputTruncated, error, clock);
            context.SetResult(new { runId = run.Id, status = status.ToString().ToLowerInvariant(), exitCode = result.ExitCode });
            if (status == ScheduledTaskRunStatus.Failed)
                throw JobFailedException.Permanent("scheduled_task.failed", "The scheduled task failed", error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && run.Status == ScheduledTaskRunStatus.Running)
        {
            await FinishAsync(context, db, run, task, ScheduledTaskRunStatus.Cancelled, null, null, false, "The run was cancelled.", clock, CancellationToken.None);
            throw;
        }
    }

    // ---- outcomes -------------------------------------------------------------------------------------------------------------------

    private static async Task SkipAsync(JobContext context, AetheraDbContext db, ScheduledTaskRun run, ScheduledTask task, IClock clock, string reason)
    {
        await context.Log.WriteSystemAsync($"Skipped: {reason}", CancellationToken.None);
        await FinishAsync(context, db, run, task, ScheduledTaskRunStatus.Skipped, null, null, false, reason, clock);
        context.SetResult(new { runId = run.Id, status = "skipped", reason });
    }

    /// <summary>Ends the run as failed and fails the job with the same reason.</summary>
    private static async Task FailAsync(
        JobContext context, AetheraDbContext db, ScheduledTaskRun run, int? exitCode, string? tail, bool truncated, string error, IClock clock)
    {
        var task = await db.ScheduledTasks.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == run.TaskId, CancellationToken.None);
        await FinishAsync(context, db, run, task, ScheduledTaskRunStatus.Failed, exitCode, tail, truncated, error, clock);
        throw JobFailedException.Permanent("scheduled_task.failed", "The scheduled task failed", error);
    }

    private static async Task FinishAsync(
        JobContext context, AetheraDbContext db, ScheduledTaskRun run, ScheduledTask? task, ScheduledTaskRunStatus status, int? exitCode, string? tail,
        bool truncated, string? error, IClock clock, CancellationToken? token = null)
    {
        var ct = token ?? CancellationToken.None;
        run.Status = status;
        run.ExitCode = exitCode;
        run.OutputTail = tail is { Length: > 0 } ? (tail.Length > 60000 ? tail[^60000..] : tail) : null;
        run.OutputTruncated = truncated;
        run.Error = error is null ? null : context.Secrets.Redact(error);
        run.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        var service = context.Services.GetRequiredService<ScheduledTaskService>();
        try
        {
            await service.PruneRunsAsync(run.TaskId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Services.GetRequiredService<ILogger<ScheduledTaskRunJobHandler>>().LogWarning(ex, "Pruning the runs of task {TaskId} failed", run.TaskId);
        }

        if (task is not null && status is ScheduledTaskRunStatus.Succeeded or ScheduledTaskRunStatus.Failed)
            await NotifyAsync(context, db, run, task, ct);
    }

    private static async Task NotifyAsync(JobContext context, AetheraDbContext db, ScheduledTaskRun run, ScheduledTask task, CancellationToken ct)
    {
        try
        {
            var workload = await db.Workloads.AsNoTracking().IgnoreQueryFilters().Where(w => w.Id == task.WorkloadId)
                .Select(w => new { w.Name, w.EnvironmentId, IsService = w is Service }).FirstOrDefaultAsync(ct);
            if (workload is null) return;
            var succeeded = run.Status == ScheduledTaskRunStatus.Succeeded;
            var duration = run.StartedAt is { } s && run.FinishedAt is { } f ? f - s : (TimeSpan?)null;
            var details = new Dictionary<string, string>
            {
                ["task"] = task.Name, ["workload"] = workload.Name, ["run"] = run.Id.ToString(), ["trigger"] = run.Trigger.ToString().ToLowerInvariant(),
            };
            if (run.ExitCode is { } code) details["exitCode"] = code.ToString();
            if (duration is { } d) details["duration"] = $"{d.TotalSeconds:0.#}s";
            // Never the output (it may echo the environment) and never the command line's expansion: a title, the exit code and a link.
            var notifier = context.Services.GetRequiredService<INotifier>();
            await notifier.PublishAsync(new NotificationEvent(
                context.Job.OrganizationId, succeeded ? NotificationEvents.TaskSucceeded : NotificationEvents.TaskFailed,
                succeeded ? AlertSeverity.Info : AlertSeverity.Warning,
                succeeded ? $"Task \"{task.Name}\" succeeded on {workload.Name}" : $"Task \"{task.Name}\" failed on {workload.Name}",
                succeeded ? null : run.Error)
            {
                ResourceType = "scheduled_task", ResourceId = task.Id, EnvironmentId = workload.EnvironmentId,
                Path = $"/{(workload.IsService ? "services" : "applications")}/{task.WorkloadId}#tasks",
                AlertKey = $"task:{task.Id}", Resolves = succeeded, Details = details,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // INotifier must not throw for a delivery problem; a bug in a channel must not turn a finished run into a failed job either.
            context.Services.GetRequiredService<ILogger<ScheduledTaskRunJobHandler>>().LogWarning(ex, "Publishing the notification of task {TaskId} failed", task.Id);
        }
    }

    // ---- resolution -----------------------------------------------------------------------------------------------------------------

    private static async Task<Workload?> LoadWorkloadAsync(AetheraDbContext db, Guid workloadId, CancellationToken ct) =>
        await db.Workloads.Include(w => w.EnvironmentVariables).ThenInclude(e => e.Secret).FirstOrDefaultAsync(w => w.Id == workloadId, ct);

    /// <summary>Docker label filters of the container to use: the workload's, or the service of its compose project.</summary>
    internal static IReadOnlyList<string> Filters(Workload workload, ScheduledTask task) =>
        task.ComposeService is { Length: > 0 } service
            ? [$"{ComposeProjectLabel}={ComposeProjectName(workload)}", $"{ComposeServiceLabel}={service}"]
            : [$"aethera.workload={workload.Id}"];

    /// <summary>The compose project name of a compose application (the engine names it <c>&lt;slug&gt;-&lt;first 8 hex of the id&gt;</c>).</summary>
    internal static string ComposeProjectName(Workload workload) => $"{workload.Slug}-{workload.Id.ToString("N")[..8]}";

    /// <summary>
    /// The running container to use. Two can exist for a moment during a low-downtime deployment: the one of the current deployment wins,
    /// then the most recently started.
    /// </summary>
    internal static DockerContainer? Pick(IReadOnlyList<DockerContainer>? containers, Workload workload)
    {
        // A pull request preview carries the workload label too (ADR 0012) but is never the container a task runs in.
        var running = (containers ?? []).Where(c => c.State == ContainerRunState.Running && !c.Labels.ContainsKey("aethera.preview")).ToList();
        if (running.Count <= 1) return running.FirstOrDefault();
        var current = workload.CurrentDeploymentId?.ToString();
        return running.OrderByDescending(c => current is not null && c.Labels.TryGetValue("aethera.deployment", out var d) && d == current)
            .ThenByDescending(c => c.StartedAt ?? DateTimeOffset.MinValue).First();
    }

    private static async Task<IReadOnlyList<EnvVarSpec>> BuildEnvironmentAsync(
        IServiceProvider sp, AetheraDbContext db, Workload workload, JobContext context, CancellationToken ct)
    {
        var protector = sp.GetRequiredService<ISecretProtector>();
        var list = new List<EnvVarSpec>();
        foreach (var variable in workload.EnvironmentVariables.Where(e => e.IsRuntime && !e.IsPreview).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (variable.SecretId is not { } secretId)
            {
                list.Add(EnvVarSpec.OfPlain(variable.Key, variable.Value ?? ""));
                continue;
            }

            var version = variable.Secret?.CurrentVersion ?? 0;
            var row = await db.SecretVersions.AsNoTracking().FirstOrDefaultAsync(v => v.SecretId == secretId && v.Version == version, ct);
            if (row is null) continue; // a deleted secret: the container started without it too
            var protectedValue = new ProtectedValue(row.Ciphertext, row.Nonce, row.MasterKeyVersion) { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce };
            var plaintext = Encoding.UTF8.GetString(protector.Unprotect(protectedValue, $"secret:{secretId}:v{row.Version}"));
            context.Secrets.Register(plaintext);
            list.Add(EnvVarSpec.OfSecret(variable.Key, plaintext));
        }

        return list;
    }

    private static string TransportMessage(ServerTransportException ex) => ex.Code switch
    {
        TransportErrors.Unsupported => "The agent on this server cannot run commands in containers yet: update the agent on this server.",
        TransportErrors.AgentUnavailable or TransportErrors.Unreachable => "The server could not be reached.",
        _ => $"The server could not run the command ({ex.Code}).",
    };

    private static string FormatTimeout(TimeSpan t) =>
        t.TotalHours >= 1 && t.TotalHours == Math.Floor(t.TotalHours) ? $"{t.TotalHours:0} h"
        : t.TotalMinutes >= 1 && t.TotalMinutes == Math.Floor(t.TotalMinutes) ? $"{t.TotalMinutes:0} min"
        : $"{t.TotalSeconds:0} s";
}
