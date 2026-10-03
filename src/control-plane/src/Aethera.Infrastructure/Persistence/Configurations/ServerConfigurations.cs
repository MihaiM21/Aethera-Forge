using Aethera.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aethera.Infrastructure.Persistence.Configurations;

internal sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    public void Configure(EntityTypeBuilder<Server> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Host).HasMaxLength(253);
        b.Property(x => x.SshUser).HasMaxLength(64);
        b.Property(x => x.PublicIp).HasMaxLength(45);
        b.Property(x => x.AgentVersion).HasMaxLength(64);
        b.Property(x => x.CertFingerprint).HasMaxLength(128);
        b.Property(x => x.CertSerial).HasMaxLength(128);

        // Roles: Postgres text[] of enum names (Master/Build/Storage/Ci/Worker).
        b.PrimitiveCollection(x => x.Roles).ElementType().HasConversion<string>();

        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SshCredentialSecret).WithMany().HasForeignKey(x => x.SshCredentialSecretId).OnDelete(DeleteBehavior.NoAction);

        b.ComplexProperty(x => x.Facts, f =>
        {
            f.Property(x => x.Os).HasColumnName("os").HasMaxLength(100);
            f.Property(x => x.OsVersion).HasColumnName("os_version").HasMaxLength(100);
            f.Property(x => x.Kernel).HasColumnName("kernel").HasMaxLength(100);
            f.Property(x => x.Architecture).HasColumnName("architecture").HasMaxLength(32);
            f.Property(x => x.CpuModel).HasColumnName("cpu_model").HasMaxLength(200);
            f.Property(x => x.CpuCores).HasColumnName("cpu_cores");
            f.Property(x => x.MemoryBytes).HasColumnName("memory_bytes");
            f.Property(x => x.DiskBytes).HasColumnName("disk_bytes");
            f.Property(x => x.DockerVersion).HasColumnName("docker_version").HasMaxLength(64);
            f.Property(x => x.DiscoveredAt).HasColumnName("facts_discovered_at");
        });

        b.Property(x => x.SshHostKeyFingerprint).HasMaxLength(128);
        b.ToTable("servers", t =>
        {
            t.HasCheckConstraint("ck_servers_ssh_port", "ssh_port BETWEEN 1 AND 65535");
            t.HasCheckConstraint("ck_servers_max_concurrent_builds", "max_concurrent_builds >= 1");
        });
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.SshCredentialSecretId);
        b.HasIndex(x => x.CertSerial);
    }
}

internal sealed class JoinTokenConfiguration : IEntityTypeConfiguration<JoinToken>
{
    public void Configure(EntityTypeBuilder<JoinToken> b)
    {
        b.ToTable("join_tokens", t =>
        {
            t.HasCheckConstraint("ck_join_tokens_hash_len", $"octet_length(token_hash) = {JoinToken.HashLength}");
            t.HasCheckConstraint("ck_join_tokens_ttl", "expires_at > created_at AND expires_at <= created_at + interval '24 hours'");
        });
        b.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.ServerId);
        b.HasIndex(x => x.CreatedByUserId);
    }
}

internal sealed class CertificateAuthorityConfiguration : IEntityTypeConfiguration<CertificateAuthority>
{
    public void Configure(EntityTypeBuilder<CertificateAuthority> b)
    {
        b.ToTable("certificate_authorities");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Subject).HasMaxLength(500);
        b.Property(x => x.FingerprintSha256).HasMaxLength(64);
        b.Property(x => x.KeyAlgorithm).HasMaxLength(32);
        b.HasIndex(x => x.FingerprintSha256).IsUnique();
        // At most one active authority.
        b.HasIndex(x => x.IsActive).IsUnique().HasFilter("is_active");
    }
}

internal sealed class AgentCertificateConfiguration : IEntityTypeConfiguration<AgentCertificate>
{
    public void Configure(EntityTypeBuilder<AgentCertificate> b)
    {
        b.ToTable("agent_certificates");
        b.Property(x => x.Serial).HasMaxLength(128);
        b.Property(x => x.FingerprintSha256).HasMaxLength(64);
        b.Property(x => x.SubjectUri).HasMaxLength(300);
        b.Property(x => x.RevokedReason).HasMaxLength(500);
        b.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.CertificateAuthority).WithMany().HasForeignKey(x => x.CertificateAuthorityId).OnDelete(DeleteBehavior.Restrict);
        // The handshake validation callback looks certificates up by serial.
        b.HasIndex(x => x.Serial).IsUnique();
        b.HasIndex(x => x.ServerId);
        b.HasIndex(x => x.CertificateAuthorityId);
        b.HasIndex(x => x.NotAfter);
    }
}
