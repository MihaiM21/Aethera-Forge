using System.Threading.RateLimiting;
using Aethera.Agent.V1;
using Aethera.Domain;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Persistence;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Agents.Gateway;

/// <summary>Where the authenticated <see cref="AgentIdentity"/> is kept for the rest of the call.</summary>
internal static class GatewayContext
{
    public const string IdentityKey = "aethera.agent.identity";

    public static AgentIdentity Identity(this ServerCallContext context) =>
        context.GetHttpContext().Items[IdentityKey] as AgentIdentity
        ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "A valid client certificate is required."));

    public static string? RemoteIp(this ServerCallContext context) => context.GetHttpContext().Connection.RemoteIpAddress?.ToString();
}

/// <summary>
/// Per-source-IP fixed-window limit for <c>Enroll</c> (ADR 0002: 10 attempts per minute by default, <c>Aethera:Agents:EnrollRateLimitPermits</c>).
/// Every attempt counts, whatever its outcome, so token guessing is bounded.
/// </summary>
public sealed class EnrollRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public EnrollRateLimiter(IOptions<AgentGatewayOptions> options)
    {
        var settings = options.Value;
        _limiter = PartitionedRateLimiter.Create<string, string>(ip => RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, settings.EnrollRateLimitPermits),
            Window = TimeSpan.FromSeconds(Math.Max(0.05, settings.EnrollRateLimitWindowSeconds)),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
    }

    /// <summary>Takes a permit. Returns null when allowed, otherwise the seconds to wait.</summary>
    public int? TryAcquire(string? clientIp)
    {
        using var lease = _limiter.AttemptAcquire(clientIp ?? "unknown");
        if (lease.IsAcquired) return null;
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : 60;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Requires an authenticated agent identity for every method except <c>EnrollmentService/Enroll</c> (ADR 0002 "Listener and TLS").
/// Kestrel's handshake callback already proved the certificate chains to the Aethera CA and is a client-auth certificate; this authoritative
/// step looks the serial up in the database (known, not revoked, inside its validity, belongs to the server named in the SAN, server still
/// exists), so a revocation takes effect for every new call at once.
/// </summary>
public sealed class AgentAuthInterceptor(IServiceScopeFactory scopes, IClock clock, ILogger<AgentAuthInterceptor> logger) : Interceptor
{
    private static readonly string EnrollMethod = "/" + EnrollmentService.Descriptor.FullName + "/Enroll";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (context.Method != EnrollMethod) await AuthenticateAsync(context);
        return await continuation(request, context);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context);
        await continuation(requestStream, responseStream, context);
    }

    private async Task AuthenticateAsync(ServerCallContext context)
    {
        var http = context.GetHttpContext();
        var certificate = http.Connection.ClientCertificate;
        if (certificate is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "A client certificate is required."));
        if (!AgentIdentity.TryFromCertificate(certificate, out var identity)) throw new RpcException(new Status(StatusCode.Unauthenticated, "The client certificate is not an agent certificate."));

        var now = clock.UtcNow;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var record = await db.AgentCertificates.AsNoTracking()
            .Where(c => c.Serial == identity.Serial)
            .Select(c => new { c.ServerId, c.RevokedAt, c.NotBefore, c.NotAfter })
            .FirstOrDefaultAsync(context.CancellationToken);
        var serverExists = record is not null && await db.Servers.AsNoTracking().AnyAsync(s => s.Id == record.ServerId, context.CancellationToken);

        if (record is null || record.ServerId != identity.ServerId || record.RevokedAt is not null || now < record.NotBefore || now >= record.NotAfter || !serverExists)
        {
            logger.LogWarning("Rejected a client certificate for server {ServerId} (unknown, revoked, expired or its server is gone)", identity.ServerId);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "The client certificate is not valid."));
        }

        http.Items[GatewayContext.IdentityKey] = identity;
    }
}

/// <summary><c>AgentService</c>: the long-lived stream and certificate renewal. Both need the authenticated identity set by <see cref="AgentAuthInterceptor"/>.</summary>
public sealed class AgentGrpcService(AgentSessionHandler handler, AgentEnrollmentService enrollment) : AgentService.AgentServiceBase
{
    public override Task Connect(IAsyncStreamReader<AgentMessage> requestStream, IServerStreamWriter<ControlMessage> responseStream, ServerCallContext context) =>
        handler.RunAsync(context.Identity(), requestStream, responseStream, context.CancellationToken);

    public override async Task<RenewCertificateResponse> RenewCertificate(RenewCertificateRequest request, ServerCallContext context)
    {
        try
        {
            return await enrollment.RenewAsync(context.Identity(), request.CsrPem, context.CancellationToken);
        }
        catch (CsrValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (EnrollmentException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, EnrollmentException.DeniedMessage));
        }
    }
}

/// <summary><c>EnrollmentService/Enroll</c>: the only method reachable without a client certificate. A replayed, expired or unknown token is <c>PERMISSION_DENIED</c> with one fixed message.</summary>
public sealed class EnrollmentGrpcService(AgentEnrollmentService enrollment, EnrollRateLimiter limiter) : EnrollmentService.EnrollmentServiceBase
{
    public override async Task<EnrollResponse> Enroll(EnrollRequest request, ServerCallContext context)
    {
        var ip = context.RemoteIp();
        if (limiter.TryAcquire(ip) is { } retryAfter)
        {
            context.ResponseTrailers.Add("retry-after", retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Too many enrollment attempts. Try again later."));
        }

        try
        {
            return await enrollment.EnrollAsync(request, ip, context.CancellationToken);
        }
        catch (EnrollmentException ex)
        {
            throw new RpcException(new Status(ex.Failure switch
            {
                EnrollmentFailure.Denied => StatusCode.PermissionDenied,
                EnrollmentFailure.UpgradeRequired => StatusCode.FailedPrecondition,
                _ => StatusCode.InvalidArgument,
            }, ex.Message));
        }
        catch (CsrValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }
}
