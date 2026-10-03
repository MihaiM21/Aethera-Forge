using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aethera.Infrastructure.Agents.Pki;

/// <summary>Public facts about the CA: what agents pin and trust.</summary>
public sealed record CaPublicInfo(Guid Id, string CertificatePem, string FingerprintSha256, DateTimeOffset NotBefore, DateTimeOffset NotAfter);

/// <summary>A freshly issued agent certificate and its (not yet saved) database record.</summary>
public sealed record IssuedAgentCertificate(string CertificatePem, string CaChainPem, string Serial, string FingerprintSha256, DateTimeOffset NotBefore, DateTimeOffset NotAfter, AgentCertificate Record);

/// <summary>
/// The internal CA of ADR 0002: an ECDSA P-256 root generated on first start, its private key stored encrypted with the master key
/// (the same AES-256-GCM envelope as secrets). Issues 30-day agent leaf certificates, the listener's own server certificate, validates
/// presented client certificates against itself only, and revokes by serial.
/// </summary>
public interface IInternalCa
{
    /// <summary>Loads the CA, generating and storing it on the very first call of the very first start.</summary>
    Task<CaPublicInfo> GetPublicInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs <paramref name="csr"/> for <paramref name="serverId"/>: <c>CN = server id</c>, SAN URI <c>spiffe://aethera/server/&lt;id&gt;</c>, EKU
    /// <c>clientAuth</c>; the CSR's own subject and extensions are ignored. Adds the <see cref="AgentCertificate"/> record to
    /// <paramref name="db"/> (the caller saves, so issuing joins the caller's transaction).
    /// </summary>
    Task<IssuedAgentCertificate> IssueAgentCertificateAsync(AetheraDbContext db, Guid serverId, ValidatedCsr csr, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>A server certificate (EKU serverAuth, SAN = the given host names/IPs plus loopback) with its private key, for the gRPC listener.</summary>
    X509Certificate2 IssueServerCertificate(IEnumerable<string> hosts, DateTimeOffset now);

    /// <summary>
    /// The Kestrel <c>ClientCertificateValidation</c> callback: the chain must lead to this CA only (never the OS trust store), the
    /// certificate must be a valid <c>clientAuth</c> certificate with a well-formed agent identity, and its serial must not be revoked.
    /// </summary>
    bool IsTrustedClientCertificate(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors errors);

    /// <summary>Marks every live certificate of the server revoked and remembers the serials in memory. Returns how many were revoked.</summary>
    Task<int> RevokeServerCertificatesAsync(AetheraDbContext db, Guid serverId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Reloads the in-memory set of revoked serials from the database (other API instances revoke too).</summary>
    Task RefreshRevocationsAsync(CancellationToken cancellationToken = default);

    bool IsRevoked(string serial);

    void MarkRevoked(IEnumerable<string> serials);
}

public sealed class InternalCa : IInternalCa, IDisposable
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private readonly IServiceScopeFactory _scopes;
    private readonly ISecretProtector _protector;
    private readonly AgentGatewayOptions _options;
    private readonly ILogger<InternalCa> _logger;
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private Task<CaMaterial>? _material;

    public InternalCa(IServiceScopeFactory scopes, ISecretProtector protector, IOptions<AgentGatewayOptions> options, ILogger<InternalCa> logger)
    {
        _scopes = scopes;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
    }

    private sealed record CaMaterial(CaPublicInfo Info, X509Certificate2 Certificate, ECDsa Key);

    private Task<CaMaterial> Material
    {
        get
        {
            lock (_gate) return _material ??= LoadOrCreateAsync();
        }
    }

    public async Task<CaPublicInfo> GetPublicInfoAsync(CancellationToken cancellationToken = default) =>
        (await Material.WaitAsync(cancellationToken).ConfigureAwait(false)).Info;

    // ---- issuance --------------------------------------------------------------------------------------------------------------------

    public async Task<IssuedAgentCertificate> IssueAgentCertificateAsync(
        AetheraDbContext db, Guid serverId, ValidatedCsr csr, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var ca = await Material.WaitAsync(cancellationToken).ConfigureAwait(false);
        var uri = AgentIdentity.SubjectUri(serverId);

        var request = new CertificateRequest(new X500DistinguishedName($"CN={serverId:D}"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            csr.KeyDescription == "RSA" ? X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthOid)], critical: true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(uri));
        request.CertificateExtensions.Add(san.Build(critical: true));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca.Certificate, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var notBefore = now.AddMinutes(-5);
        var notAfter = now + _options.AgentCertificateLifetime;
        if (notAfter > ca.Info.NotAfter) notAfter = ca.Info.NotAfter;

        using var certificate = request.Create(ca.Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(ca.Key), notBefore, notAfter, NewSerial());
        var serial = certificate.SerialNumber;
        var fingerprint = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

        var record = new AgentCertificate
        {
            ServerId = serverId, CertificateAuthorityId = ca.Info.Id, Serial = serial, FingerprintSha256 = fingerprint, SubjectUri = uri,
            NotBefore = notBefore, NotAfter = notAfter, CreatedAt = now, UpdatedAt = now,
        };
        db.AgentCertificates.Add(record);
        return new IssuedAgentCertificate(certificate.ExportCertificatePem(), ca.Info.CertificatePem, serial, fingerprint, notBefore, notAfter, record);
    }

