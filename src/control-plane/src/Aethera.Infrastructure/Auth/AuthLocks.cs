using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Auth;

/// <summary>
/// Transaction-scoped PostgreSQL advisory locks that serialize the few auth operations whose invariant spans several rows and so cannot be
/// protected by a unique index: "the first user is created exactly once" and "an organization always keeps an active owner". The lock is
/// released automatically at the end of the surrounding transaction, so call these only inside one.
/// </summary>
public static class AuthLocks
{
    // Arbitrary but fixed ("AETH" + 1 / 2); advisory lock keys are a global namespace within the database.
    public const long FirstRunSetup = 0x41455448_0001;
    public const long OwnerChanges = 0x41455448_0002;

    public static Task AcquireAsync(AetheraDbContext db, long key, CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);

    /// <summary>Serializes work on one user's login counters (failed attempts / lockout) across requests and instances.</summary>
    public static Task AcquireForUserAsync(AetheraDbContext db, Guid userId, CancellationToken cancellationToken = default)
    {
        var key = userId.ToString("N");
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }
}
