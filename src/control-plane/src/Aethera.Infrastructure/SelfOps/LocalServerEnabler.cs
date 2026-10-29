using Aethera.Domain;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.SelfOps;

public enum LocalServerEnableResult
{
    /// <summary>The server record and a one-time join token exist; the waiting agent container has been handed the token.</summary>
    Requested,

    /// <summary>No agent container is waiting next to the control plane (it is not part of this installation, or not up yet).</summary>
    Unavailable,

    /// <summary>An earlier request has not been picked up yet.</summary>
    InProgress,

    /// <summary>The <c>local</c> server already has a connected agent.</summary>
    AlreadyConnected,

    /// <summary>The folder shared with the agent container is not writable.</summary>
    NotWritable,
}

/// <summary>
/// Makes the machine the control plane runs on a deployment server: the <c>local</c> server record (roles master and build), a
/// one-time join token and the hand-over of that token to the agent container that waits in the same compose project. Used by the
/// "Use this machine" button and, on a fresh installation, by <see cref="LocalServerAutoEnroller"/>.
/// </summary>
public sealed class LocalServerEnabler(
    AetheraDbContext db, UpdaterControl updater, IInternalCa ca, IOptions<AgentGatewayOptions> options, IClock clock, IAuditLog audit)
{
    /// <summary>The name of the server of the control plane's own machine.</summary>
    public const string ServerName = "local";

    /// <summary>Audit action written when a request is made: the auto-enroller does nothing once one exists, so deleting the server sticks.</summary>
    public const string RequestedAction = "system.local_server_requested";

    public async Task<LocalServerEnableResult> EnableAsync(Guid organizationId, Guid? userId, bool automatic, CancellationToken cancellationToken)
    {
        if (!updater.IsLocalAgentRequestable()) return LocalServerEnableResult.Unavailable;
        if (UpdaterControl.InFlight(await updater.GetLocalAgentStatusAsync(cancellationToken))) return LocalServerEnableResult.InProgress;

        var endpoint = options.Value.EffectivePublicEndpoint;
        var server = await db.Servers.FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.Name == ServerName, cancellationToken);
        if (server?.AgentStatus == AgentStatus.Connected) return LocalServerEnableResult.AlreadyConnected;
        if (server is null)
        {
            var host = endpoint.LastIndexOf(':') is var colon and > 0 ? endpoint[..colon] : endpoint;
            server = new Server { OrganizationId = organizationId, Name = ServerName, Host = host.ToLowerInvariant(), Roles = [ServerRole.Master, ServerRole.Build] };
            // The installer's public host is usually the machine's public IP: it is what automatic sslip.io domains are made of (ADR 0011).
            if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip)) server.PublicIp = ip.ToString();
            db.Servers.Add(server);
        }

        var (token, record) = JoinTokenService.Issue(db, server.Id, clock.UtcNow, TimeSpan.FromMinutes(30), userId);
        await audit.RecordAsync(RequestedAction, "server", server.Id, new { tokenId = record.Id, automatic }, cancellationToken); // saves the server and the token
        var info = await ca.GetPublicInfoAsync(cancellationToken);
        try
        {
            await updater.RequestLocalAgentAsync(token, info.FingerprintSha256, endpoint, ServerName, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocalServerEnableResult.NotWritable;
        }

        return LocalServerEnableResult.Requested;
    }
}
