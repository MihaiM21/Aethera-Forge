using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aethera.Infrastructure.Persistence.Configurations;

/// <summary>Table-per-hierarchy: one <c>workloads</c> table, discriminator column <c>kind</c>.</summary>
internal sealed class WorkloadConfiguration : IEntityTypeConfiguration<Workload>
{
    public void Configure(EntityTypeBuilder<Workload> b)
    {
        b.ToTable("workloads", t =>
        {
            t.HasCheckConstraint("ck_workloads_application_columns",
                "kind <> 'application' OR (source_kind IS NOT NULL AND template_key IS NULL AND image IS NULL)");
            t.HasCheckConstraint("ck_workloads_service_columns",
                "kind <> 'service' OR (template_key IS NOT NULL AND image IS NOT NULL AND source_kind IS NULL)");
        });

        b.HasDiscriminator<string>("kind")
            .HasValue<Application>("application")
            .HasValue<Service>("service");
        b.Property<string>("kind").HasMaxLength(32);

        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Slug).HasMaxLength(Slug.MaxLength);

        b.HasOne(x => x.Environment).WithMany(x => x.Workloads).HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Deployment>().WithMany().HasForeignKey(x => x.CurrentDeploymentId).OnDelete(DeleteBehavior.SetNull);

        b.ComplexProperty(x => x.Runtime, r =>
        {
            r.Property(x => x.RestartPolicy).HasColumnName("restart_policy");
            r.Property(x => x.DeploymentStrategy).HasColumnName("deployment_strategy").HasMaxLength(64);
            r.Property(x => x.CpuLimit).HasColumnName("cpu_limit");
            r.Property(x => x.CpuReservation).HasColumnName("cpu_reservation");
            r.Property(x => x.MemoryLimitBytes).HasColumnName("memory_limit_bytes");
            r.Property(x => x.MemoryReservationBytes).HasColumnName("memory_reservation_bytes");
            r.Property(x => x.PidsLimit).HasColumnName("pids_limit");
            r.ComplexProperty(x => x.HealthCheck, h =>
            {
                h.Property(x => x.Type).HasColumnName("health_check_type");
                h.Property(x => x.Path).HasColumnName("health_check_path").HasMaxLength(500);
                h.Property(x => x.Port).HasColumnName("health_check_port");
                h.Property(x => x.IntervalSeconds).HasColumnName("health_check_interval_seconds");
                h.Property(x => x.TimeoutSeconds).HasColumnName("health_check_timeout_seconds");
                h.Property(x => x.Retries).HasColumnName("health_check_retries");
                h.Property(x => x.StartPeriodSeconds).HasColumnName("health_check_start_period_seconds");
            });
        });

        b.HasIndex(x => new { x.EnvironmentId, x.Slug }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.ServerId);
        b.HasIndex(x => x.CurrentDeploymentId);
    }
}

internal sealed class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> b)
    {
        b.Property(x => x.SourceKind);
        b.HasOne(x => x.GitSource).WithOne(x => x.Application).HasForeignKey<GitSource>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.BuildConfig).WithOne(x => x.Application).HasForeignKey<BuildConfig>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ImageSource).WithOne(x => x.Application).HasForeignKey<ImageSource>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ComposeSource).WithOne(x => x.Application).HasForeignKey<ComposeSource>(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> b)
    {
        b.Property(x => x.TemplateKey).HasMaxLength(100);
        b.Property(x => x.TemplateVersion).HasMaxLength(64);
        b.Property(x => x.Image).HasMaxLength(500);
        b.Property(x => x.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
    }
}

internal sealed class GitSourceConfiguration : IEntityTypeConfiguration<GitSource>
{
    public void Configure(EntityTypeBuilder<GitSource> b)
    {
        b.ToTable("git_sources");
        b.HasKey(x => x.ApplicationId);
        b.Property(x => x.RepositoryUrl).HasMaxLength(1000);
        b.Property(x => x.Branch).HasMaxLength(255);
        b.Property(x => x.CommitPin).HasMaxLength(64);
        b.HasOne(x => x.GitCredential).WithMany().HasForeignKey(x => x.GitCredentialId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.GitCredentialId);
        // Webhook receivers look applications up by repository.
        b.HasIndex(x => x.RepositoryUrl);
    }
}

