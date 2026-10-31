using System.Globalization;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Placement;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Placement;

/// <summary><c>GET /placement/suggest</c> and <c>GET /servers/capacity</c> (ADR 0012).</summary>
internal static class PlacementEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/placement/suggest", Suggest).WithName("suggestPlacement").WithTags("Placement").RequireRead()
            .WithSummary("Which server a new workload would be placed on, and why")
            .WithDescription(
                "Ranks the organization's servers for a kind of workload (application, statefulService, statelessService, build) and the CPU and memory it " +
                "reserves. Servers that are not active, whose agent is not connected, whose roles exclude the kind or that lack the unreserved memory are " +
                "not eligible; the others are ranked by unreserved memory, then free disk, then fewer workloads. Every server comes with its reasons. " +
                "Give workloadId instead of kind, cpu and memory to judge an existing workload (the server it is on does not count its own reservation).");
        api.MapGet("/servers/capacity", Capacity).WithName("listServerCapacity").WithTags("Servers").RequireRead()
            .WithSummary("Cores, memory and disk of every server, what is reserved on it and how much is left");
    }

    private static async Task<Ok<PlacementSuggestionResponse>> Suggest(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, IPlacementService placement, string? kind, double? cpu, long? memory, Guid? workloadId,
        CancellationToken ct)
    {
        http.RejectUnknownQuery("kind", "cpu", "memory", "workloadId");
        var org = actor.Org();
        if (cpu is <= 0 or > 1024) throw new ApiProblemException(ApiProblems.InvalidParameter("cpu", "Must be more than 0 and at most 1024 cores."));
        if (memory is < 0) throw new ApiProblemException(ApiProblems.InvalidParameter("memory", "Must be a number of bytes, 0 or more."));

        PlacementKind placementKind;
        Guid? exclude = null;
        if (workloadId is { } id)
        {
            var workload = await db.WorkloadsOf(org).AsNoTracking().Include(w => w.Volumes).FirstOrDefaultAsync(w => w.Id == id, ct)
                           ?? throw new ApiProblemException(ApiProblems.NotFound("application", id));
            placementKind = PlacementRules.KindOf(workload);
            cpu ??= PlacementRules.CpuOf(workload.Runtime);
            memory ??= PlacementRules.MemoryOf(workload.Runtime);
            exclude = id;
        }
        else
        {
            if (kind is null) throw new ApiProblemException(ApiProblems.InvalidParameter("kind", "Required: one of " + EnumText.Names<PlacementKind>() + ".", "required"));
            placementKind = EnumText.Parse<PlacementKind>(kind)
                            ?? throw new ApiProblemException(ApiProblems.InvalidParameter("kind", "Must be one of: " + EnumText.Names<PlacementKind>() + ".", "invalid_enum"));
        }

        var ranked = await placement.RankAsync(new PlacementRequest(org, placementKind, cpu, memory, exclude), ct);
        var best = ranked.FirstOrDefault(c => c.Eligible);
        var message = best is null
            ? ranked.Count == 0 ? "The organization has no servers yet." : "No server accepts this right now; see the reasons of each."
            : null;
        return TypedResults.Ok(new PlacementSuggestionResponse(
            placementKind, best?.ServerId,
            ranked.Select(c => new PlacementCandidateResponse(c.ServerId, c.ServerName, c.Eligible, c.Score, c.Reasons, c.CpuFreeCores, c.MemoryFreeBytes)).ToList(),
            message));
    }

    private static async Task<Ok<IReadOnlyList<ServerCapacityResponse>>> Capacity(HttpContext http, ICurrentActor actor, ServerCapacityReader capacities, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        var servers = await capacities.ReadAsync(actor.Org(), null, null, ct);
        return TypedResults.Ok<IReadOnlyList<ServerCapacityResponse>>(servers.Select(ToResponse).ToList());
    }

    internal static ServerCapacityResponse ToResponse(ServerCapacity s) => new(
        s.ServerId, s.Name, s.Lifecycle, s.Roles, s.Connected, s.Cores, s.MemoryBytes, s.DiskBytes,
        s.Usage is { } u ? new CapacityUsageResponse(u.CpuPercent, u.MemoryUsedBytes, u.MemoryTotalBytes, u.DiskUsedBytes, u.DiskTotalBytes, u.ObservedAt) : null,
        new AllocationResponse(s.CpuReserved, s.MemoryReservedBytes), new AllocationResponse(s.CpuLimits, s.MemoryLimitsBytes),
        new AllocationResponse(s.CpuCommitted, s.MemoryCommittedBytes), new FreeCapacityResponse(s.CpuFree, s.MemoryFree, s.DiskFree),
        s.WorkloadCount, s.OverCommitted, s.Warnings);

    internal static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
