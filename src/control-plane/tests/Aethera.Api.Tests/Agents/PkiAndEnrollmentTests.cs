using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aethera.Agent.V1;
using Aethera.Domain;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Crypto;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Tests.Agents;

internal static class PkiHelpers
{
    public static (ECDsa Key, string CsrPem) NewCsr(string subject = "CN=agent") =>
        NewCsr(ECDsa.Create(ECCurve.NamedCurves.nistP256), subject);

    public static (ECDsa Key, string CsrPem) NewCsr(ECDsa key, string subject) =>
        (key, new CertificateRequest(subject, key, HashAlgorithmName.SHA256).CreateSigningRequestPem());

    /// <summary>The certificate with its private key, the way an agent holds it.</summary>
    public static X509Certificate2 WithKey(string certificatePem, ECDsa key)
    {
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        using var withKey = certificate.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    public static async Task<IssuedAgentCertificate> IssueAsync(GatewayFixture fixture, Guid serverId, ECDsa key, DateTimeOffset? now = null, string subject = "CN=agent")
    {
        var ca = fixture.Get<IInternalCa>();
        var csr = CsrValidator.Validate(NewCsr(key, subject).CsrPem);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var issued = await ca.IssueAgentCertificateAsync(db, serverId, csr, now ?? DateTimeOffset.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();
        return issued;
    }
}

[Collection(GatewayCollection.Name)]
public sealed class InternalCaTests(GatewayFixture fixture)
{
    [RequiresDatabaseFact]
    public async Task The_CA_is_an_ecdsa_p256_root_valid_for_ten_years()
    {
        var info = await fixture.Get<IInternalCa>().GetPublicInfoAsync();
        using var certificate = X509Certificate2.CreateFromPem(info.CertificatePem);

        Assert.Equal("1.2.840.10045.2.1", certificate.PublicKey.Oid.Value);
        using var ec = certificate.GetECDsaPublicKey()!;
        Assert.Equal(256, ec.KeySize);
        Assert.True(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.InRange((certificate.NotAfter - certificate.NotBefore).TotalDays, 3650, 3655);
        Assert.Equal(Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(), info.FingerprintSha256);
    }

    [RequiresDatabaseFact]
    public async Task The_CA_private_key_is_stored_encrypted_with_the_master_key_envelope()
    {
        var info = await fixture.Get<IInternalCa>().GetPublicInfoAsync();
        var row = await fixture.WithDbAsync(db => db.CertificateAuthorities.AsNoTracking().SingleAsync(c => c.IsActive));
        Assert.Equal(info.Id, row.Id);
        Assert.Equal(48, row.WrappedDataKey.Length); // 32-byte data key + 16-byte GCM tag: the AES-256-GCM envelope of secrets
        Assert.Equal(12, row.PrivateKeyNonce.Length);

        // The ciphertext is not a PKCS#8 structure (it would start with an ASN.1 SEQUENCE).
        Assert.NotEqual(0x30, row.PrivateKeyCiphertext[0]);

        // Decrypting with the envelope yields the key that belongs to the published certificate.
        var protector = fixture.Get<ISecretProtector>();
        var pkcs8 = protector.Unprotect(new ProtectedValue(row.PrivateKeyCiphertext, row.PrivateKeyNonce, row.MasterKeyVersion) { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce }, $"certificate_authority:{row.Id:D}");
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        using var certificate = X509Certificate2.CreateFromPem(row.CertificatePem);
        Assert.Equal(certificate.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo(), key.ExportSubjectPublicKeyInfo());

        // And it only decrypts for this CA record.
        Assert.Throws<CryptographicException>(() => protector.Unprotect(new ProtectedValue(row.PrivateKeyCiphertext, row.PrivateKeyNonce, row.MasterKeyVersion) { WrappedDataKey = row.WrappedDataKey, WrappedDataKeyNonce = row.WrappedDataKeyNonce }, "certificate_authority:other"));
    }

    [RequiresDatabaseFact]
    public async Task Concurrent_first_starts_create_exactly_one_CA()
    {
        await using var database = await TestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProtector>(_ => new AesGcmSecretProtector(new MasterKeyring(new Dictionary<int, byte[]> { [1] = MasterKeyring.TestKey }, 1)));
        services.AddAetheraPersistence(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Aethera"] = database!.ConnectionString }).Build());
        await using var provider = services.BuildServiceProvider();

        var instances = Enumerable.Range(0, 6)
            .Select(_ => new InternalCa(provider.GetRequiredService<IServiceScopeFactory>(), provider, Options.Create(new AgentGatewayOptions()), NullLogger<InternalCa>.Instance)).ToList();
        var infos = await Task.WhenAll(instances.Select(i => Task.Run(() => i.GetPublicInfoAsync())));

        Assert.Single(infos.Select(i => i.FingerprintSha256).Distinct());
        await using var scope = provider.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AetheraDbContext>().CertificateAuthorities.CountAsync());
        instances.ForEach(i => i.Dispose());
    }

    [RequiresDatabaseFact]
    public async Task A_leaf_has_the_server_identity_and_nothing_the_agent_asked_for()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var otherServer = Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // The agent asks for somebody else's identity in the subject; the CA ignores it.
        var issued = await PkiHelpers.IssueAsync(fixture, serverId, key, subject: $"CN={otherServer}, O=Evil");

        using var certificate = X509Certificate2.CreateFromPem(issued.CertificatePem);
        Assert.Equal(serverId.ToString("D"), certificate.GetNameInfo(X509NameType.SimpleName, false));
        Assert.True(AgentIdentity.TryFromCertificate(certificate, out var identity));
        Assert.Equal(serverId, identity.ServerId);
        Assert.Equal(issued.Serial, identity.Serial);

        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Equal(["1.3.6.1.5.5.7.3.2"], eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value!)); // clientAuth only
        Assert.False(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.InRange((certificate.NotAfter - DateTime.Now).TotalDays, 29, 31);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), certificate.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo()); // the agent's own key

        var row = await fixture.WithDbAsync(db => db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Serial == issued.Serial));
        Assert.Equal(serverId, row.ServerId);
        Assert.Equal(AgentIdentity.SubjectUri(serverId), row.SubjectUri);
        Assert.Null(row.RevokedAt);
    }

    [RequiresDatabaseFact]
    public async Task Only_valid_certificates_of_this_CA_are_trusted_as_client_certificates()
    {
        var ca = fixture.Get<IInternalCa>();
        var (_, serverId) = await fixture.SeedServerAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var good = await PkiHelpers.IssueAsync(fixture, serverId, key);
        using var goodCert = PkiHelpers.WithKey(good.CertificatePem, key);
        Assert.True(ca.IsTrustedClientCertificate(goodCert, null, System.Net.Security.SslPolicyErrors.None));

        // Expired: issued 40 days ago with a 30 day life.
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expired = await PkiHelpers.IssueAsync(fixture, serverId, oldKey, now: DateTimeOffset.UtcNow.AddDays(-40));
        using var expiredCert = X509Certificate2.CreateFromPem(expired.CertificatePem);
        Assert.False(ca.IsTrustedClientCertificate(expiredCert, null, System.Net.Security.SslPolicyErrors.None));

        // A certificate with the same shape from another CA.
        var foreignRoot = ForeignCa();
        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var foreign = SignLeaf(foreignRoot.Certificate, foreignRoot.Key, foreignKey, serverId, "1.3.6.1.5.5.7.3.2");
        Assert.False(ca.IsTrustedClientCertificate(foreign, null, System.Net.Security.SslPolicyErrors.None));

        // Self-signed with the right SAN.
        using var selfKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var self = SignLeaf(null, selfKey, selfKey, serverId, "1.3.6.1.5.5.7.3.2");
        Assert.False(ca.IsTrustedClientCertificate(self, null, System.Net.Security.SslPolicyErrors.None));

        // Revoked.
        ca.MarkRevoked([good.Serial]);
        Assert.False(ca.IsTrustedClientCertificate(goodCert, null, System.Net.Security.SslPolicyErrors.None));
    }

    [RequiresDatabaseFact]
    public async Task A_server_certificate_cannot_be_used_as_a_client_certificate()
    {
        var ca = fixture.Get<IInternalCa>();
        using var serverCert = ca.IssueServerCertificate(["control.example.com", "203.0.113.5"], DateTimeOffset.UtcNow);
        Assert.True(serverCert.HasPrivateKey);
        var san = serverCert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("control.example.com", san.EnumerateDnsNames());
        Assert.Contains(System.Net.IPAddress.Parse("203.0.113.5"), san.EnumerateIPAddresses());
        Assert.Contains(System.Net.IPAddress.Loopback, san.EnumerateIPAddresses());
        Assert.False(ca.IsTrustedClientCertificate(serverCert, null, System.Net.Security.SslPolicyErrors.None));
        await Task.CompletedTask;
    }

    [RequiresDatabaseFact]
    public async Task Revoking_a_server_marks_every_live_certificate_and_remembers_the_serials()
    {
        var ca = fixture.Get<IInternalCa>();
        var (_, serverId) = await fixture.SeedServerAsync();
        using var k1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var k2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var a = await PkiHelpers.IssueAsync(fixture, serverId, k1);
        var b = await PkiHelpers.IssueAsync(fixture, serverId, k2);

        var count = await fixture.WithDbAsync(async db =>
        {
            var n = await ca.RevokeServerCertificatesAsync(db, serverId, "test", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return n;
        });

        Assert.Equal(2, count);
        Assert.True(ca.IsRevoked(a.Serial));
        Assert.True(ca.IsRevoked(b.Serial));
        var rows = await fixture.WithDbAsync(db => db.AgentCertificates.AsNoTracking().Where(c => c.ServerId == serverId).ToListAsync());
        Assert.All(rows, r => Assert.Equal("test", r.RevokedReason));
    }

    private static (X509Certificate2 Certificate, ECDsa Key) ForeignCa()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Aethera Internal CA", key, HashAlgorithmName.SHA256); // same name, different key
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        return (request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)), key);
    }

    private static X509Certificate2 SignLeaf(X509Certificate2? issuer, ECDsa issuerKey, ECDsa leafKey, Guid serverId, string eku)
    {
        var request = new CertificateRequest($"CN={serverId:D}", leafKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(AgentIdentity.SubjectUri(serverId)));
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        if (issuer is null) return request.CreateSelfSigned(from, from.AddDays(30));
        return request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), from, from.AddDays(30), RandomNumberGenerator.GetBytes(8));
    }
}

