using System.Text;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Deployments;

/// <summary>A deployment with the application it belongs to, for the organization-wide list.</summary>
public sealed record DeploymentListItemDto(DeploymentDto Deployment, string ApplicationName, string ApplicationSlug, Guid ServerId);

/// <summary>One log stream of a deployment: <c>build</c> (the build output) or <c>deploy</c> (the pipeline narration).</summary>
public sealed record DeploymentLogPageDto(string Source, string StreamId, IReadOnlyList<LogLine> Items, long NextSequence, bool HasMore, bool Ended, IReadOnlyList<string> Sources);

public sealed record RuntimeLogContainerDto(string Container, string? Error);

public sealed record RuntimeLogLineDto(string Container, DateTimeOffset Timestamp, string Stream, string Text);

public sealed record RuntimeLogsDto(IReadOnlyList<RuntimeLogContainerDto> Containers, IReadOnlyList<RuntimeLogLineDto> Lines);

/// <summary>WP4.2: the organization-wide deployment list, deployment logs (stored build and pipeline streams) and live application logs.</summary>
internal static class DeploymentLogEndpoints
{
    private const int DefaultLogLimit = 500;
    private const int MaxLogLimit = 2000;
    private const int MaxRuntimeTail = 1000;
    private static readonly TimeSpan RuntimeLogTimeout = TimeSpan.FromSeconds(8);

