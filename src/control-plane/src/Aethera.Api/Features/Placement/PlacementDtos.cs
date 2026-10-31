using Aethera.Domain;
using FluentValidation;

namespace Aethera.Api.Features.Placement;

public static class PlacementProblemCodes
{
    /// <summary>Creating a workload without a server, and no server accepts it right now.</summary>
    public const string NoEligibleServer = "placement.no_eligible_server";

    public const string MoveConfirmationRequired = "workload.move_confirmation_required";
    public const string MoveInProgress = "workload.deployment_in_progress";
}

// ---- suggestion ---------------------------------------------------------------------------------------------------------------------

/// <summary>One server judged for a placement: <c>reasons</c> start with what rules it out, then what counts for it.</summary>
public sealed record PlacementCandidateResponse(
    Guid ServerId, string ServerName, bool Eligible, double Score, IReadOnlyList<string> Reasons, double? CpuFreeCores, long? MemoryFreeBytes);

/// <summary>The answer of <c>GET /placement/suggest</c>: the best eligible server (null when none) and every server with its reasons, best first.</summary>
public sealed record PlacementSuggestionResponse(
    PlacementKind Kind, Guid? SuggestedServerId, IReadOnlyList<PlacementCandidateResponse> Candidates, string? Message);

// ---- capacity -----------------------------------------------------------------------------------------------------------------------

/// <summary>The newest host sample of a server. <c>cpuPercent</c> is the share of all cores in use (0-100).</summary>
public sealed record CapacityUsageResponse(
    double? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes, long? DiskUsedBytes, long? DiskTotalBytes, DateTimeOffset ObservedAt);

/// <summary>CPU cores and memory promised to workloads.</summary>
public sealed record AllocationResponse(double CpuCores, long MemoryBytes);

/// <summary>What is left: capacity minus <c>committed</c>; null where the server's size is not known yet. <c>diskBytes</c> is the free disk of the latest sample.</summary>
public sealed record FreeCapacityResponse(double? CpuCores, long? MemoryBytes, long? DiskBytes);

/// <summary>
/// A server's capacity and what is promised on it (ADR 0012). <c>reservations</c> and <c>limits</c> are sums over the non-deleted workloads placed
/// there; <c>committed</c> counts each workload's reservation, else its limit, else nothing. <c>overCommitted</c> is true when the committed share
/// exceeds the server's cores or memory.
/// </summary>
public sealed record ServerCapacityResponse(
    Guid ServerId, string Name, ServerLifecycle Lifecycle, IReadOnlyList<ServerRole> Roles, bool Connected,
    int? Cores, long? MemoryBytes, long? DiskBytes, CapacityUsageResponse? Usage,
    AllocationResponse Reservations, AllocationResponse Limits, AllocationResponse Committed, FreeCapacityResponse Free,
    int WorkloadCount, bool OverCommitted, IReadOnlyList<string> Warnings);

// ---- moving a workload --------------------------------------------------------------------------------------------------------------

public sealed record MoveWorkloadRequest
{
    public Guid? ServerId { get; init; }

    /// <summary>Required when the workload has volumes: they stay on the old server, so the new one starts without its data.</summary>
    public bool? Confirm { get; init; }
}

/// <summary>
/// The result of a move. <c>deploymentId</c> and <c>jobId</c> are set when a deployment on the target server was queued (the workload switches
/// servers when it starts and the old containers go after it succeeded); a workload that never ran just changes server. <c>dataStaysBehind</c> is
/// true when it has volumes: <c>volumes</c> names them (Docker volume names or host paths). Back them up first and restore them on the new server.
/// </summary>
public sealed record MoveWorkloadResponse(
    Guid WorkloadId, Guid FromServerId, Guid ToServerId, Guid? DeploymentId, Guid? JobId, bool DataStaysBehind, IReadOnlyList<string> Volumes,
    IReadOnlyList<string> Warnings, string Message);

public sealed class MoveWorkloadValidator : AbstractValidator<MoveWorkloadRequest>
{
    public MoveWorkloadValidator() => RuleFor(x => x.ServerId).NotNull();
}