internal sealed class BuildConfigConfiguration : IEntityTypeConfiguration<BuildConfig>
{
    public void Configure(EntityTypeBuilder<BuildConfig> b)
    {
        b.ToTable("build_configs");
        b.HasKey(x => x.ApplicationId);
        b.Property(x => x.Engine).HasMaxLength(64);
        b.Property(x => x.Context).HasMaxLength(500);
        b.Property(x => x.DockerfilePath).HasMaxLength(500);
        b.Property(x => x.OutputDirectory).HasMaxLength(500);
        b.Property(x => x.TargetPlatform).HasMaxLength(64);
        b.Property(x => x.BuildArgs).HasColumnType("jsonb");
    }
}

internal sealed class ImageSourceConfiguration : IEntityTypeConfiguration<ImageSource>
{
    public void Configure(EntityTypeBuilder<ImageSource> b)
    {
        b.ToTable("image_sources");
        b.HasKey(x => x.ApplicationId);
        b.Property(x => x.Image).HasMaxLength(500);
        b.Property(x => x.Tag).HasMaxLength(128);
        b.HasOne(x => x.Registry).WithMany().HasForeignKey(x => x.RegistryId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.RegistryId);
    }
}

internal sealed class ComposeSourceConfiguration : IEntityTypeConfiguration<ComposeSource>
{
    public void Configure(EntityTypeBuilder<ComposeSource> b)
    {
        b.ToTable("compose_sources", t => t.HasCheckConstraint(
            "ck_compose_sources_content", "file_path IS NOT NULL OR inline_content IS NOT NULL"));
        b.HasKey(x => x.ApplicationId);
        b.Property(x => x.FilePath).HasMaxLength(500);
    }
}

internal sealed class WorkloadPortConfiguration : IEntityTypeConfiguration<WorkloadPort>
{
    public void Configure(EntityTypeBuilder<WorkloadPort> b)
    {
        b.ToTable("workload_ports", t =>
        {
            t.HasCheckConstraint("ck_workload_ports_container_port", "container_port BETWEEN 1 AND 65535");
            t.HasCheckConstraint("ck_workload_ports_published_port", "published_port IS NULL OR published_port BETWEEN 1 AND 65535");
        });
        b.HasOne(x => x.Workload).WithMany(x => x.Ports).HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.WorkloadId, x.ContainerPort, x.Protocol }).IsUnique();
    }
}

internal sealed class EnvironmentVariableConfiguration : IEntityTypeConfiguration<EnvironmentVariable>
{
    public void Configure(EntityTypeBuilder<EnvironmentVariable> b)
    {
        b.ToTable("environment_variables", t => t.HasCheckConstraint(
            "ck_environment_variables_value_or_secret", "value IS NULL OR secret_id IS NULL"));
        b.Property(x => x.Key).HasMaxLength(255);
        b.HasOne(x => x.Workload).WithMany(x => x.EnvironmentVariables).HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Secret).WithMany().HasForeignKey(x => x.SecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.WorkloadId, x.Key }).IsUnique();
        b.HasIndex(x => x.SecretId);
    }
}

internal sealed class VolumeConfiguration : IEntityTypeConfiguration<Volume>
{
    public void Configure(EntityTypeBuilder<Volume> b)
    {
        b.ToTable("volumes");
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.MountPath).HasMaxLength(500);
        b.Property(x => x.HostPath).HasMaxLength(500);
        b.HasOne(x => x.Workload).WithMany(x => x.Volumes).HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.WorkloadId, x.MountPath }).IsUnique();
        b.HasIndex(x => new { x.WorkloadId, x.Name }).IsUnique();
    }
}