public sealed class AgentIdentityTests
{
    [Fact]
    public void A_certificate_without_the_spiffe_san_has_no_identity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={Guid.NewGuid()}", key, HashAlgorithmName.SHA256);
        using var plain = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(AgentIdentity.TryFromCertificate(plain, out _));
    }

    [Fact]
    public void The_common_name_must_agree_with_the_san()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={Guid.NewGuid()}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(AgentIdentity.SubjectUri(Guid.NewGuid())));
        request.CertificateExtensions.Add(san.Build());
        using var mismatched = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(AgentIdentity.TryFromCertificate(mismatched, out _));
    }

    [Fact]
    public void A_foreign_uri_san_is_refused()
    {
        var id = Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={id}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(AgentIdentity.SubjectUri(id)));
        san.AddUri(new Uri("https://evil.example.com/x"));
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(AgentIdentity.TryFromCertificate(cert, out _));
    }
}

[Collection(GatewayCollection.Name)]
public sealed class JoinTokenTests(GatewayFixture fixture)
{
    private async Task<(Guid ServerId, string Token)> NewTokenAsync(TimeSpan? ttl = null, DateTimeOffset? now = null)
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var token = await fixture.WithDbAsync(async db =>
        {
            var (plaintext, _) = JoinTokenService.Issue(db, serverId, now ?? DateTimeOffset.UtcNow, ttl);
            await db.SaveChangesAsync();
            return plaintext;
        });
        return (serverId, token);
    }

    [Fact]
    public void Tokens_are_256_random_bits_with_the_documented_prefix()
    {
        var a = JoinTokenService.Generate();
        var b = JoinTokenService.Generate();
        Assert.StartsWith("aeth_join_", a);
        Assert.Equal("aeth_join_".Length + 43, a.Length); // 32 bytes, base64url without padding
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]+$", a["aeth_join_".Length..]);
    }

    [RequiresDatabaseFact]
    public async Task Only_the_hash_is_stored()
    {
        var (serverId, token) = await NewTokenAsync();
        var row = await fixture.WithDbAsync(db => db.JoinTokens.AsNoTracking().SingleAsync(t => t.ServerId == serverId));
        Assert.Equal(JoinTokenService.Hash(token), row.TokenHash);
        Assert.Equal(32, row.TokenHash.Length);
        Assert.DoesNotContain(token, System.Text.Encoding.UTF8.GetString(row.TokenHash));
    }

    [RequiresDatabaseFact]
    public async Task A_token_is_consumed_exactly_once_even_under_a_race()
    {
        var (serverId, token) = await NewTokenAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => Task.Run(() => fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, token, DateTimeOffset.UtcNow, CancellationToken.None)))));

        Assert.Equal(1, results.Count(r => r is not null));
        Assert.Equal(serverId, results.Single(r => r is not null));
        var row = await fixture.WithDbAsync(db => db.JoinTokens.AsNoTracking().SingleAsync(t => t.ServerId == serverId));
        Assert.NotNull(row.UsedAt);
    }

    [RequiresDatabaseFact]
    public async Task Replayed_expired_revoked_unknown_and_malformed_tokens_are_all_refused()
    {
        var now = DateTimeOffset.UtcNow;
        var (_, used) = await NewTokenAsync();
        Assert.NotNull(await fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, used, now, CancellationToken.None)));
        Assert.Null(await fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, used, now, CancellationToken.None)));

        var (_, expired) = await NewTokenAsync(ttl: TimeSpan.FromMinutes(5), now: now.AddHours(-2));
        Assert.Null(await fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, expired, now, CancellationToken.None)));

        var (revokedServer, revoked) = await NewTokenAsync();
        await fixture.WithDbAsync(async db =>
        {
            (await db.JoinTokens.SingleAsync(t => t.ServerId == revokedServer)).Revoke(now);
            return await db.SaveChangesAsync();
        });
        Assert.Null(await fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, revoked, now, CancellationToken.None)));

        Assert.Null(await fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, JoinTokenService.Generate(), now, CancellationToken.None)));
        Assert.Null(await fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, "not-a-token", now, CancellationToken.None)));
        Assert.Null(await fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, "aeth_join_" + new string('A', 500), now, CancellationToken.None)));
    }

    [RequiresDatabaseFact]
    public async Task A_token_of_a_deleted_server_is_refused()
    {
        var (serverId, token) = await NewTokenAsync();
        await fixture.WithDbAsync(async db =>
        {
            (await db.Servers.SingleAsync(s => s.Id == serverId)).MarkDeleted(DateTimeOffset.UtcNow);
            return await db.SaveChangesAsync();
        });
        Assert.Null(await fixture.WithDbAsync(db => JoinTokenService.TryConsumeAsync(db, token, DateTimeOffset.UtcNow, CancellationToken.None)));
    }

    [Fact]
    public void The_lifetime_is_capped_at_24_hours()
    {
        Assert.Throws<DomainRuleException>(() => JoinToken.Issue(Guid.NewGuid(), new byte[32], DateTimeOffset.UtcNow, TimeSpan.FromHours(25)));
        Assert.Throws<DomainRuleException>(() => JoinToken.Issue(Guid.NewGuid(), new byte[32], DateTimeOffset.UtcNow, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromHours(1), JoinToken.DefaultTtl);
        var token = JoinToken.Issue(Guid.NewGuid(), new byte[32], DateTimeOffset.UtcNow, TimeSpan.FromHours(24));
        Assert.NotNull(token);
    }
}

