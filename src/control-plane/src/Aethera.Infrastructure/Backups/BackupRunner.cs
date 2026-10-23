using System.Text.Json.Nodes;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Crypto;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Backups;

/// <summary>
/// One backup, start to finish (ADR 0010): the run row, the dump command to the agent (or, for Aethera's own database, the dump, AEB1 encryption
/// and upload here), the recorded outcome, the notification and retention. Used by <c>backup.run</c> and, for the safety backup, by the restore job.
/// </summary>
public sealed class BackupRunner(
    AetheraDbContext db, MasterKeyring keyring, IServerTransportResolver transports, IBackupObjectStore store, IControlPlaneDump controlPlaneDump,
    INotifier notifier, BackupSecretReader secrets, WorkloadContainers containers, IClock clock, ILogger<BackupRunner> logger)
{
    /// <summary>Runs the backup of <paramref name="policyId"/> into the (already created) run <paramref name="runId"/>. Throws <see cref="JobFailedException"/> after recording a failure.</summary>
    public async Task<BackupRun> RunAsync(JobContext context, Guid policyId, Guid runId, BackupTrigger trigger, CancellationToken cancellationToken)
    {
        var policy = await db.BackupPolicies.Include(p => p.Destination).FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
        var run = await db.BackupRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (policy is null)
        {
            if (run is not null) await FailAsync(run, null, "The backup policy no longer exists.", cancellationToken);
            throw JobFailedException.Permanent("backup.policy_not_found", "The backup policy no longer exists", failedStep: "start");
        }

        if (run is null)
        {
            run = NewRun(policy, runId, trigger, context.Job.CreatedBy);
            db.BackupRuns.Add(run);
        }

        run.Status = BackupRunStatus.Running;
        run.StartedAt = clock.UtcNow;
        run.FinishedAt = null;
        run.Error = null;
        run.JobId = context.JobId;
        run.Trigger = trigger;
        await db.SaveChangesAsync(cancellationToken);

        var name = await DescribeAsync(policy, cancellationToken);
        await context.Log.WriteSystemAsync($"Backing up {name} to \"{policy.Destination.Name}\" ({(trigger == BackupTrigger.PreRestore ? "safety backup before a restore" : trigger.ToString().ToLowerInvariant())}).", cancellationToken);
        try
        {
            if (policy.Kind == BackupKind.ControlPlane) await RunControlPlaneAsync(context, policy, run, cancellationToken);
            else await RunWorkloadAsync(context, policy, run, cancellationToken);
        }
        catch (Exception ex) when (ex is BackupFailure or BackupEngineException or ServerTransportException)
        {
            var failure = ex switch
            {
                BackupFailure f => f,
                BackupEngineException e => new BackupFailure("backup.engine", e.Message),
                _ => FromTransport((ServerTransportException)ex),
            };
            var message = context.Secrets.Redact(failure.Message);
            var retry = failure.Transient && context.Attempt < context.Job.MaxAttempts;
            await context.Log.WriteSystemAsync($"Backup failed: {message}{(retry ? " (will retry)" : "")}", CancellationToken.None);
            await FailAsync(run, retry ? BackupRunStatus.Pending : null, message, CancellationToken.None);
            if (!retry) await NotifyAsync(policy, run, name, success: false, message, trigger);
            throw failure.Transient
                ? JobFailedException.Transient(failure.Code, "The backup failed", message, "backup", failure)
                : JobFailedException.Permanent(failure.Code, "The backup failed", message, "backup", failure);
        }

        policy.LastRunAt = run.FinishedAt;
        await db.SaveChangesAsync(CancellationToken.None);
        await context.Log.WriteSystemAsync($"Backup stored as {run.ObjectKey} ({Size(run.StoredSizeBytes)}, sha256 {run.StoredSha256}).", cancellationToken);
        await NotifyAsync(policy, run, name, success: true, null, trigger);
        if (trigger != BackupTrigger.PreRestore) await ApplyRetentionAsync(context, policy, cancellationToken);
        return run;
    }

    public static BackupRun NewRun(BackupPolicy policy, Guid runId, BackupTrigger trigger, Guid? createdBy) => new()
    {
        Id = runId, OrganizationId = policy.OrganizationId, PolicyId = policy.Id, WorkloadId = policy.WorkloadId, DestinationId = policy.DestinationId,
        Kind = policy.Kind, Trigger = trigger, Status = BackupRunStatus.Pending, CreatedByUserId = createdBy,
    };

    private static BackupFailure FromTransport(ServerTransportException ex) => ex.Code == TransportErrors.Unsupported
        ? new BackupFailure("backup.agent_too_old", "The agent on this server is too old for backups (it lacks the backup.v1 capability). Update the agent (Servers > Update agent) and run the backup again.", inner: ex)
        : new BackupFailure(ex.Code, ex.Message, ex.Transient, ex);

    // ---- workloads ------------------------------------------------------------------------------------------------------------

    private async Task RunWorkloadAsync(JobContext context, BackupPolicy policy, BackupRun run, CancellationToken ct)
    {
        var workload = await LoadWorkloadAsync(policy.WorkloadId!.Value, ct)
                       ?? throw new BackupFailure("backup.workload_not_found", "The application or service no longer exists.");
        var destination = policy.Destination;
        var secret = await DestinationSecretAsync(destination, context, ct);
        var key = BackupKeys.Current(keyring);

        BackupSourceSpec source;
        string extension;
        if (policy.Kind == BackupKind.Database)
        {
            if (workload is not Service service) throw new BackupFailure("backup.not_a_service", "Only services have database backups.");
            var engine = BackupEngines.ForTemplate(service.TemplateKey)
                         ?? throw new BackupFailure("backup.unsupported_template", $"Database backups are not supported for the '{service.TemplateKey}' template.");
            var running = await containers.ListAsync(workload.ServerId, workload.Id, runningOnly: true, context, ct);
            if (running.Count == 0) throw new BackupFailure("backup.not_running", $"'{workload.Name}' has no running container. Start it before backing it up.");
            var engineContext = await EngineContextAsync(service, WorkloadContainers.NameOf(running[0]), context, ct);
            source = engine.Dump(engineContext);
            extension = engine.Extension;
            run.Engine = engine.Name;
            await context.Log.WriteSystemAsync($"Dumping the {engine.Name} database inside {engineContext.Container}.", ct);
        }
        else
        {
            var volume = await db.Volumes.AsNoTracking().FirstOrDefaultAsync(v => v.Id == policy.VolumeId && v.WorkloadId == workload.Id, ct)
                         ?? throw new BackupFailure("backup.volume_not_found", "The volume of this policy no longer exists.");
            if (volume.HostPath is { Length: > 0 }) throw new BackupFailure("backup.bind_mount", "Bind mounts are not backed up; only named volumes are.");
            source = new VolumeArchiveSource(volume.Name);
            extension = "tar.gz";
            run.Engine = "volume";
            await context.Log.WriteSystemAsync($"Archiving volume {volume.Name} (mounted at {volume.MountPath}).", ct);
        }

        var org = policy.OrganizationId;
        var objectKey = BackupLocations.ObjectKey(destination.Kind == BackupDestinationKind.Local ? null : destination.PathPrefix, org, workload.Slug, workload.Id, policy.Kind,
            run.StartedAt!.Value, run.Id, extension);
        run.ObjectKey = objectKey;
        run.KeyId = key.KeyId;
        await db.SaveChangesAsync(ct);

        var command = new BackupRunCommand(run.Id.ToString("D"), source, BackupLocations.Location(destination, secret, objectKey), key.ToSpec());
        await context.Log.WriteSystemAsync($"Sending the backup command to the agent (encrypted with key {key.KeyId}).", ct);
        var options = WorkloadContainers.Options(context, "backup.run");
        var outcome = await transports.ExecuteAsync<BackupRunCommand, BackupRunOutcome>(workload.ServerId, command, new CommandOptions
        {
            IdempotencyKey = options.IdempotencyKey, JobId = options.JobId, OrganizationId = options.OrganizationId, ActorUserId = options.ActorUserId,
            OnLog = entry => context.Log.WriteAsync(entry.Stream, entry.Text).AsTask().GetAwaiter().GetResult(),
        }, ct);
        if (!outcome.Outcome.Succeeded)
            throw new BackupFailure("backup.agent_failed", outcome.Outcome.ErrorMessage is { Length: > 0 } m ? m : $"The agent reported: {outcome.Outcome.Status}.", inner: null);

        var result = outcome.Outcome.Result!;
        Succeed(run, result.RawSizeBytes, result.StoredSizeBytes, result.StoredSha256);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Workload?> LoadWorkloadAsync(Guid id, CancellationToken ct) =>
        await db.Workloads.Include(w => w.Volumes).Include(w => w.EnvironmentVariables).FirstOrDefaultAsync(w => w.Id == id, ct);

    /// <summary>The variables of the service with secret-backed ones resolved (and registered for log redaction), plus its volumes.</summary>
    public async Task<BackupEngineContext> EngineContextAsync(Service service, string container, JobContext context, CancellationToken ct)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in service.EnvironmentVariables.Where(v => v.IsRuntime))
        {
            var value = variable.SecretId is { } secretId ? await secrets.ReadAsync(secretId, context.Secrets, ct) : variable.Value;
            if (value is not null) vars[variable.Key] = value;
        }

        string? database = null;
        try
        {
            database = (JsonNode.Parse(service.ConfigJson) as JsonObject)?["backup"]?["database"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // an unreadable setting falls back to the template's default database
        }

        return new BackupEngineContext(container, vars, database, service.Volumes.Select(v => new BackupVolumeInfo(v.Name, v.MountPath, v.HostPath is { Length: > 0 })).ToList());
    }

    private async Task<string> DestinationSecretAsync(BackupDestination destination, JobContext context, CancellationToken ct) =>
        destination.Kind == BackupDestinationKind.Local || destination.SecretAccessKeySecretId is not { } id
            ? ""
            : await secrets.ReadAsync(id, context.Secrets, ct);

    // ---- Aethera's own database -------------------------------------------------------------------------------------------------

    private async Task RunControlPlaneAsync(JobContext context, BackupPolicy policy, BackupRun run, CancellationToken ct)
    {
        var destination = policy.Destination;
        if (destination.Kind != BackupDestinationKind.S3) throw new BackupFailure("backup.s3_required", "Aethera's own database can only be backed up to an S3-compatible destination.");
        var secret = await DestinationSecretAsync(destination, context, ct);
        var key = BackupKeys.Current(keyring);
        run.Engine = "aethera";
        run.KeyId = key.KeyId;

        await context.Log.WriteSystemAsync("Dumping Aethera's own database (pg_dump -Fc).", ct);
        await using var dump = await controlPlaneDump.CreateAsync(run.StartedAt!.Value, ct);
        var encrypted = dump.Path + ".aeb";
        Aeb1Result sealedResult;
        await using (var input = File.OpenRead(dump.Path))
        await using (var output = File.Create(encrypted))
            sealedResult = await Aeb1Encryptor.EncryptAsync(input, output, key.Key, key.KeyId, cancellationToken: ct);

        var objectKey = BackupLocations.ObjectKey(destination.PathPrefix, policy.OrganizationId, null, null, BackupKind.ControlPlane, run.StartedAt!.Value, run.Id, "pgdump");
        run.ObjectKey = objectKey;
        await db.SaveChangesAsync(ct);
        await context.Log.WriteSystemAsync($"Encrypted with key {key.KeyId} (AEB1), uploading {Size(sealedResult.StoredBytes)}.", ct);
        try
        {
            await store.UploadFileAsync(BackupLocations.S3(destination, secret), objectKey, encrypted, ct);
        }
        catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or Amazon.Runtime.AmazonClientException or HttpRequestException or IOException)
        {
            throw new BackupFailure("backup.upload_failed", "The upload failed: " + S3BackupObjectStore.Describe(ex), transient: ex is HttpRequestException, inner: ex);
        }

        Succeed(run, sealedResult.RawBytes, sealedResult.StoredBytes, sealedResult.StoredSha256);
        await db.SaveChangesAsync(ct);
    }

    // ---- outcome, notification, retention -----------------------------------------------------------------------------------------

    private void Succeed(BackupRun run, long raw, long stored, string sha)
    {
        run.Status = BackupRunStatus.Succeeded;
        run.RawSizeBytes = raw;
        run.StoredSizeBytes = stored;
        run.StoredSha256 = sha;
        run.FinishedAt = clock.UtcNow;
        run.Error = null;
    }

    private async Task FailAsync(BackupRun run, BackupRunStatus? status, string message, CancellationToken ct)
    {
        run.Status = status ?? BackupRunStatus.Failed;
        run.Error = message.Length > 1900 ? message[..1900] : message;
        run.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<string> DescribeAsync(BackupPolicy policy, CancellationToken ct)
    {
        if (policy.WorkloadId is not { } id) return "Aethera's own database";
        var workload = await db.Workloads.AsNoTracking().Where(w => w.Id == id).Select(w => w.Name).FirstOrDefaultAsync(ct);
        return workload ?? "a deleted workload";
    }

    private async Task NotifyAsync(BackupPolicy policy, BackupRun run, string name, bool success, string? error, BackupTrigger trigger)
    {
        try
        {
            Guid? environmentId = null;
            string? path = null;
            if (policy.WorkloadId is { } workloadId)
            {
                var w = await db.Workloads.AsNoTracking().Where(x => x.Id == workloadId)
                    .Select(x => new { x.EnvironmentId, IsService = x is Service }).FirstOrDefaultAsync();
                environmentId = w?.EnvironmentId;
                path = $"/{(w?.IsService == true ? "services" : "applications")}/{workloadId}?tab=backups";
            }
            else path = "/settings?tab=backups";

            var details = new Dictionary<string, string> { ["policyId"] = policy.Id.ToString(), ["runId"] = run.Id.ToString(), ["trigger"] = trigger.ToString().ToLowerInvariant() };
            if (run.StoredSizeBytes is { } size) details["size"] = Size(size);
            await notifier.PublishAsync(new NotificationEvent(
                policy.OrganizationId, success ? NotificationEvents.BackupSucceeded : NotificationEvents.BackupFailed,
                success ? AlertSeverity.Info : AlertSeverity.Critical,
                success ? $"Backup of {name} succeeded" : $"Backup of {name} failed",
                success ? $"Stored {Size(run.StoredSizeBytes)} in the backup destination." : error)
            {
                ResourceType = policy.WorkloadId is null ? "system" : "workload", ResourceId = policy.WorkloadId, EnvironmentId = environmentId, Path = path,
                AlertKey = $"backup:{policy.Id}", Resolves = success, Details = details,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // INotifier must not throw for a delivery problem, but a bug there must not fail a backup that worked.
            logger.LogWarning(ex, "Could not publish the notification of backup run {RunId}", run.Id);
        }
    }

    private async Task ApplyRetentionAsync(JobContext context, BackupPolicy policy, CancellationToken ct)
    {
        try
        {
            var candidates = await db.BackupRuns.Where(r => r.PolicyId == policy.Id && r.Status == BackupRunStatus.Succeeded && r.PrunedAt == null)
                .Select(r => new { r.Id, r.FinishedAt, r.CreatedAt }).ToListAsync(ct);
            var zone = CronSchedule.TryParse(policy.Schedule, policy.TimeZone, out var schedule, out _) ? schedule.TimeZone : TimeZoneInfo.Utc;
            var prune = BackupRetention.ToPrune(candidates.Select(c => new RetentionCandidate(c.Id, c.FinishedAt ?? c.CreatedAt)).ToList(),
                policy.KeepDaily, policy.KeepWeekly, policy.KeepMonthly, zone);
            if (prune.Count == 0) return;

            await context.Log.WriteSystemAsync($"Retention ({policy.KeepDaily} daily, {policy.KeepWeekly} weekly, {policy.KeepMonthly} monthly): removing {prune.Count} old backup(s).", ct);
            var runs = await db.BackupRuns.Where(r => prune.Contains(r.Id)).ToListAsync(ct);
            await DeleteObjectsAsync(context, policy, runs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The backup itself succeeded: a retention problem is reported in the log and retried by the next run.
            await context.Log.WriteSystemAsync($"Retention failed: {context.Secrets.Redact(ex.Message)}", CancellationToken.None);
            logger.LogWarning(ex, "Retention of backup policy {PolicyId} failed", policy.Id);
        }
    }

    private async Task DeleteObjectsAsync(JobContext context, BackupPolicy policy, List<BackupRun> runs, CancellationToken ct)
    {
        var destination = policy.Destination;
        var keys = runs.Where(r => r.ObjectKey is not null).Select(r => r.ObjectKey!).ToList();
        if (destination.Kind == BackupDestinationKind.S3)
        {
            var secret = await DestinationSecretAsync(destination, context, ct);
            await store.DeleteAsync(BackupLocations.S3(destination, secret), keys, ct);
        }
        else if (keys.Count > 0 && policy.WorkloadId is { } workloadId)
        {
            var serverId = await db.Workloads.AsNoTracking().Where(w => w.Id == workloadId).Select(w => (Guid?)w.ServerId).FirstOrDefaultAsync(ct);
            if (serverId is null) return;
            var resolved = await transports.ExecuteAsync<BackupDeleteCommand, Unit>(serverId.Value,
                new BackupDeleteCommand(keys.Select(k => (BackupLocationSpec)new LocalBackupLocationSpec(k)).ToList()), WorkloadContainers.Options(context, $"backup.delete.{Guid.NewGuid():N}"), ct);
            resolved.Outcome.EnsureSucceeded();
        }

        var now = clock.UtcNow;
        foreach (var run in runs) run.PrunedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static string Size(long? bytes) => bytes is null ? "unknown size" : bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KiB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MiB" : $"{bytes / 1073741824.0:0.##} GiB";
}

/// <summary><c>backup.run</c>: a scheduled, manual or safety backup of one policy.</summary>
public sealed class BackupRunJobHandler : IJobHandler
{
    public string Type => BackupJobs.RunType;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.GetPayload<BackupRunPayload>();
        var runner = context.Services.GetRequiredService<BackupRunner>();
        var run = await runner.RunAsync(context, payload.PolicyId, payload.RunId, payload.Trigger, cancellationToken);
        context.SetResult(new { runId = run.Id, objectKey = run.ObjectKey, storedSizeBytes = run.StoredSizeBytes, keyId = run.KeyId });
    }
}
