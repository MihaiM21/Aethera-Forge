using System.Text.RegularExpressions;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Registries;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Secrets;

public sealed record CreateRegistryRequest
{
    public string? Name { get; init; }

    /// <summary>Host (<c>ghcr.io</c>, <c>registry.example.com:5000</c>) or URL (<c>https://index.docker.io/v1/</c>).</summary>
    public string? Url { get; init; }

    public string? Username { get; init; }

    /// <summary>Password or access token. Write-only: stored as a secret, never returned.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Path under the registry host that images built for another server are pushed to (ADR 0012): <c>mihaim21</c> on <c>ghcr.io</c> gives
    /// <c>ghcr.io/mihaim21/&lt;app&gt;:&lt;deployment&gt;</c>. Stored in lower case. Absent = the username.
    /// </summary>
    public string? Namespace { get; init; }
}

public sealed record UpdateRegistryRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public string? Url { get; init; }
    public string? Username { get; init; }

    /// <summary>See <see cref="CreateRegistryRequest.Namespace"/>; null = the username.</summary>
    public string? Namespace { get; init; }

    /// <summary>A new password adds a version to the stored secret; <c>null</c> removes the stored credential.</summary>
    public string? Password { get; init; }
}

/// <summary>A registry. The credential is write-only: only <c>hasCredentials</c> tells whether one is stored.</summary>
public sealed record RegistryResponse(
    Guid Id, string Name, string Url, string? Username, bool HasCredentials, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Namespace = null);

/// <summary>
/// The verdict of <c>POST /registries/{id}/test</c>. <c>result</c> is <c>ok</c> (the registry answered and accepted the stored credentials, or
/// needs none), <c>unauthorized</c> (reachable, credentials missing or rejected) or <c>unreachable</c> (no usable answer). <c>message</c>
/// is for people and holds no secret; <c>httpStatus</c> is the last status the registry answered, when there was one.
/// </summary>
public sealed record RegistryTestResponse(string Result, string Message, int? HttpStatus)
{
    internal static string ResultName(RegistryTestOutcome outcome) => outcome switch
    {
        RegistryTestOutcome.Ok => "ok",
        RegistryTestOutcome.Unauthorized => "unauthorized",
        _ => "unreachable",
    };
}

public static partial class RegistryRules
{
    [GeneratedRegex(@"\A(https?://)?[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:\d{1,5})?(/[^\s\x00-\x1f\x7f]*)?\z")]
    private static partial Regex UrlPattern();

    public static bool IsValidUrl(string? url) => url is not null && UrlPattern().IsMatch(url);

    // Registry repository names (lower case letters, digits, '.', '_', '-', separated by '/'); typed in any case, stored lower case.
    [GeneratedRegex(@"\A[A-Za-z0-9]+([._-][A-Za-z0-9]+)*(/[A-Za-z0-9]+([._-][A-Za-z0-9]+)*)*\z")]
    private static partial Regex NamespacePattern();

    public static bool IsValidNamespace(string? value) => value is not null && value.Length <= 255 && NamespacePattern().IsMatch(value.Trim());

    /// <summary>The stored form of a namespace: trimmed, lower case; null when empty.</summary>
    public static string? NormalizeNamespace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('/').ToLowerInvariant();
}

public sealed class CreateRegistryValidator : AbstractValidator<CreateRegistryRequest>
{
    public CreateRegistryValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Url).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Url).Must(RegistryRules.IsValidUrl).WithErrorCode("pattern")
            .WithMessage("Must be a registry host such as ghcr.io or registry.example.com:5000, optionally with an http(s):// scheme.")
            .When(x => !string.IsNullOrEmpty(x.Url));
        RuleFor(x => x.Username).MaximumLength(200);
        RuleFor(x => x.Namespace).Must(RegistryRules.IsValidNamespace).WithErrorCode("pattern")
            .WithMessage("Must be a user or organization name such as mihaim21 (letters, digits, '.', '_', '-', and '/' between parts).")
            .When(x => !string.IsNullOrWhiteSpace(x.Namespace));
        RuleFor(x => x.Password).MaximumLength(SecretRules.MaxValueLength);
    }
}

