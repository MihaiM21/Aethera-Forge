using System.Text.RegularExpressions;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Dns;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.DnsProviders;

public static class DnsProblemCodes
{
    public const string ProviderInUse = "dns_provider.in_use";
}

public sealed record CreateDnsProviderRequest
{
    public string? Name { get; init; }

    /// <summary>Only <c>cloudflare</c> exists; the default.</summary>
    public DnsProviderKind? Kind { get; init; }

    /// <summary>Cloudflare API token with <c>Zone:DNS:Edit</c>. Write-only: stored as a managed secret, never returned.</summary>
    public string? ApiToken { get; init; }
}

public sealed record UpdateDnsProviderRequest
{
    [NotClearable] public string? Name { get; init; }

    /// <summary>A new token adds a version to the stored secret and clears the verification (verify again to refresh the zones).</summary>
    [NotClearable] public string? ApiToken { get; init; }
}

/// <summary>A DNS provider. The token is write-only: <c>hasToken</c> only says that one is stored.</summary>
public sealed record DnsProviderResponse(
    Guid Id, string Name, DnsProviderKind Kind, bool HasToken, IReadOnlyList<string> Zones, DateTimeOffset? LastVerifiedAt, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Result of <c>POST /dns-providers/{id}/verify</c>.</summary>
public sealed record DnsProviderVerifyResponse(bool Valid, bool DnsAccess, IReadOnlyList<string> Zones, string? Message, DnsProviderResponse Provider);

public static partial class DnsProviderRules
{
    public const int MaxTokenLength = 500;

    // Cloudflare tokens are URL-safe characters; anything with whitespace or control characters was pasted wrongly (a header injection too).
    [GeneratedRegex(@"\A[^\s\x00-\x1f\x7f]+\z")]
    private static partial Regex TokenPattern();

    public static bool IsValidToken(string? token) => token is not null && TokenPattern().IsMatch(token.Trim());
}

public sealed class CreateDnsProviderValidator : AbstractValidator<CreateDnsProviderRequest>
{
    public CreateDnsProviderValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ApiToken).NotEmpty().MaximumLength(DnsProviderRules.MaxTokenLength);
        RuleFor(x => x.ApiToken).Must(DnsProviderRules.IsValidToken).WithErrorCode("pattern").WithMessage("Must not contain spaces or line breaks.")
            .When(x => !string.IsNullOrEmpty(x.ApiToken));
        RuleFor(x => x.Kind).IsInEnum().When(x => x.Kind is not null);
    }
}

public sealed class UpdateDnsProviderValidator : AbstractValidator<UpdateDnsProviderRequest>
{
    public UpdateDnsProviderValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.ApiToken).NotEmpty().MaximumLength(DnsProviderRules.MaxTokenLength).When(x => x.ApiToken is not null);
        RuleFor(x => x.ApiToken).Must(DnsProviderRules.IsValidToken).WithErrorCode("pattern").WithMessage("Must not contain spaces or line breaks.")
            .When(x => !string.IsNullOrEmpty(x.ApiToken));
    }
}

internal static class DnsProviderEndpoints
{
    private static readonly SortDefinition<DnsProvider> Sorts = new SortDefinition<DnsProvider>("name")
        .Add("name", p => p.Name).Add("createdAt", p => p.CreatedAt).Add("updatedAt", p => p.UpdatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/dns-providers").WithTags("DNS providers");

        // Listing is open to every role (the domain forms offer the providers); nothing in a response is secret.
        group.MapGet("/", List).WithName("listDnsProviders").RequireRead();
        // The API token is a secret: besides Administrator and write, an API token needs secrets:write (the write scope excludes secrets).
        group.MapPost("/", Create).WithName("createDnsProvider").Validate<CreateDnsProviderRequest>().RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapGet("/{id:guid}", Get).WithName("getDnsProvider").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateDnsProvider")
            .Accepts<UpdateDnsProviderRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateDnsProviderRequest>().RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapDelete("/{id:guid}", Delete).WithName("deleteDnsProvider").RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapPost("/{id:guid}/verify", Verify).WithName("verifyDnsProvider").RequireAdmin();
    }

