using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources;

/// <summary>
/// Organization-scoped queries and reference lookups. Rows of other organizations are indistinguishable from missing rows (404, or a
/// 422 <c>not_found</c> when the id came from a request body).
/// </summary>
public static class Lookups
{
    // ---- scoped queries -------------------------------------------------------------------------------------------------------------

    public static IQueryable<Project> ProjectsOf(this AetheraDbContext db, Guid org) => db.Projects.Where(p => p.OrganizationId == org);

    public static IQueryable<ProjectEnvironment> EnvironmentsOf(this AetheraDbContext db, Guid org) =>
        db.Environments.Where(e => e.Project.OrganizationId == org);

    public static IQueryable<Server> ServersOf(this AetheraDbContext db, Guid org) => db.Servers.Where(s => s.OrganizationId == org);

    public static IQueryable<Secret> SecretsOf(this AetheraDbContext db, Guid org) => db.Secrets.Where(s => s.OrganizationId == org);

    public static IQueryable<Registry> RegistriesOf(this AetheraDbContext db, Guid org) => db.Registries.Where(r => r.OrganizationId == org);

    public static IQueryable<Application> ApplicationsOf(this AetheraDbContext db, Guid org) =>
        db.Applications.Where(a => a.Environment.Project.OrganizationId == org);

    public static IQueryable<Service> ServicesOf(this AetheraDbContext db, Guid org) =>
        db.Services.Where(s => s.Environment.Project.OrganizationId == org);

    public static IQueryable<Workload> WorkloadsOf(this AetheraDbContext db, Guid org) =>
        db.Workloads.Where(w => w.Environment.Project.OrganizationId == org);

    public static IQueryable<Volume> VolumesOf(this AetheraDbContext db, Guid org) =>
        db.Volumes.Where(v => v.Workload.Environment.Project.OrganizationId == org && v.Workload.DeletedAt == null);

    public static IQueryable<WorkloadDomain> DomainsOf(this AetheraDbContext db, Guid org) =>
        db.Domains.Where(d => d.Workload.Environment.Project.OrganizationId == org && d.Workload.DeletedAt == null);

    // ---- by id (404) ----------------------------------------------------------------------------------------------------------------

    public static async Task<Project> GetProjectAsync(this AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.ProjectsOf(org).Include(p => p.Environments.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("project", id));
    }

    public static async Task<ProjectEnvironment> GetEnvironmentAsync(this AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.EnvironmentsOf(org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("environment", id));
    }

    public static async Task<Server> GetServerAsync(this AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.ServersOf(org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("server", id));
    }

    public static async Task<Secret> GetSecretAsync(this AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.SecretsOf(org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("secret", id));
    }

    public static async Task<Registry> GetRegistryAsync(this AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.RegistriesOf(org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("registry", id));
    }

    // ---- references in request bodies (422 not_found) ---------------------------------------------------------------------------------

    public static ApiProblemException BadReference(string pointer, string what) =>
        new(ApiProblems.Validation([FieldError.AtPointer(pointer, "not_found", $"No {what} with this id exists.")]));

    public static async Task<ProjectEnvironment?> FindEnvironmentAsync(this AetheraDbContext db, Guid org, Guid? id, CancellationToken ct) =>
        id is { } value ? await db.EnvironmentsOf(org).Include(e => e.Project).FirstOrDefaultAsync(e => e.Id == value, ct) : null;

    public static async Task<Server?> FindServerAsync(this AetheraDbContext db, Guid org, Guid? id, CancellationToken ct) =>
        id is { } value ? await db.ServersOf(org).FirstOrDefaultAsync(s => s.Id == value, ct) : null;

    public static async Task<ProjectEnvironment> RequireEnvironmentAsync(
        this AetheraDbContext db, Guid org, Guid? id, string pointer, CancellationToken ct) =>
        id is { } value && await db.EnvironmentsOf(org).Include(e => e.Project).FirstOrDefaultAsync(e => e.Id == value, ct) is { } env
            ? env
            : throw BadReference(pointer, "environment");

    public static async Task<Server> RequireServerAsync(this AetheraDbContext db, Guid org, Guid? id, string pointer, CancellationToken ct) =>
        id is { } value && await db.ServersOf(org).FirstOrDefaultAsync(s => s.Id == value, ct) is { } server
            ? server
            : throw BadReference(pointer, "server");

    public static async Task<Registry> RequireRegistryAsync(this AetheraDbContext db, Guid org, Guid id, string pointer, CancellationToken ct) =>
        await db.RegistriesOf(org).FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw BadReference(pointer, "registry");

    public static async Task<Workload> RequireWorkloadAsync(this AetheraDbContext db, Guid org, Guid? id, string pointer, CancellationToken ct) =>
        id is { } value && await db.WorkloadsOf(org).Include(w => w.Environment).FirstOrDefaultAsync(w => w.Id == value, ct) is { } workload
            ? workload
            : throw BadReference(pointer, "application or service");

    /// <summary>
    /// The secret, if it exists in the organization and the caller may bind it to the workload: an API token needs <c>secrets:write</c>; a managed
    /// secret is never bindable (a generated service secret only to its own service); an organization-wide secret needs an Administrator; the
    /// secret's scope must contain the workload. Otherwise 403 (<c>secret.binding_forbidden</c>) or 422.
    /// </summary>
    public static async Task<Secret> RequireSecretForWorkloadAsync(
        this AetheraDbContext db, ICurrentActor actor, Guid org, Guid secretId, Workload workload, string pointer, CancellationToken ct)
    {
        ManagedSecrets.RequireTokenScope(actor);
        var secret = await db.SecretsOf(org).FirstOrDefaultAsync(s => s.Id == secretId, ct) ?? throw BadReference(pointer, "secret");
        ManagedSecrets.EnsureMayBind(actor, secret, workload);
        var environment = workload.Environment ?? await db.Environments.FirstAsync(e => e.Id == workload.EnvironmentId, ct);
        var usable = secret.Scope switch
        {
            SecretScope.Organization => true,
            SecretScope.Project => secret.ProjectId == environment.ProjectId,
            SecretScope.Environment => secret.EnvironmentId == workload.EnvironmentId,
            SecretScope.Workload => secret.WorkloadId == workload.Id,
            _ => false,
        };
        return usable
            ? secret
            : throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer(pointer, "scope_mismatch",
                "This secret is scoped to a different project, environment or workload.")]));
    }
}

/// <summary>Collects "this id does not exist" errors of several body references so they are reported together as one 422.</summary>
public sealed class ReferenceCheck
{
    private readonly List<FieldError> _errors = [];

    public T? Check<T>(T? found, string pointer, string what) where T : class
    {
        if (found is null) _errors.Add(FieldError.AtPointer(pointer, "not_found", $"No {what} with this id exists."));
        return found;
    }

    public void ThrowIfAny()
    {
        if (_errors.Count > 0) throw new ApiProblemException(ApiProblems.Validation(_errors));
    }
}
