using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Jobs;

/// <summary>
/// Which organization a job belongs to: the <c>organization_id</c> column, set when the job is enqueued (from the request, else from the
/// acting user's organization). Jobs created by the system (webhooks, schedules) carry the organization of the resource they act on, so
/// no job is visible to more than one organization.
/// </summary>
public static class JobVisibility
{
    public static IQueryable<Job> VisibleTo(this IQueryable<Job> jobs, Guid organizationId) =>
        jobs.Where(j => j.OrganizationId == organizationId);
}

/// <summary>The log stream id of a job.</summary>
public static class JobStreams
{
    public const string Prefix = "job:";

    public static string StreamId(Guid jobId) => Prefix + jobId.ToString("D");

    public static bool TryParse(string streamId, out Guid jobId)
    {
        jobId = default;
        return streamId.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParse(streamId.AsSpan(Prefix.Length), out jobId);
    }
}

/// <summary>Outcome of authorizing a subscription to a log stream.</summary>
/// <param name="Allowed">False also when the stream does not exist: callers must not learn which.</param>
/// <param name="EndedReason">Non-null when the stream is complete and no more lines will come (<c>succeeded</c>, <c>failed</c>, <c>cancelled</c>).</param>
public sealed record LogStreamAccess(bool Allowed, string? EndedReason = null)
{
    public static readonly LogStreamAccess Denied = new(false);
}

/// <summary>
/// Decides who may read a kind of log stream (by prefix: <c>job:</c>, later <c>build:</c>, <c>deploy:</c>, <c>agent:</c>). Register one
/// per kind; a stream no authorizer handles cannot be subscribed to.
/// </summary>
public interface ILogStreamAuthorizer
{
    bool Handles(string streamId);

    Task<LogStreamAccess> AuthorizeAsync(string streamId, Guid organizationId, CancellationToken cancellationToken);
}

/// <summary>Authorizes <c>job:&lt;id&gt;</c> streams through <see cref="JobVisibility"/>.</summary>
public sealed class JobLogStreamAuthorizer(IServiceScopeFactory scopes) : ILogStreamAuthorizer
{
    public bool Handles(string streamId) => JobStreams.TryParse(streamId, out _);

    public async Task<LogStreamAccess> AuthorizeAsync(string streamId, Guid organizationId, CancellationToken cancellationToken)
    {
        if (!JobStreams.TryParse(streamId, out var jobId)) return LogStreamAccess.Denied;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var status = await db.Jobs.AsNoTracking().VisibleTo(organizationId)
            .Where(j => j.Id == jobId)
            .Select(j => (JobStatus?)j.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (status is null) return LogStreamAccess.Denied;
        return new LogStreamAccess(true, status switch
        {
            JobStatus.Succeeded => "succeeded",
            JobStatus.Failed => "failed",
            JobStatus.Cancelled => "cancelled",
            _ => null,
        });
    }
}