    private static async Task<Ok<Page<DnsProviderResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, string? q,
        CancellationToken ct)
    {
        http.RejectUnknownQuery("q");
        var query = db.DnsProviders.Where(p => p.OrganizationId == actor.Org()).AsNoTracking();
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(p => EF.Functions.ILike(p.Name, like, "\\"));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"q={q}", ct);
        return TypedResults.Ok(new Page<DnsProviderResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Created<DnsProviderResponse>> Create(
        CreateDnsProviderRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        CancellationToken ct)
    {
        var org = actor.Org();
        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(db, org, name, null, ct);

        var provider = new DnsProvider { OrganizationId = org, Name = name, Kind = request.Kind ?? DnsProviderKind.Cloudflare };
        var secret = vault.Create(org, SecretName(provider), $"API token of the DNS provider '{name}'.", request.ApiToken!.Trim(), purpose: SecretPurpose.DnsCredential);
        provider.ApiTokenSecretId = secret.Id;

        db.DnsProviders.Add(provider);
        await audit.RecordAsync("dns_provider.created", "dns_provider", provider.Id, new { name, kind = provider.Kind }, ct);
        http.SetETag(provider.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("dns-providers", provider.Id), ToResponse(provider));
    }

    private static async Task<Ok<DnsProviderResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var provider = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(provider.RowVersion);
        return TypedResults.Ok(ToResponse(provider));
    }

    private static async Task<Ok<DnsProviderResponse>> Update(
        Guid id, PatchRequest<UpdateDnsProviderRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        SecretVault vault, CancellationToken ct)
    {
        var org = actor.Org();
        var provider = await FindAsync(db, org, id, tracking: true, ct);
        http.CheckIfMatch(provider.RowVersion);
        var body = patch.Body;

        var changed = new List<string>();
        if (patch.Has("name") && body.Name is { } name && name.Trim() != provider.Name)
        {
            await EnsureNameFreeAsync(db, org, name.Trim(), id, ct);
            provider.Name = name.Trim();
            changed.Add("name");
        }

        if (patch.Has("apiToken") && !string.IsNullOrEmpty(body.ApiToken))
        {
            vault.AddVersion(await db.Secrets.FirstAsync(s => s.Id == provider.ApiTokenSecretId, ct), body.ApiToken.Trim());
            // The old zones and verification belong to the old token.
            provider.Zones = [];
            provider.LastVerifiedAt = null;
            provider.LastError = null;
            changed.Add("apiToken");
        }

        await audit.RecordAsync("dns_provider.updated", "dns_provider", id, new { name = provider.Name, changed }, ct);
        http.SetETag(provider.RowVersion);
        return TypedResults.Ok(ToResponse(provider));
    }

    private static async Task<NoContent> Delete(
        Guid id, string? confirm, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var provider = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(provider.RowVersion);
        Confirmation.Require(confirm, provider.Name);

        var users = await db.Domains.CountAsync(d => d.DnsProviderId == id, ct);
        if (users > 0)
            throw new ApiProblemException(ApiProblems.Conflict(DnsProblemCodes.ProviderInUse, $"{users} domain(s) use this DNS provider. Remove it from them first."));

        var now = clock.UtcNow;
        provider.MarkDeleted(now);
        (await db.Secrets.FirstAsync(s => s.Id == provider.ApiTokenSecretId, ct)).MarkDeleted(now);
        await audit.RecordAsync("dns_provider.deleted", "dns_provider", id, new { name = provider.Name }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<DnsProviderVerifyResponse>> Verify(
        Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, ICloudflareClient cloudflare,
        IClock clock, CancellationToken ct)
    {
        var provider = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        var secret = await db.Secrets.AsNoTracking().FirstAsync(s => s.Id == provider.ApiTokenSecretId, ct);
        var token = (await vault.RevealAsync(secret, null, ct)).Value;

        var report = await CloudflareInspector.InspectAsync(cloudflare, token, ct);
        if (report.Valid)
        {
            provider.Zones = report.Zones.ToList();
            provider.LastVerifiedAt = clock.UtcNow;
        }

        provider.LastError = report.Message is { Length: > 1000 } text ? text[..1000] : report.Message;
        await audit.RecordAsync("dns_provider.verified", "dns_provider", id,
            new { name = provider.Name, valid = report.Valid, dnsAccess = report.DnsAccess, zones = report.Zones.Count }, ct);
        http.SetETag(provider.RowVersion);
        return TypedResults.Ok(new DnsProviderVerifyResponse(report.Valid, report.DnsAccess, report.Zones, report.Message, ToResponse(provider)));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------

    private static async Task<DnsProvider> FindAsync(AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.DnsProviders.Where(p => p.OrganizationId == org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("dns_provider", id));
    }

    private static async Task EnsureNameFreeAsync(AetheraDbContext db, Guid org, string name, Guid? exceptId, CancellationToken ct)
    {
        if (await db.DnsProviders.AnyAsync(p => p.OrganizationId == org && p.Name == name && p.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("dns_provider", $"A DNS provider named '{name}' already exists."));
    }

    private static string SecretName(DnsProvider provider) => $"dns/{provider.Id.ToString("N")[^12..]}";

    internal static DnsProviderResponse ToResponse(DnsProvider p) =>
        new(p.Id, p.Name, p.Kind, HasToken: true, p.Zones, p.LastVerifiedAt, p.LastError, p.CreatedAt, p.UpdatedAt);
}
