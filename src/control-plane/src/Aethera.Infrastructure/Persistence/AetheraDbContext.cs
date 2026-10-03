using System.Linq.Expressions;
using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aethera.Infrastructure.Persistence;

public sealed class AetheraDbContext(DbContextOptions<AetheraDbContext> options, TimeProvider? clock = null)
    : DbContext(options)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Identity & access
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    // Projects
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectEnvironment> Environments => Set<ProjectEnvironment>();

    // Servers
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<JoinToken> JoinTokens => Set<JoinToken>();
    public DbSet<CertificateAuthority> CertificateAuthorities => Set<CertificateAuthority>();
    public DbSet<AgentCertificate> AgentCertificates => Set<AgentCertificate>();

    // Workloads
    public DbSet<Workload> Workloads => Set<Workload>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<GitSource> GitSources => Set<GitSource>();
    public DbSet<BuildConfig> BuildConfigs => Set<BuildConfig>();
    public DbSet<ImageSource> ImageSources => Set<ImageSource>();
    public DbSet<ComposeSource> ComposeSources => Set<ComposeSource>();
    public DbSet<GitCredential> GitCredentials => Set<GitCredential>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<WorkloadPort> WorkloadPorts => Set<WorkloadPort>();
    public DbSet<EnvironmentVariable> EnvironmentVariables => Set<EnvironmentVariable>();
    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<Network> Networks => Set<Network>();
    public DbSet<WorkloadNetwork> WorkloadNetworks => Set<WorkloadNetwork>();
    public DbSet<WorkloadDomain> Domains => Set<WorkloadDomain>();
    public DbSet<ImageRecord> Images => Set<ImageRecord>();

    // Secrets & registries
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<SecretVersion> SecretVersions => Set<SecretVersion>();
    public DbSet<Registry> Registries => Set<Registry>();

    // Deployments
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentStepRun> DeploymentSteps => Set<DeploymentStepRun>();
    public DbSet<Build> Builds => Set<Build>();

    // Jobs
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<LogChunk> LogChunks => Set<LogChunk>();

    // Audit, events, monitoring, system
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ResourceEvent> ResourceEvents => Set<ResourceEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<InstanceSetting> Settings => Set<InstanceSetting>();
    public DbSet<MetricSample> MetricSamples => Set<MetricSample>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // timestamptz only accepts offset 0; normalise whatever offset callers supply.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Soft-delete query filters on a principal make "required" navigations from unfiltered dependents
        // look suspicious to EF. This is intentional (history rows keep pointing at deleted parents).
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AetheraDbContext).Assembly);
        ApplyEnumConversions(modelBuilder.Model);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => t.BaseType is null && !t.IsOwned()))
        {
            var clr = entityType.ClrType;

            if (typeof(Entity).IsAssignableFrom(clr))
                modelBuilder.Entity(clr).Property(nameof(Entity.Id)).ValueGeneratedNever();

            if (typeof(MutableEntity).IsAssignableFrom(clr))
            {
                // Postgres xmin system column as optimistic concurrency token.
                modelBuilder.Entity(clr).Property(nameof(MutableEntity.RowVersion)).IsRowVersion();
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clr))
                ApplySoftDeleteFilter(entityType);
        }
    }

    /// <summary>
    /// Every enum column (including enum elements of primitive collections and complex-type members) is stored as a
    /// camelCase string, unless a configuration chose another conversion explicitly (e.g. smallint log enums).
    /// </summary>
    private static void ApplyEnumConversions(IMutableModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
            ApplyEnumConversions(entityType);
    }

    private static void ApplyEnumConversions(IMutableTypeBase type)
    {
        foreach (var property in type.GetProperties())
        {
            if (property.GetValueConverter() is not null || property.GetProviderClrType() is not null) continue; // explicit conversion chosen

            if (property.IsPrimitiveCollection)
            {
                var element = property.GetElementType();
                if (element is not null && element.GetValueConverter() is null && TryCreateConverter(element.ClrType, out var elementConverter))
                    element.SetValueConverter(elementConverter);
            }
            else if (TryCreateConverter(property.ClrType, out var converter))
            {
                property.SetValueConverter(converter);
                property.SetMaxLength(32);
            }
        }

        foreach (var complex in type.GetComplexProperties())
            ApplyEnumConversions(complex.ComplexType);
    }

    private static bool TryCreateConverter(Type clrType, out ValueConverter converter)
    {
        var enumType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (!enumType.IsEnum)
        {
            converter = null!;
            return false;
        }
        converter = (ValueConverter)Activator.CreateInstance(typeof(CamelCaseEnumConverter<>).MakeGenericType(enumType))!;
        return true;
    }

    private static void ApplySoftDeleteFilter(IMutableEntityType entityType)
    {
        var parameter = Expression.Parameter(entityType.ClrType, "e");
        var deletedAt = Expression.Property(parameter, nameof(ISoftDeletable.DeletedAt));
        var body = Expression.Equal(deletedAt, Expression.Constant(null, typeof(DateTimeOffset?)));
        entityType.SetQueryFilter(Expression.Lambda(body, parameter));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        OnBeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        OnBeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void OnBeforeSave()
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case AuditEvent when entry.State is EntityState.Modified or EntityState.Deleted:
                    // Audit rows are append-only (retention deletes go through raw SQL / the retention job).
                    throw new InvalidOperationException("Audit events are append-only and cannot be modified or deleted through EF.");
                case MutableEntity when entry.State == EntityState.Modified:
                    entry.Property(nameof(MutableEntity.UpdatedAt)).CurrentValue = now;
                    break;
                case GitSource or BuildConfig or ImageSource or ComposeSource when entry.State == EntityState.Modified:
                    entry.Property("UpdatedAt").CurrentValue = now;
                    break;
            }
        }
    }
}
