using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Aethera.Infrastructure.SelfOps;
using Npgsql;

namespace Aethera.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "Aethera";
    public const string AutoMigrateKey = "Aethera:Database:AutoMigrate";
    public const string BackupBeforeMigrateKey = "Aethera:Database:BackupBeforeMigrate";

    /// <summary>
    /// Registers <see cref="AetheraDbContext"/> using <c>ConnectionStrings:Aethera</c>. The connection string is read
    /// lazily, so hosts that never touch the database (and tests) start without one.
    /// </summary>
    public static IServiceCollection AddAetheraPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        // One pooled data source for the whole process (also usable by the job runner's raw SQL / LISTEN connections).
        services.TryAddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    $"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");
            return CreateDataSource(connectionString);
        });

        services.AddDbContext<AetheraDbContext>((sp, options) => Configure(options, sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }

    /// <summary>Builds the Npgsql data source. Dynamic JSON is required for the jsonb-mapped dictionaries.</summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString) =>
        new NpgsqlDataSourceBuilder(connectionString).EnableDynamicJson().Build();

    internal static void Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsAssembly(typeof(AetheraDbContext).Assembly.GetName().Name))
            .UseSnakeCaseNamingConvention();

    /// <summary>
    /// Applies pending migrations when <c>Aethera:Database:AutoMigrate</c> is true (see <see cref="SafeMigrator"/>: a database newer than the
    /// code stops the start, an existing installation is backed up first unless <c>Aethera:Database:BackupBeforeMigrate</c> is false).
    /// Returns whether it ran.
    /// </summary>
    public static async Task<bool> MigrateAetheraDatabaseIfEnabledAsync(
        this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!bool.TryParse(configuration[AutoMigrateKey], out var autoMigrate) || !autoMigrate) return false;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("Aethera.Migrations");
        var maintenance = configuration.GetSection(MaintenanceOptions.Section).Get<MaintenanceOptions>() ?? new MaintenanceOptions();
        var backupEnabled = !bool.TryParse(configuration[BackupBeforeMigrateKey], out var flag) || flag;
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        var backup = backupEnabled && !string.IsNullOrWhiteSpace(connectionString) ? new DatabaseBackup(connectionString, maintenance.BackupDirectory, logger) : null;

        await SafeMigrator.MigrateAsync(db, backup, maintenance.PreMigrationBackupsKept, DateTimeOffset.UtcNow, logger, cancellationToken);
        return true;
    }
}
