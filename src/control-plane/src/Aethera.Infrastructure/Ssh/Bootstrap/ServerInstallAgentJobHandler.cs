using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Infrastructure.Ssh.Bootstrap;

/// <summary>Payload of <c>server.install_agent</c>: ids only. Credentials are read from the server's secret when the job runs.</summary>
public sealed class ServerInstallAgentPayload
{
    public Guid ServerId { get; set; }

    /// <summary>A host key fingerprint (<c>SHA256:...</c>) the user verified; it is pinned before the first command is sent.</summary>
    public string? HostKeyFingerprint { get; set; }
}

/// <summary>
/// <c>server.install_agent</c> (lock key <c>server:&lt;id&gt;:maintenance</c>): the SSH bootstrap of <see cref="SshBootstrapService"/> as a job
/// whose log shows each step. Not resumable: a crashed install is reported as failed and started again by the user (a new join token).
/// </summary>
public sealed class ServerInstallAgentJobHandler : IJobHandler
{
    public const string JobType = "server.install_agent";

    public string Type => JobType;

    public bool Resumable => false;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.GetPayload<ServerInstallAgentPayload>();
        var service = context.Services.GetRequiredService<SshBootstrapService>();
        Task Log(string line) => context.Log.WriteSystemAsync(line, cancellationToken).AsTask();

        try
        {
            var result = await service.InstallAsync(payload.ServerId, payload.HostKeyFingerprint, Log, context.Secrets, cancellationToken);
            context.SetResult(new { architecture = result.Architecture, binarySha256 = result.BinarySha256, version = result.InstalledVersion, sessionConnected = result.SessionConnected });
        }
        catch (SshBootstrapException ex)
        {
            await context.Log.WriteAsync(LogStream.Stderr, ex.Message, cancellationToken);
            throw JobFailedException.Permanent(ex.Code, "The agent could not be installed", ex.Message, "install", ex);
        }
        catch (ServerTransportException ex)
        {
            await context.Log.WriteAsync(LogStream.Stderr, ex.Message, cancellationToken);
            // Unreachable hosts may come back; a changed host key or refused credentials need a person.
            throw ex.Transient
                ? JobFailedException.Transient(ex.Code, "The server could not be reached over SSH", ex.Message, "connect", ex)
                : JobFailedException.Permanent(ex.Code, "The SSH connection was refused", ex.Message, "connect", ex);
        }
        catch (SshConnectException ex)
        {
            throw JobFailedException.Permanent(ex.Authentication ? SshErrors.AuthFailed : TransportErrors.Unreachable, "The SSH connection failed", ex.Message, "connect", ex);
        }
    }
}
