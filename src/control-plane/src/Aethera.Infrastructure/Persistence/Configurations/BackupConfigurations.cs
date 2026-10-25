using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aethera.Infrastructure.Persistence.Configurations;

internal sealed class BackupDestinationConfiguration : IEntityTypeConfiguration<BackupDestination>
{
    public void Configure(EntityTypeBuilder<BackupDestination> b)
    {
        b.ToTable("backup_destinations", t =>
            t.HasCheckConstraint("ck_backup_destinations_s3",
                "kind <> 's3' OR (endpoint IS NOT NULL AND bucket IS NOT NULL AND access_key_id IS NOT NULL AND secret_access_key_secret_id IS NOT NULL)"));
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Endpoint).HasMaxLength(500);
        b.Property(x => x.Region).HasMaxLength(100);
        b.Property(x => x.Bucket).HasMaxLength(255);
        b.Property(x => x.PathPrefix).HasMaxLength(500);
        b.Property(x => x.AccessKeyId).HasMaxLength(255);
        b.Property(x => x.LastTestError).HasMaxLength(1000);
        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SecretAccessKeySecret).WithMany().HasForeignKey(x => x.SecretAccessKeySecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.SecretAccessKeySecretId);
    }
}

internal sealed class BackupPolicyConfiguration : IEntityTypeConfiguration<BackupPolicy>
{
    public void Configure(EntityTypeBuilder<BackupPolicy> b)
    {
        b.ToTable("backup_policies", t =>
        {
            t.HasCheckConstraint("ck_backup_policies_target",
                "(kind = 'controlPlane' AND workload_id IS NULL AND volume_id IS NULL) OR (kind = 'database' AND workload_id IS NOT NULL AND volume_id IS NULL) " +
                "OR (kind = 'volume' AND workload_id IS NOT NULL AND volume_id IS NOT NULL)");
            t.HasCheckConstraint("ck_backup_policies_retention", "keep_daily >= 0 AND keep_weekly >= 0 AND keep_monthly >= 0 AND keep_daily + keep_weekly + keep_monthly >= 1");
        });
        b.Property(x => x.Schedule).HasMaxLength(100);
        b.Property(x => x.TimeZone).HasMaxLength(100);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Workload).WithMany().HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Volume).WithMany().HasForeignKey(x => x.VolumeId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Destination).WithMany().HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Restrict);

        // One policy per workload and kind (per volume for volume policies); one control-plane policy per organization.
        b.HasIndex(x => new { x.OrganizationId, x.WorkloadId, x.Kind, x.VolumeId }).IsUnique().AreNullsDistinct(false);
        b.HasIndex(x => x.WorkloadId);
        b.HasIndex(x => x.VolumeId);
        b.HasIndex(x => x.DestinationId);
        b.HasIndex(x => x.NextRunAt).HasFilter("enabled");
    }
}

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> b)
    {
        b.ToTable("backup_runs");
        b.Property(x => x.Engine).HasMaxLength(32);
        b.Property(x => x.ObjectKey).HasMaxLength(1024);
        b.Property(x => x.StoredSha256).HasMaxLength(64);
        b.Property(x => x.KeyId).HasMaxLength(64);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<BackupDestination>().WithMany().HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.PolicyId, x.CreatedAt });
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.HasIndex(x => x.WorkloadId);
        b.HasIndex(x => x.DestinationId);
        b.HasIndex(x => x.JobId);
    }
}
