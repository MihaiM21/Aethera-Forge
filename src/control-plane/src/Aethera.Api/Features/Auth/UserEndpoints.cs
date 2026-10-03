using Aethera.Api.Http;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;

namespace Aethera.Api.Features.Auth;

internal static class UserEndpoints
{
    private const string AdminOnly = "Admin role (the `admin` scope for API tokens).";

    public static void MapUserEndpoints(this IEndpointRouteBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Users")
            .RequireRole(AetheraPolicies.Admin).RequireScope(Scopes.Admin);

        users.MapGet("/", async (
                HttpContext http, UserService service, CancellationToken ct,
                int limit = PageRequest.DefaultLimit, string? cursor = null, string? sort = null, string? role = null, bool? isActive = null, string? q = null) =>
            {
                QueryGuard.RejectUnknown(http, "limit", "cursor", "sort", "role", "isActive", "q");
                return Results.Ok(await service.ListAsync(new UserListQuery(new PageRequest(limit, cursor), sort, role, isActive, q), ct));
            })
            .WithName("listUsers")
            .WithSummary("List users")
            .WithDescription(
                AdminOnly + " Filters: `role` (comma-separated), `isActive`, `q` (substring of email or name). " +
                "Sort: `email` (default) or `createdAt`, optionally prefixed with `-`.")
            .Produces<Page<UserResponse>>();

        users.MapPost("/", async (CreateUserRequest body, UserService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(body, ct);
                return Results.Created($"/api/v1/users/{created.Id}", created);
            })
            .WithName("createUser")
            .WithSummary("Create a user with an initial password")
            .WithDescription(AdminOnly + " Only an Owner can create an Owner.")
            .Validate<CreateUserRequest>()
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ProducesProblems(
                (StatusCodes.Status409Conflict, "Email already in use (user.already_exists)."),
                (StatusCodes.Status403Forbidden, "Role too low, only an owner can grant the owner role (auth.forbidden), or the CSRF token is missing (auth.csrf_invalid)."));

        users.MapGet("/{id:guid}", async (Guid id, UserService service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct)))
            .WithName("getUser")
            .WithSummary("Get a user")
            .WithDescription(AdminOnly)
            .Produces<UserResponse>()
            .ProducesProblems((StatusCodes.Status404NotFound, "No such user in this organization (user.not_found)."));

        users.MapPatch("/{id:guid}", async (Guid id, UpdateUserRequest body, UserService service, CancellationToken ct) =>
                Results.Ok(await service.UpdateAsync(id, body, ct)))
            .WithName("updateUser")
            .WithSummary("Update a user (JSON Merge Patch)")
            .WithDescription(
                AdminOnly + " Change `email` or `displayName`, deactivate or reactivate with `isActive`, or reset the password with `password` " +
                "(which signs the user out everywhere). Only an Owner can change an Owner, and the last active Owner cannot be deactivated.")
            .Validate<UpdateUserRequest>()
            .Accepts<UpdateUserRequest>("application/merge-patch+json", "application/json")
            .Produces<UserResponse>()
            .ProducesProblems(
                (StatusCodes.Status404NotFound, "No such user (user.not_found)."),
                (StatusCodes.Status409Conflict, "Email already in use (user.already_exists) or this would leave no active owner (users.last_owner)."),
                (StatusCodes.Status403Forbidden, "Only an owner can change an owner (auth.forbidden), or the CSRF token is missing (auth.csrf_invalid)."));

        users.MapDelete("/{id:guid}", async (Guid id, UserService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return Results.NoContent();
            })
            .WithName("deleteUser")
            .WithSummary("Delete a user")
            .WithDescription(
                AdminOnly + " Soft delete: the account, its sessions and its API tokens stop working and the email address is freed. " +
                "The last active Owner cannot be deleted.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(
                (StatusCodes.Status404NotFound, "No such user (user.not_found)."),
                (StatusCodes.Status409Conflict, "The last active owner cannot be removed (users.last_owner)."),
                (StatusCodes.Status403Forbidden, "Only an owner can delete an owner (auth.forbidden), or the CSRF token is missing (auth.csrf_invalid)."));

        users.MapPut("/{id:guid}/role", async (Guid id, SetUserRoleRequest body, UserService service, CancellationToken ct) =>
                Results.Ok(await service.SetRoleAsync(id, body.Role, ct)))
            .WithName("setUserRole")
            .WithSummary("Set a user's role")
            .WithDescription(
                AdminOnly + " Only an Owner can grant the Owner role or change an Owner's role. The last active Owner cannot be demoted.")
            .Validate<SetUserRoleRequest>()
            .Produces<UserResponse>()
            .ProducesProblems(
                (StatusCodes.Status404NotFound, "No such user (user.not_found)."),
                (StatusCodes.Status409Conflict, "This would leave no active owner (users.last_owner)."),
                (StatusCodes.Status403Forbidden, "Only an owner can grant or change the owner role (auth.forbidden), or the CSRF token is missing (auth.csrf_invalid)."));
    }
}
