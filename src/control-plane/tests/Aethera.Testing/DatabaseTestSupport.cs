using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aethera.Api.Tests;

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

/// <summary>Creates a fresh, fully migrated database for one test class and drops it afterwards.</summary>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    private NpgsqlConnectionStringBuilder? _admin;
    private ServiceProvider? _services;
    private string? _databaseName;

    public bool Enabled => _services is not null;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var raw = System.Environment.GetEnvironmentVariable(RequiresDatabaseFactAttribute.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw)) return;

        _admin = new NpgsqlConnectionStringBuilder(raw) { Pooling = false };
        _databaseName = "aethera_it_" + Guid.NewGuid().ToString("N");

        await using (var admin = new NpgsqlConnection(_admin.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(raw) { Database = _databaseName }.ConnectionString;
        _services = BuildServices(ConnectionString);

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AetheraDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_admin is null || _databaseName is null) return;

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_admin.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
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
