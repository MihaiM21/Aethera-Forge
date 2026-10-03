using System.Text.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Features.Resources.Services;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Workloads;

public sealed record CreateServiceRequest
{
    public string? Name { get; init; }
    public string? Slug { get; init; }
    public string? Description { get; init; }
    public Guid? EnvironmentId { get; init; }
    public Guid? ServerId { get; init; }

    /// <summary>A key of <c>GET /service-templates</c> (postgres, redis, ...).</summary>
    public string? TemplateKey { get; init; }

    /// <summary>One of the template's versions; the template default when absent.</summary>
    public string? Version { get; init; }

    /// <summary>Overrides the image of the chosen version.</summary>
    public string? Image { get; init; }

    /// <summary>Template-specific settings, stored as given.</summary>
    public JsonObject? Config { get; init; }

    public RuntimeRequest? Runtime { get; init; }
}

public sealed record UpdateServiceRequest
{
    [NotClearable] public string? Name { get; init; }
    public string? Description { get; init; }
    [NotClearable] public Guid? ServerId { get; init; }
    [NotClearable] public string? Image { get; init; }
    public string? TemplateVersion { get; init; }

    /// <summary>Merge-patched into the stored config (RFC 7396): null removes a key.</summary>
    public JsonObject? Config { get; init; }

    [NotClearable] public RuntimeRequest? Runtime { get; init; }
}

public sealed record ServiceResponse(
    Guid Id, string Name, string Slug, string? Description, Guid ProjectId, Guid EnvironmentId, Guid ServerId, string TemplateKey,
    string? TemplateVersion, string Image, JsonNode? Config, WorkloadStatusResponse State, RuntimeResponse Runtime,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ServiceSummary(
    Guid Id, string Name, string Slug, string? Description, Guid ProjectId, Guid EnvironmentId, Guid ServerId, string TemplateKey,
    string? TemplateVersion, string Image, DesiredState DesiredState, WorkloadStatus Status, string? StatusReason, Guid? CurrentDeploymentId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class CreateServiceValidator : AbstractValidator<CreateServiceRequest>
{
    public CreateServiceValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug).Must(Slug.IsValid).WithErrorCode("pattern")
            .WithMessage("Use lowercase letters, digits and single hyphens (1-63 characters).").When(x => x.Slug is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.EnvironmentId).NotNull();
        RuleFor(x => x.ServerId).NotNull();
        RuleFor(x => x.TemplateKey).NotEmpty()
            .Must(k => k is null || ServiceTemplates.Find(k) is not null).WithErrorCode("not_found")
            .WithMessage($"Unknown template. Available: {string.Join(", ", ServiceTemplates.All.Select(t => t.Key))}.");
        RuleFor(x => x.Image).MaximumLength(500).Matches(@"^[A-Za-z0-9][A-Za-z0-9._\-/:@]*$").When(x => x.Image is not null);
        RuleFor(x => x.Config).Must(c => c is null || c.ToJsonString().Length <= 64 * 1024).WithErrorCode("too_long")
            .WithMessage("The config must be at most 64 KiB.");
        RuleFor(x => x).Custom((x, context) =>
        {
            if (x.Version is null || ServiceTemplates.Find(x.TemplateKey) is not { } template || x.Image is not null) return;
            if (ServiceTemplates.Version(template, x.Version) is null)
                context.AddFailure(new FluentValidation.Results.ValidationFailure("Version",
                    $"Unknown version. Available: {string.Join(", ", template.Versions.Select(v => v.Version))}.") { ErrorCode = "not_found" });
        });
        RuleFor(x => x.Runtime).SetValidator(new RuntimeRequestValidator()!);
    }
}

public sealed class UpdateServiceValidator : AbstractValidator<UpdateServiceRequest>
{
    public UpdateServiceValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Image).MaximumLength(500).Matches(@"^[A-Za-z0-9][A-Za-z0-9._\-/:@]*$").When(x => x.Image is not null);
        RuleFor(x => x.TemplateVersion).MaximumLength(64);
        RuleFor(x => x.Config).Must(c => c is null || c.ToJsonString().Length <= 64 * 1024).WithErrorCode("too_long")
            .WithMessage("The config must be at most 64 KiB.");
        RuleFor(x => x.Runtime).SetValidator(new RuntimeRequestValidator()!);
    }
}

internal static class ServiceEndpoints
{
    private static readonly SortDefinition<Service> Sorts = new SortDefinition<Service>("-createdAt")
        .Add("createdAt", s => s.CreatedAt).Add("updatedAt", s => s.UpdatedAt).Add("name", s => s.Name).Add("slug", s => s.Slug);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/service-templates", ListTemplates).WithName("listServiceTemplates").WithTags("Services").RequireRead();

