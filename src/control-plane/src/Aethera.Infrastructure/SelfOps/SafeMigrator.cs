using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.SelfOps;

/// <summary>What a start-up migration found.</summary>
/// <param name="Applied">Migrations already in the database.</param>
/// <param name="Pending">Migrations this version of the code would apply.</param>
/// <param name="Unknown">Applied migrations this version of the code does not contain: the database is from a newer Aethera.</param>
public sealed record MigrationPlan(IReadOnlyList<string> Applied, IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown);

/// <summary>The database was migrated by a newer Aethera than the one that is starting. Starting would run old code on a schema it does not know.</summary>
public sealed class DatabaseNewerThanApplicationException(IReadOnlyList<string> unknown)
    : Exception($"The database has {unknown.Count} migration(s) this version of Aethera does not know (the newest is {unknown[^1]}). " +
                "It was upgraded by a newer release; start that release again, or restore a backup taken before the upgrade (pre-migration backups are in the backups folder).")
{
    public IReadOnlyList<string> Unknown { get; } = unknown;
}

/// <summary>
/// Applies pending EF Core migrations the safe way (WP5.2): refuses to start on a database that is newer than the code (a downgrade),
/// and takes a <c>pg_dump</c> backup first when an existing installation is about to change shape. A failed backup stops the start; a
/// missing <c>pg_dump</c> (developer machines) only logs a warning. EF Core serialises concurrent migrators with its own database lock.
/// </summary>
public static class SafeMigrator
{
    public static async Task<MigrationPlan> PlanAsync(DbContext db, CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var known = db.Database.GetMigrations().ToList();
        var pending = known.Except(applied).ToList();
        var unknown = applied.Except(known).ToList();
        return new MigrationPlan(applied, pending, unknown);
    }

    /// <param name="backup">Null disables the pre-migration backup.</param>
    /// <returns>The backup taken, if any.</returns>
    public static async Task<BackupFile?> MigrateAsync(
        DbContext db, IDatabaseBackup? backup, int keepPreMigrationBackups, DateTimeOffset now, ILogger logger, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(db, cancellationToken);
        if (plan.Unknown.Count > 0) throw new DatabaseNewerThanApplicationException(plan.Unknown);

        BackupFile? taken = null;
        if (plan.Pending.Count > 0 && plan.Applied.Count > 0)
        {
            logger.LogInformation("{Count} pending migration(s) on an existing database: {First} .. {Last}", plan.Pending.Count, plan.Pending[0], plan.Pending[^1]);
            if (backup is null) logger.LogWarning("Pre-migration backup is disabled; migrating without one");
            else if (!backup.IsAvailable()) logger.LogWarning("pg_dump is not available, so no backup is taken before migrating");
            else
            {
                taken = await backup.BackupAsync("pre-migration", now, cancellationToken);
                backup.Prune("pre-migration", keepPreMigrationBackups);
            }
        }

        if (plan.Pending.Count > 0)
        {
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Database is at {Migration}", plan.Pending[^1]);
        }
        return taken;
    }
}
