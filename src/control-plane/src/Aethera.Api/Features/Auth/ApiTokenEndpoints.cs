using Aethera.Api.Http;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;

namespace Aethera.Api.Features.Auth;

internal static class ApiTokenEndpoints
{
    public static void MapApiTokenEndpoints(this IEndpointRouteBuilder api)
    {
        var tokens = api.MapGroup("/api-tokens").WithTags("ApiTokens");

        tokens.MapGet("/", async (
                HttpContext http, ApiTokenService service, CancellationToken ct,
                int limit = PageRequest.DefaultLimit, string? cursor = null, Guid? userId = null, string? status = null) =>
            {
                QueryGuard.RejectUnknown(http, "limit", "cursor", "userId", "status");
                return Results.Ok(await service.ListAsync(new ApiTokenListQuery(new PageRequest(limit, cursor), userId, status), ct));
            })
            .WithName("listApiTokens")
            .WithSummary("List API tokens")
            .WithDescription(
                "Your own tokens; Admin and Owner see every token of the organization and can narrow with `userId`. " +
                "Filter `status`: `active`, `revoked`, `expired` (comma-separated). Newest first. The secret is never returned.")
            .RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Read)
            .Produces<Page<ApiTokenResponse>>()
            .ProducesProblems((StatusCodes.Status403Forbidden, "Asked for another user's tokens without being an admin (auth.forbidden)."));

        tokens.MapPost("/", async (CreateApiTokenRequest body, ApiTokenService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(body, ct);
                return Results.Created($"/api/v1/api-tokens/{created.Id}", created);
            })
            .WithName("createApiToken")
            .WithSummary("Create an API token")
            .WithDescription(
                "Creates a token for yourself. **The plaintext `token` is returned only in this response**; store it now. " +
                "Scopes cannot exceed what your role allows: viewer `read`; developer adds `write` and `deploy`; admin and owner everything. " +
                "`expiresAt` defaults to 90 days, may be at most one year ahead, and may be `null` (never) for admin and owner only. " +
                "With an API token this needs the `admin` scope.")
            .RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Admin)
            .AddEndpointFilter<NoStoreFilter>()
            .Validate<CreateApiTokenRequest>()
            .Produces<CreatedApiTokenResponse>(StatusCodes.Status201Created)
            .ProducesProblems(
                (StatusCodes.Status422UnprocessableEntity, "Invalid input (validation.failed; errors[] codes required, invalid_scope, scope_not_allowed, never_not_allowed, in_past, too_far)."),
                (StatusCodes.Status403Forbidden, "The CSRF token is missing on a session request (auth.csrf_invalid) or the API token lacks the admin scope (auth.insufficient_scope)."));

        tokens.MapGet("/{id:guid}", async (Guid id, ApiTokenService service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct)))
            .WithName("getApiToken")
            .WithSummary("Get an API token")
            .WithDescription("Your own token, or any token of the organization for Admin and Owner. Never includes the secret.")
            .RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Read)
            .Produces<ApiTokenResponse>()
            .ProducesProblems((StatusCodes.Status404NotFound, "No such token, or it belongs to someone else (api_token.not_found)."));

        tokens.MapDelete("/{id:guid}", async (Guid id, ApiTokenService service, CancellationToken ct) =>
            {
                await service.RevokeAsync(id, ct);
                return Results.NoContent();
            })
            .WithName("revokeApiToken")
            .WithSummary("Revoke an API token")
            .WithDescription(
                "Takes effect immediately; the token row stays (with `revokedAt`) so history remains. Revoking twice is fine. " +
                "Your own token, or any token of the organization for Admin and Owner. With an API token this needs the `admin` scope.")
            .RequireRole(AetheraPolicies.Viewer).RequireScope(Scopes.Admin)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(
                (StatusCodes.Status404NotFound, "No such token, or it belongs to someone else (api_token.not_found)."),
                (StatusCodes.Status403Forbidden, "The CSRF token is missing on a session request (auth.csrf_invalid)."));
    }
}
