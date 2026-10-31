using Aethera.Domain;

namespace Aethera.Infrastructure.Placement;

/// <summary>
/// <see cref="IPlacementService"/> as ADR 0012 describes it. A server is <b>not eligible</b> when its lifecycle is not Active, its agent is not
/// connected, its roles exclude the kind, or (when the request names a memory reservation and the server's memory is known) the unreserved
/// memory is smaller. Eligible servers are ranked by unreserved memory, then free disk, then fewer workloads; a server that only accepts the
/// kind because it is the control plane's own machine (<see cref="RoleFit.MasterFallback"/>) comes after the ones that have the role. Every
/// candidate carries its reasons.
/// </summary>
public sealed class PlacementService(ServerCapacityReader capacities) : IPlacementService
{
    public async Task<IReadOnlyList<PlacementCandidate>> RankAsync(PlacementRequest request, CancellationToken ct)
    {
        var servers = await capacities.ReadAsync(request.OrganizationId, request.ExcludeWorkloadId, null, ct);
        return Rank(servers, request);
    }

    /// <summary>The ranking itself, over capacities already read (pure; exposed for tests).</summary>
    public static IReadOnlyList<PlacementCandidate> Rank(IReadOnlyList<ServerCapacity> servers, PlacementRequest request)
    {
        var judged = servers.Select(s => Judge(s, request)).ToList();

        var eligible = judged.Where(j => j.Eligible)
            .OrderBy(j => j.Fit == RoleFit.MasterFallback ? 1 : 0)
            .ThenByDescending(j => j.Capacity.MemoryFree ?? long.MinValue)
            .ThenByDescending(j => j.Capacity.DiskFree ?? long.MinValue)
            .ThenBy(j => j.Capacity.WorkloadCount)
            .ThenBy(j => j.Capacity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rejected = judged.Where(j => !j.Eligible).OrderBy(j => j.Capacity.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // The score is the position among the eligible servers (the best has the highest); there is no meaningful number to compare across rankings.
        var result = new List<PlacementCandidate>(judged.Count);
        for (var i = 0; i < eligible.Count; i++)
            result.Add(ToCandidate(eligible[i], eligible.Count - i));
        result.AddRange(rejected.Select(j => ToCandidate(j, 0)));
        return result;
    }

    private sealed record Judgement(ServerCapacity Capacity, RoleFit Fit, bool Eligible, List<string> Reasons);

    private static PlacementCandidate ToCandidate(Judgement j, double score) =>
        new(j.Capacity.ServerId, j.Capacity.Name, j.Eligible, score, j.Reasons, j.Capacity.CpuFree, j.Capacity.MemoryFree);

    private static Judgement Judge(ServerCapacity s, PlacementRequest request)
    {
        var fit = PlacementRules.Fit(s.Roles, request.Kind);
        var blockers = new List<string>();
        var notes = new List<string>();

        if (s.Lifecycle != ServerLifecycle.Active)
            blockers.Add($"The server is {s.Lifecycle.ToString().ToLowerInvariant()}, not active.");
        if (!s.Connected)
            blockers.Add(s.Transport == ServerTransport.Ssh ? "The server cannot be reached over SSH." : "The agent is not connected.");
        if (fit == RoleFit.Excluded)
            blockers.Add($"Its roles ({PlacementRules.RoleNames(s.Roles)}) do not include {PlacementRules.RequiredRole(request.Kind).ToString().ToLowerInvariant()}, which {PlacementRules.KindName(request.Kind)} needs.");

        if (request.MemoryBytes is { } needed && s.MemoryFree is { } free && free < needed)
            blockers.Add($"Only {PlacementRules.Bytes(Math.Max(0, free))} of memory is unreserved here; {PlacementRules.Bytes(needed)} are needed.");

        switch (fit)
        {
            case RoleFit.Open: notes.Add("It has no roles, so it accepts everything."); break;
            case RoleFit.Matches: notes.Add($"It has the {PlacementRules.RequiredRole(request.Kind).ToString().ToLowerInvariant()} role."); break;
            case RoleFit.MasterFallback: notes.Add("It is the control plane's own machine; a server with the right role is preferred."); break;
        }

        if (s.MemoryFree is { } memoryFree && s.MemoryBytes is { } memory)
            notes.Add($"{PlacementRules.Bytes(Math.Max(0, memoryFree))} of {PlacementRules.Bytes(memory)} memory is unreserved.");
        else
            notes.Add("Its memory is not known yet.");
        if (s.DiskFree is { } disk) notes.Add($"{PlacementRules.Bytes(disk)} of disk is free.");
        notes.Add(s.WorkloadCount == 1 ? "1 workload is placed there." : $"{s.WorkloadCount} workloads are placed there.");
        if (request.CpuCores is { } cpu && s.CpuFree is { } cpuFree && cpuFree < cpu)
            notes.Add($"CPU would be over-committed: {Math.Max(0, cpuFree):0.##} of {s.Cores} cores are unreserved, {cpu:0.##} are needed.");

        // Blockers first: they are why the server is not chosen.
        return new Judgement(s, fit, blockers.Count == 0, [.. blockers, .. notes]);
    }
}
