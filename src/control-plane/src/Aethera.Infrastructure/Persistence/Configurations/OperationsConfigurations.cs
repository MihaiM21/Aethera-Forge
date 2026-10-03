using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aethera.Infrastructure.Persistence.Configurations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.ToTable("jobs", t => t.HasCheckConstraint("ck_jobs_attempts", "attempt >= 0 AND max_attempts >= 1 AND retry_no >= 0"));
        b.Ignore(x => x.IsTerminal);
        b.Ignore(x => x.CancelRequested);
        b.Property(x => x.Type).HasMaxLength(100);
        b.Property(x => x.ResourceType).HasMaxLength(64);
        b.Property(x => x.LockKey).HasMaxLength(200);
        b.Property(x => x.LockedBy).HasMaxLength(200);
        b.Property(x => x.IdempotencyKey).HasMaxLength(255);
        b.Property(x => x.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.ResultJson).HasColumnName("result").HasColumnType("jsonb");
        b.Property(x => x.ErrorJson).HasColumnName("error").HasColumnType("jsonb");

        b.HasOne<Job>().WithMany().HasForeignKey(x => x.ParentJobId).OnDelete(DeleteBehavior.SetNull);
        // Tenancy: every visibility filter is organization_id = <actor org>; the list endpoint pages by created_at within it.
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt }).HasDatabaseName("ix_jobs_organization_created_at");

        // ADR 0004 claim query: WHERE status = 'queued' AND run_after <= now() ... ORDER BY priority DESC, run_after, id
        // FOR UPDATE SKIP LOCKED. A partial index on the queued rows keeps it tiny and ordered.
        b.HasIndex(x => new { x.Priority, x.RunAfter, x.Id })
            .HasDatabaseName("ix_jobs_claim")
            .IsDescending(true, false, false)
            .HasFilter("status = 'queued'");
        // Per-lock-key concurrency check (NOT EXISTS running job with the same key).
        b.HasIndex(x => x.LockKey).HasDatabaseName("ix_jobs_running_lock_key").HasFilter("status = 'running'");
        // Reaper: running jobs whose lease expired.
        b.HasIndex(x => x.LeaseExpiresAt).HasDatabaseName("ix_jobs_running_lease").HasFilter("status = 'running'");
        b.HasIndex(x => new { x.ResourceType, x.ResourceId, x.CreatedAt })
            .HasDatabaseName("ix_jobs_resource")
            .IsDescending(false, false, true);
        // Webhook delivery ids etc. must not enqueue twice.
        b.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        b.HasIndex(x => x.ParentJobId);
    }
}

internal sealed class LogChunkConfiguration : IEntityTypeConfiguration<LogChunk>
{
    public void Configure(EntityTypeBuilder<LogChunk> b)
    {
        b.ToTable("log_chunks");
        b.HasKey(x => new { x.StreamId, x.Sequence });
        b.Property(x => x.StreamId).HasMaxLength(200);
        b.Property(x => x.Timestamp).HasColumnName("ts");
        b.Property(x => x.Source).HasConversion<short>().HasColumnType("smallint");
        b.Property(x => x.Stream).HasConversion<short>().HasColumnType("smallint");
        b.Property(x => x.Data).HasColumnName("data");
        // Retention sweeps by age.
        b.HasIndex(x => x.Timestamp);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.Property(x => x.ActorLabel).HasMaxLength(320);
        b.Property(x => x.Action).HasMaxLength(100);
        b.Property(x => x.ResourceType).HasMaxLength(64);
        b.Property(x => x.ResourceName).HasMaxLength(255);
        b.Property(x => x.IpAddress).HasMaxLength(45);
        b.Property(x => x.UserAgent).HasMaxLength(512);
        b.Property(x => x.RequestId).HasMaxLength(100);
        b.Property(x => x.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.OrganizationId, x.OccurredAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.ResourceType, x.ResourceId });
        b.HasIndex(x => x.ActorUserId);
    }
}

internal sealed class ResourceEventConfiguration : IEntityTypeConfiguration<ResourceEvent>
{
    public void Configure(EntityTypeBuilder<ResourceEvent> b)
    {
        b.ToTable("resource_events");
        b.Property(x => x.ResourceType).HasMaxLength(64);
        b.Property(x => x.Kind).HasMaxLength(64);
        b.Property(x => x.Axis).HasMaxLength(32);
        b.Property(x => x.OldValue).HasMaxLength(64);
        b.Property(x => x.NewValue).HasMaxLength(64);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.HasIndex(x => new { x.ResourceType, x.ResourceId, x.OccurredAt }).IsDescending(false, false, true);
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_records");
        b.HasKey(x => new { x.PrincipalId, x.Key });
        b.Property(x => x.Key).HasMaxLength(255);
        b.Property(x => x.Method).HasMaxLength(16);
        b.Property(x => x.Path).HasMaxLength(1000);
        b.Property(x => x.ResponseHeadersJson).HasColumnName("response_headers").HasColumnType("jsonb");
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class InstanceSettingConfiguration : IEntityTypeConfiguration<InstanceSetting>
{
    public void Configure(EntityTypeBuilder<InstanceSetting> b)
    {
        b.ToTable("settings");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(200);
        b.Property(x => x.ValueJson).HasColumnName("value").HasColumnType("jsonb");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.UpdatedByUserId);
    }
}

internal sealed class MetricSampleConfiguration : IEntityTypeConfiguration<MetricSample>
{
    public void Configure(EntityTypeBuilder<MetricSample> b)
    {
        b.ToTable("metric_samples");
        b.Property(x => x.Id).ValueGeneratedOnAdd().UseIdentityByDefaultColumn();
        b.Property(x => x.ContainerId).HasMaxLength(128);
        b.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ServerId, x.Resolution, x.Timestamp });
        b.HasIndex(x => new { x.ServerId, x.ContainerId, x.Resolution, x.Timestamp }).HasFilter("container_id IS NOT NULL");
    }
}
