using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Workloads;

public sealed record CreateEnvVarRequest
{
    public string? Key { get; init; }

    /// <summary>A plain value. Exactly one of <c>value</c> and <c>secretId</c>.</summary>
    public string? Value { get; init; }

    /// <summary>A reference to a secret (<c>POST /secrets</c>); the value is then resolved at deploy time and never returned.</summary>
    public Guid? SecretId { get; init; }

    public bool? IsBuildTime { get; init; }
    public bool? IsRuntime { get; init; }
}

public sealed record UpdateEnvVarRequest
{
    [NotClearable] public string? Key { get; init; }
    public string? Value { get; init; }
    public Guid? SecretId { get; init; }
    [NotClearable] public bool? IsBuildTime { get; init; }
    [NotClearable] public bool? IsRuntime { get; init; }
}

public sealed record ImportEnvVarsRequest
{
    /// <summary>The text of a <c>.env</c> file.</summary>
    public string? Content { get; init; }

    /// <summary>Replace the value of variables that already exist. Secret-backed variables are never replaced by an import.</summary>
    public bool? Overwrite { get; init; }

    public bool? IsBuildTime { get; init; }
    public bool? IsRuntime { get; init; }
}

public sealed record EnvVarResponse(
    Guid Id, Guid WorkloadId, string Key, string Value, bool IsSecret, Guid? SecretId, string? SecretName, int? SecretVersion, bool IsBuildTime,
    bool IsRuntime, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ImportEnvVarsResponse(IReadOnlyList<string> Created, IReadOnlyList<string> Updated, IReadOnlyList<string> Skipped);

public sealed class CreateEnvVarValidator : AbstractValidator<CreateEnvVarRequest>
{
    public CreateEnvVarValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(255).Must(k => k is null || EnvironmentVariable.IsValidKey(k)).WithErrorCode("pattern")
            .WithMessage("Must start with a letter or underscore and contain only letters, digits and underscores.");
        RuleFor(x => x.Value).MaximumLength(EnvVarRules.MaxValueLength);
        RuleFor(x => x).Custom((x, context) =>
        {
            if ((x.Value is null) == (x.SecretId is null))
                context.AddFailure(new FluentValidation.Results.ValidationFailure(x.Value is null ? "Value" : "SecretId",
                    "Provide exactly one of value (a plain value) and secretId (a reference to a secret).") { ErrorCode = x.Value is null ? "required" : "not_unique" });
            if (x.IsBuildTime == false && x.IsRuntime == false)
                context.AddFailure(new FluentValidation.Results.ValidationFailure("IsRuntime", "A variable must be available at build time, runtime or both.")
                    { ErrorCode = "invalid" });
        });
    }
}

public sealed class UpdateEnvVarValidator : AbstractValidator<UpdateEnvVarRequest>
{
    public UpdateEnvVarValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(255).Must(k => k is null || EnvironmentVariable.IsValidKey(k)).WithErrorCode("pattern")
            .WithMessage("Must start with a letter or underscore and contain only letters, digits and underscores.").When(x => x.Key is not null);
        RuleFor(x => x.Value).MaximumLength(EnvVarRules.MaxValueLength);
    }
}

public sealed class ImportEnvVarsValidator : AbstractValidator<ImportEnvVarsRequest>
{
    public ImportEnvVarsValidator()
    {
        RuleFor(x => x.Content).NotNull().MaximumLength(10 * 1024 * 1024);
    }
}

internal static class EnvVarRules
{
    public const int MaxValueLength = 64 * 1024;
}

/// <summary>The env var sub-resource of applications and services; one instance of the routes per workload kind.</summary>
internal static class EnvVarEndpoints
{
    private static readonly SortDefinition<EnvironmentVariable> Sorts = new SortDefinition<EnvironmentVariable>("key")
        .Add("key", v => v.Key).Add("createdAt", v => v.CreatedAt).Add("updatedAt", v => v.UpdatedAt);

