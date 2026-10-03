using System.Text.Json.Nodes;
using Aethera.Api.Features.Resources.Trust;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Workloads;

internal static class ApplicationEndpoints
{
    private static readonly SortDefinition<Application> Sorts = new SortDefinition<Application>("-createdAt")
        .Add("createdAt", a => a.CreatedAt).Add("updatedAt", a => a.UpdatedAt).Add("name", a => a.Name).Add("slug", a => a.Slug);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/applications").WithTags("Applications");

        group.MapGet("/", List).WithName("listApplications").RequireRead();
        group.MapPost("/", Create).WithName("createApplication").Validate<CreateApplicationRequest>().RequireWrite();
        group.MapGet("/{id:guid}", Get).WithName("getApplication").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateApplication")
            .Accepts<UpdateApplicationRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateApplicationRequest>().RequireWrite();
        group.MapDelete("/{id:guid}", Delete).WithName("deleteApplication").RequireWrite();
    }

    // ---- handlers -------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<ApplicationSummary>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page,
        string? sort, Guid? projectId, Guid? environmentId, Guid? serverId, string? status, string? sourceKind, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("projectId", "environmentId", "serverId", "status", "sourceKind", "q");
        var statuses = ResourceHttp.ParseEnumFilter<WorkloadStatus>(status, "status");
        var kinds = ResourceHttp.ParseEnumFilter<ApplicationSourceKind>(sourceKind, "sourceKind");
        var like = ResourceHttp.LikePattern(q);

        var query = db.ApplicationsOf(actor.Org()).AsNoTracking().Include(a => a.Environment).Include(a => a.GitSource).Include(a => a.ImageSource).AsQueryable();
        if (projectId is { } p) query = query.Where(a => a.Environment.ProjectId == p);
        if (environmentId is { } e) query = query.Where(a => a.EnvironmentId == e);
        if (serverId is { } s) query = query.Where(a => a.ServerId == s);
        if (statuses is not null) query = query.Where(a => statuses.Contains(a.Status));
        if (kinds is not null) query = query.Where(a => kinds.Contains(a.SourceKind));
        if (like is not null) query = query.Where(a => EF.Functions.ILike(a.Name, like, "\\") || EF.Functions.ILike(a.Slug, like, "\\"));

        var context = $"projectId={projectId}&environmentId={environmentId}&serverId={serverId}&status={status}&sourceKind={sourceKind}&q={q}";
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, context, ct);
        return TypedResults.Ok(new Page<ApplicationSummary>(items.Select(ToSummary).ToList(), next));
    }

    private static async Task<Created<ApplicationResponse>> Create(
        CreateApplicationRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, TrustPolicy trust,
        CancellationToken ct)
    {
        var org = actor.Org();
        TrustChecks.CheckCompose(actor, trust, request.Compose?.InlineContent, "/compose/inlineContent");
        TrustChecks.CheckPorts(actor, trust, request.Runtime?.Ports);
        var references = new ReferenceCheck();
        var environment = references.Check(await db.FindEnvironmentAsync(org, request.EnvironmentId, ct), "/environmentId", "environment");
        var server = references.Check(await db.FindServerAsync(org, request.ServerId, ct), "/serverId", "server");
        references.ThrowIfAny();
        var name = request.Name!.Trim();
        var slug = WorkloadSupport.ResolveSlug(request.Slug, name);
        await WorkloadSupport.EnsureSlugFreeAsync(db, environment!.Id, slug, "application", null, ct);

        var kind = EnumText.Parse<ApplicationSourceKind>(request.SourceKind)!.Value;
        var app = new Application
        {
            EnvironmentId = environment!.Id, ServerId = server!.Id, Name = name, Slug = slug, Description = request.Description, SourceKind = kind,
        };
        app.Environment = environment;
        await ApplySourcesAsync(db, org, app, request.GitSource, request.Image, request.Compose, request.Build, _ => true, null, create: true, resetBuild: false, ct);
        RuntimeMapper.Apply(app, request.Runtime, _ => true);

        db.Applications.Add(app);
        await audit.RecordAsync("application.created", "application", app.Id,
            new { name, slug, environmentId = environment.Id, projectId = environment.ProjectId, serverId = server.Id, sourceKind = kind }, ct);

        http.SetETag(app.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("applications", app.Id), ToResponse(app));
    }

    private static async Task<Ok<ApplicationResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var app = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(app.RowVersion);
        return TypedResults.Ok(ToResponse(app));
    }

    private static async Task<Ok<ApplicationResponse>> Update(
        Guid id, PatchRequest<UpdateApplicationRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        TrustPolicy trust, CancellationToken ct)
    {
        var org = actor.Org();
        var app = await FindAsync(db, org, id, tracking: true, ct);
        http.CheckIfMatch(app.RowVersion);
        var body = patch.Body;

        // Root-equivalent settings need an Administrator (ADR 0006); values the request leaves as they are are not checked again.
        if (patch.Has("compose.inlineContent"))
            TrustChecks.CheckCompose(actor, trust, body.Compose?.InlineContent, "/compose/inlineContent", app.ComposeSource?.InlineContent);
        if (patch.Has("runtime.ports")) TrustChecks.CheckPorts(actor, trust, body.Runtime?.Ports, app.Ports);

        var changed = new List<string>();
        if (patch.Has("name") && body.Name is { } name) { app.Name = name.Trim(); changed.Add("name"); }
        if (patch.Has("description")) { app.Description = body.Description; changed.Add("description"); }
        if (patch.Has("serverId"))
        {
            var server = await db.RequireServerAsync(org, body.ServerId, "/serverId", ct);
            app.ServerId = server.Id;
            await WorkloadSupport.MoveDomainsAsync(db, app.Id, server.Id, ct);
            changed.Add("serverId");
        }

        var kind = app.SourceKind;
        void NotApplicable(string property) => throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer(
            "/" + property, "not_applicable", $"Does not apply to source kind '{kind.ToString().ToLowerInvariant()}'.")]));
        if (patch.Has("gitSource") && !ApplicationRules.UsesGit(kind)) NotApplicable("gitSource");
        if (patch.Has("image") && kind != ApplicationSourceKind.DockerImage) NotApplicable("image");
        if (patch.Has("compose") && kind != ApplicationSourceKind.Compose) NotApplicable("compose");
        if (patch.Has("build") && !ApplicationRules.UsesBuild(kind)) NotApplicable("build");

        var buildArgsPatch = patch.Get("build.buildArgs") as JsonObject;
        await ApplySourcesAsync(db, org, app,
            patch.Has("gitSource") ? body.GitSource : null, patch.Has("image") ? body.Image : null, patch.Has("compose") ? body.Compose : null,
            patch.Has("build") ? body.Build ?? new BuildConfigRequest() : null,
            path => patch.Has(path), buildArgsPatch, create: false, resetBuild: patch.IsNull("build"), ct);
        foreach (var section in new[] { "gitSource", "image", "compose", "build" })
        {
            if (patch.Has(section)) changed.Add(section);
        }

        if (patch.Has("runtime") && body.Runtime is { } runtime)
        {
            RuntimeMapper.Apply(app, runtime, path => patch.Has("runtime." + path));
            changed.Add("runtime");
        }

        await audit.RecordAsync("application.updated", "application", id, new { slug = app.Slug, changed }, ct);
        http.SetETag(app.RowVersion);
        return TypedResults.Ok(ToResponse(app));
    }

    private static async Task<NoContent> Delete(
        Guid id, string? confirm, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        var app = await db.ApplicationsOf(org).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound("application", id));
        http.CheckIfMatch(app.RowVersion);
        Confirmation.Require(confirm, app.Slug);

        var now = clock.UtcNow;
        app.MarkDeleted(now);
        WorkloadSupport.SoftDeleteDomains(db, await db.Domains.Where(d => d.WorkloadId == id).ToListAsync(ct), now);
        await audit.RecordAsync("application.deleted", "application", id, new { slug = app.Slug, environmentId = app.EnvironmentId }, ct);
        return TypedResults.NoContent();
    }

    // ---- loading / mapping ----------------------------------------------------------------------------------------------------------

    private static async Task<Application> FindAsync(AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.ApplicationsOf(org).Include(a => a.Environment).Include(a => a.GitSource).Include(a => a.BuildConfig)
            .Include(a => a.ImageSource).Include(a => a.ComposeSource).Include(a => a.Ports).AsSplitQuery().AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("application", id));
    }

    internal static ApplicationResponse ToResponse(Application a) => new(
        a.Id, a.Name, a.Slug, a.Description, a.Environment.ProjectId, a.EnvironmentId, a.ServerId, a.SourceKind, WorkloadSupport.StatusOf(a),
        a.GitSource is { } g ? new GitSourceResponse(g.Provider, g.RepositoryUrl, g.Branch, g.CommitPin, g.GitCredentialId, g.AutoDeploy) : null,
        a.ImageSource is { } i ? new ImageSourceResponse(i.RegistryId, i.Image, i.Tag, i.PullPolicy) : null,
        a.ComposeSource is { } c ? new ComposeSourceResponse(c.FilePath, c.InlineContent) : null,
        a.BuildConfig is { } b
            ? new BuildConfigResponse(b.Engine, b.Context, b.DockerfilePath, b.DockerfileInline, b.InstallCommand, b.BuildCommand, b.StartCommand,
                b.OutputDirectory, ToJson(b.BuildArgs), b.CacheEnabled, b.TargetPlatform)
            : null,
        RuntimeMapper.ToResponse(a), a.CreatedAt, a.UpdatedAt);

    // A JsonObject, not a dictionary: dictionary keys would be run through the camelCase policy, and build argument names are exact.
    private static JsonObject ToJson(IReadOnlyDictionary<string, string> values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal)) json[key] = value;
        return json;
    }

    private static ApplicationSummary ToSummary(Application a) => new(
        a.Id, a.Name, a.Slug, a.Description, a.Environment.ProjectId, a.EnvironmentId, a.ServerId, a.SourceKind, a.DesiredState, a.Status,
        a.StatusReason, a.CurrentDeploymentId, a.GitSource?.RepositoryUrl, a.ImageSource is { } i ? $"{i.Image}:{i.Tag}" : null,
        a.CreatedAt, a.UpdatedAt);

    // ---- applying source configuration (create and merge-patch) ---------------------------------------------------------------------

    /// <summary>
    /// Creates or merges the nested source configuration. <paramref name="has"/> tells which dotted property is part of the request (always
    /// true on create); a null section is left alone.
    /// </summary>
    private static async Task ApplySourcesAsync(
        AetheraDbContext db, Guid org, Application app, GitSourceRequest? git, ImageSourceRequest? image, ComposeSourceRequest? compose,
        BuildConfigRequest? build, Func<string, bool> has, JsonObject? buildArgsPatch, bool create, bool resetBuild, CancellationToken ct)
    {

        if (git is not null)
        {
            var existing = app.GitSource;
            if (existing is null)
            {
                if (string.IsNullOrWhiteSpace(git.RepositoryUrl))
                    throw Required("/gitSource/repositoryUrl");
                existing = app.GitSource = new GitSource { ApplicationId = app.Id, Application = app, RepositoryUrl = git.RepositoryUrl };
            }

            if (has("gitSource.repositoryUrl") && git.RepositoryUrl is { } url) existing.RepositoryUrl = url;
            if (has("gitSource.provider")) existing.Provider = EnumText.Parse<GitProvider>(git.Provider) ?? GitProvider.Generic;
            if (has("gitSource.branch")) existing.Branch = git.Branch ?? "main";
            if (has("gitSource.commitPin")) existing.CommitPin = git.CommitPin;
            if (has("gitSource.autoDeploy")) existing.AutoDeploy = git.AutoDeploy ?? false;
            if (has("gitSource.gitCredentialId"))
            {
                if (git.GitCredentialId is { } credentialId
                    && !await db.GitCredentials.AnyAsync(c => c.Id == credentialId && c.OrganizationId == org, ct))
                    throw Lookups.BadReference("/gitSource/gitCredentialId", "git credential");
                existing.GitCredentialId = git.GitCredentialId;
            }
        }

        if (image is not null)
        {
            var existing = app.ImageSource;
            if (existing is null)
            {
                if (string.IsNullOrWhiteSpace(image.Image)) throw Required("/image/image");
                existing = app.ImageSource = new ImageSource { ApplicationId = app.Id, Application = app, Image = image.Image };
            }

            if (has("image.image") && image.Image is { } reference) existing.Image = reference;
            if (has("image.tag")) existing.Tag = image.Tag ?? "latest";
            if (has("image.pullPolicy")) existing.PullPolicy = EnumText.Parse<ImagePullPolicy>(image.PullPolicy) ?? ImagePullPolicy.IfNotPresent;
            if (has("image.registryId"))
            {
                if (image.RegistryId is { } registryId) await db.RequireRegistryAsync(org, registryId, "/image/registryId", ct);
                existing.RegistryId = image.RegistryId;
            }
        }

        if (compose is not null)
        {
            var existing = app.ComposeSource ??= new ComposeSource { ApplicationId = app.Id, Application = app };
            if (has("compose.filePath")) existing.FilePath = string.IsNullOrWhiteSpace(compose.FilePath) ? null : compose.FilePath;
            if (has("compose.inlineContent")) existing.InlineContent = string.IsNullOrWhiteSpace(compose.InlineContent) ? null : compose.InlineContent;
            if (!(existing.FilePath is not null ^ existing.InlineContent is not null))
                throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/compose/filePath", "required",
                    "Provide exactly one of filePath (a file in the repository) or inlineContent.")]));
            if (existing.FilePath is not null && app.GitSource is null)
                throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/gitSource", "required",
                    "A compose file path needs the git source it lives in.")]));
        }

        if (ApplicationRules.UsesBuild(app.SourceKind) && (build is not null || app.BuildConfig is null))
        {
            var existing = app.BuildConfig ??= new BuildConfig { ApplicationId = app.Id, Application = app, Engine = ApplicationRules.EngineOf(app.SourceKind) ?? BuildEngines.Dockerfile };
            if (resetBuild)
            {
                var defaults = new BuildConfig { ApplicationId = app.Id, Engine = ApplicationRules.EngineOf(app.SourceKind) ?? BuildEngines.Dockerfile };
                build = new BuildConfigRequest { Engine = defaults.Engine };
                has = _ => true;
            }

            if (build is not null)
            {
                if (has("build.engine") || resetBuild)
                {
                    var engine = build.Engine ?? ApplicationRules.EngineOf(app.SourceKind) ?? BuildEngines.Dockerfile;
                    if (ApplicationRules.EngineOf(app.SourceKind) is { } implied && engine != implied)
                        throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/build/engine", ResourceProblemCodes.Mismatch,
                            $"Source kind '{app.SourceKind.ToString().ToLowerInvariant()}' builds with '{implied}'.")]));
                    existing.Engine = engine;
                }

                if (has("build.context") || resetBuild) existing.Context = build.Context ?? ".";
                if (has("build.dockerfilePath") || resetBuild) existing.DockerfilePath = build.DockerfilePath;
                if (has("build.dockerfileInline") || resetBuild) existing.DockerfileInline = build.DockerfileInline;
                if (has("build.installCommand") || resetBuild) existing.InstallCommand = build.InstallCommand;
                if (has("build.buildCommand") || resetBuild) existing.BuildCommand = build.BuildCommand;
                if (has("build.startCommand") || resetBuild) existing.StartCommand = build.StartCommand;
                if (has("build.outputDirectory") || resetBuild) existing.OutputDirectory = build.OutputDirectory;
                if (has("build.cacheEnabled") || resetBuild) existing.CacheEnabled = build.CacheEnabled ?? true;
                if (has("build.targetPlatform") || resetBuild) existing.TargetPlatform = build.TargetPlatform;
                if (resetBuild) existing.BuildArgs = [];
                else if (buildArgsPatch is not null)
                {
                    // Merge patch of an object: null removes a key, anything else sets it.
                    var merged = new Dictionary<string, string>(existing.BuildArgs);
                    foreach (var (key, value) in buildArgsPatch)
                    {
                        if (!EnvironmentVariable.IsValidKey(key)) continue; // rejected by the validator; defensive
                        if (value is null) merged.Remove(key);
                        else merged[key] = value.ToString();
                    }

                    existing.BuildArgs = merged;
                }
                else if (create && build.BuildArgs is not null) existing.BuildArgs = new Dictionary<string, string>(build.BuildArgs);
            }
        }
    }

    private static ApiProblemException Required(string pointer) =>
        new(ApiProblems.Validation([FieldError.AtPointer(pointer, "required", "Required when this configuration does not exist yet.")]));
}
