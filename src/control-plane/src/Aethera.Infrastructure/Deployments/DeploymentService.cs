using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Deployments;

/// <summary>Payload of <c>application.deploy</c>: only the deployment id; the configuration is frozen when the job starts.</summary>
public sealed class DeployPayload
{
    public Guid DeploymentId { get; set; }
}

public enum LifecycleAction
{
    Stop,
    Start,
    Restart,
}

/// <summary>Payload of <c>application.lifecycle</c>.</summary>
public sealed class LifecyclePayload
{
    public Guid ApplicationId { get; set; }
    public LifecycleAction Action { get; set; }
}

/// <summary>Payload of <c>application.teardown</c>: the workload was deleted; its containers (and compose project) go.</summary>
public sealed class TeardownPayload
{
    public Guid WorkloadId { get; set; }
    public Guid ServerId { get; set; }
}

/// <summary>A request the current state of the application does not allow (mapped to a 409 by the API).</summary>
public sealed class DeploymentConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Producer side of deployments (API, webhooks, schedules): creates the deployment row and enqueues its job.</summary>
public sealed class DeploymentService(AetheraDbContext db, IJobQueue jobs, IClock clock, ICurrentActor actor)
{
    public const string DeployJobType = "application.deploy";
    public const string LifecycleJobType = "application.lifecycle";
    public const string TeardownJobType = "application.teardown";

    /// <summary>
    /// Attempts of a deploy job. An attempt that fails because the server cannot be reached leaves the deployment resumable; the last one
    /// ends it Failed (the job's retries are what makes a webhook push during an agent reconnect work).
    /// </summary>
    public const int DeployMaxAttempts = 3;

    /// <summary>The per-application lock key shared by every job that changes the application (ADR 0004).</summary>
    public static string LockKey(Guid applicationId) => $"app:{applicationId}";

    /// <summary>
    /// True when the workload ever put something on its server: a deployment that started (or is running). A workload that was never deployed has
    /// no container to remove.
    /// </summary>
    public Task<bool> HasDeploymentsAsync(Guid workloadId, CancellationToken ct = default) =>
        db.Deployments.AsNoTracking().AnyAsync(d => d.WorkloadId == workloadId && d.Status != DeploymentStatus.Queued && d.Status != DeploymentStatus.Cancelled, ct);

    /// <summary>
    /// Queues the removal of a deleted workload's containers (compose: <c>compose down</c> without volumes) and, with them, its proxy routes (they are
    /// labels of the containers). Under the workload's lock key, so it runs after a deployment that is in flight. Volumes are kept. Call it after the
    /// soft delete is tracked: the job and the delete are saved together.
    /// </summary>
    /// <param name="resourceType"><c>application</c> or <c>service</c> (what Jobs shows).</param>
    public async Task<Job> QueueTeardownAsync(Workload workload, string resourceType, CancellationToken ct = default) =>
        await jobs.EnqueueAsync(new JobRequest(TeardownJobType, new TeardownPayload { WorkloadId = workload.Id, ServerId = workload.ServerId })
        {
            OrganizationId = await OrganizationOfAsync(workload.ServerId, ct), Resource = new JobResource(resourceType, workload.Id), LockKey = LockKey(workload.Id),
            MaxAttempts = 3,
        }, ct);

    /// <summary>Queues a deployment from the application's current configuration (or a redeploy of it).</summary>
    public Task<QueuedDeployment> DeployAsync(Guid applicationId, DeploymentTrigger trigger, DeploymentRequestInfo? info = null, CancellationToken ct = default) =>
        QueueAsync(applicationId, trigger, null, info, ct);