    public static void Map(IEndpointRouteBuilder api, string collection, string singular)
    {
        var pascal = char.ToUpperInvariant(singular[0]) + singular[1..];
        var group = api.MapGroup($"/{collection}/{{workloadId:guid}}/env-vars").WithTags("Environment variables");
        var isApplication = singular == "application";

        group.MapGet("/", (Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors,
                [AsParameters] PageRequest page, string? sort, string? q, CancellationToken ct) =>
                List(isApplication, workloadId, http, db, actor, cursors, page, sort, q, ct))
            .WithName($"list{pascal}EnvVars").RequireRead();

        group.MapPost("/", (Guid workloadId, CreateEnvVarRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
                CancellationToken ct) => Create(isApplication, workloadId, request, http, db, actor, audit, ct))
            .WithName($"create{pascal}EnvVar").Validate<CreateEnvVarRequest>().RequireWrite();

        group.MapGet("/{envVarId:guid}", (Guid workloadId, Guid envVarId, HttpContext http, AetheraDbContext db, ICurrentActor actor,
                CancellationToken ct) => Get(isApplication, workloadId, envVarId, http, db, actor, ct))
            .WithName($"get{pascal}EnvVar").RequireRead();

        group.MapPatch("/{envVarId:guid}", (Guid workloadId, Guid envVarId, PatchRequest<UpdateEnvVarRequest> patch, HttpContext http,
                AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct) =>
                Update(isApplication, workloadId, envVarId, patch, http, db, actor, audit, ct))
            .WithName($"update{pascal}EnvVar").Accepts<UpdateEnvVarRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateEnvVarRequest>().RequireWrite();

        group.MapDelete("/{envVarId:guid}", (Guid workloadId, Guid envVarId, HttpContext http, AetheraDbContext db, ICurrentActor actor,
                IAuditLog audit, CancellationToken ct) => Delete(isApplication, workloadId, envVarId, http, db, actor, audit, ct))
            .WithName($"delete{pascal}EnvVar").RequireWrite();

        group.MapPost("/import", (Guid workloadId, ImportEnvVarsRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor,
                IAuditLog audit, CancellationToken ct) => Import(isApplication, workloadId, request, http, db, actor, audit, ct))
            .WithName($"import{pascal}EnvVars").Validate<ImportEnvVarsRequest>()
            .WithMetadata(new RequestSizeLimitAttribute(10 * 1024 * 1024)).RequireWrite();

        group.MapGet("/export", (Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct) =>
                Export(isApplication, workloadId, http, db, actor, ct))
            .WithName($"export{pascal}EnvVars").Produces<string>(200, "text/plain").RequireRead();
    }

