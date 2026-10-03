using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Domain;
using Aethera.Infrastructure.Auth;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aethera.Api.Features.Auth;

/// <summary>Query parameters of <c>GET /api-tokens</c> after parsing.</summary>
public sealed record ApiTokenListQuery(PageRequest Page, Guid? UserId, string? Status);

/// <summary>
/// API token management. Everyone manages their own tokens; Admin and Owner can also list, read and revoke every token of the
/// organization. A token that belongs to someone else is a <c>404</c> to a caller who may not manage it.
/// </summary>
public sealed class ApiTokenService(
    AetheraDbContext db,
    IClock clock,
    IAuditLog audit,
    ICurrentActor actor,
    KeysetCursor cursors,
    IOptionsMonitor<AuthOptions> options)
{
    private const int PrefixCollisionRetries = 3;
    private static readonly string[] Statuses = ["active", "revoked", "expired"];

    private Guid UserId => actor.UserId ?? throw new ApiProblemException(ApiProblems.Unauthenticated());

    private Guid OrganizationId => actor.OrganizationId ?? throw new ApiProblemException(ApiProblems.Unauthenticated());

    private OrganizationRole Role => actor.Role ?? throw new ApiProblemException(ApiProblems.Unauthenticated());

    private bool IsAdmin => Role >= OrganizationRole.Admin;

    // ---- create ------------------------------------------------------------------------------------------------------------------

    public async Task<CreatedApiTokenResponse> CreateAsync(CreateApiTokenRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var now = clock.UtcNow;
        var scopes = request.Scopes.Distinct(StringComparer.Ordinal).ToList();

        var errors = new List<FieldError>();
        for (var i = 0; i < request.Scopes.Count; i++)
        {
            if (!ScopeCeiling.IsAllowed(Role, request.Scopes[i]))
                errors.Add(FieldError.AtPointer($"/scopes/{i}", "scope_not_allowed",
                    $"A {Role.ToString().ToLowerInvariant()} cannot create a token with the '{request.Scopes[i]}' scope. " +
                    $"Allowed: {string.Join(", ", ScopeCeiling.AllowedFor(Role))}."));
        }

        DateTimeOffset? expiresAt;
        if (!request.ExpiresAtSpecified)
        {
            expiresAt = now.AddDays(settings.ApiTokenDefaultLifetimeDays);
        }
        else if (request.ExpiresAt is not { } requested)
        {
            expiresAt = null;
            if (!IsAdmin)
                errors.Add(FieldError.AtPointer("/expiresAt", "never_not_allowed", "Only an admin or owner can create a token that never expires."));
        }
        else
        {
            expiresAt = requested.ToUniversalTime();
            if (expiresAt <= now)
                errors.Add(FieldError.AtPointer("/expiresAt", "in_past", "Must be in the future."));
            else if (expiresAt > now.AddDays(settings.ApiTokenMaxLifetimeDays))
                errors.Add(FieldError.AtPointer("/expiresAt", "too_far",
                    $"A token can be valid for at most {settings.ApiTokenMaxLifetimeDays} days."));
        }

        if (errors.Count > 0) throw new ApiProblemException(ApiProblems.Validation(errors));

        for (var attempt = 1; ; attempt++)
        {
            var generated = ApiTokenFormat.Generate();
            var token = new ApiToken
            {
                OrganizationId = OrganizationId,
                CreatedByUserId = UserId,
                Name = request.Name.Trim(),
                Prefix = generated.Prefix,
                SecretHash = generated.SecretHash,
                Scopes = scopes,
                ExpiresAt = expiresAt,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ApiTokens.Add(token);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt < PrefixCollisionRetries && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // 62^8 prefixes make a collision astronomically unlikely, but the unique index is the arbiter: draw again.
                db.Entry(token).State = EntityState.Detached;
                continue;
            }

            await audit.RecordAsync("api_tokens.created", "api_token", token.Id,
                new { name = token.Name, scopes = token.Scopes, prefix = token.Prefix, expiresAt = token.ExpiresAt }, cancellationToken);
            return new CreatedApiTokenResponse(
                token.Id, token.CreatedByUserId, token.Name, token.Prefix, token.Scopes, token.ExpiresAt, token.CreatedAt, generated.Plaintext);
        }
    }

    // ---- read --------------------------------------------------------------------------------------------------------------------

    public async Task<Page<ApiTokenResponse>> ListAsync(ApiTokenListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page.Validated();
        var now = clock.UtcNow;

        // Not an admin: only your own. Admin without a filter: everyone's.
        var ownerFilter = IsAdmin ? query.UserId : UserId;
        if (!IsAdmin && query.UserId is { } other && other != UserId)
            throw new ApiProblemException(ApiProblems.Forbidden("Only an admin can list another user's tokens."));

        var statuses = QueryGuard.Split(query.Status).Select(s => s.ToLowerInvariant()).Distinct().ToList();
        foreach (var status in statuses.Where(s => !Statuses.Contains(s)))
            throw new ApiProblemException(ApiProblems.InvalidParameter(
                "status", $"Unknown status '{status}'. Allowed: {string.Join(", ", Statuses)}.", "invalid_enum"));
        var context = $"-createdAt,id|user={ownerFilter}|status={string.Join(',', statuses.Order())}";

        var tokens = db.ApiTokens.AsNoTracking().Where(t => t.OrganizationId == OrganizationId);
        if (ownerFilter is { } owner) tokens = tokens.Where(t => t.CreatedByUserId == owner);
        if (statuses.Count > 0)
        {
            var wantActive = statuses.Contains("active");
            var wantRevoked = statuses.Contains("revoked");
            var wantExpired = statuses.Contains("expired");
            tokens = tokens.Where(t =>
                (wantRevoked && t.RevokedAt != null)
                || (wantExpired && t.RevokedAt == null && t.ExpiresAt != null && t.ExpiresAt <= now)
                || (wantActive && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now)));
        }

        if (page.Cursor is not null)
        {
            var position = cursors.Decode(page.Cursor, context);
            if (position.SortValues.Count != 1 || position.SortValues[0] is not { } created)
                throw new ApiProblemException(ApiProblems.InvalidCursor());
            var createdAt = KeysetCursor.ParseDateTimeOffset(created);
            var id = position.Id;
            tokens = tokens.Where(t => t.CreatedAt < createdAt || (t.CreatedAt == createdAt && t.Id.CompareTo(id) < 0));
        }

        var fetched = await tokens.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Take(page.Limit + 1).ToListAsync(cancellationToken);
        return Page<ApiTokenResponse>.FromOverfetch(fetched.Select(ToResponse).ToList(), page.Limit, last =>
            cursors.Encode(new KeysetPosition([KeysetCursor.Format(last.CreatedAt)], last.Id), context));
    }

    public async Task<ApiTokenResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToResponse(await FindAsync(id, tracked: false, cancellationToken));

    // ---- revoke ------------------------------------------------------------------------------------------------------------------

    /// <summary>Revokes the token immediately. Revoking an already revoked token succeeds without a second audit event.</summary>
    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await FindAsync(id, tracked: true, cancellationToken);
        if (token.RevokedAt is not null) return;

        token.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("api_tokens.revoked", "api_token", token.Id,
            new { name = token.Name, prefix = token.Prefix, owner = token.CreatedByUserId }, cancellationToken);
    }

    private async Task<ApiToken> FindAsync(Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? db.ApiTokens : db.ApiTokens.AsNoTracking();
        var token = await query.FirstOrDefaultAsync(t => t.Id == id && t.OrganizationId == OrganizationId, cancellationToken);
        // Someone else's token looks exactly like a missing one.
        return token is null || (!IsAdmin && token.CreatedByUserId != UserId)
            ? throw new ApiProblemException(AuthProblems.ApiTokenNotFound(id))
            : token;
    }

    private static ApiTokenResponse ToResponse(ApiToken token) =>
        new(token.Id, token.CreatedByUserId, token.Name, token.Prefix, token.Scopes, token.ExpiresAt, token.CreatedAt,
            token.LastUsedAt, token.LastUsedIp, token.RevokedAt);
}
