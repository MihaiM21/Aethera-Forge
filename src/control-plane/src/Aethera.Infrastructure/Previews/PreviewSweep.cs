using Aethera.Domain;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Previews;

/// <summary>What one sweep did.</summary>
public sealed record PreviewSweepReport(int Stale, int Retried, int Deployed);

/// <summary>
/// <c>preview.sweep</c>, daily per organization: removes previews whose pull request has had no event for 14 days (a closed event that never
/// arrived, a webhook that was removed), those of deleted applications, retries teardowns that got stuck, and deploys the Pending previews for which
/// a slot is free.
/// </summary>
public sealed class PreviewSweepJobHandler : IJobHandler
{
    public string Type => PreviewJobs.SweepType;

    /// <summary>A teardown that has been Closing this long without finishing is queued again.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(1);

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.GetPayload<PreviewSweepPayload>();
        var sp = context.Services;
        var report = await SweepAsync(sp.GetRequiredService<AetheraDbContext>(), sp.GetRequiredService<PreviewService>(), sp.GetRequiredService<IClock>(),
            payload.OrganizationId, cancellationToken);
        await context.Log.WriteSystemAsync($"Preview sweep: {report.Stale} stale preview(s) removed, {report.Retried} teardown(s) retried, {report.Deployed} pending preview(s) deployed.", cancellationToken);
        context.SetResult(report);
    }

    /// <summary>One pass over the previews of an organization. Public for tests.</summary>
    public static async Task<PreviewSweepReport> SweepAsync(AetheraDbContext db, PreviewService previews, IClock clock, Guid organizationId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var live = await db.Previews
            .Where(p => p.Status != PreviewStatus.Removed && db.Workloads.IgnoreQueryFilters().Any(w => w.Id == p.ApplicationId && w.Environment.Project.OrganizationId == organizationId))
            .ToListAsync(ct);
        var liveApplications = live.Select(p => p.ApplicationId).ToList();
        var deletedApplications = (await db.Workloads.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.DeletedAt != null && liveApplications.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct)).ToHashSet();

        int stale = 0, retried = 0;
        foreach (var preview in live)
        {
            try
            {
                if (preview.Status == PreviewStatus.Closing)
                {
                    if (now - (preview.StatusChangedAt ?? preview.UpdatedAt) < StuckAfter) continue;
                    await QueueAsync(db, previews, clock, preview, $"retry:{now:yyyyMMddHH}", ct);
                    retried++;
                    continue;
                }

                var silent = now - (preview.LastEventAt ?? preview.CreatedAt) > PreviewJobs.StaleAfter;
                if (!silent && !deletedApplications.Contains(preview.ApplicationId)) continue;
                preview.Status = PreviewStatus.Closing;
                preview.StatusReason = deletedApplications.Contains(preview.ApplicationId)
                    ? "The application was deleted."
                    : $"No pull request event for {PreviewJobs.StaleAfter.TotalDays:0} days.";
                preview.StatusChangedAt = now;
                await QueueAsync(db, previews, clock, preview, "sweep", ct);
                stale++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One preview that cannot be queued (its server was removed, say) must not stop the others.
                db.ChangeTracker.Clear();
            }
        }

        // Slots freed by the teardowns above are used by the next sweep or the teardown itself; Pending previews of applications with a free slot go now.
        var deployed = 0;
        var applicationIds = await db.Previews.AsNoTracking().Where(p => p.Status == PreviewStatus.Pending).Select(p => p.ApplicationId).Distinct().ToListAsync(ct);
        foreach (var applicationId in applicationIds.Where(id => !deletedApplications.Contains(id)))
            deployed += await previews.PromotePendingAsync(applicationId, ct);

        return new PreviewSweepReport(stale, retried, deployed);
    }

    private static async Task QueueAsync(AetheraDbContext db, PreviewService previews, IClock clock, PreviewEnvironment preview, string reason, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await previews.QueueTeardownAsync(preview.ApplicationId, preview, ct, reason);
        PreviewAudit.Record(db, clock, await OrganizationAsync(db, preview.ApplicationId, ct), "preview.teardown_requested", preview.Id,
            new { applicationId = preview.ApplicationId, number = preview.Number, reason });
        await db.SaveChangesAsync(ct);
    }

    private static Task<Guid> OrganizationAsync(AetheraDbContext db, Guid applicationId, CancellationToken ct) =>
        db.Workloads.IgnoreQueryFilters().AsNoTracking().Where(w => w.Id == applicationId).Select(w => w.Environment.Project.OrganizationId).FirstAsync(ct);
}

/// <summary>
/// Once an hour: queues the daily <c>preview.sweep</c> of every organization. The idempotency key carries the date, so a restart or a second API
/// instance queues nothing more, and a sweep whose job was lost is queued again by the next hour's pass.
/// </summary>
public sealed class PreviewSweepScheduler(IServiceScopeFactory scopes, IClock clock, IHostEnvironment environment, ILogger<PreviewSweepScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (AetheraHost.IsOpenApiGeneration || environment.IsEnvironment("Testing")) return; // tests call QueueOnceAsync
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); // start-up is busy: migrations, agents reconnecting
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try
                {
                    await QueueOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Queuing the preview sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Queues today's sweep for every organization that has previews. Returns how many jobs were queued (an existing one is not counted twice). Public for tests.</summary>
    public async Task<int> QueueOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var organizations = await db.Previews.AsNoTracking()
            .Select(p => db.Workloads.IgnoreQueryFilters().Where(w => w.Id == p.ApplicationId).Select(w => w.Environment.Project.OrganizationId).First())
            .Distinct().ToListAsync(ct);
        var day = clock.UtcNow.UtcDateTime.ToString("yyyyMMdd");
        foreach (var organizationId in organizations)
        {
            await jobs.EnqueueAsync(new JobRequest(PreviewJobs.SweepType, new PreviewSweepPayload { OrganizationId = organizationId })
            {
                OrganizationId = organizationId, Resource = new JobResource("organization", organizationId), IdempotencyKey = $"{PreviewJobs.SweepType}:{organizationId}:{day}",
                LockKey = $"{PreviewJobs.SweepType}:{organizationId}",
            }, ct);
        }

        return organizations.Count;
    }
}