    // ---- handlers -------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<EnvVarResponse>>> List(
        bool isApplication, Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, PageRequest page,
        string? sort, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("q");
        var workload = await FindWorkloadAsync(db, actor.Org(), isApplication, workloadId, ct);
        var like = ResourceHttp.LikePattern(q);
        var query = db.EnvironmentVariables.AsNoTracking().Include(v => v.Secret).Where(v => v.WorkloadId == workload.Id);
        if (like is not null) query = query.Where(v => EF.Functions.ILike(v.Key, like, "\\"));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workload={workload.Id}&q={q}", ct);
        return TypedResults.Ok(new Page<EnvVarResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Created<EnvVarResponse>> Create(
        bool isApplication, Guid workloadId, CreateEnvVarRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor,
        IAuditLog audit, CancellationToken ct)
    {
        var org = actor.Org();
        var workload = await FindWorkloadAsync(db, org, isApplication, workloadId, ct);
        var key = request.Key!.Trim();
        await EnsureKeyFreeAsync(db, workload.Id, key, null, ct);

        Secret? secret = null;
        if (request.SecretId is { } secretId) secret = await db.RequireSecretForWorkloadAsync(org, secretId, workload, "/secretId", ct);
        var variable = new EnvironmentVariable
        {
            WorkloadId = workload.Id, Key = key, Value = request.Value, SecretId = secret?.Id, Secret = secret,
            IsBuildTime = request.IsBuildTime ?? false, IsRuntime = request.IsRuntime ?? true,
        };
        db.EnvironmentVariables.Add(variable);
        await audit.RecordAsync("env_var.created", "env_var", variable.Id,
            new { workloadId = workload.Id, variable = key, backing = secret is null ? "plain" : "secret" }, ct);

        http.SetETag(variable.RowVersion);
        return TypedResults.Created($"{ResourceHttp.Path(isApplication ? "applications" : "services", workload.Id)}/env-vars/{variable.Id}", ToResponse(variable));
    }

    private static async Task<Ok<EnvVarResponse>> Get(
        bool isApplication, Guid workloadId, Guid envVarId, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var variable = await FindAsync(db, actor.Org(), isApplication, workloadId, envVarId, tracking: false, ct);
        http.SetETag(variable.RowVersion);
        return TypedResults.Ok(ToResponse(variable));
    }

    private static async Task<Ok<EnvVarResponse>> Update(
        bool isApplication, Guid workloadId, Guid envVarId, PatchRequest<UpdateEnvVarRequest> patch, HttpContext http, AetheraDbContext db,
        ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var org = actor.Org();
        var variable = await FindAsync(db, org, isApplication, workloadId, envVarId, tracking: true, ct);
        http.CheckIfMatch(variable.RowVersion);
        var body = patch.Body;

        if (patch.Has("key") && body.Key is { } key && key != variable.Key)
        {
            await EnsureKeyFreeAsync(db, variable.WorkloadId, key, variable.Id, ct);
            variable.Key = key.Trim();
        }

        var value = variable.Value;
        var secretId = variable.SecretId;
        if (patch.Has("value") && patch.Has("secretId") && body.Value is not null && body.SecretId is not null)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/secretId", "not_unique",
                "Provide either value or secretId, not both.")]));
        if (patch.Has("value")) { value = body.Value; if (value is not null) secretId = null; }
        if (patch.Has("secretId")) { secretId = body.SecretId; if (secretId is not null) value = null; }
        if (value is null && secretId is null)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/value", "required",
                "A variable needs a value or a secretId.")]));

        if (secretId != variable.SecretId)
        {
            var workload = await FindWorkloadAsync(db, org, isApplication, workloadId, ct);
            variable.Secret = secretId is { } id ? await db.RequireSecretForWorkloadAsync(org, id, workload, "/secretId", ct) : null;
            variable.SecretId = secretId;
        }

        variable.Value = value;
        if (patch.Has("isBuildTime") && body.IsBuildTime is { } build) variable.IsBuildTime = build;
        if (patch.Has("isRuntime") && body.IsRuntime is { } runtime) variable.IsRuntime = runtime;
        if (!variable.IsBuildTime && !variable.IsRuntime)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/isRuntime", "invalid",
                "A variable must be available at build time, runtime or both.")]));

        await audit.RecordAsync("env_var.updated", "env_var", variable.Id,
            new { workloadId = variable.WorkloadId, variable = variable.Key, backing = variable.SecretId is null ? "plain" : "secret" }, ct);
        http.SetETag(variable.RowVersion);
        return TypedResults.Ok(ToResponse(variable));
    }

    private static async Task<NoContent> Delete(
        bool isApplication, Guid workloadId, Guid envVarId, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        CancellationToken ct)
    {
        var variable = await FindAsync(db, actor.Org(), isApplication, workloadId, envVarId, tracking: true, ct);
        http.CheckIfMatch(variable.RowVersion);
        db.EnvironmentVariables.Remove(variable);
        await audit.RecordAsync("env_var.deleted", "env_var", variable.Id, new { workloadId = variable.WorkloadId, variable = variable.Key }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ImportEnvVarsResponse>> Import(
        bool isApplication, Guid workloadId, ImportEnvVarsRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor,
        IAuditLog audit, CancellationToken ct)
    {
        var workload = await FindWorkloadAsync(db, actor.Org(), isApplication, workloadId, ct);
        var (values, errors) = DotEnv.Parse(request.Content!);
        if (errors.Count > 0)
            throw new ApiProblemException(ApiProblems.Validation(errors.Take(50).Select(e =>
                FieldError.AtPointer("/content", "dotenv.invalid_line", $"Line {e.Line}: {e.Message}"))));
        if (values.Any(v => v.Value.Length > EnvVarRules.MaxValueLength))
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/content", "too_long",
                $"A value is longer than {EnvVarRules.MaxValueLength} characters.")]));

        var overwrite = request.Overwrite ?? false;
        var existing = await db.EnvironmentVariables.Where(v => v.WorkloadId == workload.Id).ToDictionaryAsync(v => v.Key, ct);
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        foreach (var (key, value) in values)
        {
            if (!existing.TryGetValue(key, out var variable))
            {
                db.EnvironmentVariables.Add(new EnvironmentVariable
                {
                    WorkloadId = workload.Id, Key = key, Value = value,
                    IsBuildTime = request.IsBuildTime ?? false, IsRuntime = request.IsRuntime ?? true,
                });
                created.Add(key);
            }
            else if (!overwrite || variable.SecretId is not null)
            {
                skipped.Add(key);
            }
            else
            {
                variable.Value = value;
                if (request.IsBuildTime is { } build) variable.IsBuildTime = build;
                if (request.IsRuntime is { } runtime) variable.IsRuntime = runtime;
                updated.Add(key);
            }
        }

        await audit.RecordAsync("env_var.imported", isApplication ? "application" : "service", workload.Id,
            new { created = created.Count, updated = updated.Count, skipped = skipped.Count, overwrite }, ct);
        return TypedResults.Ok(new ImportEnvVarsResponse(created, updated, skipped));
    }

    private static async Task<IResult> Export(
        bool isApplication, Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        var workload = await FindWorkloadAsync(db, actor.Org(), isApplication, workloadId, ct);
        var plain = await db.EnvironmentVariables.AsNoTracking()
            .Where(v => v.WorkloadId == workload.Id && v.SecretId == null && v.Value != null)
            .OrderBy(v => v.Key).Select(v => new { v.Key, v.Value }).ToListAsync(ct);
        var text = DotEnv.Format(plain.Select(v => new KeyValuePair<string, string>(v.Key, v.Value!)));
        return TypedResults.Text(text, "text/plain; charset=utf-8");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------

    private static async Task<Workload> FindWorkloadAsync(AetheraDbContext db, Guid org, bool isApplication, Guid id, CancellationToken ct)
    {
        IQueryable<Workload> query = isApplication ? db.ApplicationsOf(org) : db.ServicesOf(org);
        return await query.Include(w => w.Environment).FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound(isApplication ? "application" : "service", id));
    }

    private static async Task<EnvironmentVariable> FindAsync(
        AetheraDbContext db, Guid org, bool isApplication, Guid workloadId, Guid envVarId, bool tracking, CancellationToken ct)
    {
        var workload = await FindWorkloadAsync(db, org, isApplication, workloadId, ct);
        var query = db.EnvironmentVariables.Include(v => v.Secret).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(v => v.Id == envVarId && v.WorkloadId == workload.Id, ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound("env_var", envVarId));
    }

    private static async Task EnsureKeyFreeAsync(AetheraDbContext db, Guid workloadId, string key, Guid? exceptId, CancellationToken ct)
    {
        if (await db.EnvironmentVariables.AnyAsync(v => v.WorkloadId == workloadId && v.Key == key && v.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("env_var", $"The variable '{key}' already exists."));
    }

    internal static EnvVarResponse ToResponse(EnvironmentVariable v) => new(
        v.Id, v.WorkloadId, v.Key, v.SecretId is null ? v.Value ?? "" : SecretResponses.Mask, v.SecretId is not null, v.SecretId,
        v.Secret?.Name, v.Secret?.CurrentVersion, v.IsBuildTime, v.IsRuntime, v.CreatedAt, v.UpdatedAt);
}
