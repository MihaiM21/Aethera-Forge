using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aethera.Api.Tests;

/// <summary>The <c>AddJobOrganization</c> migration backfills existing jobs before the column becomes required.</summary>
public sealed class JobOrganizationMigrationTests
{
    private const string Before = "20261003143121_InitialSchema";

    [RequiresDatabaseFact]
    public async Task Backfill_UsesTheCreatorsMembership_ElseTheOldestOrganization()
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
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(Before);

            Guid oldest = Guid.CreateVersion7(), second = Guid.CreateVersion7(), user = Guid.CreateVersion7();
            var created = new[] { oldest, second }.Select((_, i) => DateTimeOffset.UtcNow.AddDays(-10 + i)).ToArray();
            await Sql(connectionString, $"""
                INSERT INTO organizations (id, name, slug, created_at, updated_at) VALUES
                  ('{oldest}', 'First', 'first', '{created[0]:O}', '{created[0]:O}'),
                  ('{second}', 'Second', 'second', '{created[1]:O}', '{created[1]:O}');
                INSERT INTO users (id, email, normalized_email, display_name, is_active, failed_login_count, created_at, updated_at)
                  VALUES ('{user}', 'u@example.com', 'U@EXAMPLE.COM', 'U', true, 0, now(), now());
                INSERT INTO organization_members (id, organization_id, user_id, role, created_at, updated_at)
                  VALUES ('{Guid.CreateVersion7()}', '{second}', '{user}', 'owner', now(), now());
                """);

            Guid byCreator = Guid.CreateVersion7(), system = Guid.CreateVersion7();
            foreach (var (id, creator) in new[] { (byCreator, $"'{user}'"), (system, "NULL") })
            {
                await Sql(connectionString, $$"""
                    INSERT INTO jobs (id, type, status, priority, payload, attempt, max_attempts, retry_no, run_after, created_by, created_at, updated_at)
                    VALUES ('{{id}}', 't.x', 'queued', 0, '{}', 0, 1, 0, now(), {{creator}}, now(), now());
                    """);
            }

            await migrator.MigrateAsync();

            Assert.Equal(second, await Scalar(connectionString, $"SELECT organization_id FROM jobs WHERE id = '{byCreator}'"));
            Assert.Equal(oldest, await Scalar(connectionString, $"SELECT organization_id FROM jobs WHERE id = '{system}'"));
            Assert.Equal("NO", await Scalar(connectionString,
                "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'jobs' AND column_name = 'organization_id'"));
            Assert.Equal("ix_jobs_organization_created_at", await Scalar(connectionString,
                "SELECT indexname FROM pg_indexes WHERE indexname = 'ix_jobs_organization_created_at'"));
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

    private static async Task<object?> Scalar(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
