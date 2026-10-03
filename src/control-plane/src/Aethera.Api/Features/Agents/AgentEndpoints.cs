using Aethera.Api.Features.Auth;
using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Enrollment;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Jobs;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Status;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Agents;

/// <summary>Problem codes raised by the agent endpoints.</summary>
public static class AgentProblemCodes
{
    public const string JoinTokenNotFound = "join_token.not_found";
    public const string CommandFailed = "server.command_failed";
}

/// <summary>Maps transport failures to ProblemDetails (ADR 0003): an unreachable agent is 503, a refused or failed command 422/502.</summary>
internal static class AgentProblems
{
    public static ApiProblem From(ServerTransportException ex) => ex.Code switch
    {
        TransportErrors.AgentUnavailable => new ApiProblem(StatusCodes.Status503ServiceUnavailable, TransportErrors.AgentUnavailable, ex.Message),
        TransportErrors.Unreachable => new ApiProblem(StatusCodes.Status503ServiceUnavailable, TransportErrors.Unreachable, ex.Message),
        TransportErrors.AgentBusy => new ApiProblem(StatusCodes.Status503ServiceUnavailable, TransportErrors.AgentBusy, ex.Message),
        TransportErrors.AckTimeout => new ApiProblem(StatusCodes.Status504GatewayTimeout, TransportErrors.AckTimeout, ex.Message),
        TransportErrors.Unsupported => new ApiProblem(StatusCodes.Status422UnprocessableEntity, TransportErrors.Unsupported, ex.Message),
        TransportErrors.CommandRejected => new ApiProblem(StatusCodes.Status422UnprocessableEntity, TransportErrors.CommandRejected, ex.Message),
        _ => new ApiProblem(StatusCodes.Status502BadGateway, ex.Code, ex.Message),
    };

    public static ApiProblem Failed<T>(CommandOutcome<T> outcome) =>
        new(StatusCodes.Status502BadGateway, AgentProblemCodes.CommandFailed,
            outcome.ErrorMessage is { Length: > 0 } message ? $"The command failed on the server: {message}" : "The command failed on the server.");
}