public sealed class UpdateRegistryValidator : AbstractValidator<UpdateRegistryRequest>
{
    public UpdateRegistryValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Url).NotEmpty().MaximumLength(500).Must(RegistryRules.IsValidUrl).WithErrorCode("pattern")
            .WithMessage("Must be a registry host such as ghcr.io or registry.example.com:5000, optionally with an http(s):// scheme.")
            .When(x => x.Url is not null);
        RuleFor(x => x.Username).MaximumLength(200);
        RuleFor(x => x.Namespace).Must(RegistryRules.IsValidNamespace).WithErrorCode("pattern")
            .WithMessage("Must be a user or organization name such as mihaim21 (letters, digits, '.', '_', '-', and '/' between parts).")
            .When(x => !string.IsNullOrWhiteSpace(x.Namespace));
        RuleFor(x => x.Password).MaximumLength(SecretRules.MaxValueLength);
    }
}

internal static class RegistryEndpoints
{
    private static readonly SortDefinition<Registry> Sorts = new SortDefinition<Registry>("name")
        .Add("name", r => r.Name).Add("createdAt", r => r.CreatedAt).Add("updatedAt", r => r.UpdatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/registries").WithTags("Registries");

        group.MapGet("/", List).WithName("listRegistries").RequireRead();
        // The registry password is a secret: besides Administrator and write, a token needs secrets:write (the write scope excludes secrets).
        group.MapPost("/", Create).WithName("createRegistry").Validate<CreateRegistryRequest>().RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapGet("/{id:guid}", Get).WithName("getRegistry").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateRegistry")
            .Accepts<UpdateRegistryRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateRegistryRequest>().RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapDelete("/{id:guid}", Delete).WithName("deleteRegistry").RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapPost("/{id:guid}/test", Test).WithName("testRegistry").RequireAdmin()
            .WithSummary("Test the connection to a registry")
            .WithDescription(
                "The control plane calls the registry's Docker Registry HTTP API v2 (GET /v2/, following the Bearer or Basic challenge with the stored " +
                "credentials) and reports ok, unauthorized or unreachable. Loopback and link-local addresses are refused. The answer is 200 in all three cases.");
    }

