using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Features.Resources.Services;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Projects;

public sealed record CreateEnvironmentRequest
{
    public string? Name { get; init; }
    public string? Slug { get; init; }
    public string? Description { get; init; }

    /// <summary>Extra confirmation for destructive actions. Defaults to true for an environment with the slug <c>production</c>.</summary>
    public bool? IsProduction { get; init; }
}

public sealed record UpdateEnvironmentRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public string? Slug { get; init; }
    public string? Description { get; init; }
    [NotClearable] public bool? IsProduction { get; init; }
}

public sealed record CreateProjectRequest
{
    public string? Name { get; init; }
    public string? Slug { get; init; }
    public string? Description { get; init; }

    /// <summary>The environments to create. When the property is absent, one <c>production</c> environment is created.</summary>
    public List<CreateEnvironmentRequest>? Environments { get; init; }
}

public sealed record UpdateProjectRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public string? Slug { get; init; }
    public string? Description { get; init; }
}

public sealed record CreateProjectFromTemplateRequest
{
    /// <summary>A key of <c>GET /project-templates</c>.</summary>
    public string? TemplateKey { get; init; }

    public string? Name { get; init; }
    public string? Slug { get; init; }
    public string? Description { get; init; }

    /// <summary>The server the template's applications and services are placed on. Required when the template has any.</summary>
    public Guid? ServerId { get; init; }
}

