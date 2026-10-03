using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Domain;
using Aethera.Infrastructure.Auth;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aethera.Api.Features.Auth;

/// <summary>Query parameters of <c>GET /users</c> after parsing.</summary>
public sealed record UserListQuery(PageRequest Page, string? Sort, string? Role, bool? IsActive, string? Q);

/// <summary>
/// User administration (Admin and above). Rules enforced here, on top of the endpoint policy:
/// <list type="bullet">
/// <item>Only an Owner may grant the Owner role, and only an Owner may change, deactivate or delete an Owner.</item>
/// <item>An organization always keeps at least one <em>active</em> Owner: demoting, deactivating or deleting the last one is
/// <c>409 users.last_owner</c>. The check runs inside a transaction holding an advisory lock, so two owners cannot remove each other
/// concurrently.</item>
/// <item>Deactivating, deleting or resetting the password of a user signs them out everywhere.</item>
/// </list>
/// </summary>
public sealed class UserService(
    AetheraDbContext db,
    IClock clock,
    IAuditLog audit,
    ICurrentActor actor,
    PasswordService passwords,
    SessionStore sessions,
    KeysetCursor cursors)
{
    private static readonly string[] SortableFields = ["email", "createdAt"];

    private Guid OrganizationId => actor.OrganizationId ?? throw new ApiProblemException(ApiProblems.Unauthenticated());

    private OrganizationRole ActorRole => actor.Role ?? throw new ApiProblemException(ApiProblems.Unauthenticated());

    // ---- read --------------------------------------------------------------------------------------------------------------------

    public async Task<Page<UserResponse>> ListAsync(UserListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page.Validated();
        var order = SortSpec.Parse(query.Sort, SortableFields, "email");
        if (order.Fields.Count != 1)
            throw new ApiProblemException(ApiProblems.InvalidParameter("sort", "Sort by a single field: email or createdAt.", "invalid_sort"));
        var field = order.Fields[0];

        var roles = QueryGuard.Split(query.Role).Select(ParseRole).ToList();
        var search = query.Q?.Trim().ToLowerInvariant();
        var context = $"{order.Canonical}|role={string.Join(',', roles.Order())}|active={query.IsActive}|q={search}";

        var rows = from m in db.OrganizationMembers.AsNoTracking()
                   where m.OrganizationId == OrganizationId
                   join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                   select new { User = u, m.Role };

        if (roles.Count > 0) rows = rows.Where(r => roles.Contains(r.Role));
        if (query.IsActive is { } active) rows = rows.Where(r => r.User.IsActive == active);
        if (!string.IsNullOrEmpty(search))
            rows = rows.Where(r => r.User.NormalizedEmail.Contains(search) || r.User.DisplayName.ToLower().Contains(search));

        if (page.Cursor is not null)
        {
            var position = cursors.Decode(page.Cursor, context);
            var id = position.Id;
            var value = position.SortValues.Count == 1 ? position.SortValues[0] : null;
            if (value is null) throw new ApiProblemException(ApiProblems.InvalidCursor());

            if (field.Name == "email")
            {
                rows = field.Descending
                    ? rows.Where(r => r.User.NormalizedEmail.CompareTo(value) < 0 || (r.User.NormalizedEmail == value && r.User.Id.CompareTo(id) < 0))
                    : rows.Where(r => r.User.NormalizedEmail.CompareTo(value) > 0 || (r.User.NormalizedEmail == value && r.User.Id.CompareTo(id) > 0));
            }
            else
            {
                var created = KeysetCursor.ParseDateTimeOffset(value);
                rows = field.Descending
                    ? rows.Where(r => r.User.CreatedAt < created || (r.User.CreatedAt == created && r.User.Id.CompareTo(id) < 0))
                    : rows.Where(r => r.User.CreatedAt > created || (r.User.CreatedAt == created && r.User.Id.CompareTo(id) > 0));
            }
        }

        var ordered = (field.Name, field.Descending) switch
        {
            ("email", false) => rows.OrderBy(r => r.User.NormalizedEmail).ThenBy(r => r.User.Id),
            ("email", true) => rows.OrderByDescending(r => r.User.NormalizedEmail).ThenByDescending(r => r.User.Id),
            (_, false) => rows.OrderBy(r => r.User.CreatedAt).ThenBy(r => r.User.Id),
            _ => rows.OrderByDescending(r => r.User.CreatedAt).ThenByDescending(r => r.User.Id),
        };

        var fetched = await ordered.Take(page.Limit + 1).ToListAsync(cancellationToken);
        var items = fetched.Select(r => ToResponse(r.User, r.Role)).ToList();
        return Page<UserResponse>.FromOverfetch(items, page.Limit, last =>
        {
            var sortValue = field.Name == "email" ? last.Email.ToLowerInvariant() : KeysetCursor.Format(last.CreatedAt);
            return cursors.Encode(new KeysetPosition([sortValue], last.Id), context);
        });
    }

    public async Task<UserResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await (from m in db.OrganizationMembers.AsNoTracking()
                         where m.OrganizationId == OrganizationId && m.UserId == id
                         join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                         select new { User = u, m.Role }).FirstOrDefaultAsync(cancellationToken);
        return row is null ? throw new ApiProblemException(AuthProblems.UserNotFound(id)) : ToResponse(row.User, row.Role);
    }

    // ---- create ------------------------------------------------------------------------------------------------------------------

    public async Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        EnsureCanGrant(request.Role);
        var email = request.Email.Trim();
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == User.NormalizeEmail(email), cancellationToken))
            throw new ApiProblemException(AuthProblems.UserAlreadyExists());

        var user = new User(email, request.DisplayName.Trim());
        user.PasswordHash = passwords.Hash(user, request.Password);
        var membership = new OrganizationMember { OrganizationId = OrganizationId, User = user, Role = request.Role };
        db.AddRange(user, membership);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ApiProblemException(AuthProblems.UserAlreadyExists());
        }

        await audit.RecordAsync("users.created", "user", user.Id, new { email = user.Email, role = request.Role }, cancellationToken);
        return ToResponse(user, request.Role);
    }

    // ---- update ------------------------------------------------------------------------------------------------------------------

    public async Task<UserResponse> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireAsync(db, AuthLocks.OwnerChanges, cancellationToken);

        var membership = await FindMembershipAsync(id, cancellationToken);
        var user = membership.User;
        EnsureCanManage(membership.Role);

        var changed = new List<string>();
        if (request.Email is { } email && !string.Equals(email.Trim(), user.Email, StringComparison.Ordinal))
        {
            var normalized = User.NormalizeEmail(email);
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != user.Id, cancellationToken))
                throw new ApiProblemException(AuthProblems.UserAlreadyExists());
            user.SetEmail(email);
            changed.Add("email");
        }

        if (request.DisplayName is { } displayName && displayName.Trim() != user.DisplayName)
        {
            user.DisplayName = displayName.Trim();
            changed.Add("displayName");
        }

        var revokeSessions = false;
        if (request.IsActive is { } isActive && isActive != user.IsActive)
        {
            if (!isActive) await EnsureNotLastOwnerAsync(membership, cancellationToken);
            user.IsActive = isActive;
            if (isActive)
            {
                user.FailedLoginCount = 0;
                user.LockoutEndAt = null;
            }
            else
            {
                revokeSessions = true;
            }

            changed.Add("isActive");
        }

        var passwordReset = false;
        if (request.Password is { } password)
        {
            if (PasswordPolicy.Check(password, user.Email) == PasswordProblem.SameAsEmail)
                throw new ApiProblemException(AuthProblems.Field("/password", "password.same_as_email", "Must not be the same as the email address."));
            user.PasswordHash = passwords.Hash(user, password);
            user.FailedLoginCount = 0;
            user.LockoutEndAt = null;
            revokeSessions = true;
            passwordReset = true;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ApiProblemException(AuthProblems.UserAlreadyExists());
        }

        if (revokeSessions) await sessions.RevokeAllAsync(user.Id, cancellationToken: cancellationToken);
        if (changed.Count > 0)
            await audit.RecordAsync("users.updated", "user", user.Id, new { changed }, cancellationToken);
        if (passwordReset)
            await audit.RecordAsync("users.password_reset", "user", user.Id, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(user, membership.Role);
    }

    public async Task<UserResponse> SetRoleAsync(Guid id, OrganizationRole role, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireAsync(db, AuthLocks.OwnerChanges, cancellationToken);

        var membership = await FindMembershipAsync(id, cancellationToken);
        EnsureCanManage(membership.Role);
        EnsureCanGrant(role);
        if (membership.Role == role) return ToResponse(membership.User, role);

        if (membership.Role == OrganizationRole.Owner) await EnsureNotLastOwnerAsync(membership, cancellationToken);

        var previous = membership.Role;
        membership.Role = role;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync("users.role_changed", "user", id, new { from = previous, to = role }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(membership.User, role);
    }

    /// <summary>Soft-deletes the user: the account disappears, its sessions and API tokens stop working, and the email address becomes free.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuthLocks.AcquireAsync(db, AuthLocks.OwnerChanges, cancellationToken);

        var membership = await FindMembershipAsync(id, cancellationToken);
        EnsureCanManage(membership.Role);
        await EnsureNotLastOwnerAsync(membership, cancellationToken);

        var now = clock.UtcNow;
        var user = membership.User;
        user.IsActive = false;
        user.MarkDeleted(now);
        await db.SaveChangesAsync(cancellationToken);

        await sessions.RevokeAllAsync(user.Id, cancellationToken: cancellationToken);
        await db.ApiTokens.Where(t => t.CreatedByUserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.UpdatedAt, now), cancellationToken);
        await audit.RecordAsync("users.deleted", "user", user.Id, new { email = user.Email }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ---- rules -------------------------------------------------------------------------------------------------------------------

    /// <summary>Only an Owner may touch an Owner's account.</summary>
    private void EnsureCanManage(OrganizationRole targetRole)
    {
        if (targetRole == OrganizationRole.Owner && ActorRole != OrganizationRole.Owner)
            throw new ApiProblemException(ApiProblems.Forbidden("Only an owner can change an owner."));
    }

    /// <summary>Only an Owner may hand out the Owner role.</summary>
    private void EnsureCanGrant(OrganizationRole role)
    {
        if (role == OrganizationRole.Owner && ActorRole != OrganizationRole.Owner)
            throw new ApiProblemException(ApiProblems.Forbidden("Only an owner can grant the owner role."));
    }

    /// <summary>
    /// Refuses an operation that would leave the organization without an active Owner. Call it only while holding
    /// <see cref="AuthLocks.OwnerChanges"/> and only when <paramref name="membership"/> is about to stop being an active Owner.
    /// </summary>
    private async Task EnsureNotLastOwnerAsync(OrganizationMember membership, CancellationToken cancellationToken)
    {
        if (membership.Role != OrganizationRole.Owner || !membership.User.IsActive) return;

        var activeOwners = await (from m in db.OrganizationMembers
                                  join u in db.Users on m.UserId equals u.Id
                                  where m.OrganizationId == membership.OrganizationId && m.Role == OrganizationRole.Owner && u.IsActive
                                  select m.Id).CountAsync(cancellationToken);
        if (activeOwners <= 1) throw new ApiProblemException(AuthProblems.LastOwner());
    }

    private async Task<OrganizationMember> FindMembershipAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.OrganizationMembers.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.OrganizationId == OrganizationId && m.UserId == userId, cancellationToken)
        ?? throw new ApiProblemException(AuthProblems.UserNotFound(userId));

    private static OrganizationRole ParseRole(string value) =>
        Enum.TryParse<OrganizationRole>(value, ignoreCase: true, out var role) && Enum.IsDefined(role)
            ? role
            : throw new ApiProblemException(ApiProblems.InvalidParameter(
                "role", $"Unknown role '{value}'. Allowed: viewer, developer, admin, owner.", "invalid_enum"));

    private static UserResponse ToResponse(User user, OrganizationRole role) =>
        new(user.Id, user.Email, user.DisplayName, role, user.IsActive, user.LastLoginAt, user.CreatedAt, user.UpdatedAt);
}
