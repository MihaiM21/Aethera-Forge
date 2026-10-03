using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aethera.Infrastructure.Persistence;

/// <summary>Design-time factory used by <c>dotnet ef</c>. Reads <c>AETHERA_DB</c>, defaulting to the dev compose database.</summary>
public sealed class AetheraDbContextFactory : IDesignTimeDbContextFactory<AetheraDbContext>
{
    public const string DefaultConnectionString = "Host=localhost;Database=aethera;Username=aethera;Password=aethera";

    public AetheraDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("AETHERA_DB");
        if (string.IsNullOrWhiteSpace(connectionString)) connectionString = DefaultConnectionString;

        var options = new DbContextOptionsBuilder<AetheraDbContext>();
        // The data source is intentionally not disposed: the CLI process is short-lived.
        PersistenceServiceCollectionExtensions.Configure(options, PersistenceServiceCollectionExtensions.CreateDataSource(connectionString));
        return new AetheraDbContext(options.Options);
    }
}
