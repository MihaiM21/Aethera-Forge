using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aethera.Infrastructure.Persistence.Configurations;

internal sealed class SecretConfiguration : IEntityTypeConfiguration<Secret>
{
    public void Configure(EntityTypeBuilder<Secret> b)
    {
        b.ToTable("secrets", t => t.HasCheckConstraint(
            "ck_secrets_single_scope",
            "num_nonnulls(project_id, environment_id, workload_id) <= 1"));
        b.Ignore(x => x.Scope); // derived from the scope FKs
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ProjectEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Workload>().WithMany().HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);

        // Same name may exist at different scopes; within one scope it is unique among live secrets.
        b.HasIndex(x => new { x.OrganizationId, x.ProjectId, x.EnvironmentId, x.WorkloadId, x.Name })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.ProjectId);
        b.HasIndex(x => x.EnvironmentId);
        b.HasIndex(x => x.WorkloadId);
        b.HasMany(x => x.Versions).WithOne(x => x.Secret).HasForeignKey(x => x.SecretId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SecretVersionConfiguration : IEntityTypeConfiguration<SecretVersion>
{
    public void Configure(EntityTypeBuilder<SecretVersion> b)
    {
        b.ToTable("secret_versions", t => t.HasCheckConstraint("ck_secret_versions_version", "version >= 1"));
        b.HasKey(x => new { x.SecretId, x.Version });
    }
}

internal sealed class RegistryConfiguration : IEntityTypeConfiguration<Registry>
{
    public void Configure(EntityTypeBuilder<Registry> b)
    {
        b.ToTable("registries");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Url).HasMaxLength(500);
        b.Property(x => x.Username).HasMaxLength(200);
        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PasswordSecret).WithMany().HasForeignKey(x => x.PasswordSecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.PasswordSecretId);
    }
}