public sealed record EnvironmentResponse(
    Guid Id, Guid ProjectId, string Name, string Slug, string? Description, bool IsProduction, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ProjectResponse(
    Guid Id, string Name, string Slug, string? Description, string? TemplateKey, IReadOnlyList<EnvironmentResponse> Environments,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreatedWorkloadResponse(Guid Id, string Kind, string Name, string Slug, Guid EnvironmentId);

public sealed record ProjectFromTemplateResponse(ProjectResponse Project, IReadOnlyList<CreatedWorkloadResponse> Workloads);

public sealed class CreateEnvironmentValidator : AbstractValidator<CreateEnvironmentRequest>
{
    public CreateEnvironmentValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug).Must(Slug.IsValid).WithErrorCode("pattern")
            .WithMessage("Use lowercase letters, digits and single hyphens (1-63 characters).").When(x => x.Slug is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public sealed class UpdateEnvironmentValidator : AbstractValidator<UpdateEnvironmentRequest>
{
    public UpdateEnvironmentValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Slug).Must(Slug.IsValid).WithErrorCode("pattern")
            .WithMessage("Use lowercase letters, digits and single hyphens (1-63 characters).").When(x => x.Slug is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public sealed class CreateProjectValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug).Must(Slug.IsValid).WithErrorCode("pattern")
            .WithMessage("Use lowercase letters, digits and single hyphens (1-63 characters).").When(x => x.Slug is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Environments).Must(e => e is null || e.Count <= 20).WithErrorCode("too_long").WithMessage("At most 20 environments.");
        RuleForEach(x => x.Environments).SetValidator(new CreateEnvironmentValidator());
        RuleFor(x => x.Environments).Must(e => e is null || e.Select(x => ProjectEndpoints.EnvironmentSlug(x)).Distinct().Count() == e.Count)
            .WithErrorCode("not_unique").WithMessage("Environment slugs must be unique within the project.");
    }
}

public sealed class UpdateProjectValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Slug).Must(Slug.IsValid).WithErrorCode("pattern")
            .WithMessage("Use lowercase letters, digits and single hyphens (1-63 characters).").When(x => x.Slug is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public sealed class CreateProjectFromTemplateValidator : AbstractValidator<CreateProjectFromTemplateRequest>
{
    public CreateProjectFromTemplateValidator()
    {
        RuleFor(x => x.TemplateKey).NotEmpty().Must(k => k is null || ProjectTemplates.Find(k) is not null).WithErrorCode("not_found")
            .WithMessage($"Unknown template. Available: {string.Join(", ", ProjectTemplates.All.Select(t => t.Key))}.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug).Must(Slug.IsValid).WithErrorCode("pattern")
            .WithMessage("Use lowercase letters, digits and single hyphens (1-63 characters).").When(x => x.Slug is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.ServerId).NotNull().When(x => ProjectTemplates.Find(x.TemplateKey) is { Workloads.Count: > 0 })
            .WithMessage("This template places applications and services on a server; choose one.");
    }
}

internal static class ProjectEndpoints
{
    private static readonly SortDefinition<Project> ProjectSorts = new SortDefinition<Project>("-createdAt")
        .Add("createdAt", p => p.CreatedAt).Add("updatedAt", p => p.UpdatedAt).Add("name", p => p.Name).Add("slug", p => p.Slug);

    private static readonly SortDefinition<ProjectEnvironment> EnvironmentSorts = new SortDefinition<ProjectEnvironment>("createdAt")
        .Add("createdAt", e => e.CreatedAt).Add("name", e => e.Name).Add("slug", e => e.Slug);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/project-templates", () => TypedResults.Ok(ProjectTemplates.All)).WithName("listProjectTemplates").WithTags("Projects").RequireRead();

        var projects = api.MapGroup("/projects").WithTags("Projects");
        projects.MapGet("/", ListProjects).WithName("listProjects").RequireRead();
        projects.MapPost("/", CreateProject).WithName("createProject").Validate<CreateProjectRequest>().RequireWrite();
        projects.MapPost("/from-template", CreateFromTemplate).WithName("createProjectFromTemplate").Validate<CreateProjectFromTemplateRequest>().RequireWrite();
        projects.MapGet("/{id:guid}", GetProject).WithName("getProject").RequireRead();
        projects.MapPatch("/{id:guid}", UpdateProject).WithName("updateProject")
            .Accepts<UpdateProjectRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateProjectRequest>().RequireWrite();
        projects.MapDelete("/{id:guid}", DeleteProject).WithName("deleteProject").RequireWrite();
        projects.MapGet("/{id:guid}/environments", ListEnvironments).WithName("listProjectEnvironments").RequireRead();
        projects.MapPost("/{id:guid}/environments", CreateEnvironment).WithName("createProjectEnvironment")
            .Validate<CreateEnvironmentRequest>().RequireWrite();

        var environments = api.MapGroup("/environments").WithTags("Projects");
        environments.MapGet("/{id:guid}", GetEnvironment).WithName("getEnvironment").RequireRead();
        environments.MapPatch("/{id:guid}", UpdateEnvironment).WithName("updateEnvironment")
            .Accepts<UpdateEnvironmentRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateEnvironmentRequest>().RequireWrite();
        environments.MapDelete("/{id:guid}", DeleteEnvironment).WithName("deleteEnvironment").RequireWrite();
    }

    internal static string EnvironmentSlug(CreateEnvironmentRequest e) =>
        string.IsNullOrWhiteSpace(e.Slug) ? Slug.FromName(e.Name ?? "") : e.Slug;

    // ---- projects -------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<ProjectResponse>>> ListProjects(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, string? q,
        CancellationToken ct)
    {
        http.RejectUnknownQuery("q");
        var query = db.ProjectsOf(actor.Org()).AsNoTracking().Include(p => p.Environments.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)).AsQueryable();
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(p => EF.Functions.ILike(p.Name, like, "\\") || EF.Functions.ILike(p.Slug, like, "\\"));
        var (items, next) = await ProjectSorts.PageAsync(query, sort, page, cursors, $"q={q}", ct);
        return TypedResults.Ok(new Page<ProjectResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Created<ProjectResponse>> CreateProject(
        CreateProjectRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var org = actor.Org();
        var name = request.Name!.Trim();
        var slug = ResolveSlug(request.Slug, name);
        await EnsureProjectSlugFreeAsync(db, org, slug, null, ct);

        var project = new Project { OrganizationId = org, Name = name, Slug = slug, Description = request.Description };
        var environments = request.Environments ?? [new CreateEnvironmentRequest { Name = "Production", Slug = "production" }];
        foreach (var environment in environments) project.Environments.Add(NewEnvironment(project, environment));

        db.Projects.Add(project);
        await audit.RecordAsync("project.created", "project", project.Id,
            new { name, slug, environments = project.Environments.Select(e => e.Slug).ToList() }, ct);
        http.SetETag(project.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("projects", project.Id), ToResponse(project));
    }

    private static async Task<Created<ProjectFromTemplateResponse>> CreateFromTemplate(
        CreateProjectFromTemplateRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        CancellationToken ct)
    {
        var org = actor.Org();
        var template = ProjectTemplates.Find(request.TemplateKey)!;
        var name = request.Name!.Trim();
        var slug = ResolveSlug(request.Slug, name);
        await EnsureProjectSlugFreeAsync(db, org, slug, null, ct);
        var server = template.Workloads.Count > 0 ? await db.RequireServerAsync(org, request.ServerId, "/serverId", ct) : null;

        var project = new Project { OrganizationId = org, Name = name, Slug = slug, Description = request.Description, TemplateKey = template.Key };
        foreach (var environment in template.Environments)
            project.Environments.Add(NewEnvironment(project, new CreateEnvironmentRequest { Name = environment.Name, Slug = environment.Slug, IsProduction = environment.IsProduction }));

        var target = project.Environments.FirstOrDefault(e => e.IsProduction) ?? project.Environments[0];
        var created = new List<(Workload Workload, string Kind)>();
        foreach (var item in template.Workloads)
        {
            Workload workload;
            if (item.Kind == "service")
            {
                var serviceTemplate = ServiceTemplates.Find(item.ServiceTemplateKey)!;
                workload = ServiceFactory.Build(vault, org, target, server!, serviceTemplate, item.Name, item.Slug, null, null, null, null);
            }
            else
            {
                var app = new Application
                {
                    EnvironmentId = target.Id, ServerId = server!.Id, Name = item.Name, Slug = item.Slug, SourceKind = item.SourceKind!.Value,
                    Description = "Placeholder from the project template: connect a repository or image to deploy it.",
                };
                app.Environment = target;
                if (item.Port is { } port) app.Ports.Add(new WorkloadPort { WorkloadId = app.Id, ContainerPort = port, IsHttp = item.IsHttp });
                workload = app;
            }

            db.Workloads.Add(workload);
            created.Add((workload, item.Kind));
        }

        db.Projects.Add(project);
        await audit.RecordAsync("project.created", "project", project.Id,
            new
            {
                name, slug, template = template.Key, environments = project.Environments.Select(e => e.Slug).ToList(),
                workloads = created.Select(c => new { c.Workload.Id, c.Kind, slug = c.Workload.Slug }).ToList(),
            }, ct);
        foreach (var secretId in created.SelectMany(c => c.Workload.EnvironmentVariables).Where(v => v.SecretId is not null).Select(v => v.SecretId!.Value))
            await audit.RecordAsync("secret.created", "secret", secretId, new { generated = true, project = slug }, ct);

        http.SetETag(project.RowVersion);
        var body = new ProjectFromTemplateResponse(ToResponse(project),
            created.Select(c => new CreatedWorkloadResponse(c.Workload.Id, c.Kind, c.Workload.Name, c.Workload.Slug, c.Workload.EnvironmentId)).ToList());
        return TypedResults.Created(ResourceHttp.Path("projects", project.Id), body);
    }

    private static async Task<Ok<ProjectResponse>> GetProject(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var project = await db.GetProjectAsync(actor.Org(), id, tracking: false, ct);
        http.SetETag(project.RowVersion);
        return TypedResults.Ok(ToResponse(project));
    }

    private static async Task<Ok<ProjectResponse>> UpdateProject(
        Guid id, PatchRequest<UpdateProjectRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        CancellationToken ct)
    {
        var org = actor.Org();
        var project = await db.GetProjectAsync(org, id, tracking: true, ct);
        http.CheckIfMatch(project.RowVersion);

        var changed = new List<string>();
        if (patch.Has("name") && patch.Body.Name is { } name) { project.Name = name.Trim(); changed.Add("name"); }
        if (patch.Has("slug") && patch.Body.Slug is { } slug && slug != project.Slug)
        {
            await EnsureProjectSlugFreeAsync(db, org, slug, id, ct);
            project.Slug = slug;
            changed.Add("slug");
        }

        if (patch.Has("description")) { project.Description = patch.Body.Description; changed.Add("description"); }

        await audit.RecordAsync("project.updated", "project", id, new { slug = project.Slug, changed }, ct);
        http.SetETag(project.RowVersion);
        return TypedResults.Ok(ToResponse(project));
    }

    private static async Task<NoContent> DeleteProject(
        Guid id, string? confirm, bool? cascade, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock,
        CancellationToken ct)
    {
        var project = await db.GetProjectAsync(actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(project.RowVersion);
        Confirmation.Require(confirm, project.Slug);

        var workloads = await db.Workloads.Where(w => w.Environment.ProjectId == id).ToListAsync(ct);
        if (workloads.Count > 0 && cascade != true)
            throw new ApiProblemException(ApiProblems.Conflict(ResourceProblemCodes.ProjectNotEmpty,
                $"The project still has {workloads.Count} application(s) or service(s). Delete them first, or repeat the request with cascade=true."));

        var now = clock.UtcNow;
        await SoftDeleteWorkloadsAsync(db, workloads, now, ct);
        foreach (var environment in project.Environments) environment.MarkDeleted(now);
        project.MarkDeleted(now);
        await audit.RecordAsync("project.deleted", "project", id,
            new { slug = project.Slug, cascade = cascade == true, environments = project.Environments.Count, workloads = workloads.Count }, ct);
        return TypedResults.NoContent();
    }

    // ---- environments ---------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<EnvironmentResponse>>> ListEnvironments(
        Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort,
        CancellationToken ct)
    {
        http.RejectUnknownQuery();
        var org = actor.Org();
        if (!await db.ProjectsOf(org).AnyAsync(p => p.Id == id, ct)) throw new ApiProblemException(ApiProblems.NotFound("project", id));
        var query = db.EnvironmentsOf(org).AsNoTracking().Where(e => e.ProjectId == id);
        var (items, next) = await EnvironmentSorts.PageAsync(query, sort, page, cursors, $"project={id}", ct);
        return TypedResults.Ok(new Page<EnvironmentResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Created<EnvironmentResponse>> CreateEnvironment(
        Guid id, CreateEnvironmentRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var project = await db.GetProjectAsync(actor.Org(), id, tracking: true, ct);
        var environment = NewEnvironment(project, request);
        if (project.Environments.Any(e => e.Slug == environment.Slug))
            throw new ApiProblemException(ApiProblems.AlreadyExists("environment", $"The project already has an environment with the slug '{environment.Slug}'."));

        db.Environments.Add(environment);
        await audit.RecordAsync("environment.created", "environment", environment.Id,
            new { projectId = project.Id, slug = environment.Slug, isProduction = environment.IsProduction }, ct);
        http.SetETag(environment.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("environments", environment.Id), ToResponse(environment));
    }

    private static async Task<Ok<EnvironmentResponse>> GetEnvironment(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var environment = await db.GetEnvironmentAsync(actor.Org(), id, tracking: false, ct);
        http.SetETag(environment.RowVersion);
        return TypedResults.Ok(ToResponse(environment));
    }

    private static async Task<Ok<EnvironmentResponse>> UpdateEnvironment(
        Guid id, PatchRequest<UpdateEnvironmentRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        CancellationToken ct)
    {
        var environment = await db.GetEnvironmentAsync(actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(environment.RowVersion);

        var changed = new List<string>();
        if (patch.Has("name") && patch.Body.Name is { } name) { environment.Name = name.Trim(); changed.Add("name"); }
        if (patch.Has("slug") && patch.Body.Slug is { } slug && slug != environment.Slug)
        {
            if (await db.Environments.AnyAsync(e => e.ProjectId == environment.ProjectId && e.Slug == slug && e.Id != id, ct))
                throw new ApiProblemException(ApiProblems.AlreadyExists("environment", $"The project already has an environment with the slug '{slug}'."));
            environment.Slug = slug;
            changed.Add("slug");
        }

        if (patch.Has("description")) { environment.Description = patch.Body.Description; changed.Add("description"); }
        if (patch.Has("isProduction") && patch.Body.IsProduction is { } production) { environment.IsProduction = production; changed.Add("isProduction"); }

        await audit.RecordAsync("environment.updated", "environment", id, new { slug = environment.Slug, changed }, ct);
        http.SetETag(environment.RowVersion);
        return TypedResults.Ok(ToResponse(environment));
    }

    private static async Task<NoContent> DeleteEnvironment(
        Guid id, string? confirm, bool? cascade, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock,
        CancellationToken ct)
    {
        var environment = await db.GetEnvironmentAsync(actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(environment.RowVersion);
        Confirmation.Require(confirm, environment.Slug);

        var workloads = await db.Workloads.Where(w => w.EnvironmentId == id).ToListAsync(ct);
        if (workloads.Count > 0 && cascade != true)
            throw new ApiProblemException(ApiProblems.Conflict(ResourceProblemCodes.EnvironmentNotEmpty,
                $"The environment still has {workloads.Count} application(s) or service(s). Delete them first, or repeat the request with cascade=true."));

        var now = clock.UtcNow;
        await SoftDeleteWorkloadsAsync(db, workloads, now, ct);
        environment.MarkDeleted(now);
        await audit.RecordAsync("environment.deleted", "environment", id,
            new { projectId = environment.ProjectId, slug = environment.Slug, cascade = cascade == true, workloads = workloads.Count }, ct);
        return TypedResults.NoContent();
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------

    private static async Task SoftDeleteWorkloadsAsync(AetheraDbContext db, List<Workload> workloads, DateTimeOffset now, CancellationToken ct)
    {
        if (workloads.Count == 0) return;
        var ids = workloads.Select(w => w.Id).ToList();
        WorkloadSupport.SoftDeleteDomains(db, await db.Domains.Where(d => ids.Contains(d.WorkloadId)).ToListAsync(ct), now);
        foreach (var workload in workloads) workload.MarkDeleted(now);
    }

    private static ProjectEnvironment NewEnvironment(Project project, CreateEnvironmentRequest request)
    {
        var name = request.Name!.Trim();
        var slug = ResolveSlug(request.Slug, name);
        return new ProjectEnvironment
        {
            ProjectId = project.Id, Project = project, Name = name, Slug = slug, Description = request.Description,
            IsProduction = request.IsProduction ?? slug == "production",
        };
    }

    private static string ResolveSlug(string? slug, string name)
    {
        var resolved = string.IsNullOrWhiteSpace(slug) ? Slug.FromName(name) : slug;
        if (!Slug.IsValid(resolved))
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/slug", "pattern",
                "Could not derive a slug from the name; provide one (lowercase letters, digits and hyphens).")]));
        return resolved;
    }

    private static async Task EnsureProjectSlugFreeAsync(AetheraDbContext db, Guid org, string slug, Guid? exceptId, CancellationToken ct)
    {
        if (await db.Projects.AnyAsync(p => p.OrganizationId == org && p.Slug == slug && p.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("project", $"A project with the slug '{slug}' already exists."));
    }

    internal static ProjectResponse ToResponse(Project p) => new(
        p.Id, p.Name, p.Slug, p.Description, p.TemplateKey,
        p.Environments.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Select(ToResponse).ToList(), p.CreatedAt, p.UpdatedAt);

    internal static EnvironmentResponse ToResponse(ProjectEnvironment e) =>
        new(e.Id, e.ProjectId, e.Name, e.Slug, e.Description, e.IsProduction, e.CreatedAt, e.UpdatedAt);
}
