namespace Aethera.Domain;

/// <summary>What is being placed (ADR 0012): it decides which server roles accept it.</summary>
public enum PlacementKind
{
    /// <summary>An application: <see cref="ServerRole.Worker"/> servers.</summary>
    Application = 0,

    /// <summary>A database or a stateful service (a template with a data volume): <see cref="ServerRole.Storage"/> servers.</summary>
    StatefulService = 1,

    /// <summary>A service without data (Grafana, an admin UI): <see cref="ServerRole.Worker"/> servers.</summary>
    StatelessService = 2,

    /// <summary>A build: <see cref="ServerRole.Build"/> servers.</summary>
    Build = 3,
}

/// <param name="CpuCores">The workload's CPU reservation (or limit); null = nothing reserved.</param>
/// <param name="MemoryBytes">The workload's memory reservation (or limit); null = nothing reserved.</param>
/// <param name="ExcludeWorkloadId">A workload being moved: its own reservation does not count against its current server.</param>
public sealed record PlacementRequest(
    Guid OrganizationId, PlacementKind Kind, double? CpuCores = null, long? MemoryBytes = null, Guid? ExcludeWorkloadId = null);

/// <summary>One server judged for a <see cref="PlacementRequest"/>. Reasons are short sentences for the UI, in order of importance.</summary>
/// <param name="Score">Higher is better; meaningless when not <paramref name="Eligible"/>.</param>
/// <param name="MemoryFreeBytes">Memory left after the reservations of what is placed there; null when the server's memory is unknown.</param>
public sealed record PlacementCandidate(
    Guid ServerId, string ServerName, bool Eligible, double Score, IReadOnlyList<string> Reasons,
    double? CpuFreeCores = null, long? MemoryFreeBytes = null);

/// <summary>Chooses servers for workloads and builds (ADR 0012, "Roles and placement").</summary>
public interface IPlacementService
{
    /// <summary>Every active server of the organisation, eligible ones first, best first.</summary>
    Task<IReadOnlyList<PlacementCandidate>> RankAsync(PlacementRequest request, CancellationToken ct);
}
