using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Aethera.Api.Features.Resources;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Placement;

namespace Aethera.Api.Features.Placement;

/// <summary>Where a new workload goes, and what to tell the caller about the choice.</summary>
/// <param name="Automatic">True when Aethera chose the server (the request named none).</param>
public sealed record PlacementOutcome(Server Server, bool Automatic, IReadOnlyList<string> Warnings);

/// <summary>The server choice of creating an application or a service (ADR 0012): an explicit server wins and is checked, none asks the placement service.</summary>
public static class WorkloadPlacement
{
    /// <param name="requested">The server named in the request, already looked up in the organization; null = choose.</param>
    /// <param name="cpuCores">The CPU reservation (else limit) the workload asks for, if any.</param>
    /// <param name="memoryBytes">The memory reservation (else limit) the workload asks for, if any.</param>
    /// <exception cref="ApiProblemException">422 <c>placement.no_eligible_server</c>: no server was named and none accepts the workload.</exception>
    public static async Task<PlacementOutcome> ChooseAsync(
        AetheraDbContext db, Guid organizationId, Server? requested, PlacementKind kind, double? cpuCores, long? memoryBytes,
        IPlacementService placement, ServerCapacityReader capacities, CancellationToken ct)
    {
        if (requested is not null)
        {
            var warnings = new List<string>();
            if (PlacementRules.RoleWarning(requested.Name, requested.Roles, kind) is { } roleWarning) warnings.Add(roleWarning);

            // An explicit choice is never refused for lack of room, but it is told when the server gets over-committed by it.
            if ((await capacities.ReadAsync(organizationId, null, requested.Id, ct)).FirstOrDefault() is { } capacity)
            {
                if (memoryBytes is { } memory && capacity.MemoryFree is { } free && free < memory)
                    warnings.Add($"{requested.Name} has {PlacementRules.Bytes(Math.Max(0, free))} of memory unreserved and this asks for {PlacementRules.Bytes(memory)}: the server will be over-committed.");
                if (cpuCores is { } cpu && capacity.CpuFree is { } cpuFree && cpuFree < cpu)
                    warnings.Add($"{requested.Name} has {Math.Max(0, cpuFree):0.##} CPU cores unreserved and this asks for {cpu:0.##}: the server will be over-committed.");
            }

            return new PlacementOutcome(requested, false, warnings);
        }

        var ranked = await placement.RankAsync(new PlacementRequest(organizationId, kind, cpuCores, memoryBytes), ct);
        var best = ranked.FirstOrDefault(c => c.Eligible);
        if (best is null) throw new ApiProblemException(NoEligibleServer(kind, ranked));

        // The ranking read the servers a moment ago; the entity is needed to create the workload.
        var server = await db.FindServerAsync(organizationId, best.ServerId, ct)
                     ?? throw new ApiProblemException(NoEligibleServer(kind, ranked));
        return new PlacementOutcome(server, true, []);
    }

    /// <summary>The 422 for "no server accepts it": names every server and why not, so the caller can fix it or choose by hand.</summary>
    public static ApiProblem NoEligibleServer(PlacementKind kind, IReadOnlyList<PlacementCandidate> ranked)
    {
        var detail = ranked.Count == 0
            ? $"The organization has no server to place {PlacementRules.KindName(kind)} on. Add a server first."
            : $"No server accepts {PlacementRules.KindName(kind)} right now. " + string.Join(" ", ranked.Select(c => $"{c.ServerName}: {c.Reasons.FirstOrDefault()}")) +
              " Fix that, or choose a server yourself.";
        return new ApiProblem(StatusCodes.Status422UnprocessableEntity, PlacementProblemCodes.NoEligibleServer, detail)
            .WithExtension("errors", new[] { FieldError.AtPointer("/serverId", "no_eligible_server", detail) })
            .WithExtension("candidates", ranked.Select(c => new PlacementCandidateResponse(c.ServerId, c.ServerName, c.Eligible, c.Score, c.Reasons, c.CpuFreeCores, c.MemoryFreeBytes)).ToList());
    }
}
