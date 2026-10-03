using Aethera.Agent.V1;
using Aethera.Domain;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Persistence;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.Agents.Enrollment;

/// <summary>Why <see cref="AgentEnrollmentService.EnrollAsync"/> refused.</summary>
public enum EnrollmentFailure
{
    /// <summary>The join token is unknown, expired, revoked or used. One answer for all of them: no oracle.</summary>
    Denied = 0,

    /// <summary>The request itself is malformed (missing field, bad CSR, unsupported key).</summary>
    Invalid = 1,

    /// <summary>The protocol revision is no longer supported.</summary>
    UpgradeRequired = 2,
}

public sealed class EnrollmentException(EnrollmentFailure failure, string message) : Exception(message)
{
    public EnrollmentFailure Failure { get; } = failure;

    /// <summary>The single message every denial carries, whatever the real reason.</summary>
    public const string DeniedMessage = "The join token is not valid.";
}

/// <summary>
/// <c>EnrollmentService/Enroll</c> (ADR 0002): consume the one-time join token atomically, sign the CSR with the internal CA, activate the
/// pre-created server. Token consumption and certificate issuance share one transaction, so a failure after the token was spent returns it.
/// </summary>
public sealed class AgentEnrollmentService(
    IServiceScopeFactory scopes, IInternalCa ca, IClock clock, IOptions<AgentGatewayOptions> options, AgentAudit audit, ILogger<AgentEnrollmentService> logger)
{
    private readonly AgentGatewayOptions _options = options.Value;

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, string? remoteIp, CancellationToken cancellationToken)
    {
        var token = request.JoinToken?.Value;
        if (!JoinTokenService.LooksLikeToken(token))
        {
            await audit.RecordAsync("agent.enroll_failed", "server", null, new { reason = "denied", ip = remoteIp }, AuditActorType.Agent, ipAddress: remoteIp, cancellationToken: cancellationToken);
            throw new EnrollmentException(EnrollmentFailure.Denied, EnrollmentException.DeniedMessage);
        }

        if (request.ProtocolVersion < (uint)_options.MinProtocolVersion)
            throw new EnrollmentException(EnrollmentFailure.UpgradeRequired, "The agent protocol version is no longer supported. Update the agent.");

        // The CSR is checked before the token: its validity says nothing about the token, so this is no oracle, and a malformed CSR does not burn a token.
        ValidatedCsr csr;
        try
        {
            csr = CsrValidator.Validate(request.CsrPem);
        }
        catch (CsrValidationException ex)
        {
            await audit.RecordAsync("agent.enroll_failed", "server", null, new { reason = "invalid_csr", ip = remoteIp }, AuditActorType.Agent, ipAddress: remoteIp, cancellationToken: cancellationToken);
            throw new EnrollmentException(EnrollmentFailure.Invalid, ex.Message);
        }

        var now = clock.UtcNow;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var serverId = await JoinTokenService.TryConsumeAsync(db, token!, now, cancellationToken);
        if (serverId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            await audit.RecordAsync("agent.enroll_failed", "server", null, new { reason = "denied", ip = remoteIp }, AuditActorType.Agent, ipAddress: remoteIp, cancellationToken: cancellationToken);
            throw new EnrollmentException(EnrollmentFailure.Denied, EnrollmentException.DeniedMessage);
        }

        var server = await db.Servers.FirstAsync(s => s.Id == serverId, cancellationToken);
        var issued = await ca.IssueAgentCertificateAsync(db, server.Id, csr, now, cancellationToken);

        server.CertSerial = issued.Serial;
        server.CertFingerprint = issued.FingerprintSha256;
        server.CertExpiresAt = issued.NotAfter;
        server.AgentVersion = Truncate(request.AgentVersion, 64);
        AgentFacts.Apply(server, request.Host, now);
        if (server.Lifecycle == ServerLifecycle.Pending) server.Lifecycle = ServerLifecycle.Active;
        if (server.AgentStatus is AgentStatus.NotInstalled) server.SetAgentStatus(AgentStatus.Unknown, now);
        db.ResourceEvents.Add(new ResourceEvent { ResourceType = "server", ResourceId = server.Id, Kind = "agent.enrolled", Detail = Truncate(request.AgentVersion, 64), OccurredAt = now });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync("agent.enrolled", "server", server.Id, new { serial = issued.Serial, notAfter = issued.NotAfter, agentVersion = server.AgentVersion, ip = remoteIp },
            AuditActorType.Agent, server.OrganizationId, ipAddress: remoteIp, cancellationToken: cancellationToken);
        logger.LogInformation("Server {ServerId} enrolled its agent (certificate {Serial})", server.Id, issued.Serial);

        var info = await ca.GetPublicInfoAsync(cancellationToken);
        return new EnrollResponse
        {
            ServerId = server.Id.ToString("D"), ClientCertificatePem = issued.CertificatePem, CaChainPem = issued.CaChainPem,
            CaFingerprintSha256 = info.FingerprintSha256, ControlPlaneEndpoint = _options.EffectivePublicEndpoint,
            CertificateNotAfter = Timestamp.FromDateTimeOffset(issued.NotAfter), RenewBefore = Duration.FromTimeSpan(_options.RenewBefore),
        };
    }

    /// <summary>
    /// <c>AgentService.RenewCertificate</c>: a fresh certificate for the caller's server (authenticated by its still-valid certificate), signed
    /// over a new key pair. The previous certificate stays valid until the agent reconnects with the new one, at which point the session
    /// handler revokes it (ADR 0002 "Renewal").
    /// </summary>
    public async Task<RenewCertificateResponse> RenewAsync(AgentIdentity identity, string? csrPem, CancellationToken cancellationToken)
    {
        var csr = CsrValidator.Validate(csrPem); // CsrValidationException -> INVALID_ARGUMENT in the gRPC layer
        var now = clock.UtcNow;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == identity.ServerId, cancellationToken)
            ?? throw new EnrollmentException(EnrollmentFailure.Denied, EnrollmentException.DeniedMessage);

        var issued = await ca.IssueAgentCertificateAsync(db, server.Id, csr, now, cancellationToken);
        server.CertSerial = issued.Serial;
        server.CertFingerprint = issued.FingerprintSha256;
        server.CertExpiresAt = issued.NotAfter;
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync("agent.certificate_renewed", "server", server.Id, new { serial = issued.Serial, notAfter = issued.NotAfter, previousSerial = identity.Serial },
            AuditActorType.Agent, server.OrganizationId, cancellationToken: cancellationToken);
        return new RenewCertificateResponse
        {
            ClientCertificatePem = issued.CertificatePem, CaChainPem = issued.CaChainPem, CertificateNotAfter = Timestamp.FromDateTimeOffset(issued.NotAfter),
        };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