internal sealed class NetworkConfiguration : IEntityTypeConfiguration<Network>
{
    public void Configure(EntityTypeBuilder<Network> b)
    {
        b.ToTable("networks");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.DockerName).HasMaxLength(128);
        b.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Environment).WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ServerId, x.DockerName }).IsUnique();
        b.HasIndex(x => x.ProjectId);
        b.HasIndex(x => x.EnvironmentId);
    }
}

internal sealed class WorkloadNetworkConfiguration : IEntityTypeConfiguration<WorkloadNetwork>
{
    public void Configure(EntityTypeBuilder<WorkloadNetwork> b)
    {
        b.ToTable("workload_networks");
        b.HasKey(x => new { x.WorkloadId, x.NetworkId });
        b.HasOne(x => x.Workload).WithMany(x => x.Networks).HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Network).WithMany().HasForeignKey(x => x.NetworkId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.NetworkId);
    }
}

internal sealed class WorkloadDomainConfiguration : IEntityTypeConfiguration<WorkloadDomain>
{
    public void Configure(EntityTypeBuilder<WorkloadDomain> b)
    {
        b.ToTable("domains", t =>
            t.HasCheckConstraint("ck_domains_path_prefix", "path_prefix LIKE '/%'"));
        b.Property(x => x.Hostname).HasMaxLength(253);
        b.Property(x => x.PathPrefix).HasMaxLength(500);
        b.Property(x => x.CertificateError).HasMaxLength(1000);
        b.Property(x => x.ProxyRouteName).HasMaxLength(200);
        b.HasOne(x => x.Workload).WithMany(x => x.Domains).HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);

        // One live route per hostname + path prefix across the whole installation.
        b.HasIndex(x => new { x.Hostname, x.PathPrefix }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.WorkloadId);
        b.HasIndex(x => x.ServerId);
    }
}

internal sealed class ImageRecordConfiguration : IEntityTypeConfiguration<ImageRecord>
{
    public void Configure(EntityTypeBuilder<ImageRecord> b)
    {
        b.ToTable("images");
        b.Ignore(x => x.Reference);
        b.Property(x => x.Repository).HasMaxLength(500);
        b.Property(x => x.Tag).HasMaxLength(128);
        b.Property(x => x.Digest).HasMaxLength(128);
        b.HasOne(x => x.Workload).WithMany().HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Deployment).WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.WorkloadId, x.CreatedAt });
        b.HasIndex(x => x.DeploymentId);
        // Cleanup policies scan the live images of a server.
        b.HasIndex(x => new { x.ServerId, x.CreatedAt }).HasFilter("removed_at IS NULL");
    }
}

internal sealed class GitCredentialConfiguration : IEntityTypeConfiguration<GitCredential>
{
    public void Configure(EntityTypeBuilder<GitCredential> b)
    {
        b.ToTable("git_credentials");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Username).HasMaxLength(200);
        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Secret).WithMany().HasForeignKey(x => x.SecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.SecretId);
    }
}

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> b)
    {
        b.ToTable("webhook_endpoints");
        b.Property(x => x.BranchFilter).HasMaxLength(255);
        b.HasOne(x => x.Workload).WithMany().HasForeignKey(x => x.WorkloadId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Secret).WithMany().HasForeignKey(x => x.SecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.WorkloadId);
        b.HasIndex(x => x.SecretId);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("webhook_deliveries");
        b.Property(x => x.DeliveryId).HasMaxLength(255);
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.Ref).HasMaxLength(255);
        b.Property(x => x.CommitSha).HasMaxLength(64);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.HasOne(x => x.Endpoint).WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        // The provider's delivery id is the idempotency key of the delivery.
        b.HasIndex(x => new { x.EndpointId, x.DeliveryId }).IsUnique();
        b.HasIndex(x => new { x.EndpointId, x.ReceivedAt }).IsDescending(false, true);
    }
}