internal static class AgentEndpoints
{
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder api)
    {
        var servers = api.MapGroup("/servers").WithTags("Servers");

        // ---- join tokens / CA / reset
        servers.MapPost("/{id:guid}/join-tokens", CreateJoinToken).WithName("createServerJoinToken")
            .WithSummary("Create a one-time join token and the install command for a server")
            .WithDescription("The token is shown once and only its hash is stored. Default lifetime 1 hour, maximum 24 hours. Consumed by `EnrollmentService/Enroll`.")
            .Validate<CreateJoinTokenRequest>().AddEndpointFilter<NoStoreFilter>().RequireAdmin(Scopes.ServersWrite)
            .Produces<JoinTokenResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapGet("/{id:guid}/join-tokens", ListJoinTokens).WithName("listServerJoinTokens")
            .WithSummary("List the join tokens of a server (never their values)")
            .RequireRead().Produces<IReadOnlyList<JoinTokenSummary>>().ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapDelete("/{id:guid}/join-tokens/{tokenId:guid}", RevokeJoinToken).WithName("revokeServerJoinToken")
            .WithSummary("Revoke an unused join token")
            .RequireAdmin(Scopes.ServersWrite).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapPost("/{id:guid}/agent/reset", ResetAgent).WithName("resetServerAgent")
            .WithSummary("Reset the agent: revoke its certificates, close its stream and invalidate unused join tokens")
            .WithDescription("Destructive for the agent's access: it must re-enroll with a new join token. Needs `?confirm=<server name>`.")
            .RequireAdmin(Scopes.ServersWrite).Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status428PreconditionRequired);
        api.MapGet("/agent/ca", GetCa).WithName("getAgentCa").WithTags("Servers")
            .WithSummary("The internal CA certificate and fingerprint agents pin").RequireRead().Produces<AgentCaResponse>();

        // ---- status / events / metrics / discovery
        servers.MapGet("/{id:guid}/status", GetStatus).WithName("getServerStatus")
            .WithSummary("Server health as separate axes: server, agent, Docker, application")
            .RequireRead().Produces<ServerHealthResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapGet("/{id:guid}/events", ListEvents).WithName("listServerEvents")
            .WithSummary("Recent status transitions and lifecycle events of a server, newest first")
            .RequireRead().Produces<IReadOnlyList<ResourceEventResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapGet("/{id:guid}/metrics/latest", GetLatestMetrics).WithName("getServerLatestMetrics")
            .WithSummary("The most recent host and container samples")
            .RequireRead().Produces<LatestMetricsResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapGet("/{id:guid}/metrics", GetMetricSeries).WithName("getServerMetrics")
            .WithSummary("A metric time series")
            .WithDescription("`resolution` is `auto` (default), `raw`, `fiveMinutes` or `oneHour`. `containerId` selects a container instead of the host. At most `maxPoints` (default 500, maximum 2000) are returned.")
            .RequireRead().Produces<MetricSeriesResponse>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        servers.MapGet("/{id:guid}/discovery", GetDiscovery).WithName("getServerDiscovery")
            .WithSummary("Discovered host facts and the last full discovery report (spec section 55)")
            .RequireRead().Produces<DiscoveryResponse>().ProducesProblem(StatusCodes.Status404NotFound);

        // ---- Docker inventory (a command to the agent, live)
        servers.MapGet("/{id:guid}/docker/containers", ListContainers).WithName("listServerContainers")
            .WithSummary("Containers on the server (live, through the agent)").RequireRead()
            .Produces<IReadOnlyList<DockerContainer>>().ProducesProblem(StatusCodes.Status503ServiceUnavailable).ProducesProblem(StatusCodes.Status502BadGateway);
        servers.MapGet("/{id:guid}/docker/images", ListImages).WithName("listServerImages")
            .WithSummary("Images on the server (live, through the agent)").RequireRead()
            .Produces<IReadOnlyList<DockerImage>>().ProducesProblem(StatusCodes.Status503ServiceUnavailable).ProducesProblem(StatusCodes.Status502BadGateway);
        servers.MapGet("/{id:guid}/docker/volumes", ListVolumes).WithName("listServerVolumes")
            .WithSummary("Volumes on the server (live, through the agent)").RequireRead()
            .Produces<IReadOnlyList<DockerVolume>>().ProducesProblem(StatusCodes.Status503ServiceUnavailable).ProducesProblem(StatusCodes.Status502BadGateway);
        servers.MapGet("/{id:guid}/docker/networks", ListNetworks).WithName("listServerNetworks")
            .WithSummary("Networks on the server (live, through the agent)").RequireRead()
            .Produces<IReadOnlyList<DockerNetwork>>().ProducesProblem(StatusCodes.Status503ServiceUnavailable).ProducesProblem(StatusCodes.Status502BadGateway);

        // ---- maintenance (jobs)
        servers.MapPost("/{id:guid}/maintenance/prune", Prune).WithName("pruneServer")
            .WithSummary("Prune unused Docker objects on the server (a job)")
            .WithDescription("Enqueues `server.prune`. Rollback-point images are always kept. `volumes` deletes data and needs `?confirm=<server name>`.")
            .Validate<PruneRequest>().RequireAdmin(Scopes.ServersWrite)
            .Produces<JobDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status428PreconditionRequired);
        servers.MapPost("/{id:guid}/maintenance/refresh-discovery", RefreshDiscovery).WithName("refreshServerDiscovery")
            .WithSummary("Ask the agent to re-run resource discovery (a job)")
            .RequireAdmin(Scopes.ServersWrite).Produces<JobDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    // ================================================================ join tokens / CA

    private static async Task<IResult> CreateJoinToken(
        Guid id, CreateJoinTokenRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock,
        IInternalCa ca, IOptions<AgentGatewayOptions> options, CancellationToken ct)
    {
        var settings = options.Value;
        var server = await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var ttl = request.TtlMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : TimeSpan.FromMinutes(settings.JoinTokenDefaultMinutes);
        var (token, record) = JoinTokenService.Issue(db, server.Id, clock.UtcNow, ttl, actor.UserId);
        await audit.RecordAsync("server.join_token_created", "server", id, new { tokenId = record.Id, expiresAt = record.ExpiresAt }, ct); // saves the token too

        var info = await ca.GetPublicInfoAsync(ct);
        var endpoint = settings.EffectivePublicEndpoint;
        var script = settings.InstallScriptUrl is { Length: > 0 } configured ? configured : $"{http.Request.Scheme}://{http.Request.Host}/install-agent.sh";
        var command = $"curl -fsSL {script} | sudo bash -s -- --token {token} --endpoint {endpoint} --ca-sha256 {info.FingerprintSha256}";
        return TypedResults.Created($"{ResourceHttp.Path("servers", id)}/join-tokens",
            new JoinTokenResponse(record.Id, id, token, record.ExpiresAt, endpoint, info.FingerprintSha256, command));
    }

    private static async Task<Ok<IReadOnlyList<JoinTokenSummary>>> ListJoinTokens(Guid id, AetheraDbContext db, ICurrentActor actor, IClock clock, CancellationToken ct)
    {
        await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var now = clock.UtcNow;
        var tokens = await db.JoinTokens.AsNoTracking().Where(t => t.ServerId == id).OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync(ct);
        IReadOnlyList<JoinTokenSummary> list = tokens.Select(t => new JoinTokenSummary(
            t.Id, t.RevokedAt is not null ? "revoked" : t.UsedAt is not null ? "used" : t.ExpiresAt <= now ? "expired" : "active",
            t.CreatedAt, t.ExpiresAt, t.UsedAt, t.RevokedAt, t.CreatedByUserId)).ToList();
        return TypedResults.Ok(list);
    }

    private static async Task<NoContent> RevokeJoinToken(Guid id, Guid tokenId, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var token = await db.JoinTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.ServerId == id, ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound("join_token", tokenId));
        if (token.UsedAt is null)
        {
            token.Revoke(clock.UtcNow);
            await audit.RecordAsync("server.join_token_revoked", "server", id, new { tokenId }, ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ResetAgent(
        Guid id, string? confirm, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, IInternalCa ca, AgentSessionRegistry registry, CancellationToken ct)
    {
        var server = await db.GetServerAsync(actor.Org(), id, tracking: true, ct);
        Confirmation.Require(confirm, server.Name);
        var now = clock.UtcNow;

        var revoked = await ca.RevokeServerCertificatesAsync(db, id, "agent reset", now, ct);
        foreach (var token in await db.JoinTokens.Where(t => t.ServerId == id && t.UsedAt == null && t.RevokedAt == null).ToListAsync(ct)) token.Revoke(now);
        var before = server.AgentStatus;
        if (server.SetAgentStatus(AgentStatus.Unknown, now))
            db.ResourceEvents.Add(new ResourceEvent { ResourceType = "server", ResourceId = id, Kind = "status.changed", Axis = "agent", OldValue = ServerStatusMachine.Camel(before), NewValue = "unknown", Detail = "agent reset", ActorUserId = actor.UserId, OccurredAt = now });
        server.CertSerial = null;
        server.CertFingerprint = null;
        server.CertExpiresAt = null;
        await audit.RecordAsync("server.agent_reset", "server", id, new { revokedCertificates = revoked }, ct); // saves everything above

        registry.Disconnect(id, Aethera.Agent.V1.DisconnectReason.Revoked, "The agent was reset. Re-enroll it with a new join token.");
        return TypedResults.NoContent();
    }

    private static async Task<Ok<AgentCaResponse>> GetCa(IInternalCa ca, IOptions<AgentGatewayOptions> options, CancellationToken ct)
    {
        var info = await ca.GetPublicInfoAsync(ct);
        return TypedResults.Ok(new AgentCaResponse(info.CertificatePem, info.FingerprintSha256, info.NotBefore, info.NotAfter, options.Value.EffectivePublicEndpoint));
    }

    // ================================================================ status / events / metrics / discovery

    private static async Task<Ok<ServerHealthResponse>> GetStatus(Guid id, AetheraDbContext db, ICurrentActor actor, IClock clock, AgentSessionRegistry registry, CancellationToken ct)
    {
        var server = await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var workloads = await db.Workloads.AsNoTracking().Where(w => w.ServerId == id).Select(w => new { w.Status, w.DesiredState }).ToListAsync(ct);
        var summary = ServerStatusMachine.Summarize(workloads.Select(w => (w.Status, w.DesiredState)));
        var health = ServerStatusMachine.Derive(server, summary);
        var session = registry.Session(id) is { IsClosed: false } live
            ? new AgentSessionResponse(live.SessionId, live.AgentVersion, live.ConnectedAt, live.LastRtt?.TotalMilliseconds, live.ClockSkew?.TotalSeconds, live.Capabilities.Order().ToList())
            : null;
        return TypedResults.Ok(new ServerHealthResponse(
            id, ToAxis(health.Server), ToAxis(health.Agent), ToAxis(health.Docker), ToAxis(health.Application), health.FirstFailingLayer,
            new ApplicationSummaryResponse(summary.Total, summary.Unavailable, summary.Healthy, summary.Unknown), session, clock.UtcNow));
    }

    private static AxisResponse ToAxis(AxisView v) => new(v.Axis, v.Health, v.BlockedBy, v.Since, v.Stale, v.Detail);

    private static async Task<Ok<IReadOnlyList<ResourceEventResponse>>> ListEvents(
        Guid id, int? limit, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        http.RejectUnknownQuery("limit");
        if (limit is < 1 or > 200) throw new ApiProblemException(ApiProblems.InvalidParameter("limit", "Must be between 1 and 200."));
        await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var events = await db.ResourceEvents.AsNoTracking().Where(e => e.ResourceType == "server" && e.ResourceId == id)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(limit ?? 50).ToListAsync(ct);
        IReadOnlyList<ResourceEventResponse> list = events.Select(e => new ResourceEventResponse(e.Id, e.Kind, e.Axis, e.OldValue, e.NewValue, e.Detail, e.OccurredAt)).ToList();
        return TypedResults.Ok(list);
    }

    private static async Task<Ok<LatestMetricsResponse>> GetLatestMetrics(Guid id, AetheraDbContext db, ICurrentActor actor, IClock clock, CancellationToken ct)
    {
        await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var newest = await db.MetricSamples.AsNoTracking().Where(m => m.ServerId == id && m.Resolution == MetricResolution.Raw)
            .OrderByDescending(m => m.Timestamp).Select(m => (DateTimeOffset?)m.Timestamp).FirstOrDefaultAsync(ct);
        if (newest is not { } latest) return TypedResults.Ok(new LatestMetricsResponse(id, null, [], null, true));

        var window = latest - TimeSpan.FromSeconds(30); // one report = one timestamp; the window absorbs per-container timing
        var rows = await db.MetricSamples.AsNoTracking().Where(m => m.ServerId == id && m.Resolution == MetricResolution.Raw && m.Timestamp >= window)
            .OrderByDescending(m => m.Timestamp).ToListAsync(ct);
        var host = rows.FirstOrDefault(r => r.ContainerId is null);
        var containers = rows.Where(r => r.ContainerId is not null).GroupBy(r => r.ContainerId!).Select(g => g.First())
            .Select(r => new ContainerMetricResponse(r.ContainerId!, r.WorkloadId, r.Timestamp, r.CpuPercent, r.MemoryUsedBytes, r.MemoryTotalBytes, r.NetRxBytes, r.NetTxBytes))
            .OrderBy(c => c.ContainerId, StringComparer.Ordinal).ToList();
        return TypedResults.Ok(new LatestMetricsResponse(id, host is null ? null : ToPoint(host, null), containers, latest, clock.UtcNow - latest > TimeSpan.FromMinutes(1)));
    }

    private static async Task<Ok<MetricSeriesResponse>> GetMetricSeries(
        Guid id, DateTimeOffset? from, DateTimeOffset? to, string? resolution, string? containerId, int? maxPoints,
        HttpContext http, AetheraDbContext db, ICurrentActor actor, IClock clock, CancellationToken ct)
    {
        http.RejectUnknownQuery("from", "to", "resolution", "containerId", "maxPoints");
        await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var end = (to ?? clock.UtcNow).ToUniversalTime();
        var start = (from ?? end.AddHours(-1)).ToUniversalTime();
        if (start >= end) throw new ApiProblemException(ApiProblems.InvalidParameter("from", "Must be earlier than 'to'."));
        if (end - start > TimeSpan.FromDays(400)) throw new ApiProblemException(ApiProblems.InvalidParameter("from", "The range may span at most 400 days."));
        var limit = maxPoints ?? 500;
        if (limit is < 10 or > 2000) throw new ApiProblemException(ApiProblems.InvalidParameter("maxPoints", "Must be between 10 and 2000."));

        var chosen = (resolution ?? "auto") switch
        {
            "auto" => end - start <= TimeSpan.FromHours(6) ? MetricResolution.Raw : end - start <= TimeSpan.FromDays(14) ? MetricResolution.FiveMinutes : MetricResolution.OneHour,
            "raw" => MetricResolution.Raw,
            "fiveMinutes" => MetricResolution.FiveMinutes,
            "oneHour" => MetricResolution.OneHour,
            _ => throw new ApiProblemException(ApiProblems.InvalidParameter("resolution", "Must be one of: auto, raw, fiveMinutes, oneHour.")),
        };

        var query = db.MetricSamples.AsNoTracking().Where(m => m.ServerId == id && m.Resolution == chosen && m.Timestamp >= start && m.Timestamp <= end);
        query = string.IsNullOrEmpty(containerId) ? query.Where(m => m.ContainerId == null) : query.Where(m => m.ContainerId == containerId);
        var rows = await query.OrderBy(m => m.Timestamp).ToListAsync(ct);

        var stride = (int)Math.Ceiling(rows.Count / (double)limit);
        var picked = stride <= 1 ? rows : rows.Where((_, index) => index % stride == 0).ToList();
        var points = new List<MetricPointResponse>(picked.Count);
        MetricSample? previous = null;
        foreach (var row in picked)
        {
            points.Add(ToPoint(row, previous));
            previous = row;
        }

        var name = chosen switch { MetricResolution.Raw => "raw", MetricResolution.FiveMinutes => "fiveMinutes", _ => "oneHour" };
        return TypedResults.Ok(new MetricSeriesResponse(id, name, start, end, string.IsNullOrEmpty(containerId) ? null : containerId, points));
    }

    private static MetricPointResponse ToPoint(MetricSample row, MetricSample? previous)
    {
        double? Rate(long? now, long? before)
        {
            if (previous is null || now is null || before is null || now < before) return null; // first point, or the counter was reset
            var seconds = (row.Timestamp - previous.Timestamp).TotalSeconds;
            return seconds > 0 ? (now.Value - before.Value) / seconds : null;
        }

        return new MetricPointResponse(
            row.Timestamp, row.CpuPercent, row.MemoryUsedBytes, row.MemoryTotalBytes, row.DiskUsedBytes, row.DiskTotalBytes, row.NetRxBytes, row.NetTxBytes,
            Rate(row.NetRxBytes, previous?.NetRxBytes), Rate(row.NetTxBytes, previous?.NetTxBytes), row.Load1, row.Load5, row.Load15);
    }

    private static async Task<Ok<DiscoveryResponse>> GetDiscovery(
        Guid id, AetheraDbContext db, ICurrentActor actor, AgentDiscoveryStore store, AgentSessionRegistry registry, CancellationToken ct)
    {
        var server = await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var f = server.Facts;
        var stored = await store.LoadAsync(id, ct);
        return TypedResults.Ok(new DiscoveryResponse(
            id, new ServerFactsResponse(f.Os, f.OsVersion, f.Kernel, f.Architecture, f.CpuModel, f.CpuCores, f.MemoryBytes, f.DiskBytes, f.DockerVersion, f.DiscoveredAt),
            stored?.Info, stored?.StoredAt, !registry.IsConnected(id)));
    }

    // ================================================================ Docker inventory

    private static Task<Ok<IReadOnlyList<DockerContainer>>> ListContainers(
        Guid id, bool? all, HttpContext http, AetheraDbContext db, ICurrentActor actor, IServerTransportResolver resolver, CancellationToken ct)
    {
        http.RejectUnknownQuery("all");
        return Inventory<ContainerListCommand, IReadOnlyList<DockerContainer>>(id, new ContainerListCommand(all ?? true), db, actor, resolver, ct);
    }

    private static Task<Ok<IReadOnlyList<DockerImage>>> ListImages(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IServerTransportResolver resolver, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        return Inventory<ImageListCommand, IReadOnlyList<DockerImage>>(id, new ImageListCommand(), db, actor, resolver, ct);
    }

    private static Task<Ok<IReadOnlyList<DockerVolume>>> ListVolumes(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IServerTransportResolver resolver, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        return Inventory<VolumeListCommand, IReadOnlyList<DockerVolume>>(id, new VolumeListCommand(), db, actor, resolver, ct);
    }

    private static Task<Ok<IReadOnlyList<DockerNetwork>>> ListNetworks(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IServerTransportResolver resolver, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        return Inventory<NetworkListCommand, IReadOnlyList<DockerNetwork>>(id, new NetworkListCommand(), db, actor, resolver, ct);
    }

    private static async Task<Ok<T>> Inventory<TCommand, T>(
        Guid id, TCommand command, AetheraDbContext db, ICurrentActor actor, IServerTransportResolver resolver, CancellationToken ct)
        where TCommand : IServerCommand<T>
    {
        var org = actor.Org();
        await db.GetServerAsync(org, id, tracking: false, ct);
        var options = new CommandOptions { IdempotencyKey = Guid.CreateVersion7().ToString("N"), OrganizationId = org, ActorUserId = actor.UserId, Deadline = DateTimeOffset.UtcNow.AddSeconds(30) };
        try
        {
            var resolved = await resolver.ExecuteAsync<TCommand, T>(id, command, options, ct);
            if (!resolved.Outcome.Succeeded || resolved.Outcome.Result is null) throw new ApiProblemException(AgentProblems.Failed(resolved.Outcome));
            return TypedResults.Ok(resolved.Outcome.Result);
        }
        catch (ServerTransportException ex)
        {
            throw new ApiProblemException(AgentProblems.From(ex));
        }
    }

    // ================================================================ maintenance

    private static async Task<IResult> Prune(
        Guid id, PruneRequest request, string? confirm, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IJobQueue queue, CancellationToken ct)
    {
        var org = actor.Org();
        var server = await db.GetServerAsync(org, id, tracking: false, ct);
        if (request.Volumes) Confirmation.Require(confirm, server.Name); // data loss: the user must type the server name
        var payload = new ServerPrunePayload
        {
            ServerId = id, StoppedContainers = request.StoppedContainers, DanglingImages = request.DanglingImages, UnusedImages = request.UnusedImages,
            UnusedNetworks = request.UnusedNetworks, BuildCache = request.BuildCache, Volumes = request.Volumes, OlderThanHours = request.OlderThanHours,
        };
        var job = await queue.EnqueueAsync(new JobRequest(ServerPruneJobHandler.JobType, payload)
        {
            OrganizationId = org, Resource = new JobResource("server", id), LockKey = $"server:{id}:maintenance", MaxAttempts = 3,
        }, ct);
        await audit.RecordAsync("server.prune_requested", "server", id,
            new { jobId = job.Id, request.StoppedContainers, request.DanglingImages, request.UnusedImages, request.UnusedNetworks, request.BuildCache, request.Volumes, request.OlderThanHours }, ct);
        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, ct);
        return Results.Accepted(dto.Links.Self, dto);
    }

    private static async Task<IResult> RefreshDiscovery(Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IJobQueue queue, CancellationToken ct)
    {
        var org = actor.Org();
        await db.GetServerAsync(org, id, tracking: false, ct);
        var job = await queue.EnqueueAsync(new JobRequest(ServerDiscoveryRefreshJobHandler.JobType, new ServerRefreshPayload { ServerId = id })
        {
            OrganizationId = org, Resource = new JobResource("server", id), LockKey = $"server:{id}:maintenance", MaxAttempts = 3,
        }, ct);
        await audit.RecordAsync("server.discovery_refresh_requested", "server", id, new { jobId = job.Id }, ct);
        var dto = await JobMapper.ToDtoWithPositionAsync(db, job, ct);
        return Results.Accepted(dto.Links.Self, dto);
    }
}
