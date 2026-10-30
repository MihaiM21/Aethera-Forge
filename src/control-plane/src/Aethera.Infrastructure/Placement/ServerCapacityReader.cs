using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Placement;

/// <summary>The newest host sample of a server (CPU is the share of all cores in use).</summary>
public sealed record CapacityUsage(
    double? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes, long? DiskUsedBytes, long? DiskTotalBytes, DateTimeOffset ObservedAt);

/// <summary>
/// What a server has and what is promised on it (ADR 0012): the facts the agent discovered, the latest usage, and the sums over the non-deleted
/// workloads placed there. A workload's <i>committed</i> share is its reservation, else its limit, else nothing.
/// </summary>
public sealed record ServerCapacity(
    Guid ServerId, string Name, ServerLifecycle Lifecycle, ServerTransport Transport, IReadOnlyList<ServerRole> Roles, bool Connected,
    int? Cores, long? MemoryBytes, long? DiskBytes, CapacityUsage? Usage,
    double CpuReserved, double CpuLimits, double CpuCommitted, long MemoryReservedBytes, long MemoryLimitsBytes, long MemoryCommittedBytes,
    int WorkloadCount, bool OverCommitted, IReadOnlyList<string> Warnings)
{
    /// <summary>Cores left after the committed share; null when the server's cores are unknown.</summary>
    public double? CpuFree => Cores is { } cores ? cores - CpuCommitted : null;

    /// <summary>Memory left after the committed share; null when the server's memory is unknown.</summary>
    public long? MemoryFree => MemoryBytes is { } memory ? memory - MemoryCommittedBytes : null;

    /// <summary>Disk free now, from the latest sample, else the facts' size when nothing was sampled (then it is the whole disk).</summary>
    public long? DiskFree => Usage is { DiskTotalBytes: { } total, DiskUsedBytes: { } used } ? total - used : null;
}

/// <summary>Reads <see cref="ServerCapacity"/> for the servers of an organization. Shared by the placement service and the capacity endpoint.</summary>
public sealed class ServerCapacityReader(AetheraDbContext db)
{
    /// <summary>
    /// A server counts as connected when its agent session is up, or, for an SSH server, when the control plane's probe does not say it is down.
    /// </summary>
    public static bool IsConnected(Server s) =>
        s.Transport == ServerTransport.Ssh ? s.ReachabilityStatus != ReachabilityStatus.Unreachable : s.AgentStatus == AgentStatus.Connected;

    /// <param name="excludeWorkloadId">A workload being moved: its own reservation does not count against the server it is on.</param>
    /// <param name="serverId">Only this server.</param>
    public async Task<IReadOnlyList<ServerCapacity>> ReadAsync(Guid organizationId, Guid? excludeWorkloadId, Guid? serverId, CancellationToken ct)
    {
        var servers = await db.Servers.AsNoTracking().Where(s => s.OrganizationId == organizationId && (serverId == null || s.Id == serverId))
            .OrderBy(s => s.Name).ToListAsync(ct);
        if (servers.Count == 0) return [];
        var ids = servers.Select(s => s.Id).ToList();

        var sums = (await db.Workloads.AsNoTracking()
                .Where(w => ids.Contains(w.ServerId) && w.Id != excludeWorkloadId)
                .GroupBy(w => w.ServerId)
                .Select(g => new
                {
                    ServerId = g.Key,
                    Count = g.Count(),
                    CpuReserved = g.Sum(w => w.Runtime.CpuReservation ?? 0),
                    CpuLimits = g.Sum(w => w.Runtime.CpuLimit ?? 0),
                    CpuCommitted = g.Sum(w => w.Runtime.CpuReservation ?? w.Runtime.CpuLimit ?? 0),
                    MemoryReserved = g.Sum(w => w.Runtime.MemoryReservationBytes ?? 0),
                    MemoryLimits = g.Sum(w => w.Runtime.MemoryLimitBytes ?? 0),
                    MemoryCommitted = g.Sum(w => w.Runtime.MemoryReservationBytes ?? w.Runtime.MemoryLimitBytes ?? 0),
                })
                .ToListAsync(ct))
            .ToDictionary(x => x.ServerId);

        var result = new List<ServerCapacity>(servers.Count);
        foreach (var server in servers)
        {
            var usage = await LatestUsageAsync(server.Id, ct);
            var sum = sums.GetValueOrDefault(server.Id);
            var cpuCommitted = sum?.CpuCommitted ?? 0;
            var memoryCommitted = sum?.MemoryCommitted ?? 0;
            var cores = server.Facts.CpuCores;
            var memory = server.Facts.MemoryBytes;
            var cpuOver = cores is { } c && cpuCommitted > c;
            var memoryOver = memory is { } m && memoryCommitted > m;

            var warnings = new List<string>();
            if (cpuOver) warnings.Add($"CPU reservations add up to {cpuCommitted:0.##} cores on a server with {cores}.");
            if (memoryOver) warnings.Add($"Memory reservations add up to {PlacementRules.Bytes(memoryCommitted)} on a server with {PlacementRules.Bytes(memory!.Value)}.");
            // Limits above the machine are normal (they cap bursts), but worth saying: all of them used at once would not fit.
            if (!memoryOver && memory is { } total && (sum?.MemoryLimits ?? 0) > total)
                warnings.Add($"Memory limits add up to {PlacementRules.Bytes(sum!.MemoryLimits)}, more than the {PlacementRules.Bytes(total)} of the server; they will not all fit if every workload peaks.");

            result.Add(new ServerCapacity(
                server.Id, server.Name, server.Lifecycle, server.Transport, server.Roles.OrderBy(r => r).ToList(), IsConnected(server),
                cores, memory, server.Facts.DiskBytes, usage,
                sum?.CpuReserved ?? 0, sum?.CpuLimits ?? 0, cpuCommitted, sum?.MemoryReserved ?? 0, sum?.MemoryLimits ?? 0, memoryCommitted,
                sum?.Count ?? 0, cpuOver || memoryOver, warnings));
        }

        return result;
    }

    private async Task<CapacityUsage?> LatestUsageAsync(Guid serverId, CancellationToken ct) =>
        await db.MetricSamples.AsNoTracking()
            .Where(m => m.ServerId == serverId && m.ContainerId == null && m.Resolution == MetricResolution.Raw)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => new CapacityUsage(m.CpuPercent, m.MemoryUsedBytes, m.MemoryTotalBytes, m.DiskUsedBytes, m.DiskTotalBytes, m.Timestamp))
            .FirstOrDefaultAsync(ct);
}
