namespace Aethera.Domain;

public enum DnsProviderKind
{
    Cloudflare = 0,
}

/// <summary>
/// A DNS provider account (ADR 0010): ACME DNS-01 challenges (wildcards, certificates before the DNS switch) and DNS checks that see past a
/// CDN proxy. The API token is a managed secret (<see cref="SecretPurpose.DnsCredential"/>).
/// </summary>
public class DnsProvider : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }
    public DnsProviderKind Kind { get; set; } = DnsProviderKind.Cloudflare;
    public Guid ApiTokenSecretId { get; set; }

    /// <summary>Zones the token can see (refreshed by verification), e.g. <c>turnone.com</c>.</summary>
    public List<string> Zones { get; set; } = [];

    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string? LastError { get; set; }
}
