using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Deployments;

public sealed record CreateGitCredentialRequest
{
    public string? Name { get; init; }

    /// <summary><c>token</c> (HTTPS access token), <c>deployKey</c> (SSH private key) or <c>basicAuth</c> (HTTPS username and password).</summary>
    public string? Kind { get; init; }

    /// <summary><c>generic</c>, <c>gitHub</c> or <c>gitLab</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>HTTPS user name. Tokens default to a provider-neutral placeholder.</summary>
    public string? Username { get; init; }

    /// <summary>The token, password or PEM/OpenSSH private key. Write-only: stored as a secret, never returned.</summary>
    public string? Value { get; init; }

    /// <summary>For deploy keys: the public half, so it can be shown to the person registering the key with the Git host.</summary>
    public string? PublicKey { get; init; }
}

public sealed record GitCredentialResponse(
    Guid Id, string Name, GitCredentialKind Kind, GitProvider Provider, string? Username, string? PublicKey, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class CreateGitCredentialValidator : AbstractValidator<CreateGitCredentialRequest>
{
    public CreateGitCredentialValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Kind).NotEmpty().Must(k => Enum.TryParse<GitCredentialKind>(k, ignoreCase: true, out _)).WithErrorCode("enum")
            .WithMessage("Must be token, deployKey or basicAuth.");
        RuleFor(x => x.Provider).Must(p => Enum.TryParse<GitProvider>(p, ignoreCase: true, out _)).WithErrorCode("enum")
            .WithMessage("Must be generic, gitHub or gitLab.").When(x => !string.IsNullOrEmpty(x.Provider));
        RuleFor(x => x.Value).NotEmpty().MaximumLength(SecretRules.MaxValueLength);
        RuleFor(x => x.Username).MaximumLength(200);
        RuleFor(x => x.PublicKey).MaximumLength(8192);
    }
}

internal static class GitCredentialEndpoints
{
    private static readonly SortDefinition<GitCredential> Sorts = new SortDefinition<GitCredential>("name")
        .Add("name", c => c.Name).Add("createdAt", c => c.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/git-credentials").WithTags("Git credentials");
        group.MapGet("/", List).WithName("listGitCredentials").RequireRead();
        // Like registries: the material is a secret, so besides Administrator and write a token needs secrets:write.
        group.MapPost("/", Create).WithName("createGitCredential").Validate<CreateGitCredentialRequest>().RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapGet("/{id:guid}", Get).WithName("getGitCredential").RequireRead();
        group.MapDelete("/{id:guid}", Delete).WithName("deleteGitCredential").RequireAdmin().RequireScope(Scopes.SecretsWrite);
    }

    private static IQueryable<GitCredential> Of(AetheraDbContext db, Guid org) => db.GitCredentials.Where(c => c.OrganizationId == org);

    private static async Task<Ok<Page<GitCredentialResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        var (items, next) = await Sorts.PageAsync(Of(db, actor.Org()).AsNoTracking(), sort, page, cursors, "", ct);
        return TypedResults.Ok(new Page<GitCredentialResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Ok<GitCredentialResponse>> Get(Guid id, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var credential = await Of(db, actor.Org()).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
                         ?? throw new ApiProblemException(ApiProblems.NotFound("git_credential", id));
        return TypedResults.Ok(ToResponse(credential));
    }

    private static async Task<Created<GitCredentialResponse>> Create(
        CreateGitCredentialRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, CancellationToken ct)
    {
        var org = actor.Org();
        var name = request.Name!.Trim();
        if (await Of(db, org).AnyAsync(c => c.Name == name, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("git_credential", $"A Git credential named '{name}' already exists."));

        var kind = Enum.Parse<GitCredentialKind>(request.Kind!, ignoreCase: true);
        var provider = string.IsNullOrEmpty(request.Provider) ? GitProvider.Generic : Enum.Parse<GitProvider>(request.Provider, ignoreCase: true);
        var credential = new GitCredential { OrganizationId = org, Name = name, Kind = kind, Provider = provider, Username = request.Username, PublicKey = request.PublicKey };
        var secret = vault.Create(org, $"git/{credential.Id.ToString("N")[^12..]}", $"Material of Git credential '{name}'.", request.Value!, purpose: SecretPurpose.GitCredential);
        credential.SecretId = secret.Id;
        credential.Secret = secret;
        db.GitCredentials.Add(credential);
        await audit.RecordAsync("git_credential.created", "git_credential", credential.Id, new { name, kind, provider }, ct);
        return TypedResults.Created(ResourceHttp.Path("git-credentials", credential.Id), ToResponse(credential));
    }

    private static async Task<NoContent> Delete(Guid id, string? confirm, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var credential = await Of(db, actor.Org()).Include(c => c.Secret).FirstOrDefaultAsync(c => c.Id == id, ct)
                         ?? throw new ApiProblemException(ApiProblems.NotFound("git_credential", id));
        Confirmation.Require(confirm, credential.Name);
        var users = await db.GitSources.CountAsync(g => g.GitCredentialId == id && g.Application.DeletedAt == null, ct);
        if (users > 0) throw new ApiProblemException(ApiProblems.Conflict("git_credential.in_use", $"{users} application(s) use this credential."));
        var now = clock.UtcNow;
        credential.MarkDeleted(now);
        credential.Secret.MarkDeleted(now);
        await audit.RecordAsync("git_credential.deleted", "git_credential", id, new { name = credential.Name }, ct);
        return TypedResults.NoContent();
    }

    private static GitCredentialResponse ToResponse(GitCredential c) => new(c.Id, c.Name, c.Kind, c.Provider, c.Username, c.PublicKey, c.CreatedAt, c.UpdatedAt);
}
