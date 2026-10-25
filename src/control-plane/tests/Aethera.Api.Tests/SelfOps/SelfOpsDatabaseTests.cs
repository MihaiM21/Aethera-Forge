using Aethera.Api.Tests.Agents;
using Aethera.Domain;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.SelfOps;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aethera.Api.Tests.SelfOps;

internal sealed class FakeBackup(bool available = true, Exception? fail = null) : IDatabaseBackup
{
    public List<string> Taken { get; } = [];
    public List<(string Kind, int Keep)> Pruned { get; } = [];
    public bool IsAvailable() => available;

    public Task<BackupFile> BackupAsync(string kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (fail is not null) throw fail;
        var name = $"aethera-{kind}-{now.UtcDateTime:yyyyMMdd-HHmmss}.dump";
        Taken.Add(kind);
        return Task.FromResult(new BackupFile(name, 1, now, kind));
    }

    public IReadOnlyList<BackupFile> List() => [];

    public int Prune(string kind, int keep)
    {
        Pruned.Add((kind, keep));
        return 0;
    }
}

/// <summary>The start-up migration against a real database: a fresh install, an upgrade, a downgrade attempt.</summary>
public sealed class SafeMigratorTests
{
    private const string Initial = "20261003143121_InitialSchema";
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static async Task WithDatabaseAsync(Func<AetheraDbContext, string, Task> test)
    {
        var raw = System.Environment.GetEnvironmentVariable(RequiresDatabaseFactAttribute.EnvironmentVariable)!;
        var admin = new NpgsqlConnectionStringBuilder(raw) { Pooling = false };
        var name = "aethera_it_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(raw) { Database = name, Pooling = false }.ConnectionString;
        await Sql(admin.ConnectionString, $"CREATE DATABASE \"{name}\"");
        try
        {
            await using var services = TestDatabaseFixture.BuildServices(connectionString);
            await using var scope = services.CreateAsyncScope();
            await test(scope.ServiceProvider.GetRequiredService<AetheraDbContext>(), connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Sql(admin.ConnectionString, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    private static async Task Sql(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [RequiresDatabaseFact]
    public async Task A_fresh_database_is_migrated_without_a_backup()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            var backup = new FakeBackup();

            var taken = await SafeMigrator.MigrateAsync(db, backup, 5, Now, NullLogger.Instance, default);

            Assert.Null(taken);
            Assert.Empty(backup.Taken);
            Assert.Empty((await SafeMigrator.PlanAsync(db, default)).Pending);
        });
    }

    [RequiresDatabaseFact]
    public async Task An_existing_installation_is_backed_up_before_pending_migrations_are_applied()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            await db.GetService<IMigrator>().MigrateAsync(Initial);
            var backup = new FakeBackup();

            var taken = await SafeMigrator.MigrateAsync(db, backup, 5, Now, NullLogger.Instance, default);

            Assert.Equal("pre-migration", taken!.Kind);
            Assert.Equal(["pre-migration"], backup.Taken);
            Assert.Equal([("pre-migration", 5)], backup.Pruned);
            Assert.Empty((await SafeMigrator.PlanAsync(db, default)).Pending);
        });
    }

    [RequiresDatabaseFact]
    public async Task A_failing_backup_stops_the_migration_and_leaves_the_schema_alone()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            await db.GetService<IMigrator>().MigrateAsync(Initial);
            var before = (await SafeMigrator.PlanAsync(db, default)).Pending;

            var ex = await Assert.ThrowsAsync<BackupFailedException>(() =>
                SafeMigrator.MigrateAsync(db, new FakeBackup(fail: new BackupFailedException("disk full")), 5, Now, NullLogger.Instance, default));

            Assert.Equal("disk full", ex.Message);
            Assert.Equal(before, (await SafeMigrator.PlanAsync(db, default)).Pending);
        });
    }

    [RequiresDatabaseFact]
    public async Task Without_pg_dump_or_with_backups_disabled_the_migration_still_runs()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            await db.GetService<IMigrator>().MigrateAsync(Initial);
            var unavailable = new FakeBackup(available: false);
            Assert.Null(await SafeMigrator.MigrateAsync(db, unavailable, 5, Now, NullLogger.Instance, default));
            Assert.Empty(unavailable.Taken);
            Assert.Empty((await SafeMigrator.PlanAsync(db, default)).Pending);
        });

        await WithDatabaseAsync(async (db, _) =>
        {
            await db.GetService<IMigrator>().MigrateAsync(Initial);
            Assert.Null(await SafeMigrator.MigrateAsync(db, null, 5, Now, NullLogger.Instance, default));
            Assert.Empty((await SafeMigrator.PlanAsync(db, default)).Pending);
        });
    }

    [RequiresDatabaseFact]
    public async Task Nothing_pending_means_no_backup_and_no_work()
    {
        await WithDatabaseAsync(async (db, _) =>
        {
            await db.Database.MigrateAsync();
            var backup = new FakeBackup();
            Assert.Null(await SafeMigrator.MigrateAsync(db, backup, 5, Now, NullLogger.Instance, default));
            Assert.Empty(backup.Taken);
        });
    }

    [RequiresDatabaseFact]
    public async Task A_database_from_a_newer_release_stops_the_start_instead_of_running_old_code_on_it()
    {
        await WithDatabaseAsync(async (db, connectionString) =>
        {
            await db.Database.MigrateAsync();
            await Sql(connectionString, "INSERT INTO \"__EFMigrationsHistory\" (migration_id, product_version) VALUES ('29990101000000_FromTheFuture', '99.0.0')");
            var backup = new FakeBackup();

            var ex = await Assert.ThrowsAsync<DatabaseNewerThanApplicationException>(() =>
                SafeMigrator.MigrateAsync(db, backup, 5, Now, NullLogger.Instance, default));

            Assert.Equal(["29990101000000_FromTheFuture"], ex.Unknown);
            Assert.Contains("newer release", ex.Message);
            Assert.Empty(backup.Taken);
        });
    }

    [RequiresDatabaseFact]
    public async Task The_start_up_hook_honours_AutoMigrate_and_leaves_the_database_alone_when_it_is_off()
    {
        await WithDatabaseAsync(async (db, connectionString) =>
        {
            var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            _ = services;
            await using var provider = TestDatabaseFixture.BuildServices(connectionString);

            var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Aethera"] = connectionString }).Build();
            Assert.False(await provider.MigrateAetheraDatabaseIfEnabledAsync(off));
            Assert.NotEmpty((await SafeMigrator.PlanAsync(db, default)).Pending);

            var on = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Aethera"] = connectionString, ["Aethera:Database:AutoMigrate"] = "true", ["Aethera:Database:BackupBeforeMigrate"] = "false",
            }).Build();
            Assert.True(await provider.MigrateAetheraDatabaseIfEnabledAsync(on));
            Assert.Empty((await SafeMigrator.PlanAsync(db, default)).Pending);
        });
    }
}

