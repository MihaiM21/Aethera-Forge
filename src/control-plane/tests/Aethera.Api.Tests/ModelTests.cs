using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests;

/// <summary>Database-free checks that the EF model and the committed migrations agree.</summary>
public sealed class ModelTests
{
    private static AetheraDbContext NewOfflineContext()
    {
        var sp = TestDatabaseFixture.BuildServices("Host=127.0.0.1;Port=1;Database=offline;Username=u;Password=p");
        return sp.GetRequiredService<AetheraDbContext>();
    }

    [Fact]
    public void ModelHasNoPendingChanges_SoMigrationsAreUpToDate()
    {
        using var db = NewOfflineContext();

        Assert.False(db.Database.HasPendingModelChanges(),
            "The EF model differs from the latest migration snapshot. Run: dotnet ef migrations add <Name> "
            + "--project src/Aethera.Infrastructure --startup-project src/Aethera.Infrastructure --output-dir Persistence/Migrations");
    }

    [Fact]
    public void InitialSchemaMigrationExists()
    {
        using var db = NewOfflineContext();

        Assert.Contains(db.Database.GetMigrations(), m => m.EndsWith("_InitialSchema", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryTableAndColumnIsSnakeCase()
    {
        using var db = NewOfflineContext();
        var offenders = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null) continue;
            if (table.Any(char.IsUpper)) offenders.Add(table);
            var id = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entity.GetSchema());
            offenders.AddRange(entity.GetProperties()
                .Select(p => p.GetColumnName(id))
                .Where(c => c is not null && c.Any(char.IsUpper))!);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void MutableAggregatesUseXminAsConcurrencyToken()
    {
        using var db = NewOfflineContext();
        foreach (var type in new[] { typeof(Domain.Project), typeof(Domain.Server), typeof(Domain.Workload), typeof(Domain.Deployment), typeof(Domain.Job) })
        {
            var property = db.Model.FindEntityType(type)!.FindProperty(nameof(Domain.MutableEntity.RowVersion))!;
            Assert.True(property.IsConcurrencyToken, type.Name);
            Assert.Equal("xmin", property.GetColumnName());
        }
    }

    [Fact]
    public void AuditEventsCannotBeModifiedThroughEf()
    {
        using var db = NewOfflineContext();
        var evt = new Domain.AuditEvent { Action = "project.created", OrganizationId = Guid.CreateVersion7() };
        db.Attach(evt);
        db.Entry(evt).State = EntityState.Modified;

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public void AddAetheraPersistence_FailsLazilyWhenConnectionStringMissing()
    {
        var services = new ServiceCollection();
        services.AddAetheraPersistence(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<AetheraDbContext>());
    }
}
