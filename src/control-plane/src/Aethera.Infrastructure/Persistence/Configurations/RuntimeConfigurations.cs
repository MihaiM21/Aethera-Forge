using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aethera.Infrastructure.Persistence.Configurations;

internal sealed class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> b)
    {
        b.ToTable("deployments", t => t.HasCheckConstraint("ck_deployments_number", "number >= 1"));
        b.Ignore(x => x.IsTerminal);
        b.Ignore(x => x.IsInProgress);
        b.Ignore(x => x.CanRollbackTo);
        b.Ignore(x => x.Duration);

        b.Property(x => x.Strategy).HasMaxLength(64);
        b.Property(x => x.FailureCode).HasMaxLength(100);
        b.Property(x => x.FailureReason).HasMaxLength(Deployment.MaxFailureReasonLength);
        b.Property(x => x.RepositoryUrl).HasMaxLength(1000);
        b.Property(x => x.Ref).HasMaxLength(255);
        b.Property(x => x.CommitSha).HasMaxLength(64);
        b.Property(x => x.CommitMessage).HasMaxLength(2000);
        b.Property(x => x.CommitAuthor).HasMaxLength(255);
        b.Property(x => x.ImageRef).HasMaxLength(1000);
        b.Property(x => x.ImageDigest).HasMaxLength(128);
        b.Property(x => x.ConfigSnapshotJson).HasColumnName("config_snapshot").HasColumnType("jsonb");
        b.Property(x => x.HealthCheckResultJson).HasColumnName("health_check").HasColumnType("jsonb");
        b.PrimitiveCollection(x => x.ContainerIds);

        b.HasOne(x => x.Workload).WithMany().HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ProjectEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.RollbackOfDeployment).WithMany().HasForeignKey(x => x.RollbackOfDeploymentId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.TriggeredByUserId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApiToken>().WithMany().HasForeignKey(x => x.TriggeredByApiTokenId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Build>().WithMany().HasForeignKey(x => x.BuildId).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(x => x.Builds).WithOne(x => x.Deployment).HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Steps).WithOne(x => x.Deployment).HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.WorkloadId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.WorkloadId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => x.EnvironmentId);
        b.HasIndex(x => new { x.ServerId, x.Status });
        b.HasIndex(x => x.RollbackOfDeploymentId);
        b.HasIndex(x => x.TriggeredByUserId);
        b.HasIndex(x => x.TriggeredByApiTokenId);
        b.HasIndex(x => x.JobId);
        b.HasIndex(x => x.BuildId);
        // "Which deployment is live" and the rollback picker.
        b.HasIndex(x => x.WorkloadId).HasDatabaseName("ix_deployments_running").HasFilter("status = 'running'");
        // Named overload: a second index over the same columns as the unique (workload, number) one.
        b.HasIndex(x => new { x.WorkloadId, x.Number }, "ix_deployments_rollback_points").HasFilter("is_rollback_point");
    }
}

internal sealed class DeploymentStepRunConfiguration : IEntityTypeConfiguration<DeploymentStepRun>
{
    public void Configure(EntityTypeBuilder<DeploymentStepRun> b)
    {
        b.ToTable("deployment_steps");
        b.HasKey(x => new { x.DeploymentId, x.Step });
        b.Property(x => x.ErrorCode).HasMaxLength(100);
        b.Property(x => x.ErrorMessage).HasMaxLength(Deployment.MaxFailureReasonLength);
        b.Property(x => x.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
    }
}

internal sealed class BuildEntityConfiguration : IEntityTypeConfiguration<Build>
{
    public void Configure(EntityTypeBuilder<Build> b)
    {
        b.ToTable("builds");
        b.Property(x => x.Engine).HasMaxLength(64);
        b.Property(x => x.Platform).HasMaxLength(64);
        b.Property(x => x.CommitSha).HasMaxLength(64);
        b.Property(x => x.ResultImage).HasMaxLength(1000);
        b.Property(x => x.ResultImageDigest).HasMaxLength(128);
        b.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.DeploymentId, x.Attempt }).IsUnique();
        b.HasIndex(x => x.JobId);
    }
}