/// <summary>Scheduled maintenance on a real database: what retention deletes and what it must not, the backup and the prune schedule.</summary>
[Collection(GatewayCollection.Name)]
public sealed class MaintenanceServiceTests(GatewayFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestMaintenance(
        IServiceScopeFactory scopes, IOptions<MaintenanceOptions> options, AgentSessionRegistry sessions, IDatabaseBackup? backup)
        : MaintenanceService(scopes, new Clock(), options, new ConfigurationBuilder().Build(), sessions, NullLogger<MaintenanceService>.Instance)
    {
        public override IDatabaseBackup? CreateBackup() => backup;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private TestMaintenance Service(MaintenanceOptions? options = null, IDatabaseBackup? backup = null) =>
        new(fixture.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options ?? new MaintenanceOptions { BackupEnabled = backup is not null }),
            fixture.Get<AgentSessionRegistry>(), backup);

    [RequiresDatabaseFact]
    public async Task Retention_deletes_only_what_is_past_its_window()
    {
        var (orgId, serverId) = await fixture.SeedServerAsync();
        var oldStream = $"job:{Guid.NewGuid()}";
        var freshStream = $"job:{Guid.NewGuid()}";
        var marker = Guid.NewGuid().ToString("N");
        var oldSessionUser = Guid.Empty;
        await fixture.WithDbAsync(async db =>
        {
            db.LogChunks.AddRange(
                new LogChunk { StreamId = oldStream, Sequence = 1, Timestamp = Now.AddDays(-31), Data = "old" },
                new LogChunk { StreamId = freshStream, Sequence = 1, Timestamp = Now.AddDays(-29), Data = "fresh" });
            db.ResourceEvents.AddRange(
                new ResourceEvent { ResourceType = "server", ResourceId = serverId, Kind = $"old-{marker}", OccurredAt = Now.AddDays(-91) },
                new ResourceEvent { ResourceType = "server", ResourceId = serverId, Kind = $"fresh-{marker}", OccurredAt = Now.AddDays(-89) });
            await db.SaveChangesAsync();
            var empty = "{}";
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO audit_events (id, organization_id, actor_type, action, metadata, occurred_at)
                VALUES ({Guid.CreateVersion7()}, {orgId}, 'system', {"old." + marker}, {empty}::jsonb, {Now.AddDays(-366)}),
                       ({Guid.CreateVersion7()}, {orgId}, 'system', {"fresh." + marker}, {empty}::jsonb, {Now.AddDays(-364)})
                """);
            oldSessionUser = await db.Users.Select(u => u.Id).FirstAsync();
            db.IdempotencyRecords.AddRange(
                new IdempotencyRecord { PrincipalId = oldSessionUser, Key = "old-" + marker, Method = "POST", Path = "/x", RequestHash = [1], CreatedAt = Now.AddDays(-9), ExpiresAt = Now.AddDays(-8) },
                new IdempotencyRecord { PrincipalId = oldSessionUser, Key = "fresh-" + marker, Method = "POST", Path = "/x", RequestHash = [1], CreatedAt = Now, ExpiresAt = Now.AddDays(1) });
            await db.SaveChangesAsync();
            return 0;
        });

        var report = await Service().RunOnceAsync(Now, force: false, default);

        Assert.Empty(report.Errors);
        Assert.True(report.LogChunksDeleted >= 1);
        Assert.True(report.EventsDeleted >= 1);
        Assert.True(report.AuditEventsDeleted >= 1);
        Assert.True(report.IdempotencyRecordsDeleted >= 1);
        await fixture.WithDbAsync(async db =>
        {
            Assert.Equal(["fresh"], await db.LogChunks.Where(c => c.StreamId == oldStream || c.StreamId == freshStream).Select(c => c.Data).ToListAsync());
            Assert.Equal([$"fresh-{marker}"], await db.ResourceEvents.Where(e => e.Kind.EndsWith(marker)).Select(e => e.Kind).ToListAsync());
            Assert.Equal(["fresh." + marker], await db.AuditEvents.Where(e => e.Action.EndsWith(marker)).Select(e => e.Action).ToListAsync());
            Assert.Equal(["fresh-" + marker], await db.IdempotencyRecords.Where(r => r.Key.EndsWith(marker)).Select(r => r.Key).ToListAsync());
            return 0;
        });
    }

    [RequiresDatabaseFact]
    public async Task A_retention_of_zero_days_keeps_that_data_forever()
    {
        var (orgId, _) = await fixture.SeedServerAsync();
        var marker = Guid.NewGuid().ToString("N");
        await fixture.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO audit_events (id, organization_id, actor_type, action, metadata, occurred_at) VALUES ({Guid.CreateVersion7()}, {orgId}, 'system', {"ancient." + marker}, {"{}"}::jsonb, {Now.AddYears(-5)})");
            return 0;
        });

        await Service(new MaintenanceOptions { AuditRetentionDays = 0, BackupEnabled = false, PruneEnabled = false }).RunOnceAsync(Now, force: false, default);

        Assert.Equal(1, await fixture.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "ancient." + marker)));
    }

    [RequiresDatabaseFact]
    public async Task The_backup_runs_when_due_then_not_again_until_its_interval_has_passed()
    {
        var backup = new FakeBackup();
        var service = Service(new MaintenanceOptions { PruneEnabled = false, BackupsKept = 3, BackupIntervalHours = 24 }, backup);
        // A separate "last backup" per run needs a clean state row.
        await fixture.WithDbAsync(async db => await db.Settings.Where(s => s.Key == MaintenanceService.StateKey).ExecuteDeleteAsync());

        var first = await service.RunOnceAsync(Now, force: false, default);
        var soon = await service.RunOnceAsync(Now.AddHours(2), force: false, default);
        var later = await service.RunOnceAsync(Now.AddHours(25), force: false, default);
        var manual = await service.RunOnceAsync(Now.AddHours(26), force: true, default);

        Assert.StartsWith("aethera-scheduled-", first.BackupTaken);
        Assert.Null(soon.BackupTaken);
        Assert.StartsWith("aethera-scheduled-", later.BackupTaken);
        Assert.StartsWith("aethera-manual-", manual.BackupTaken);
        Assert.Equal(["scheduled", "scheduled", "manual"], backup.Taken);
        Assert.Contains(("scheduled", 3), backup.Pruned);
        var state = await service.GetStateAsync(default);
        Assert.Equal(Now.AddHours(26), state.LastBackupAt);
        Assert.Equal(Now.AddHours(26), state.LastRun!.At);
    }

    [RequiresDatabaseFact]
    public async Task A_failing_backup_is_reported_and_does_not_stop_the_rest()
    {
        await fixture.WithDbAsync(async db => await db.Settings.Where(s => s.Key == MaintenanceService.StateKey).ExecuteDeleteAsync());
        var service = Service(new MaintenanceOptions { PruneEnabled = false }, new FakeBackup(fail: new BackupFailedException("pg_dump failed: connection refused")));

        var report = await service.RunOnceAsync(Now, force: false, default);

        Assert.Equal(["backup: pg_dump failed: connection refused"], report.Errors);
        Assert.Null(report.BackupTaken);
        Assert.Null((await service.GetStateAsync(default)).LastBackupAt); // not recorded, so the next pass tries again
    }

    [RequiresDatabaseFact]
    public async Task Without_pg_dump_the_backup_task_says_so()
    {
        await fixture.WithDbAsync(async db => await db.Settings.Where(s => s.Key == MaintenanceService.StateKey).ExecuteDeleteAsync());
        var report = await Service(new MaintenanceOptions { PruneEnabled = false }, new FakeBackup(available: false)).RunOnceAsync(Now, force: false, default);
        Assert.Contains(report.Errors, e => e.Contains("pg_dump is not installed"));
    }

    [RequiresDatabaseFact]
    public async Task Connected_servers_get_a_safe_prune_job_once_a_week_and_never_a_volume_prune()
    {
        var (orgId, serverId) = await fixture.SeedServerAsync();
        await fixture.WithDbAsync(async db => await db.Settings.Where(s => s.Key == MaintenanceService.StateKey).ExecuteDeleteAsync());
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        var service = Service(new MaintenanceOptions { BackupEnabled = false, PruneIntervalHours = 168, PruneKeepHours = 72 });

        var first = await service.RunOnceAsync(Now, force: false, default);
        var again = await service.RunOnceAsync(Now.AddDays(1), force: false, default);
        var nextWeek = await service.RunOnceAsync(Now.AddDays(8), force: false, default);

        Assert.True(first.PruneJobsQueued >= 1);
        Assert.Equal(0, again.PruneJobsQueued);
        Assert.True(nextWeek.PruneJobsQueued >= 1);
        var jobs = await fixture.WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.Type == "server.prune" && j.ResourceId == serverId).ToListAsync());
        Assert.Equal(2, jobs.Count);
        foreach (var job in jobs)
        {
            Assert.Equal(orgId, job.OrganizationId);
            Assert.Equal($"server:{serverId}:maintenance", job.LockKey);
            using var payload = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
            var root = payload.RootElement;
            Assert.False(root.GetProperty("volumes").GetBoolean());
            Assert.False(root.GetProperty("stoppedContainers").GetBoolean());
            Assert.True(root.GetProperty("danglingImages").GetBoolean());
            Assert.True(root.GetProperty("unusedImages").GetBoolean());
            Assert.True(root.GetProperty("buildCache").GetBoolean());
            Assert.Equal(72, root.GetProperty("olderThanHours").GetInt32());
        }
    }

    [RequiresDatabaseFact]
    public async Task Disabled_pruning_queues_nothing_even_when_forced()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        await Service(new MaintenanceOptions { BackupEnabled = false, PruneEnabled = false }).RunOnceAsync(Now, force: true, default);
        Assert.Equal(0, await fixture.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == "server.prune" && j.ResourceId == serverId)));
    }
}
