using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Secrets;

public sealed record CreateSecretRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }

    /// <summary>The secret value. Write-only: it is never returned except by <c>POST /secrets/{id}/reveal</c>.</summary>
    public string? Value { get; init; }

    /// <summary>At most one of the three scope ids; none = organization scope.</summary>
    public Guid? ProjectId { get; init; }
    public Guid? EnvironmentId { get; init; }
    public Guid? WorkloadId { get; init; }
}

public sealed record UpdateSecretRequest
{
    [NotClearable] public string? Name { get; init; }
    public string? Description { get; init; }
}

public sealed record RotateSecretRequest
{
    public string? Value { get; init; }
}

/// <summary>Secret metadata. <c>value</c> is always the mask; the plaintext is never part of this shape.</summary>
public sealed record SecretResponse(
    Guid Id, string Name, string? Description, SecretScope Scope, Guid? ProjectId, Guid? EnvironmentId, Guid? WorkloadId, int CurrentVersion,
    string Value, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? RotatedAt);

public sealed record RevealSecretResponse(Guid Id, string Name, int Version, string Value);

public static class SecretResponses
{
    public const string Mask = "********";

    public static SecretResponse ToResponse(Secret s) => new(
        s.Id, s.Name, s.Description, s.Scope, s.ProjectId, s.EnvironmentId, s.WorkloadId, s.CurrentVersion, Mask, s.CreatedAt, s.UpdatedAt, s.RotatedAt);
}

public static class SecretRules
{
    public const int MaxValueLength = 64 * 1024;
}

public sealed class CreateSecretValidator : AbstractValidator<CreateSecretRequest>
{
    public CreateSecretValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Value).NotNull().MaximumLength(SecretRules.MaxValueLength);
        RuleFor(x => x).Must(x => new[] { x.ProjectId, x.EnvironmentId, x.WorkloadId }.Count(id => id is not null) <= 1)
            .OverridePropertyName("ProjectId").WithErrorCode("not_unique")
            .WithMessage("A secret has at most one scope: projectId, environmentId or workloadId.");
    }
}

public sealed class UpdateSecretValidator : AbstractValidator<UpdateSecretRequest>
{
    public UpdateSecretValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public sealed class RotateSecretValidator : AbstractValidator<RotateSecretRequest>
{
    public RotateSecretValidator()
    {
        RuleFor(x => x.Value).NotNull().MaximumLength(SecretRules.MaxValueLength);
    }
}

internal static class SecretEndpoints
{
    private static readonly SortDefinition<Secret> Sorts = new SortDefinition<Secret>("-createdAt")
        .Add("createdAt", s => s.CreatedAt).Add("updatedAt", s => s.UpdatedAt).Add("name", s => s.Name);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/secrets").WithTags("Secrets");

