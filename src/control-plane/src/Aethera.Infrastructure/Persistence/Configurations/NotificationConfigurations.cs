using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aethera.Infrastructure.Persistence.Configurations;

internal sealed class EmailSettingsConfiguration : IEntityTypeConfiguration<EmailSettings>
{
    public void Configure(EntityTypeBuilder<EmailSettings> b)
    {
        b.ToTable("email_settings", t => t.HasCheckConstraint("ck_email_settings_port", "smtp_port IS NULL OR smtp_port BETWEEN 1 AND 65535"));
        b.Property(x => x.SmtpHost).HasMaxLength(253);
        b.Property(x => x.SmtpUsername).HasMaxLength(320);
        b.Property(x => x.FromAddress).HasMaxLength(320);
        b.Property(x => x.FromName).HasMaxLength(200);
        b.Property(x => x.LastTestError).HasMaxLength(1000);
        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Secret>().WithMany().HasForeignKey(x => x.SmtpPasswordSecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Secret>().WithMany().HasForeignKey(x => x.ResendApiKeySecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.OrganizationId).IsUnique();
        b.HasIndex(x => x.SmtpPasswordSecretId);
        b.HasIndex(x => x.ResendApiKeySecretId);
    }
}

internal sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    public void Configure(EntityTypeBuilder<NotificationChannel> b)
    {
        b.ToTable("notification_channels", t =>
            t.HasCheckConstraint("ck_notification_channels_webhook", "kind <> 'webhook' OR (webhook_url IS NOT NULL AND webhook_secret_id IS NOT NULL)"));
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.WebhookUrl).HasMaxLength(2000);
        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Secret>().WithMany().HasForeignKey(x => x.WebhookSecretId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ProjectEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.WebhookSecretId);
        b.HasIndex(x => x.ProjectId);
        b.HasIndex(x => x.EnvironmentId);
    }
}

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> b)
    {
        b.ToTable("alerts");
        b.Property(x => x.Type).HasMaxLength(100);
        b.Property(x => x.DedupKey).HasMaxLength(300);
        b.Property(x => x.ResourceType).HasMaxLength(100);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Message).HasMaxLength(4000);
        b.Property(x => x.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);

        // At most one open alert per key; resolved ones are history.
        b.HasIndex(x => new { x.OrganizationId, x.DedupKey }).IsUnique().HasFilter("state = 'open'");
        b.HasIndex(x => new { x.OrganizationId, x.State, x.OpenedAt });
        b.HasIndex(x => new { x.ResourceType, x.ResourceId });
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> b)
    {
        b.ToTable("notification_deliveries");
        b.Property(x => x.EventType).HasMaxLength(100);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.HasOne<NotificationChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.ChannelId, x.CreatedAt });
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        b.HasIndex(x => x.AlertId);
    }
}
