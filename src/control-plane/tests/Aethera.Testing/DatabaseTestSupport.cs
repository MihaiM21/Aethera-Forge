using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aethera.Testing;

/// <summary>
/// Marks a test that needs a real PostgreSQL server. Set <c>AETHERA_TEST_DB</c> to a connection string for a role that may
/// CREATE DATABASE (each test class gets its own throw-away database, dropped afterwards). Skipped when unset.
/// </summary>
public sealed class RequiresDatabaseFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "AETHERA_TEST_DB";

    public RequiresDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"{EnvironmentVariable} is not set; point it at a Postgres server (e.g. "
                 + "\"Host=localhost;Port=5432;Username=aethera;Password=aethera;Database=postgres\") to run database tests.";
    }
}

/// <summary>
/// A throw-away, fully migrated PostgreSQL database named <c>aethera_it_&lt;guid&gt;</c>, created on the server that
/// <c>AETHERA_TEST_DB</c> points at and dropped on dispose.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly NpgsqlConnectionStringBuilder _admin;
    private readonly string _databaseName;

    private TestDatabase(NpgsqlConnectionStringBuilder admin, string databaseName, string connectionString)
    {
        _admin = admin;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(RequiresDatabaseFactAttribute.EnvironmentVariable));

    /// <summary>Connection string of the new database.</summary>
    public string ConnectionString { get; }

    /// <summary>Creates and migrates a database, or returns null when <c>AETHERA_TEST_DB</c> is not set.</summary>
    public static async Task<TestDatabase?> CreateAsync()
    {
        var raw = System.Environment.GetEnvironmentVariable(RequiresDatabaseFactAttribute.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var admin = new NpgsqlConnectionStringBuilder(raw) { Pooling = false };
        var name = "aethera_it_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        var database = new TestDatabase(admin, name, new NpgsqlConnectionStringBuilder(raw) { Database = name }.ConnectionString);
        await using var services = TestDatabaseFixture.BuildServices(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AetheraDbContext>().Database.MigrateAsync();
        return database;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_admin.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>Creates a fresh, fully migrated database for one test class and drops it afterwards.</summary>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    private TestDatabase? _database;
    private ServiceProvider? _services;

    public bool Enabled => _services is not null;

    public string ConnectionString => _database?.ConnectionString ?? "";

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        if (_database is not null) _services = BuildServices(_database.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_database is not null) await _database.DisposeAsync();
    }

    /// <summary>A new context on its own scope (own change tracker and connection).</summary>
    public ContextLease NewContext()
    {
        var scope = (_services ?? throw new InvalidOperationException("Database fixture is disabled.")).CreateScope();
        return new ContextLease(scope);
    }

    public static ServiceProvider BuildServices(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Aethera"] = connectionString })
            .Build();
        return new ServiceCollection().AddAetheraPersistence(configuration).BuildServiceProvider();
    }
}

public sealed class ContextLease(IServiceScope scope) : IDisposable
{
    public AetheraDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();

    public void Dispose() => scope.Dispose();
}

/// <summary><see cref="RequiresDatabaseFactAttribute"/> for theories.</summary>
public sealed class RequiresDatabaseTheoryAttribute : TheoryAttribute
{
    public RequiresDatabaseTheoryAttribute()
    {
        if (!TestDatabase.IsConfigured)
            Skip = $"{RequiresDatabaseFactAttribute.EnvironmentVariable} is not set; database tests are skipped.";
    }
}