        group.MapGet("/", List).WithName("listSecrets").RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.SecretsRead);
        group.MapPost("/", Create).WithName("createSecret").Validate<CreateSecretRequest>()
            .RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.SecretsWrite);
        group.MapGet("/{id:guid}", Get).WithName("getSecret").RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.SecretsRead);
        group.MapPatch("/{id:guid}", Update).WithName("updateSecret")
            .Accepts<UpdateSecretRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateSecretRequest>().RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.SecretsWrite);
        group.MapDelete("/{id:guid}", Delete).WithName("deleteSecret").RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.SecretsWrite);
        group.MapPost("/{id:guid}/rotate", Rotate).WithName("rotateSecret").Validate<RotateSecretRequest>()
            .RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.SecretsWrite);

        // Revealing plaintext is the most sensitive operation of the API: Admin role, the secrets:write scope (which covers reveal, see
        // Scopes.SecretsWrite) and an audit event for every call.
        group.MapPost("/{id:guid}/reveal", Reveal).WithName("revealSecret").RequireRole(AetheraPolicies.Admin).RequireScope(Scopes.SecretsWrite);
    }

    private static async Task<Ok<Page<SecretResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort,
        Guid? projectId, Guid? environmentId, Guid? workloadId, string? scope, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("projectId", "environmentId", "workloadId", "scope", "q");
        var scopes = ResourceHttp.ParseEnumFilter<SecretScope>(scope, "scope");
        var like = ResourceHttp.LikePattern(q);

        var query = db.SecretsOf(actor.Org()).AsNoTracking();
        if (projectId is { } p) query = query.Where(s => s.ProjectId == p);
        if (environmentId is { } e) query = query.Where(s => s.EnvironmentId == e);
        if (workloadId is { } w) query = query.Where(s => s.WorkloadId == w);
        if (scopes is not null)
        {
            var wantOrg = scopes.Contains(SecretScope.Organization);
            var wantProject = scopes.Contains(SecretScope.Project);
            var wantEnvironment = scopes.Contains(SecretScope.Environment);
            var wantWorkload = scopes.Contains(SecretScope.Workload);
            query = query.Where(s =>
                (wantWorkload && s.WorkloadId != null)
                || (wantEnvironment && s.WorkloadId == null && s.EnvironmentId != null)
                || (wantProject && s.WorkloadId == null && s.EnvironmentId == null && s.ProjectId != null)
                || (wantOrg && s.WorkloadId == null && s.EnvironmentId == null && s.ProjectId == null));
        }

        if (like is not null) query = query.Where(s => EF.Functions.ILike(s.Name, like, "\\"));

        var context = $"projectId={projectId}&environmentId={environmentId}&workloadId={workloadId}&scope={scope}&q={q}";
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, context, ct);
        return TypedResults.Ok(new Page<SecretResponse>(items.Select(SecretResponses.ToResponse).ToList(), next));
    }

    private static async Task<Created<SecretResponse>> Create(
        CreateSecretRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        CancellationToken ct)
    {
        var org = actor.Org();
        if (request.ProjectId is { } project && !await db.ProjectsOf(org).AnyAsync(p => p.Id == project, ct))
            throw Lookups.BadReference("/projectId", "project");
        if (request.EnvironmentId is { } environment && !await db.EnvironmentsOf(org).AnyAsync(e => e.Id == environment, ct))
            throw Lookups.BadReference("/environmentId", "environment");
        if (request.WorkloadId is { } workload && !await db.WorkloadsOf(org).AnyAsync(w => w.Id == workload, ct))
            throw Lookups.BadReference("/workloadId", "application or service");

        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(db, org, name, request.ProjectId, request.EnvironmentId, request.WorkloadId, null, ct);

        var secret = vault.Create(org, name, request.Description, request.Value!, request.ProjectId, request.EnvironmentId, request.WorkloadId);
        await audit.RecordAsync("secret.created", "secret", secret.Id, new { name, scope = secret.Scope, version = secret.CurrentVersion }, ct);

        http.SetETag(secret.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("secrets", secret.Id), SecretResponses.ToResponse(secret));
    }

    private static async Task<Ok<SecretResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var secret = await db.GetSecretAsync(actor.Org(), id, tracking: false, ct);
        http.SetETag(secret.RowVersion);
        return TypedResults.Ok(SecretResponses.ToResponse(secret));
    }

    private static async Task<Ok<SecretResponse>> Update(
        Guid id, PatchRequest<UpdateSecretRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        CancellationToken ct)
    {
        var org = actor.Org();
        var secret = await db.GetSecretAsync(org, id, tracking: true, ct);
        http.CheckIfMatch(secret.RowVersion);

        var changed = new List<string>();
        if (patch.Has("name") && patch.Body.Name is { } name && name.Trim() != secret.Name)
        {
            name = name.Trim();
            await EnsureNameFreeAsync(db, org, name, secret.ProjectId, secret.EnvironmentId, secret.WorkloadId, id, ct);
            secret.Name = name;
            changed.Add("name");
        }

        if (patch.Has("description")) { secret.Description = patch.Body.Description; changed.Add("description"); }

        await audit.RecordAsync("secret.updated", "secret", id, new { changed }, ct);
        http.SetETag(secret.RowVersion);
        return TypedResults.Ok(SecretResponses.ToResponse(secret));
    }

    private static async Task<NoContent> Delete(
        Guid id, string? confirm, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var secret = await db.GetSecretAsync(actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(secret.RowVersion);
        Confirmation.Require(confirm, secret.Name);

        var references = await CountReferencesAsync(db, id, ct);
        if (references.Total > 0)
            throw new ApiProblemException(ApiProblems.Conflict(ResourceProblemCodes.SecretInUse,
                $"The secret is still referenced ({references.Describe()}). Remove those references first."));

        secret.MarkDeleted(clock.UtcNow);
        await audit.RecordAsync("secret.deleted", "secret", id, new { name = secret.Name }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<SecretResponse>> Rotate(
        Guid id, RotateSecretRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        CancellationToken ct)
    {
        var secret = await db.GetSecretAsync(actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(secret.RowVersion);
        vault.AddVersion(secret, request.Value!);
        await audit.RecordAsync("secret.rotated", "secret", id, new { name = secret.Name, version = secret.CurrentVersion }, ct);
        http.SetETag(secret.RowVersion);
        return TypedResults.Ok(SecretResponses.ToResponse(secret));
    }

    private static async Task<Ok<RevealSecretResponse>> Reveal(
        Guid id, int? version, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, CancellationToken ct)
    {
        http.RejectUnknownQuery("version");
        var secret = await db.GetSecretAsync(actor.Org(), id, tracking: false, ct);
        var (revealedVersion, value) = await vault.RevealAsync(secret, version, ct);

        // The audit event is written before the plaintext leaves the server; it records who and which version, never the value.
        await audit.RecordAsync("secret.revealed", "secret", id, new { name = secret.Name, version = revealedVersion }, ct);
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new RevealSecretResponse(secret.Id, secret.Name, revealedVersion, value));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------

    private static async Task EnsureNameFreeAsync(
        AetheraDbContext db, Guid org, string name, Guid? projectId, Guid? environmentId, Guid? workloadId, Guid? exceptId, CancellationToken ct)
    {
        if (await db.Secrets.AnyAsync(s => s.OrganizationId == org && s.Name == name && s.ProjectId == projectId
                && s.EnvironmentId == environmentId && s.WorkloadId == workloadId && s.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("secret", $"A secret named '{name}' already exists in this scope."));
    }

    private sealed record References(int EnvVars, int Registries, int Servers, int GitCredentials, int Webhooks)
    {
        public int Total => EnvVars + Registries + Servers + GitCredentials + Webhooks;

        public string Describe() => string.Join(", ", new[]
        {
            EnvVars > 0 ? $"{EnvVars} environment variable(s)" : null,
            Registries > 0 ? $"{Registries} registry(ies)" : null,
            Servers > 0 ? $"{Servers} server(s)" : null,
            GitCredentials > 0 ? $"{GitCredentials} git credential(s)" : null,
            Webhooks > 0 ? $"{Webhooks} webhook(s)" : null,
        }.Where(s => s is not null));
    }

    private static async Task<References> CountReferencesAsync(AetheraDbContext db, Guid secretId, CancellationToken ct) => new(
        await db.EnvironmentVariables.CountAsync(v => v.SecretId == secretId && v.Workload.DeletedAt == null, ct),
        await db.Registries.CountAsync(r => r.PasswordSecretId == secretId, ct),
        await db.Servers.CountAsync(s => s.SshCredentialSecretId == secretId, ct),
        await db.GitCredentials.CountAsync(c => c.SecretId == secretId, ct),
        await db.WebhookEndpoints.CountAsync(w => w.SecretId == secretId && w.Workload.DeletedAt == null, ct));
}
