using Aethera.Domain;
using Aethera.Engine.Git;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Previews;

/// <summary>What a pull request event did: the delivery outcome and, when a deployment was queued, which.</summary>
public sealed record PreviewEventResult(WebhookOutcome Outcome, string? Detail, Deployment? Deployment = null, Guid? JobId = null)
{
    public static PreviewEventResult Ignored(string detail) => new(WebhookOutcome.Ignored, detail);
}

/// <summary>A request the state of the preview does not allow (mapped to a 409 by the API).</summary>
public sealed class PreviewConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The lifecycle of pull request previews (ADR 0012): webhook events create or update a <see cref="PreviewEnvironment"/> and queue its deployment,
/// a closed pull request queues its teardown, the API redeploys and removes. Deployments and teardowns of a preview share the lock
/// <see cref="PreviewJobs.LockKey"/>, never the application's.
/// </summary>
public sealed class PreviewService(AetheraDbContext db, IJobQueue jobs, IClock clock, ILogger<PreviewService> logger)
{
    // ---- webhook events ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Handles a pull request event of the webhook of <paramref name="app"/> (loaded with its git source). Never throws for an event that cannot
    /// be previewed: it comes back <see cref="WebhookOutcome.Ignored"/> with the reason, which the delivery log shows.
    /// </summary>
    public async Task<PreviewEventResult> HandleEventAsync(Application app, WebhookEndpoint endpoint, PullRequestEvent pr, CancellationToken ct)
    {
        if (app.GitSource is not { } git) return PreviewEventResult.Ignored("The application has no git source.");
        // A pull request that closes takes its preview down even when previews were turned off meanwhile; nothing else happens while they are off.
        if (!app.Previews.Enabled && pr.Action is not (PullRequestAction.Closed or PullRequestAction.Merged))
            return PreviewEventResult.Ignored("Pull request previews are turned off for this application.");

        var existing = await db.Previews.FirstOrDefaultAsync(p => p.ApplicationId == app.Id && p.Number == pr.Number, ct);
        var now = clock.UtcNow;
        switch (pr.Action)
        {
            case PullRequestAction.Other:
                return PreviewEventResult.Ignored("This pull request event does not change a preview.");

            case PullRequestAction.Closed or PullRequestAction.Merged:
                if (existing is null or { Status: PreviewStatus.Removed or PreviewStatus.Closing })
                    return PreviewEventResult.Ignored(existing is null ? "The pull request has no preview." : "The preview is already being removed.");
                existing.ClosedAt = now;
                existing.LastEventAt = now;
                var job = await CloseAsync(app, existing, pr.Action == PullRequestAction.Merged ? "The pull request was merged." : "The pull request was closed.", ct);
                PreviewAudit.Record(db, clock, await OrganizationOfAsync(app.Id, ct), "preview.teardown_requested", existing.Id,
                    new { applicationId = app.Id, number = existing.Number, merged = pr.Action == PullRequestAction.Merged });
                await db.SaveChangesAsync(ct);
                return new PreviewEventResult(WebhookOutcome.Accepted, $"Removing the preview of pull request #{pr.Number}.", JobId: job.Id);
        }

        // Opened, reopened or updated.
        if (!string.Equals(pr.TargetBranch, git.Branch, StringComparison.Ordinal))
            return PreviewEventResult.Ignored($"The pull request targets '{pr.TargetBranch}', not the application's branch '{git.Branch}'.");
        string repositoryUrl = git.RepositoryUrl;
        if (pr.FromFork)
        {
            if (!app.Previews.AllowForks)
                return PreviewEventResult.Ignored("The pull request comes from a fork. Previews of forks are off for this application: their code would run with the application's variables.");
            if (PreviewGitUrls.ForkCloneUrl(git.RepositoryUrl, pr.SourceRepositoryUrl) is not { } forkUrl)
                return PreviewEventResult.Ignored("The fork is not on the git host of the application's repository, or its repository is gone.");
            repositoryUrl = forkUrl;
        }

        if (!DeploymentSnapshotFactory.IsSafeBranch(pr.SourceBranch) || !DeploymentSnapshotFactory.IsCommitSha(pr.HeadSha))
            return PreviewEventResult.Ignored("The branch or commit of the pull request cannot be deployed.");

        var reopening = existing is { Status: PreviewStatus.Removed or PreviewStatus.Closing };
        // A push to a pull request that was closed is an event that arrived late; only a reopen (or a push after the preview was removed by hand) revives it.
        if (reopening && pr.Action == PullRequestAction.Updated && existing?.ClosedAt is not null)
            return PreviewEventResult.Ignored("The pull request was closed; its preview is removed.");

        var preview = existing ?? await CreateAsync(app, endpoint.Provider, pr, repositoryUrl, now, ct);
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            if (reopening)
            {
                preview.ServerId = app.Previews.ServerId ?? app.ServerId;
                preview.Status = PreviewStatus.Pending;
                preview.StatusReason = null;
                preview.ClosedAt = null;
                preview.RemovedAt = null;
                preview.Hostname = null;
                preview.CurrentDeploymentId = null;
            }

            preview.Title = Trim(pr.Title, 500);
            preview.AuthorLogin = Trim(pr.AuthorLogin, 255);
            preview.SourceBranch = pr.SourceBranch;
            preview.SourceRepositoryUrl = repositoryUrl;
            preview.FromFork = pr.FromFork;
            preview.HeadSha = pr.HeadSha;
            preview.LastEventAt = now;
        }, ct);

        var queued = await DeployOrHoldAsync(app, preview, DeploymentTrigger.PullRequest, ct);
        var organizationId = await OrganizationOfAsync(app.Id, ct);
        PreviewAudit.Record(db, clock, organizationId, queued is null ? "preview.held" : "preview.deploy_requested", preview.Id,
            new { applicationId = app.Id, number = preview.Number, fromFork = preview.FromFork, commit = preview.HeadSha });
        await db.SaveChangesAsync(ct);
        return queued is null
            ? new PreviewEventResult(WebhookOutcome.Accepted, $"Pull request #{pr.Number} is waiting for a free preview slot ({MaxActive(app)} previews may run at once).")
            : new PreviewEventResult(WebhookOutcome.Accepted, null, queued.Deployment, queued.JobId);
    }

    private async Task<PreviewEnvironment> CreateAsync(Application app, GitProvider provider, PullRequestEvent pr, string repositoryUrl, DateTimeOffset now, CancellationToken ct)
    {
        var preview = new PreviewEnvironment
        {
            ApplicationId = app.Id, ServerId = app.Previews.ServerId ?? app.ServerId, Provider = provider, Number = pr.Number, Title = Trim(pr.Title, 500),
            AuthorLogin = Trim(pr.AuthorLogin, 255), SourceBranch = pr.SourceBranch, SourceRepositoryUrl = repositoryUrl, FromFork = pr.FromFork, HeadSha = pr.HeadSha,
            Status = PreviewStatus.Pending, StatusChangedAt = now, LastEventAt = now,
        };
        db.Previews.Add(preview);
        await db.SaveChangesAsync(ct);
        return preview;
    }

    // ---- API operations --------------------------------------------------------------------------------------------------------------

    /// <summary>Deploys the preview of pull request <paramref name="number"/> again, at the commit of its last event (the API's "redeploy").</summary>
    /// <exception cref="PreviewConflictException">The pull request was closed, or every preview slot is taken.</exception>
    public async Task<QueuedDeployment> RedeployAsync(Application app, PreviewEnvironment preview, CancellationToken ct)
    {
        if (preview.Status is PreviewStatus.Closing or PreviewStatus.Removed)
            throw new PreviewConflictException("preview.removed", "The preview was removed. Reopen the pull request (or push to it) to get a new one.");
        await ConcurrencySupport.SaveWithRetryAsync(db, () => preview.LastEventAt = clock.UtcNow, ct);
        var queued = await DeployOrHoldAsync(app, preview, DeploymentTrigger.PullRequest, ct)
                     ?? throw new PreviewConflictException("preview.no_slot", $"{MaxActive(app)} previews are running already; remove one first or raise the limit.");
        return queued;
    }

    /// <summary>Removes the preview by hand (the API's "remove"); the pull request may still be open, and its next push creates the preview again.</summary>
    public async Task<Job?> RemoveAsync(Application app, PreviewEnvironment preview, CancellationToken ct)
    {
        if (preview.Status is PreviewStatus.Closing or PreviewStatus.Removed) return null;
        return await CloseAsync(app, preview, "Removed by hand.", ct);
    }

    // ---- closing ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Marks the preview Closing, cancels the deployments that did not start yet and queues <c>preview.teardown</c>. A pull request that was
    /// closed keeps <see cref="PreviewEnvironment.ClosedAt"/>; a preview removed by hand does not (a later push may bring it back).
    /// </summary>
    private async Task<Job> CloseAsync(Application app, PreviewEnvironment preview, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await CancelQueuedAsync(preview.Id, now, ct);
        await ConcurrencySupport.SaveWithRetryAsync(db, () =>
        {
            preview.Status = PreviewStatus.Closing;
            preview.StatusReason = reason;
            preview.StatusChangedAt = now;
        }, ct);

        return await QueueTeardownAsync(app.Id, preview, ct);
    }

    /// <summary>Queues the teardown of <paramref name="preview"/>, which must be Closing. Once per closing: the key carries the time it started.</summary>
    internal async Task<Job> QueueTeardownAsync(Guid applicationId, PreviewEnvironment preview, CancellationToken ct, string? attempt = null) =>
        await jobs.EnqueueAsync(new JobRequest(PreviewJobs.TeardownType, new PreviewTeardownPayload { PreviewId = preview.Id })
        {
            OrganizationId = await OrganizationOfAsync(applicationId, ct), Resource = new JobResource("application", applicationId),
            LockKey = PreviewJobs.LockKey(preview.Id), MaxAttempts = 3,
            IdempotencyKey = $"{PreviewJobs.TeardownType}:{preview.Id}:{preview.StatusChangedAt?.UtcTicks}{(attempt is null ? "" : ":" + attempt)}",
        }, ct);

    private async Task CancelQueuedAsync(Guid previewId, DateTimeOffset now, CancellationToken ct)
    {
        // A queued deployment whose job starts later finds it finished and does nothing; one in progress is left to its job (teardown waits for the lock).
        var queued = await db.Deployments.Include(d => d.Steps).Where(d => d.PreviewId == previewId && d.Status == DeploymentStatus.Queued).ToListAsync(ct);
        foreach (var deployment in queued) deployment.MarkCancelled(now);
        if (queued.Count > 0) await db.SaveChangesAsync(ct);
    }

    // ---- deploying -------------------------------------------------------------------------------------------------------------------

    /// <summary>The previews that may run at once (<see cref="PreviewConfig.MaxActive"/>, 10 by default).</summary>
    public static int MaxActive(Application app) => app.Previews.MaxActive is > 0 and var max ? max : PreviewConfig.DefaultMaxActive;

    /// <summary>
    /// Previews that hold a slot: deploying, running, or failed with a container still serving (the previous deployment). A pending one is waiting
    /// for a slot, not using one.
    /// </summary>
    private Task<int> UsedSlotsAsync(Guid applicationId, Guid exceptPreviewId, CancellationToken ct) =>
        db.Previews.CountAsync(p => p.ApplicationId == applicationId && p.Id != exceptPreviewId
            && (p.Status == PreviewStatus.Deploying || p.Status == PreviewStatus.Running || p.Status == PreviewStatus.Failed && p.CurrentDeploymentId != null), ct);

    /// <summary>
    /// Queues a deployment of the preview at its head commit, or keeps it Pending when every slot is taken (returns null then). A preview that
    /// already holds a slot never waits for one.
    /// </summary>
    internal async Task<QueuedDeployment?> DeployOrHoldAsync(Application app, PreviewEnvironment preview, DeploymentTrigger trigger, CancellationToken ct)
    {
        var holdsSlot = preview.Status is PreviewStatus.Deploying or PreviewStatus.Running || preview is { Status: PreviewStatus.Failed, CurrentDeploymentId: not null };
        if (!holdsSlot && await UsedSlotsAsync(app.Id, preview.Id, ct) >= MaxActive(app))
        {
            await ConcurrencySupport.SaveWithRetryAsync(db, () =>
            {
                preview.Status = PreviewStatus.Pending;
                preview.StatusReason = $"Waiting for a free slot: {MaxActive(app)} previews are running.";
                preview.StatusChangedAt = clock.UtcNow;
            }, ct);
            return null;
        }

        // A newer commit replaces the deployments that did not start yet.
        await CancelQueuedAsync(preview.Id, clock.UtcNow, ct);

        Deployment deployment;
        for (var attempt = 0; ; attempt++)
        {
            var now = clock.UtcNow;
            preview.Status = PreviewStatus.Deploying;
            preview.StatusChangedAt = now;
            deployment = Deployment.Queue(app.Id, app.EnvironmentId, preview.ServerId, app.AllocateDeploymentNumber(), trigger, now, app.Runtime.DeploymentStrategy);
            deployment.PreviewId = preview.Id;
            deployment.SourceType = app.SourceKind;
            deployment.RepositoryUrl = preview.SourceRepositoryUrl;
            deployment.Ref = preview.SourceBranch;
            deployment.CommitSha = preview.HeadSha;
            deployment.CommitMessage = Trim(preview.Title, 500);
            deployment.CommitAuthor = Trim(preview.AuthorLogin, 200);
            preview.StatusReason = $"Deployment #{deployment.Number} queued";
            db.Deployments.Add(deployment);
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (Exception ex) when (attempt < 5 && ex is DbUpdateConcurrencyException or DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } })
            {
                // The application row is also written by the deployment of production (status) and by another request taking the next number.
                db.Entry(deployment).State = EntityState.Detached;
                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is Workload && e.State != EntityState.Detached).ToList())
                    await entry.ReloadAsync(ct);
            }
        }

        var job = await jobs.EnqueueAsync(new JobRequest(DeploymentService.DeployJobType, new DeployPayload { DeploymentId = deployment.Id })
        {
            OrganizationId = await OrganizationOfAsync(app.Id, ct), Resource = new JobResource("application", app.Id), LockKey = PreviewJobs.LockKey(preview.Id),
            MaxAttempts = DeploymentService.DeployMaxAttempts,
        }, ct);
        return new QueuedDeployment(deployment, job.Id);
    }

    /// <summary>Deploys the Pending previews of an application, oldest first, while slots are free. Returns how many were queued.</summary>
    public async Task<int> PromotePendingAsync(Guid applicationId, CancellationToken ct)
    {
        var app = await db.Applications.Include(a => a.GitSource).FirstOrDefaultAsync(a => a.Id == applicationId, ct);
        if (app is null || !app.Previews.Enabled) return 0;

        var pending = await db.Previews.Where(p => p.ApplicationId == applicationId && p.Status == PreviewStatus.Pending && p.RemovedAt == null)
            .OrderBy(p => p.LastEventAt).ThenBy(p => p.Number).ToListAsync(ct);
        var queued = 0;
        foreach (var preview in pending)
        {
            try
            {
                if (await DeployOrHoldAsync(app, preview, DeploymentTrigger.PullRequest, ct) is null) break;
                queued++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Deploying the pending preview {PreviewId} failed", preview.Id);
                break;
            }
        }

        return queued;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------

    private async Task<Guid> OrganizationOfAsync(Guid applicationId, CancellationToken ct) =>
        await db.Workloads.IgnoreQueryFilters().AsNoTracking().Where(w => w.Id == applicationId).Select(w => w.Environment.Project.OrganizationId).FirstAsync(ct);

    private static string? Trim(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