        var group = api.MapGroup("/services").WithTags("Services");
        group.MapGet("/", List).WithName("listServices").RequireRead();
        group.MapPost("/", Create).WithName("createService").Validate<CreateServiceRequest>().RequireWrite();
        group.MapGet("/{id:guid}", Get).WithName("getService").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateService")
            .Accepts<UpdateServiceRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateServiceRequest>().RequireWrite();
        group.MapDelete("/{id:guid}", Delete).WithName("deleteService").RequireWrite();
    }

    private static Ok<IReadOnlyList<ServiceTemplate>> ListTemplates() => TypedResults.Ok(ServiceTemplates.All);

    private static async Task<Ok<Page<ServiceSummary>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page,
        string? sort, Guid? projectId, Guid? environmentId, Guid? serverId, string? status, string? templateKey, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("projectId", "environmentId", "serverId", "status", "templateKey", "q");
        var statuses = ResourceHttp.ParseEnumFilter<WorkloadStatus>(status, "status");
        var like = ResourceHttp.LikePattern(q);

        var query = db.ServicesOf(actor.Org()).AsNoTracking().Include(s => s.Environment).AsQueryable();
        if (projectId is { } p) query = query.Where(s => s.Environment.ProjectId == p);
        if (environmentId is { } e) query = query.Where(s => s.EnvironmentId == e);
        if (serverId is { } sv) query = query.Where(s => s.ServerId == sv);
        if (statuses is not null) query = query.Where(s => statuses.Contains(s.Status));
        if (!string.IsNullOrWhiteSpace(templateKey)) query = query.Where(s => s.TemplateKey == templateKey);
        if (like is not null) query = query.Where(s => EF.Functions.ILike(s.Name, like, "\\") || EF.Functions.ILike(s.Slug, like, "\\"));

        var context = $"projectId={projectId}&environmentId={environmentId}&serverId={serverId}&status={status}&templateKey={templateKey}&q={q}";
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, context, ct);
        return TypedResults.Ok(new Page<ServiceSummary>(items.Select(ToSummary).ToList(), next));
    }

    private static async Task<Created<ServiceResponse>> Create(
        CreateServiceRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        CancellationToken ct)
    {
        var org = actor.Org();
        var references = new ReferenceCheck();
        var environment = references.Check(await db.FindEnvironmentAsync(org, request.EnvironmentId, ct), "/environmentId", "environment")!;
        var server = references.Check(await db.FindServerAsync(org, request.ServerId, ct), "/serverId", "server")!;
        references.ThrowIfAny();
        var template = ServiceTemplates.Find(request.TemplateKey)!;
        var name = request.Name!.Trim();
        var slug = WorkloadSupport.ResolveSlug(request.Slug, name);
        await WorkloadSupport.EnsureSlugFreeAsync(db, environment.Id, slug, "service", null, ct);

        var service = ServiceFactory.Build(vault, org, environment, server, template, name, slug, request.Description, request.Version, request.Image, request.Config);
        if (request.Runtime is { } runtime) RuntimeMapper.Apply(service, runtime, RuntimeMapper.PresentIn(runtime));

        db.Services.Add(service);
        await audit.RecordAsync("service.created", "service", service.Id,
            new { name, slug, environmentId = environment.Id, projectId = environment.ProjectId, serverId = server.Id, template = template.Key,
                  version = service.TemplateVersion, generatedSecrets = service.EnvironmentVariables.Count(v => v.Secret is not null) }, ct);

        http.SetETag(service.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("services", service.Id), ToResponse(service));
    }

    private static async Task<Ok<ServiceResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var service = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(service.RowVersion);
        return TypedResults.Ok(ToResponse(service));
    }

    private static async Task<Ok<ServiceResponse>> Update(
        Guid id, PatchRequest<UpdateServiceRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        CancellationToken ct)
    {
        var org = actor.Org();
        var service = await FindAsync(db, org, id, tracking: true, ct);
        http.CheckIfMatch(service.RowVersion);
        var body = patch.Body;

        var changed = new List<string>();
        if (patch.Has("name") && body.Name is { } name) { service.Name = name.Trim(); changed.Add("name"); }
        if (patch.Has("description")) { service.Description = body.Description; changed.Add("description"); }
        if (patch.Has("serverId"))
        {
            service.ServerId = (await db.RequireServerAsync(org, body.ServerId, "/serverId", ct)).Id;
            await WorkloadSupport.MoveDomainsAsync(db, service.Id, service.ServerId, ct);
            changed.Add("serverId");
        }

        if (patch.Has("image") && body.Image is { } image) { service.Image = image; changed.Add("image"); }
        if (patch.Has("templateVersion")) { service.TemplateVersion = body.TemplateVersion; changed.Add("templateVersion"); }
        if (patch.Get("config") is JsonObject configPatch)
        {
            var current = JsonNode.Parse(service.ConfigJson) ?? new JsonObject();
            var merged = JsonMergePatch.Apply(current, configPatch);
            service.ConfigJson = (merged ?? new JsonObject()).ToJsonString();
            changed.Add("config");
        }
        else if (patch.IsNull("config"))
        {
            service.ConfigJson = "{}";
            changed.Add("config");
        }

        if (patch.Has("runtime") && body.Runtime is { } runtime)
        {
            RuntimeMapper.Apply(service, runtime, path => patch.Has("runtime." + path));
            changed.Add("runtime");
        }

        await audit.RecordAsync("service.updated", "service", id, new { slug = service.Slug, changed }, ct);
        http.SetETag(service.RowVersion);
        return TypedResults.Ok(ToResponse(service));
    }

    private static async Task<NoContent> Delete(
        Guid id, string? confirm, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var service = await db.ServicesOf(actor.Org()).FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound("service", id));
        http.CheckIfMatch(service.RowVersion);
        Confirmation.Require(confirm, service.Slug);

        var now = clock.UtcNow;
        service.MarkDeleted(now);
        WorkloadSupport.SoftDeleteDomains(db, await db.Domains.Where(d => d.WorkloadId == id).ToListAsync(ct), now);
        await audit.RecordAsync("service.deleted", "service", id, new { slug = service.Slug, template = service.TemplateKey }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Service> FindAsync(AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.ServicesOf(org).Include(s => s.Environment).Include(s => s.Ports).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("service", id));
    }

    internal static ServiceResponse ToResponse(Service s) => new(
        s.Id, s.Name, s.Slug, s.Description, s.Environment.ProjectId, s.EnvironmentId, s.ServerId, s.TemplateKey, s.TemplateVersion, s.Image,
        JsonNode.Parse(s.ConfigJson), WorkloadSupport.StatusOf(s), RuntimeMapper.ToResponse(s), s.CreatedAt, s.UpdatedAt);

    private static ServiceSummary ToSummary(Service s) => new(
        s.Id, s.Name, s.Slug, s.Description, s.Environment.ProjectId, s.EnvironmentId, s.ServerId, s.TemplateKey, s.TemplateVersion, s.Image,
        s.DesiredState, s.Status, s.StatusReason, s.CurrentDeploymentId, s.CreatedAt, s.UpdatedAt);
}

