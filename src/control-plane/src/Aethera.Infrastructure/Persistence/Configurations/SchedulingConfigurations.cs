using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aethera.Infrastructure.Persistence.Configurations;

internal sealed class ScheduledTaskConfiguration : IEntityTypeConfiguration<ScheduledTask>
{
    public void Configure(EntityTypeBuilder<ScheduledTask> b)
    {
        b.ToTable("scheduled_tasks", t => t.HasCheckConstraint("ck_scheduled_tasks_timeout", "timeout_seconds BETWEEN 1 AND 86400"));
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Command).HasMaxLength(4000);
        b.Property(x => x.ComposeService).HasMaxLength(200);
        b.Property(x => x.Schedule).HasMaxLength(100);
        b.Property(x => x.TimeZone).HasMaxLength(100);
        b.HasOne(x => x.Workload).WithMany().HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.WorkloadId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.NextRunAt).HasFilter("enabled AND deleted_at IS NULL");
    }
}

internal sealed class ScheduledTaskRunConfiguration : IEntityTypeConfiguration<ScheduledTaskRun>
{
    public void Configure(EntityTypeBuilder<ScheduledTaskRun> b)
    {
        b.ToTable("scheduled_task_runs");
        b.Property(x => x.OutputTail).HasMaxLength(70000);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.TaskId, x.CreatedAt });
        b.HasIndex(x => x.WorkloadId);
        b.HasIndex(x => x.JobId);
    }
}

internal sealed class DnsProviderConfiguration : IEntityTypeConfiguration<DnsProvider>
{
    public void Configure(EntityTypeBuilder<DnsProvider> b)
    {
        b.ToTable("dns_providers");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Secret>().WithMany().HasForeignKey(x => x.ApiTokenSecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.ApiTokenSecretId);
    }
}
