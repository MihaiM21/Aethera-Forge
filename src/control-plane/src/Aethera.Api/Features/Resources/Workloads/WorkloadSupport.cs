using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Workloads;

/// <summary>Helpers shared by the application and service endpoints.</summary>
internal static class WorkloadSupport
{
    public static string ResolveSlug(string? slug, string name, string pointer = "/slug")
    {
        var resolved = string.IsNullOrWhiteSpace(slug) ? Slug.FromName(name) : slug;
        if (!Slug.IsValid(resolved))
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer(pointer, "pattern",
                "Could not derive a slug from the name; provide one (lowercase letters, digits and hyphens).")]));
        return resolved;
    }

    /// <summary>Applications and services share one slug space per environment (<c>ix_workloads_environment_id_slug</c>).</summary>
    public static async Task EnsureSlugFreeAsync(
        AetheraDbContext db, Guid environmentId, string slug, string resource, Guid? exceptId, CancellationToken ct)
    {
        if (await db.Workloads.AnyAsync(w => w.EnvironmentId == environmentId && w.Slug == slug && w.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists(resource,
                $"The environment already has an application or service with the slug '{slug}'."));
    }

    /// <summary>404 (<c>application.not_found</c> / <c>service.not_found</c>) unless the workload of the URL exists in the organization.</summary>
    public static async Task RequireWorkloadRouteAsync(AetheraDbContext db, Guid org, bool isApplication, Guid id, CancellationToken ct)
    {
        var exists = isApplication
            ? await db.ApplicationsOf(org).AnyAsync(a => a.Id == id, ct)
            : await db.ServicesOf(org).AnyAsync(s => s.Id == id, ct);
        if (!exists) throw new ApiProblemException(ApiProblems.NotFound(isApplication ? "application" : "service", id));
    }

    public static WorkloadStatusResponse StatusOf(Workload w) =>
        new(w.DesiredState, w.Status, w.StatusReason, w.StatusChangedAt, w.StatusObservedAt, w.CurrentDeploymentId);

    /// <summary>Domains record the server of their workload (the proxy lives there): keep them in step when the workload moves.</summary>
    public static async Task MoveDomainsAsync(AetheraDbContext db, Guid workloadId, Guid serverId, CancellationToken ct)
    {
        foreach (var domain in await db.Domains.Where(d => d.WorkloadId == workloadId && d.ServerId != serverId).ToListAsync(ct))
            domain.ServerId = serverId;
    }

    public static void SoftDeleteDomains(AetheraDbContext db, IEnumerable<WorkloadDomain> domains, DateTimeOffset now)
    {
        foreach (var domain in domains) domain.MarkDeleted(now);
    }
}
