using System.Text.Json.Nodes;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Features.Resources.Services;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Crypto;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Backups;

/// <summary>
/// <c>backup.restore</c> (lock key of the workload, so a restore never runs during a deployment). <b>Same</b>: a safety backup first (if it fails nothing
/// is touched), the workload is stopped where the engine replaces files (Redis, volumes), the agent restores the stream (checking its SHA-256), the
/// workload is started again, whatever happened. <b>New</b>: a service is made from the same template and version (generated credentials included),
/// deployed, and the backup is restored into it, which tests a backup without touching production. Lives in the API assembly because it reuses the
/// service creation code.
/// </summary>
public sealed class BackupRestoreJobHandler : IJobHandler
{
    private static readonly TimeSpan DeployTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Polling interval while the new service deploys (a field so tests need not wait).</summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public string Type => BackupJobs.RestoreType;

    /// <summary>A half-finished restore must not be silently repeated: the person decides.</summary>
    public bool Resumable => false;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var payload = context.GetPayload<BackupRestorePayload>();
        Guid? targetId = null;
        try
        {
            targetId = await RestoreAsync(context, payload, cancellationToken);
            await BackupAudit.RecordAsync(db, clock, context.Job, "backup.restored", "backup_run", payload.RunId,
                new { target = payload.Target, workloadId = targetId }, CancellationToken.None);
            await context.Log.WriteSystemAsync("Restore finished.", cancellationToken);
        }
        catch (JobFailedException ex)
        {
            await BackupAudit.RecordAsync(db, clock, context.Job, "backup.restore_failed", "backup_run", payload.RunId,
                new { target = payload.Target, code = ex.Error.Code, error = context.Secrets.Redact(ex.Error.Detail ?? ex.Error.Title) }, CancellationToken.None);
            throw;
        }
    }

    private static async Task<Guid> RestoreAsync(JobContext context, BackupRestorePayload payload, CancellationToken ct)
    {
        var sp = context.Services;
        var db = sp.GetRequiredService<AetheraDbContext>();
        var transports = sp.GetRequiredService<IServerTransportResolver>();
        var keyring = sp.GetRequiredService<MasterKeyring>();
        var runner = sp.GetRequiredService<BackupRunner>();
        var secrets = sp.GetRequiredService<BackupSecretReader>();
        var containers = sp.GetRequiredService<WorkloadContainers>();

        var run = await db.BackupRuns.FirstOrDefaultAsync(r => r.Id == payload.RunId, ct)
                  ?? throw JobFailedException.Permanent("backup.run_not_found", "The backup no longer exists", failedStep: "start");
        if (run.Status != BackupRunStatus.Succeeded || run.PrunedAt is not null || run.ObjectKey is null || run.KeyId is null)
            throw JobFailedException.Permanent(BackupProblemCodes.RunNotRestorable, "This backup cannot be restored", "Only a backup that succeeded and was not pruned can be restored.", "start");
        var policy = await db.BackupPolicies.Include(p => p.Destination).FirstOrDefaultAsync(p => p.Id == run.PolicyId, ct)
                     ?? throw JobFailedException.Permanent("backup.policy_not_found", "The backup policy no longer exists", failedStep: "start");
        var source = await db.Workloads.Include(w => w.Volumes).Include(w => w.EnvironmentVariables).FirstOrDefaultAsync(w => w.Id == run.WorkloadId, ct)
                     ?? throw JobFailedException.Permanent("backup.workload_not_found", "The application or service no longer exists", failedStep: "start");

        if (!BackupKeys.TryForKeyId(keyring, run.KeyId, out var key))
            throw JobFailedException.Permanent("backup.key_unavailable", "The key of this backup is not available",
                $"The backup was encrypted with key {run.KeyId}, which cannot be derived from the configured master key. Configure the master key version it was made with.", "key");

        var destination = policy.Destination;
        var secret = destination.Kind == BackupDestinationKind.S3 && destination.SecretAccessKeySecretId is { } secretId
            ? await ReadSecret(secrets, secretId, context, ct) : "";
        var location = BackupLocations.Location(destination, secret, run.ObjectKey);

        var target = source;
        if (payload.Target == "new")
        {
            target = await CreateServiceAsync(context, db, source as Service ?? throw JobFailedException.Permanent("backup.not_a_service", "Only a service can be restored into a new service", failedStep: "start"),
                payload.NewServiceName ?? throw JobFailedException.Permanent("backup.name_missing", "The new service needs a name", failedStep: "start"), run.Id, ct);
        }
        else
        {
            await context.Log.WriteSystemAsync($"Taking a safety backup of {source.Name} before it is overwritten.", ct);
            var safety = BackupRunner.NewRun(policy, Guid.CreateVersion7(), BackupTrigger.PreRestore, context.Job.CreatedBy);
            db.BackupRuns.Add(safety);
            await db.SaveChangesAsync(ct);
            try
            {
                await runner.RunAsync(context, policy.Id, safety.Id, BackupTrigger.PreRestore, ct);
            }
            catch (JobFailedException ex)
            {
                throw JobFailedException.Permanent("backup.restore_safety_backup_failed", "The safety backup failed: nothing was restored",
                    $"{ex.Error.Detail ?? ex.Error.Title} Nothing was changed.", "safety_backup", ex);
            }
        }

        // What the stream goes into, and which containers must be stopped meanwhile.
        BackupSinkSpec sink;
        var stopFirst = false;
        if (run.Kind == BackupKind.Database)
        {
            var service = target as Service ?? throw JobFailedException.Permanent("backup.not_a_service", "Only a service has database backups", failedStep: "restore");
            var engine = BackupEngines.ForTemplate(service.TemplateKey)
                         ?? throw JobFailedException.Permanent("backup.unsupported_template", $"Database backups are not supported for '{service.TemplateKey}'", failedStep: "restore");
            var running = await containers.ListAsync(service.ServerId, service.Id, runningOnly: true, context, ct);
            if (running.Count == 0)
                throw JobFailedException.Permanent("backup.not_running", $"'{service.Name}' has no running container", "Start it, then restore again. The safety backup was kept.", "restore");
            try
            {
                var engineContext = await runner.EngineContextAsync(await LoadWithVariablesAsync(db, service.Id, ct) as Service ?? service, WorkloadContainers.NameOf(running[0]), context, ct);
                sink = engine.Restore(engineContext);
            }
            catch (BackupEngineException ex)
            {
                throw JobFailedException.Permanent("backup.engine", "The restore cannot be prepared", ex.Message, "restore");
            }

            stopFirst = engine.StopsForRestore;
        }
        else
        {
            var volume = await db.Volumes.AsNoTracking().FirstOrDefaultAsync(v => v.Id == policy.VolumeId && v.WorkloadId == target.Id, ct)
                         ?? throw JobFailedException.Permanent("backup.volume_not_found", "The volume of this backup no longer exists", failedStep: "restore");
            sink = new VolumeRestoreSink(Aethera.Engine.Deployments.VolumeNames.DockerName(target.Slug, target.Id, volume.Name), ClearFirst: true);
            stopFirst = true;
        }

        var stopped = new List<string>();
        try
        {
            if (stopFirst)
            {
                foreach (var container in await containers.ListAsync(target.ServerId, target.Id, runningOnly: true, context, ct))
                {
                    var name = WorkloadContainers.NameOf(container);
                    await context.Log.WriteSystemAsync($"Stopping {name} for the restore.", ct);
                    var stop = await transports.ExecuteAsync<ContainerStopCommand, DockerContainer>(target.ServerId, new ContainerStopCommand(name),
                        WorkloadContainers.Options(context, $"restore.stop.{name}"), ct);
                    stop.Outcome.EnsureSucceeded();
                    stopped.Add(name);
                }
            }

            await context.Log.WriteSystemAsync($"Restoring {run.ObjectKey} (key {key!.KeyId}) into {target.Name}.", ct);
            try
            {
                var command = new BackupRestoreCommand(run.Id.ToString("D"), location, key.ToSpec(), sink, run.StoredSha256);
                var resolved = await transports.ExecuteAsync<BackupRestoreCommand, BackupRestoreOutcome>(target.ServerId, command, new CommandOptions
                {
                    IdempotencyKey = context.IdempotencyKey("restore.run"), JobId = context.JobId, OrganizationId = context.Job.OrganizationId, ActorUserId = context.Job.CreatedBy,
                    OnLog = entry => context.Log.WriteAsync(entry.Stream, entry.Text).AsTask().GetAwaiter().GetResult(),
                }, ct);
                if (!resolved.Outcome.Succeeded)
                    throw JobFailedException.Permanent("backup.restore_failed", "The restore failed",
                        context.Secrets.Redact(resolved.Outcome.ErrorMessage ?? resolved.Outcome.Status.ToString()), "restore");
                await context.Log.WriteSystemAsync($"Restored {resolved.Outcome.Result!.RestoredBytes} bytes.", ct);
            }
            catch (ServerTransportException ex)
            {
                throw ex.Code == TransportErrors.Unsupported
                    ? JobFailedException.Permanent("backup.agent_too_old", "The agent is too old for restores", "Update the agent (Servers > Update agent) and try again.", "restore", ex)
                    : JobFailedException.Permanent(ex.Code, "The agent could not run the restore", ex.Message, "restore", ex);
            }
        }
        finally
        {
            foreach (var name in stopped)
            {
                try
                {
                    await context.Log.WriteSystemAsync($"Starting {name} again.", CancellationToken.None);
                    await transports.ExecuteAsync<ContainerStartCommand, DockerContainer>(target.ServerId, new ContainerStartCommand(name),
                        WorkloadContainers.Options(context, $"restore.start.{name}"), CancellationToken.None);
                }
                catch (ServerTransportException ex)
                {
                    await context.Log.WriteSystemAsync($"Could not start {name} again: {ex.Message}. Start it from the workload page.", CancellationToken.None);
                }
            }
        }

        context.SetResult(new { workloadId = target.Id, workloadName = target.Name, target = payload.Target, newService = payload.Target == "new" });
        return target.Id;
    }

    private static Task<Workload?> LoadWithVariablesAsync(AetheraDbContext db, Guid id, CancellationToken ct) =>
        db.Workloads.Include(w => w.Volumes).Include(w => w.EnvironmentVariables).FirstOrDefaultAsync(w => w.Id == id, ct);

    private static async Task<string> ReadSecret(BackupSecretReader secrets, Guid id, JobContext context, CancellationToken ct)
    {
        try
        {
            return await secrets.ReadAsync(id, context.Secrets, ct);
        }
        catch (BackupFailure ex)
        {
            throw JobFailedException.Permanent(ex.Code, "A secret of the backup destination could not be read", ex.Message, "start", ex);
        }
    }

    /// <summary>A service from the template and version of <paramref name="source"/>, deployed and running; its generated credentials are its own.</summary>
    private static async Task<Service> CreateServiceAsync(JobContext context, AetheraDbContext db, Service source, string name, Guid runId, CancellationToken ct)
    {
        var sp = context.Services;
        var org = context.Job.OrganizationId;
        var template = ServiceTemplates.Find(source.TemplateKey)
                       ?? throw JobFailedException.Permanent("backup.template_missing", $"The template '{source.TemplateKey}' no longer exists", failedStep: "create");
        var environment = await db.Environments.Include(e => e.Project).FirstOrDefaultAsync(e => e.Id == source.EnvironmentId, ct)
                          ?? throw JobFailedException.Permanent("backup.environment_missing", "The environment of the service no longer exists", failedStep: "create");
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == source.ServerId, ct)
                     ?? throw JobFailedException.Permanent("backup.server_missing", "The server of the service no longer exists", failedStep: "create");
        var slug = WorkloadSupport.ResolveSlug(null, name);
        if (await db.Workloads.AnyAsync(w => w.EnvironmentId == environment.Id && w.Slug == slug, ct))
            throw JobFailedException.Permanent("service.already_exists", "A service with this name already exists", $"The environment already has an application or service with the slug '{slug}'.", "create");

        var config = JsonNode.Parse(source.ConfigJson) as JsonObject ?? new JsonObject();
        config.Remove("backup"); // the new service dumps its own default database
        var vault = sp.GetRequiredService<SecretVault>();
        var service = ServiceFactory.Build(vault, org, environment, server, template, name, slug, $"Restore of a backup of {source.Name}.", source.TemplateVersion, source.Image, config);
        db.Services.Add(service);
        await BackupAudit.RecordAsync(db, sp.GetRequiredService<IClock>(), context.Job, "service.created", "service", service.Id,
            new { name, slug, template = template.Key, restoredFromBackupRun = runId, sourceServiceId = source.Id }, ct);
        await context.Log.WriteSystemAsync($"Created service {name} from the {template.Name} template ({service.Image}); deploying it.", ct);

        var queued = await sp.GetRequiredService<DeploymentService>().DeployAsync(service.Id, DeploymentTrigger.Manual, null, ct);
        var deadline = DateTimeOffset.UtcNow + DeployTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = await db.Deployments.AsNoTracking().Where(d => d.Id == queued.Deployment.Id)
                .Select(d => new { d.Status, d.FailureReason }).FirstAsync(ct);
            if (status.Status is DeploymentStatus.Failed or DeploymentStatus.Cancelled)
                throw JobFailedException.Permanent("backup.new_service_failed", "The new service did not start",
                    $"{status.FailureReason ?? status.Status.ToString()} The service {name} was created and can be deleted.", "deploy");
            // The deploy job can die without finishing the deployment row: do not wait for a deployment nobody is running.
            var deployJob = await db.Jobs.AsNoTracking().Where(j => j.Id == queued.JobId).Select(j => new { j.Status, j.ErrorJson }).FirstAsync(ct);
            // The deployment row turns Running before the job promotes the service: wait for both, so the service is Running too.
            if (status.Status == DeploymentStatus.Running && deployJob.Status == JobStatus.Succeeded) break;
            if (deployJob.Status is JobStatus.Failed or JobStatus.Cancelled)
                throw JobFailedException.Permanent("backup.new_service_failed", "The new service did not start",
                    $"The deployment job {deployJob.Status.ToString().ToLowerInvariant()}. The service {name} was created and can be deleted.", "deploy");
            if (DateTimeOffset.UtcNow > deadline)
                throw JobFailedException.Permanent("backup.new_service_timeout", "The new service did not start in time", $"Check the deployment of {name}.", "deploy");
            await Task.Delay(PollInterval, ct);
        }

        await context.Log.WriteSystemAsync($"{name} is running.", ct);
        return service;
    }
}