    public X509Certificate2 IssueServerCertificate(IEnumerable<string> hosts, DateTimeOffset now)
    {
        var ca = Material.GetAwaiter().GetResult();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=aethera-control-plane", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], critical: false));
        var san = new SubjectAlternativeNameBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts.Concat(["localhost", "127.0.0.1", "::1"]))
        {
            var name = host.Trim().Trim('[', ']');
            if (name.Length == 0 || !seen.Add(name)) continue;
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca.Certificate, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var notAfter = now.AddDays(_options.ServerCertificateDays);
        if (notAfter > ca.Info.NotAfter) notAfter = ca.Info.NotAfter;
        using var signed = request.Create(ca.Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(ca.Key), now.AddMinutes(-5), notAfter, NewSerial());
        using var withKey = signed.CopyWithPrivateKey(key);
        // Round-trip through PKCS#12: SChannel (Windows) refuses ephemeral keys for a server certificate; the key stays in memory only.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    // ---- validation and revocation ---------------------------------------------------------------------------------------------------

    public bool IsTrustedClientCertificate(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        try
        {
            var ca = Material.GetAwaiter().GetResult();
            using var ours = new X509Chain();
            ours.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            ours.ChainPolicy.CustomTrustStore.Add(ca.Certificate);
            ours.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            ours.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthOid));
            ours.ChainPolicy.DisableCertificateDownloads = true;
            if (!ours.Build(certificate)) return false;

            // Leaf directly under our root, nothing else.
            if (ours.ChainElements.Count != 2 || ours.ChainElements[^1].Certificate.Thumbprint != ca.Certificate.Thumbprint) return false;
            if (!HasClientAuthUsage(certificate)) return false;
            if (!AgentIdentity.TryFromCertificate(certificate, out _)) return false;
            return !IsRevoked(certificate.SerialNumber);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            _logger.LogWarning("Client certificate validation failed with an internal error: {Error}", ex.GetType().Name);
            return false;
        }
    }

    private static bool HasClientAuthUsage(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ClientAuthOid));

    public bool IsRevoked(string serial) => _revoked.ContainsKey(serial);

    public void MarkRevoked(IEnumerable<string> serials)
    {
        foreach (var serial in serials) _revoked[serial] = 0;
    }

    public async Task<int> RevokeServerCertificatesAsync(AetheraDbContext db, Guid serverId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var live = await db.AgentCertificates.Where(c => c.ServerId == serverId && c.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var certificate in live) certificate.Revoke(reason, now);
        // Remembered immediately so the next handshake on this instance fails; other instances pick it up on their refresh.
        MarkRevoked(live.Select(c => c.Serial));
        return live.Count;
    }

    public async Task RefreshRevocationsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var serials = await db.AgentCertificates.AsNoTracking().Where(c => c.RevokedAt != null).Select(c => c.Serial).ToListAsync(cancellationToken);
        MarkRevoked(serials);
    }

    // ---- creation / loading ----------------------------------------------------------------------------------------------------------

    private static string AssociatedData(Guid id) => $"certificate_authority:{id:D}";

    private async Task<CaMaterial> LoadOrCreateAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var row = await db.CertificateAuthorities.AsNoTracking().FirstOrDefaultAsync(c => c.IsActive);
            if (row is not null) return Load(row);

            var created = Create();
            db.CertificateAuthorities.Add(created);
            try
            {
                await db.SaveChangesAsync();
                _logger.LogInformation("Generated the internal CA {Fingerprint}", created.FingerprintSha256);
                return Load(created);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another instance generated it first: load that one.
            }
        }

        throw new InvalidOperationException("The internal CA could not be created or loaded.");
    }

    private CertificateAuthority Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = $"CN={_options.CaName}";
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));

        var id = Guid.CreateVersion7();
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            var protectedKey = _protector.Protect(pkcs8, AssociatedData(id));
            return new CertificateAuthority
            {
                Id = id, Name = _options.CaName, Subject = subject, CertificatePem = certificate.ExportCertificatePem(),
                FingerprintSha256 = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(),
                NotBefore = certificate.NotBefore.ToUniversalTime(), NotAfter = certificate.NotAfter.ToUniversalTime(),
                PrivateKeyCiphertext = protectedKey.Ciphertext, PrivateKeyNonce = protectedKey.Nonce,
                WrappedDataKey = protectedKey.WrappedDataKey!, WrappedDataKeyNonce = protectedKey.WrappedDataKeyNonce!,
                MasterKeyVersion = protectedKey.KeyVersion, IsActive = true, CreatedAt = now, UpdatedAt = now,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private CaMaterial Load(CertificateAuthority row)
    {
        var pkcs8 = _protector.Unprotect(
            new ProtectedValue(row.PrivateKeyCiphertext, row.PrivateKeyNonce, row.MasterKeyVersion)
            { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce },
            AssociatedData(row.Id));
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            var certificate = X509CertificateLoader.LoadCertificate(PemToDer(row.CertificatePem));
            var info = new CaPublicInfo(row.Id, row.CertificatePem, row.FingerprintSha256, row.NotBefore, row.NotAfter);
            return new CaMaterial(info, certificate, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static byte[] PemToDer(string pem) => PemEncoding.TryFind(pem, out var fields)
        ? Convert.FromBase64String(pem[fields.Base64Data])
        : throw new CryptographicException("The CA certificate is not valid PEM.");

    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x40); // positive, no leading zero byte
        return serial;
    }

    public void Dispose()
    {
        if (_material is { IsCompletedSuccessfully: true } done)
        {
            done.Result.Key.Dispose();
            done.Result.Certificate.Dispose();
        }
    }
}