    private static async Task<Ok<Page<RegistryResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, string? q,
        CancellationToken ct)
    {
        http.RejectUnknownQuery("q");
        var like = ResourceHttp.LikePattern(q);
        var query = db.RegistriesOf(actor.Org()).AsNoTracking();
        if (like is not null) query = query.Where(r => EF.Functions.ILike(r.Name, like, "\\") || EF.Functions.ILike(r.Url, like, "\\"));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"q={q}", ct);
        return TypedResults.Ok(new Page<RegistryResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Created<RegistryResponse>> Create(
        CreateRegistryRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        CancellationToken ct)
    {
        var org = actor.Org();
        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(db, org, name, null, ct);

        var registry = new Registry
        {
            OrganizationId = org, Name = name, Url = request.Url!.Trim(), Username = request.Username, Namespace = RegistryRules.NormalizeNamespace(request.Namespace),
        };
        if (!string.IsNullOrEmpty(request.Password))
        {
            var secret = CreateCredential(vault, org, registry, request.Password);
            registry.PasswordSecretId = secret.Id;
            registry.PasswordSecret = secret;
        }

        db.Registries.Add(registry);
        await audit.RecordAsync("registry.created", "registry", registry.Id, new { name, url = registry.Url, hasCredentials = registry.PasswordSecretId is not null }, ct);
        http.SetETag(registry.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("registries", registry.Id), ToResponse(registry));
    }

    private static async Task<Ok<RegistryResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var registry = await db.GetRegistryAsync(actor.Org(), id, tracking: false, ct);
        http.SetETag(registry.RowVersion);
        return TypedResults.Ok(ToResponse(registry));
    }

    private static async Task<Ok<RegistryResponse>> Update(
        Guid id, PatchRequest<UpdateRegistryRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        SecretVault vault, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        var registry = await db.GetRegistryAsync(org, id, tracking: true, ct);
        http.CheckIfMatch(registry.RowVersion);
        var body = patch.Body;

        var changed = new List<string>();
        if (patch.Has("name") && body.Name is { } name && name.Trim() != registry.Name)
        {
            await EnsureNameFreeAsync(db, org, name.Trim(), id, ct);
            registry.Name = name.Trim();
            changed.Add("name");
        }

        if (patch.Has("url") && body.Url is { } url) { registry.Url = url.Trim(); changed.Add("url"); }
        if (patch.Has("username")) { registry.Username = body.Username; changed.Add("username"); }
        if (patch.Has("namespace")) { registry.Namespace = RegistryRules.NormalizeNamespace(body.Namespace); changed.Add("namespace"); }

        if (patch.Has("password"))
        {
            if (!string.IsNullOrEmpty(body.Password))
            {
                if (registry.PasswordSecretId is { } secretId)
                    vault.AddVersion(await db.Secrets.FirstAsync(s => s.Id == secretId, ct), body.Password);
                else
                {
                    var secret = CreateCredential(vault, org, registry, body.Password);
                    registry.PasswordSecretId = secret.Id;
                    registry.PasswordSecret = secret;
                }
            }
            else if (registry.PasswordSecretId is { } existing)
            {
                // The credential is owned by the registry: remove it with the link.
                (await db.Secrets.FirstAsync(s => s.Id == existing, ct)).MarkDeleted(clock.UtcNow);
                registry.PasswordSecretId = null;
                registry.PasswordSecret = null;
            }

            changed.Add("credentials");
        }

        await audit.RecordAsync("registry.updated", "registry", id, new { changed }, ct);
        http.SetETag(registry.RowVersion);
        return TypedResults.Ok(ToResponse(registry));
    }

    private static async Task<NoContent> Delete(
        Guid id, string? confirm, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var registry = await db.GetRegistryAsync(actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(registry.RowVersion);
        Confirmation.Require(confirm, registry.Name);

        var users = await db.ImageSources.CountAsync(i => i.RegistryId == id && i.Application.DeletedAt == null, ct);
        if (users > 0)
            throw new ApiProblemException(ApiProblems.Conflict(ResourceProblemCodes.RegistryInUse, $"{users} application(s) pull images from this registry."));
        var buildServers = await db.Servers.CountAsync(s => s.BuildRegistryId == id, ct);
        if (buildServers > 0)
            throw new ApiProblemException(ApiProblems.Conflict(ResourceProblemCodes.RegistryInUse,
                $"{buildServers} server(s) push the images they build to this registry. Choose another build registry in their settings first."));

        var now = clock.UtcNow;
        registry.MarkDeleted(now);
        if (registry.PasswordSecretId is { } secretId) (await db.Secrets.FirstAsync(s => s.Id == secretId, ct)).MarkDeleted(now);
        await audit.RecordAsync("registry.deleted", "registry", id, new { name = registry.Name }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<RegistryTestResponse>> Test(
        Guid id, AetheraDbContext db, ICurrentActor actor, SecretVault vault, IRegistryConnectionTester tester, IAuditLog audit, CancellationToken ct)
    {
        var registry = await db.RegistriesOf(actor.Org()).AsNoTracking().Include(r => r.PasswordSecret).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound("registry", id));
        string? password = null;
        if (registry.PasswordSecret is { } secret) password = (await vault.RevealAsync(secret, null, ct)).Value;

        var result = await tester.TestAsync(registry.Url, registry.Username, password, ct);
        // The audit record has the verdict only: no message (it names the host), never the credential.
        await audit.RecordAsync("registry.tested", "registry", id, new { result = RegistryTestResponse.ResultName(result.Outcome) }, ct);
        return TypedResults.Ok(new RegistryTestResponse(RegistryTestResponse.ResultName(result.Outcome), result.Message, result.HttpStatus));
    }

    private static Secret CreateCredential(SecretVault vault, Guid org, Registry registry, string password)
    {
        var name = $"registry/{registry.Id.ToString("N")[^12..]}";
        return vault.Create(org, name, $"Credentials of registry '{registry.Name}'.", password, purpose: SecretPurpose.RegistryCredential);
    }

    private static async Task EnsureNameFreeAsync(AetheraDbContext db, Guid org, string name, Guid? exceptId, CancellationToken ct)
    {
        if (await db.Registries.AnyAsync(r => r.OrganizationId == org && r.Name == name && r.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("registry", $"A registry named '{name}' already exists."));
    }

    internal static RegistryResponse ToResponse(Registry r) =>
        new(r.Id, r.Name, r.Url, r.Username, r.PasswordSecretId is not null, r.CreatedAt, r.UpdatedAt, r.Namespace);
}