[Collection(GatewayCollection.Name)]
public sealed class EnrollmentTests(GatewayFixture fixture)
{
    private AgentEnrollmentService Service => fixture.Get<AgentEnrollmentService>();

    private async Task<(Guid ServerId, Guid OrganizationId, string Token)> PendingServerAsync(DateTimeOffset? now = null, TimeSpan? ttl = null)
    {
        var (org, serverId) = await fixture.SeedServerAsync();
        var token = await fixture.WithDbAsync(async db =>
        {
            var (plaintext, _) = JoinTokenService.Issue(db, serverId, now ?? DateTimeOffset.UtcNow, ttl);
            await db.SaveChangesAsync();
            return plaintext;
        });
        return (serverId, org, token);
    }

    private static EnrollRequest Request(string token, string csr, uint protocol = 1) => new()
    {
        JoinToken = new Aethera.Agent.V1.SecretValue { Value = token }, AgentVersion = "0.1.0", ProtocolVersion = protocol, CsrPem = csr,
        Host = new HostFacts { Hostname = "box", OsName = "Ubuntu", OsVersion = "24.04", KernelVersion = "6.8", Architecture = "amd64", CpuModel = "Xeon", CpuCoresLogical = 4, MemoryTotalBytes = 8L << 30 },
    };

    [RequiresDatabaseFact]
    public async Task Enrolling_signs_the_csr_and_activates_the_pending_server()
    {
        var (serverId, org, token) = await PendingServerAsync();
        var (key, csr) = PkiHelpers.NewCsr();
        using var _ = key;

        var response = await Service.EnrollAsync(Request(token, csr), "198.51.100.7", CancellationToken.None);

        Assert.Equal(serverId.ToString("D"), response.ServerId);
        Assert.Equal("localhost:9443", response.ControlPlaneEndpoint);
        Assert.Equal(TimeSpan.FromDays(10), response.RenewBefore.ToTimeSpan());
        var info = await fixture.Get<IInternalCa>().GetPublicInfoAsync();
        Assert.Equal(info.FingerprintSha256, response.CaFingerprintSha256);
        Assert.Equal(info.CertificatePem, response.CaChainPem);

        using var cert = X509Certificate2.CreateFromPem(response.ClientCertificatePem);
        Assert.True(AgentIdentity.TryFromCertificate(cert, out var identity));
        Assert.Equal(serverId, identity.ServerId);

        var server = await fixture.LoadServerAsync(serverId);
        Assert.Equal(ServerLifecycle.Active, server.Lifecycle);
        Assert.Equal(identity.Serial, server.CertSerial);
        Assert.Equal("0.1.0", server.AgentVersion);
        Assert.Equal("Ubuntu", server.Facts.Os);
        Assert.Equal(4, server.Facts.CpuCores);
        Assert.Equal(8L << 30, server.Facts.MemoryBytes);

        var events = await fixture.WithDbAsync(db => db.ResourceEvents.AsNoTracking().Where(e => e.ResourceId == serverId && e.Kind == "agent.enrolled").ToListAsync());
        Assert.Single(events);
        var audit = await fixture.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.ResourceId == serverId && e.Action == "agent.enrolled").ToListAsync());
        var entry = Assert.Single(audit);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(AuditActorType.Agent, entry.ActorType);
        Assert.DoesNotContain(token, entry.MetadataJson);
        Assert.Equal("198.51.100.7", entry.IpAddress);
    }

    [RequiresDatabaseFact]
    public async Task A_token_works_once_and_a_replay_is_denied()
    {
        var (_, _, token) = await PendingServerAsync();
        using var key1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var key2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Service.EnrollAsync(Request(token, PkiHelpers.NewCsr(key1, "CN=a").CsrPem), null, CancellationToken.None);
        var replay = await Assert.ThrowsAsync<EnrollmentException>(() => Service.EnrollAsync(Request(token, PkiHelpers.NewCsr(key2, "CN=a").CsrPem), null, CancellationToken.None));
        Assert.Equal(EnrollmentFailure.Denied, replay.Failure);
    }

    [RequiresDatabaseFact]
    public async Task Replayed_expired_and_unknown_tokens_get_the_identical_answer()
    {
        var (_, _, used) = await PendingServerAsync();
        var (_, _, expired) = await PendingServerAsync(now: DateTimeOffset.UtcNow.AddHours(-3), ttl: TimeSpan.FromMinutes(10));
        using var k0 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Service.EnrollAsync(Request(used, PkiHelpers.NewCsr(k0, "CN=a").CsrPem), null, CancellationToken.None);

        var messages = new List<string>();
        foreach (var token in new[] { used, expired, JoinTokenService.Generate(), "garbage" })
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var ex = await Assert.ThrowsAsync<EnrollmentException>(() => Service.EnrollAsync(Request(token, PkiHelpers.NewCsr(key, "CN=a").CsrPem), null, CancellationToken.None));
            Assert.Equal(EnrollmentFailure.Denied, ex.Failure);
            messages.Add(ex.Message);
        }

        Assert.Single(messages.Distinct()); // no oracle
        Assert.Equal(EnrollmentException.DeniedMessage, messages[0]);
    }

    [RequiresDatabaseFact]
    public async Task A_bad_csr_is_refused_without_burning_the_token()
    {
        var (_, _, token) = await PendingServerAsync();
        var bad = await Assert.ThrowsAsync<EnrollmentException>(() => Service.EnrollAsync(Request(token, "not a csr"), null, CancellationToken.None));
        Assert.Equal(EnrollmentFailure.Invalid, bad.Failure);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ok = await Service.EnrollAsync(Request(token, PkiHelpers.NewCsr(key, "CN=a").CsrPem), null, CancellationToken.None);
        Assert.NotEmpty(ok.ClientCertificatePem);
    }

    [RequiresDatabaseFact]
    public async Task An_unsupported_protocol_version_asks_for_an_upgrade()
    {
        var (_, _, token) = await PendingServerAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ex = await Assert.ThrowsAsync<EnrollmentException>(() => Service.EnrollAsync(Request(token, PkiHelpers.NewCsr(key, "CN=a").CsrPem, protocol: 0), null, CancellationToken.None));
        Assert.Equal(EnrollmentFailure.UpgradeRequired, ex.Failure);
    }

    [RequiresDatabaseFact]
    public async Task Failures_are_audited_without_the_token()
    {
        await PendingServerAsync(); // makes sure an organization exists
        var token = JoinTokenService.Generate();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<EnrollmentException>(() => Service.EnrollAsync(Request(token, PkiHelpers.NewCsr(key, "CN=a").CsrPem), "203.0.113.99", CancellationToken.None));

        await GatewayFixture.EventuallyAsync(async () =>
        {
            var rows = await fixture.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.Action == "agent.enroll_failed" && e.IpAddress == "203.0.113.99").ToListAsync());
            return rows.Count > 0 && rows.All(r => !r.MetadataJson.Contains(token) && r.ActorType == AuditActorType.Agent);
        }, "an agent.enroll_failed audit event");
    }

    [RequiresDatabaseFact]
    public async Task Renewal_issues_a_fresh_certificate_for_the_same_server()
    {
        var (serverId, _, token) = await PendingServerAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enrolled = await Service.EnrollAsync(Request(token, PkiHelpers.NewCsr(key, "CN=a").CsrPem), null, CancellationToken.None);
        using var oldCert = X509Certificate2.CreateFromPem(enrolled.ClientCertificatePem);
        AgentIdentity.TryFromCertificate(oldCert, out var identity);

        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var renewed = await Service.RenewAsync(identity, PkiHelpers.NewCsr(newKey, "CN=b").CsrPem, CancellationToken.None);

        using var newCert = X509Certificate2.CreateFromPem(renewed.ClientCertificatePem);
        Assert.NotEqual(oldCert.SerialNumber, newCert.SerialNumber);
        Assert.True(AgentIdentity.TryFromCertificate(newCert, out var renewedIdentity));
        Assert.Equal(serverId, renewedIdentity.ServerId);
        Assert.Equal(newKey.ExportSubjectPublicKeyInfo(), newCert.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());
        Assert.Equal(renewedIdentity.Serial, (await fixture.LoadServerAsync(serverId)).CertSerial);
        // The previous certificate stays valid until the agent reconnects with the new one.
        var old = await fixture.WithDbAsync(db => db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Serial == identity.Serial));
        Assert.Null(old.RevokedAt);
    }
}
