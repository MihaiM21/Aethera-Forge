using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Organizations;

public sealed record OrganizationResponse(Guid Id, string Name, string Slug, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record UpdateOrganizationRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public string? Slug { get; init; }
}

public sealed class UpdateOrganizationValidator : AbstractValidator<UpdateOrganizationRequest>
{
    public UpdateOrganizationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Slug).Must(Slug.IsValid).WithErrorCode("pattern")
            .WithMessage("Use lowercase letters, digits and single hyphens (1-63 characters).").When(x => x.Slug is not null);
    }
}

internal static class OrganizationEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/organizations").WithTags("Organizations");

        group.MapGet("/", List).WithName("listOrganizations").RequireRead();
        group.MapGet("/{id:guid}", Get).WithName("getOrganization").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateOrganization")
            .Accepts<UpdateOrganizationRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateOrganizationRequest>()
            .RequireAdmin(Scopes.Admin);
    }

    private static async Task<IResult> List(HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        var org = actor.Org();
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == org).ToListAsync(ct);
        return TypedResults.Ok(new Page<OrganizationResponse>(organization.Select(ToResponse).ToList(), null));
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var organization = await FindAsync(db, actor, id, tracking: false, ct);
        http.SetETag(organization.RowVersion);
        return TypedResults.Ok(ToResponse(organization));
    }

    private static async Task<IResult> Update(
        Guid id, PatchRequest<UpdateOrganizationRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        CancellationToken ct)
    {
        var organization = await FindAsync(db, actor, id, tracking: true, ct);
        http.CheckIfMatch(organization.RowVersion);

        var changed = new List<string>();
        if (patch.Has("name") && patch.Body.Name is { } name)
        {
            organization.Name = name.Trim();
            changed.Add("name");
        }

        if (patch.Has("slug") && patch.Body.Slug is { } slug && slug != organization.Slug)
        {
            if (await db.Organizations.AnyAsync(o => o.Slug == slug && o.Id != id, ct))
                throw new ApiProblemException(ApiProblems.AlreadyExists("organization", $"The slug '{slug}' is taken."));
            organization.Slug = slug;
            changed.Add("slug");
        }

        await audit.RecordAsync("organization.updated", "organization", id, new { changed }, ct);
        http.SetETag(organization.RowVersion);
        return TypedResults.Ok(ToResponse(organization));
    }

    private static async Task<Organization> FindAsync(AetheraDbContext db, ICurrentActor actor, Guid id, bool tracking, CancellationToken ct)
    {
        var org = actor.Org();
        var query = tracking ? db.Organizations.AsQueryable() : db.Organizations.AsNoTracking();
        return id == org ? await query.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw NotFound(id) : throw NotFound(id);
    }

    private static ApiProblemException NotFound(Guid id) => new(ApiProblems.NotFound("organization", id));

    internal static OrganizationResponse ToResponse(Organization o) => new(o.Id, o.Name, o.Slug, o.CreatedAt, o.UpdatedAt);
}