    /// <summary>Queues a rollback that re-applies the frozen configuration and image of <paramref name="targetDeploymentId"/>.</summary>
    public async Task<QueuedDeployment> RollbackAsync(Guid applicationId, Guid targetDeploymentId, CancellationToken ct = default)
    {
        var target = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == targetDeploymentId && d.WorkloadId == applicationId, ct)
                     ?? throw new KeyNotFoundException("The deployment does not exist.");
        if (!target.CanRollbackTo || target.ImageRef is not { Length: > 0 } && !IsCompose(target))
            throw new DeploymentConflictException("deployment.not_rollback_point", "Only deployments that ran successfully can be rolled back to.");
        return await QueueAsync(applicationId, DeploymentTrigger.Rollback, targetDeploymentId, null, ct);
    }

    public async Task<Job> LifecycleAsync(Guid applicationId, LifecycleAction action, CancellationToken ct = default)
    {
        var app = await db.Workloads.AsNoTracking().FirstOrDefaultAsync(a => a.Id == applicationId, ct)
                  ?? throw new KeyNotFoundException("The application does not exist.");
        if (app.CurrentDeploymentId is null)
            throw new DeploymentConflictException("application.not_deployed", "The application has no running deployment yet.");
        return await jobs.EnqueueAsync(new JobRequest(LifecycleJobType, new LifecyclePayload { ApplicationId = applicationId, Action = action })
        {
            OrganizationId = await OrganizationOfAsync(app.ServerId, ct), Resource = new JobResource("application", applicationId), LockKey = LockKey(applicationId),
        }, ct);
    }

    private static bool IsCompose(Deployment d) => d.SourceType == ApplicationSourceKind.Compose;

    private async Task<Guid> OrganizationOfAsync(Guid serverId, CancellationToken ct) =>
        await db.Servers.AsNoTracking().Where(s => s.Id == serverId).Select(s => s.OrganizationId).FirstAsync(ct);

    private async Task<QueuedDeployment> QueueAsync(Guid applicationId, DeploymentTrigger trigger, Guid? rollbackOf, DeploymentRequestInfo? info, CancellationToken ct)
    {
        // The application row is also written by the running deployment (status), and two requests may race for the next number: retry on the
        // xmin conflict with a clean tracker, the unique (workload, number) index being the final guard.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var app = await db.Workloads.FirstOrDefaultAsync(a => a.Id == applicationId, ct)
                          ?? throw new KeyNotFoundException("The application does not exist.");
                if (app is Application { GitSource: null } application) await db.Entry(application).Reference(a => a.GitSource).LoadAsync(ct);
                var now = clock.UtcNow;
                var deployment = Deployment.Queue(app.Id, app.EnvironmentId, app.ServerId, app.AllocateDeploymentNumber(), trigger, now, app.Runtime.DeploymentStrategy);
                deployment.SourceType = (app as Application)?.SourceKind ?? ApplicationSourceKind.DockerImage;
                deployment.RollbackOfDeploymentId = rollbackOf;
                deployment.TriggeredByUserId = actor.UserId;
                deployment.TriggeredByApiTokenId = actor.ApiTokenId;
                if (app is Application { GitSource: { } git })
                {
                    deployment.RepositoryUrl = git.RepositoryUrl;
                    deployment.Ref = info?.Ref ?? git.Branch;
                    deployment.CommitSha = info?.CommitSha ?? git.CommitPin;
                    deployment.CommitMessage = info?.CommitMessage;
                    deployment.CommitAuthor = info?.CommitAuthor;
                }
                db.Deployments.Add(deployment);
                await db.SaveChangesAsync(ct);

                var job = await jobs.EnqueueAsync(new JobRequest(DeployJobType, new DeployPayload { DeploymentId = deployment.Id })
                {
                    OrganizationId = await OrganizationOfAsync(app.ServerId, ct), Resource = new JobResource("application", app.Id), LockKey = LockKey(app.Id),
                    MaxAttempts = DeployMaxAttempts,
                }, ct);
                // The row is not touched again: the worker may already be running it, and its writes must not race ours. The handler records JobId.
                return new QueuedDeployment(deployment, job.Id);
            }
            catch (Exception ex) when (attempt < 5 && ex is DbUpdateConcurrencyException or DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } })
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}

/// <summary>A deployment that was just queued and the job that will run it.</summary>
public sealed record QueuedDeployment(Deployment Deployment, Guid JobId);

/// <summary>What triggered a deployment, when the trigger knows more than "someone clicked deploy" (webhooks).</summary>
public sealed record DeploymentRequestInfo(string? Ref = null, string? CommitSha = null, string? CommitMessage = null, string? CommitAuthor = null);