    private static readonly SortDefinition<Deployment> Sorts = new SortDefinition<Deployment>("-createdAt")
        .Add("createdAt", d => d.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var deployments = api.MapGroup("/deployments").WithTags("Deployments");
        deployments.MapGet("/", List).WithName("listAllDeployments").WithSummary("List deployments of all applications, newest first").RequireRead();
        deployments.MapGet("/{id:guid}/logs", Logs).WithName("getDeploymentLogs")
            .WithSummary("Read the build or pipeline log of a deployment, paged by sequence, or download it as text")
            .WithDescription("`source` is `build` (default when the deployment built an image) or `deploy`. Live lines come from the `/hubs/logs` stream named in `streamId`.")
            .RequireRead()
            .Produces<DeploymentLogPageDto>()
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .ProducesProblem(StatusCodes.Status404NotFound);

        foreach (var (prefix, noun, kind) in new[] { ("applications", "Application", "application"), ("services", "Service", "service") })
        {
            var k = kind;
            api.MapGet($"/{prefix}/{{id:guid}}/logs", (Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IServerTransportResolver transports, int? tail, DateTimeOffset? since, CancellationToken ct) =>
                    RuntimeLogs(id, k, http, db, actor, transports, tail, since, ct))
                .WithName($"get{noun}Logs").WithTags("Deployments")
                .WithSummary($"Tail the running containers of {(kind == "service" ? "a service" : "an application")}")
                .WithDescription("A bounded snapshot of the last `tail` lines per container (stdout and stderr), oldest first; poll with `since` for new lines.")
                .RequireRead()
                .Produces<RuntimeLogsDto>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }

    private static async Task<Ok<Page<DeploymentListItemDto>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort,
        string? status, Guid? applicationId, Guid? serverId, DeploymentTrigger? trigger, CancellationToken ct)
    {
        http.RejectUnknownQuery("status", "applicationId", "serverId", "trigger");
        var statuses = ResourceHttp.ParseEnumFilter<DeploymentStatus>(status, "status");
        var org = actor.Org();
        var query = db.Deployments.AsNoTracking().Include(d => d.Workload).Where(d => d.Workload.Environment.Project.OrganizationId == org);
        if (statuses is not null) query = query.Where(d => statuses.Contains(d.Status));
        if (applicationId is { } app) query = query.Where(d => d.WorkloadId == app);
        if (serverId is { } server) query = query.Where(d => d.ServerId == server);
        if (trigger is { } t) query = query.Where(d => d.Trigger == t);
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"s={status}&a={applicationId}&sv={serverId}&t={trigger}", ct);
        return TypedResults.Ok(new Page<DeploymentListItemDto>(
            items.Select(d => new DeploymentListItemDto(DeploymentEndpoints.ToDto(d, includeSteps: false), d.Workload.Name, d.Workload.Slug, d.ServerId)).ToList(), next));
    }

    private static async Task<IResult> Logs(
        Guid id, HttpContext http, AetheraDbContext db, LogReader reader, ICurrentActor actor, string? source, long? fromSequence, int? limit, bool? download, CancellationToken ct)
    {
        http.RejectUnknownQuery("source", "fromSequence", "limit", "download");
        if (fromSequence is < 0) return ApiProblems.InvalidParameter("fromSequence", "Must be zero or greater.", "range");
        if (limit is < 1 or > MaxLogLimit) return ApiProblems.InvalidParameter("limit", $"Must be between 1 and {MaxLogLimit}.", "range");
        if (source is not (null or "build" or "deploy")) return ApiProblems.InvalidParameter("source", "Use build or deploy.", "enum");

        var org = actor.Org();
        var deployment = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == id && d.Workload.Environment.Project.OrganizationId == org).FirstOrDefaultAsync(ct)
            ?? throw new ApiProblemException(ApiProblems.NotFound("deployment", id));

        var sources = new List<string>();
        if (deployment.BuildId is not null) sources.Add("build");
        if (deployment.JobId is not null) sources.Add("deploy");
        var chosen = source ?? (sources.Contains("build") ? "build" : "deploy");
        string? streamId = chosen == "build"
            ? deployment.BuildId is { } buildId ? BuildStreams.StreamId(buildId) : null
            : deployment.JobId is { } jobId ? JobStreams.StreamId(jobId) : null;
        // A stream has ended when what writes to it has finished, not when the deployment changed state: the deployment is Running before the
        // job writes its closing lines, and a client that stopped there would miss them.
        var terminal = deployment.Status is DeploymentStatus.Running or DeploymentStatus.Superseded or DeploymentStatus.Stopped or DeploymentStatus.Failed or DeploymentStatus.Cancelled;
        if (streamId is null) return Results.Ok(new DeploymentLogPageDto(chosen, "", [], fromSequence ?? 0, false, terminal, sources));
        var writerFinished = chosen == "build"
            ? deployment.BuildId is { } bid && await db.Builds.AsNoTracking().Where(b => b.Id == bid).Select(b => b.Status != BuildStatus.Running).FirstOrDefaultAsync(ct)
            : deployment.JobId is { } jid && await db.Jobs.AsNoTracking().Where(j => j.Id == jid)
                .Select(j => j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled).FirstOrDefaultAsync(ct);

        var from = fromSequence ?? 0;
        if (download == true)
        {
            return Results.Stream(async stream =>
            {
                await foreach (var line in reader.StreamAsync(streamId, from, cancellationToken: http.RequestAborted))
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(line.Text), http.RequestAborted);
            }, "text/plain; charset=utf-8", $"deployment-{deployment.Number}-{chosen}.log");
        }

        var take = limit ?? DefaultLogLimit;
        var lines = await reader.ReadAsync(streamId, from, take + 1, ct);
        var hasMore = lines.Count > take;
        var items = hasMore ? lines.Take(take).ToList() : lines;
        var next = items.Count > 0 ? items[^1].Sequence + 1 : from;
        return Results.Ok(new DeploymentLogPageDto(chosen, streamId, items, next, hasMore, writerFinished && !hasMore, sources));
    }

    private static async Task<IResult> RuntimeLogs(
        Guid id, string kind, HttpContext http, AetheraDbContext db, ICurrentActor actor, IServerTransportResolver transports, int? tail, DateTimeOffset? since, CancellationToken ct)
    {
        http.RejectUnknownQuery("tail", "since");
        if (tail is < 1 or > MaxRuntimeTail) return ApiProblems.InvalidParameter("tail", $"Must be between 1 and {MaxRuntimeTail}.", "range");

        var org = actor.Org();
        await DeploymentEndpoints.RequireWorkloadAsync(db, actor, id, kind, ct);
        var app = await db.Workloads.AsNoTracking().FirstAsync(a => a.Id == id, ct);
        var containers = app.CurrentDeploymentId is { } current
            ? await db.Deployments.AsNoTracking().Where(d => d.Id == current).Select(d => d.ContainerIds).FirstOrDefaultAsync(ct) ?? []
            : [];
        if (containers.Count == 0) return Results.Ok(new RuntimeLogsDto([], []));

        ResolvedTransport resolved;
        try
        {
            resolved = await transports.ResolveAsync(app.ServerId, TransportCapabilities.LogFollow, ct);
        }
        catch (ServerTransportException ex)
        {
            return Results.Ok(new RuntimeLogsDto(containers.Select(c => new RuntimeLogContainerDto(c, ex.Message)).ToList(), []));
        }

        var lines = new List<RuntimeLogLineDto>();
        var infos = new List<RuntimeLogContainerDto>();
        foreach (var container in containers.Take(8))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RuntimeLogTimeout);
            try
            {
                await foreach (var entry in resolved.Transport.StreamLogsAsync(app.ServerId, new LogStreamRequest(container, false, since, tail ?? 200), timeout.Token))
                {
                    if (entry.Eof) break;
                    lines.Add(new RuntimeLogLineDto(container, entry.Timestamp, entry.Stream == LogStream.Stderr ? "stderr" : "stdout", entry.Text));
                }

                infos.Add(new RuntimeLogContainerDto(container, null));
            }
            catch (ServerTransportException ex)
            {
                infos.Add(new RuntimeLogContainerDto(container, ex.Message));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                infos.Add(new RuntimeLogContainerDto(container, "Timed out reading the container log."));
            }
        }

        return Results.Ok(new RuntimeLogsDto(infos, lines.OrderBy(l => l.Timestamp).ToList()));
    }
}
