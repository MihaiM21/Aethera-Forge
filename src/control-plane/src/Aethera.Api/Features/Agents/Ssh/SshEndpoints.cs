using System.Diagnostics;
using Aethera.Api.Features.Auth;
using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Features.Resources.Servers;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Bootstrap;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Agents.Ssh;

/// <summary>
/// The SSH side of <c>/servers</c> (WP2.3, ADR 0002 "SshTransport"): add a server by SSH, install the agent over SSH (a job), the host key
/// pin (trust on first use, confirm a change), the per-server fallback switch and a connection test. No endpoint ever returns the stored
/// credential.
/// </summary>
internal static class SshEndpoints
{
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder api)
    {
        var servers = api.MapGroup("/servers").WithTags("Servers");

        servers.MapPost("/ssh", AddServer).WithName("addSshServer")
            .WithSummary("Add a server that Aethera reaches over SSH, optionally installing the agent")
            .WithDescription("Stores the private key or password as an encrypted secret that belongs to the server (it is never returned). With `installAgent` (default) a `server.install_agent` job is enqueued. A supplied `hostKeyFingerprint` is pinned before the first connection; otherwise the first key is pinned (trust on first use).")
            .Validate<AddSshServerRequest>().AddEndpointFilter<NoStoreFilter>().RequireAdmin(Scopes.ServersWrite)
            .Produces<AddSshServerResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status409Conflict);
        servers.MapPost("/ssh/host-key", ScanHostKey).WithName("scanSshHostKey")
            .WithSummary("Read the SSH host key a host presents (nothing is authenticated or stored)")
            .WithDescription("Lets the user compare the fingerprint with the one shown by the provider before trusting it. Pass it back as `hostKeyFingerprint` when adding the server.")
            .Validate<ScanHostKeyRequest>().RequireAdmin(Scopes.ServersWrite)
            .Produces<HostKeyResponse>().ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        servers.MapPost("/{id:guid}/install-agent", InstallAgent).WithName("installServerAgent")
            .WithSummary("Install and enrol the agent over SSH (a job)")
            .WithDescription("Enqueues `server.install_agent`: verifies the host key, puts the agent binary on the host and checks its SHA-256, writes the systemd unit and `agent.yaml`, enrols with a fresh join token passed on standard input, starts the service and waits for the agent's session. The job log shows each step.")
            .Validate<InstallAgentRequest>().RequireAdmin(Scopes.ServersWrite)
            .Produces<JobDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        servers.MapGet("/{id:guid}/ssh", GetState).WithName("getServerSsh")
            .WithSummary("SSH endpoint, host key pin, fallback switch and polling state of a server")
            .RequireRead().Produces<SshStateResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapPatch("/{id:guid}/ssh", UpdateState).WithName("updateServerSsh")
            .WithSummary("Turn the SSH fallback for a server on or off")
            .Accepts<UpdateSshRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateSshRequest>().RequireAdmin(Scopes.ServersWrite)
            .Produces<SshStateResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapPost("/{id:guid}/ssh/host-key/confirm", ConfirmHostKey).WithName("confirmServerSshHostKey")
            .WithSummary("Trust the host key a server presented after it changed")
            .WithDescription("Connections to the server stay blocked (`ssh.host_key_changed`) until an Administrator confirms the new fingerprint, which must be the one in `hostKey.pending`.")
            .Validate<ConfirmHostKeyRequest>().RequireAdmin(Scopes.ServersWrite)
            .Produces<SshStateResponse>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        servers.MapPost("/{id:guid}/ssh/test", TestConnection).WithName("testServerSsh")
            .WithSummary("Open an SSH connection to the server and report what it answers")
            .RequireAdmin(Scopes.ServersWrite)
            .Produces<SshTestResponse>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    // ================================================================ add / install

    private static async Task<IResult> AddServer(
        AddSshServerRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, SecretVault vault, IJobQueue queue, CancellationToken ct)
    {
        var org = actor.Org();
        var name = request.Name!.Trim();
        if (await db.Servers.AnyAsync(s => s.OrganizationId == org && s.Name == name, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("server", $"A server named '{name}' already exists."));
        if (request.PrivateKey is { Length: > 0 } key && !SshCredentialFormat.TryReadKey(key, request.Passphrase, out var keyError))
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/privateKey", "pattern", keyError!)]));

        var value = SshCredentialFormat.Compose(request.PrivateKey, request.Passphrase, request.Password);
        var secretName = await FreeSecretNameAsync(db, org, "ssh/" + name, ct);
        var secret = vault.Create(org, secretName, $"SSH credential of server '{name}'.", value, purpose: SecretPurpose.SshCredential);

        var server = new Server
        {
            OrganizationId = org, Name = name, Host = request.Host!.Trim().ToLowerInvariant(), SshPort = request.SshPort ?? 22, SshUser = request.SshUser ?? "root",
            Transport = ServerTransport.Ssh, Roles = (request.Roles ?? []).Select(r => EnumText.Parse<ServerRole>(r)!.Value).Distinct().ToList(),
            PublicIp = request.PublicIp, MaxConcurrentBuilds = request.MaxConcurrentBuilds ?? 1, SshCredentialSecretId = secret.Id,
            SshHostKeyFingerprint = request.HostKeyFingerprint, // verified out of band by the user; the first connection enforces it
        };
        server.SetAgentStatus(AgentStatus.NotInstalled, clock.UtcNow);
        db.Servers.Add(server);
        await audit.RecordAsync("server.created", "server", server.Id,
            new { name, host = server.Host, transport = server.Transport, roles = server.Roles, via = "ssh", hostKeyPinned = request.HostKeyFingerprint is not null }, ct);

        JobDto? job = null;
        if (request.InstallAgent ?? true) job = await EnqueueInstallAsync(db, queue, audit, org, server.Id, request.HostKeyFingerprint, ct);
        http.SetETag(server.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("servers", server.Id), new AddSshServerResponse(ServerEndpoints.ToResponse(server, 0), job));
    }

    private static async Task<string> FreeSecretNameAsync(AetheraDbContext db, Guid org, string baseName, CancellationToken ct)
    {
        var name = baseName.Length > 200 ? baseName[..200] : baseName;
        if (!await db.Secrets.AnyAsync(s => s.OrganizationId == org && s.Name == name, ct)) return name;
        return (baseName.Length > 190 ? baseName[..190] : baseName) + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    private static async Task<JobDto> EnqueueInstallAsync(
        AetheraDbContext db, IJobQueue queue, IAuditLog audit, Guid org, Guid serverId, string? fingerprint, CancellationToken ct)
    {
        var job = await queue.EnqueueAsync(new JobRequest(ServerInstallAgentJobHandler.JobType, new ServerInstallAgentPayload { ServerId = serverId, HostKeyFingerprint = fingerprint })
        {
            OrganizationId = org, Resource = new JobResource("server", serverId), LockKey = $"server:{serverId}:maintenance", MaxAttempts = 1,
        }, ct);
        await audit.RecordAsync("server.agent_install_requested", "server", serverId, new { jobId = job.Id, hostKeyPinned = fingerprint is not null }, ct);
        return await JobMapper.ToDtoWithPositionAsync(db, job, ct);
    }

    private static async Task<IResult> InstallAgent(
        Guid id, InstallAgentRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IJobQueue queue, CancellationToken ct)
    {
        var org = actor.Org();
        var server = await db.GetServerAsync(org, id, tracking: false, ct);
        if (server.SshCredentialSecretId is null)
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status422UnprocessableEntity, SshProblemCodes.NotConfigured, "The server has no SSH credential. Add one before installing the agent."));
        var dto = await EnqueueInstallAsync(db, queue, audit, org, id, request.HostKeyFingerprint, ct);
        return Results.Accepted(dto.Links.Self, dto);
    }

    private static async Task<Ok<HostKeyResponse>> ScanHostKey(ScanHostKeyRequest request, ISshConnector connector, IOptions<SshOptions> options, CancellationToken ct)
    {
        var settings = new SshConnectionSettings(TimeSpan.FromSeconds(options.Value.ConnectTimeoutSeconds), TimeSpan.FromSeconds(options.Value.KeepAliveSeconds));
        try
        {
            var key = await connector.ScanHostKeyAsync(request.Host!.Trim().ToLowerInvariant(), request.Port ?? 22, settings, ct);
            return TypedResults.Ok(new HostKeyResponse(key.Algorithm, key.Fingerprint));
        }
        catch (SshConnectException ex)
        {
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status503ServiceUnavailable, TransportErrors.Unreachable, ex.Message));
        }
    }

    // ================================================================ state

    private static async Task<Ok<SshStateResponse>> GetState(
        Guid id, AetheraDbContext db, ICurrentActor actor, SshSettingsStore settings, SshHostKeyService hostKeys, AgentSessionRegistry registry, CancellationToken ct) =>
        TypedResults.Ok(await BuildStateAsync(await db.GetServerAsync(actor.Org(), id, tracking: false, ct), settings, hostKeys, registry, ct));

    private static async Task<SshStateResponse> BuildStateAsync(Server server, SshSettingsStore settings, SshHostKeyService hostKeys, AgentSessionRegistry registry, CancellationToken ct)
    {
        var pending = await hostKeys.GetPendingAsync(server.Id, ct);
        var allow = await settings.FallbackAllowedAsync(server.Id, ct);
        var polling = await settings.GetAsync<SshPollingState>(SshSettingsStore.PollingKey(server.Id), ct);
        var hasCredential = server.SshCredentialSecretId is not null;
        var keyState = pending is not null ? "changed" : server.SshHostKeyFingerprint is null ? "unpinned" : "pinned";

        var (mode, degraded, reason) =
            registry.IsConnected(server.Id) ? ("agent", false, (string?)null)
            : hasCredential && allow ? ("sshPolling", true, "polling over SSH")
            : hasCredential ? ("sshIdle", false, "The SSH fallback is turned off for this server.")
            : ("none", false, null);

        return new SshStateResponse(
            server.Id, hasCredential, server.Host, server.SshPort, server.SshUser,
            new SshHostKeyStateResponse(keyState, server.SshHostKeyFingerprint, pending is null ? null : new PendingHostKeyResponse(pending.Algorithm, pending.Fingerprint, pending.SeenAt)),
            allow, mode, degraded, reason,
            polling is null || mode == "agent" ? null : new SshPollingResponse(polling.Since, polling.LastSuccessAt, polling.LastAttemptAt, polling.LastError), DateTimeOffset.UtcNow);
    }

    private static async Task<Ok<SshStateResponse>> UpdateState(
        Guid id, PatchRequest<UpdateSshRequest> patch, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SshSettingsStore settings, SshHostKeyService hostKeys,
        AgentSessionRegistry registry, CancellationToken ct)
    {
        var server = await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        if (patch.Has("allowSshFallback") && patch.Body.AllowSshFallback is { } allow)
        {
            await settings.SetAsync(SshSettingsStore.FallbackKey(id), new SshFallbackSetting(allow), ct);
            await audit.RecordAsync("server.ssh_fallback_changed", "server", id, new { allowSshFallback = allow }, ct);
        }

        return TypedResults.Ok(await BuildStateAsync(server, settings, hostKeys, registry, ct));
    }

    private static async Task<Ok<SshStateResponse>> ConfirmHostKey(
        Guid id, ConfirmHostKeyRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SshSettingsStore settings, SshHostKeyService hostKeys,
        SshConnectionPool pool, AgentSessionRegistry registry, CancellationToken ct)
    {
        var server = await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var (result, previous) = await hostKeys.ConfirmAsync(id, request.Fingerprint!, ct);
        switch (result)
        {
            case HostKeyConfirmation.Mismatch:
                throw new ApiProblemException(ApiProblems.Conflict(SshProblemCodes.HostKeyMismatch,
                    "The server presented a different key than the fingerprint you confirmed. Read the current fingerprint again and verify it."));
            case HostKeyConfirmation.NothingToConfirm:
                throw new ApiProblemException(ApiProblems.Conflict(SshProblemCodes.NothingToConfirm, "No host key change is waiting for confirmation."));
        }

        await pool.EvictAsync(id);
        await audit.RecordAsync("server.ssh_host_key_confirmed", "server", id, new { fingerprint = request.Fingerprint, previous }, ct);
        var fresh = await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        return TypedResults.Ok(await BuildStateAsync(fresh ?? server, settings, hostKeys, registry, ct));
    }

    private static async Task<Ok<SshTestResponse>> TestConnection(
        Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SshConnectionPool pool, CancellationToken ct)
    {
        var server = await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        if (server.SshCredentialSecretId is null)
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status422UnprocessableEntity, SshProblemCodes.NotConfigured, "The server has no SSH credential."));
        var watch = Stopwatch.StartNew();
        try
        {
            using var lease = await pool.AcquireAsync(id, ct);
            var result = await lease.Connection.ExecuteAsync(
                new RemoteCommand("sh -c 'uname -sm; docker version --format \"{{.Server.Version}}\" 2>/dev/null; true'"), new RemoteExecOptions { Timeout = TimeSpan.FromSeconds(20) }, ct);
            var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var uname = lines.FirstOrDefault()?.Split(' ', 2);
            await audit.RecordAsync("server.ssh_tested", "server", id, new { connected = true }, ct);
            return TypedResults.Ok(new SshTestResponse(
                true, new HostKeyResponse(lease.Connection.HostKey.Algorithm, lease.Connection.HostKey.Fingerprint), uname?.FirstOrDefault(),
                uname is { Length: 2 } ? Aethera.Infrastructure.Ssh.Metrics.SshDiscoveryScript.Arch(uname[1]) : null, lines.Skip(1).FirstOrDefault(), watch.Elapsed.TotalMilliseconds));
        }
        catch (ServerTransportException ex)
        {
            throw new ApiProblemException(AgentProblems.From(ex));
        }
    }
}