/// <summary>RFC 7396 JSON Merge Patch.</summary>
internal static class JsonMergePatch
{
    public static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject) return patch?.DeepClone();
        var result = target as JsonObject ?? new JsonObject();
        foreach (var (key, value) in patchObject)
        {
            if (value is null) result.Remove(key);
            else result[key] = Apply(result[key], value);
        }

        return result;
    }
}

/// <summary>Creates a <see cref="Service"/> (with ports, volumes, health check and env vars) from a template.</summary>
internal static class ServiceFactory
{
    /// <summary>
    /// Builds the entity graph without saving. Credentials listed by the template are generated as <b>secrets</b> scoped to the new service
    /// (random strong passwords, encrypted with the master key) and linked as secret-backed environment variables; no value appears anywhere
    /// else.
    /// </summary>
    public static Service Build(
        SecretVault vault, Guid org, ProjectEnvironment environment, Server server, ServiceTemplate template, string name, string slug,
        string? description, string? version, string? imageOverride, JsonObject? config)
    {
        var chosen = ServiceTemplates.Version(template, version);
        var service = new Service
        {
            EnvironmentId = environment.Id, ServerId = server.Id, Name = name, Slug = slug, Description = description,
            TemplateKey = template.Key, TemplateVersion = imageOverride is null ? chosen?.Version : version,
            Image = imageOverride ?? chosen?.Image ?? template.DefaultImage,
            ConfigJson = (config ?? new JsonObject()).ToJsonString(),
        };
        service.Environment = environment;

        service.Runtime.HealthCheck.Type = ServiceTemplates.ToHealthCheckType(template.HealthCheck.Type);
        service.Runtime.HealthCheck.Path = template.HealthCheck.Path;
        service.Runtime.HealthCheck.Port = template.HealthCheck.Port;
        service.Runtime.HealthCheck.IntervalSeconds = template.HealthCheck.IntervalSeconds;
        service.Runtime.HealthCheck.TimeoutSeconds = template.HealthCheck.TimeoutSeconds;
        service.Runtime.HealthCheck.Retries = template.HealthCheck.Retries;
        service.Runtime.HealthCheck.StartPeriodSeconds = template.HealthCheck.StartPeriodSeconds;

        foreach (var port in template.Ports)
            service.Ports.Add(new WorkloadPort { WorkloadId = service.Id, ContainerPort = port.ContainerPort, Protocol = PortProtocol.Tcp, IsHttp = port.IsHttp });

        var shortId = service.Id.ToString("N")[^8..];
        foreach (var volume in template.Volumes)
            service.Volumes.Add(new Volume { WorkloadId = service.Id, Name = $"{slug}-{shortId}-{volume.Name}", MountPath = volume.MountPath });

        foreach (var env in template.Env)
        {
            if (env.Generate == ServiceTemplates.Password)
            {
                var secret = vault.Create(org, env.Key, $"Generated for service '{name}'.", SecretVault.GeneratePassword(env.Length), workloadId: service.Id,
                    purpose: SecretPurpose.ServiceGenerated);
                service.EnvironmentVariables.Add(new EnvironmentVariable { WorkloadId = service.Id, Key = env.Key, SecretId = secret.Id, Secret = secret });
            }
            else
            {
                service.EnvironmentVariables.Add(new EnvironmentVariable { WorkloadId = service.Id, Key = env.Key, Value = env.Value });
            }
        }

        return service;
    }
}
