using Aethera.Domain;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Ingest;

/// <summary>
/// Turns a <c>MetricsReport</c> into raw <see cref="MetricSample"/> rows: one host row (<c>container_id</c> null) and one row per container.
/// Counters (network bytes) stay cumulative as the agent reports them; rates are derived when reading. Gaps are fine: the UI shows "no
/// data" rather than interpolating (ADR 0002).
/// </summary>
public sealed class AgentMetricsIngestor(IServiceScopeFactory scopes, IClock clock)
{
    /// <summary>A report whose own timestamp is further than this from our clock is stamped with our time (skewed agent clock).</summary>
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan WorkloadCacheTtl = TimeSpan.FromMinutes(1);

    public async Task IngestAsync(ServerAgentState state, P.MetricsReport report, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var timestamp = report.CollectedAt is { Seconds: > 0 } collected && (collected.ToDateTimeOffset() - now).Duration() <= MaxSkew ? collected.ToDateTimeOffset() : now;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var workloads = await WorkloadIdsAsync(state, db, now, cancellationToken);

        var rows = Map(state.ServerId, report, timestamp, workloads);
        if (rows.Count == 0) return;
        db.MetricSamples.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Pure mapping, exposed for tests.</summary>
    public static List<MetricSample> Map(Guid serverId, P.MetricsReport report, DateTimeOffset timestamp, ISet<Guid>? knownWorkloads)
    {
        var rows = new List<MetricSample>();
        if (report.Host is { } host)
        {
            var disk = host.Disks.FirstOrDefault(d => d.MountPoint == "/") ?? host.Disks.OrderByDescending(d => d.TotalBytes).FirstOrDefault();
            var physical = host.Interfaces.Where(i => i.Name != "lo");
            rows.Add(new MetricSample
            {
                ServerId = serverId, Timestamp = timestamp, Resolution = MetricResolution.Raw,
                CpuPercent = host.CpuPercent, MemoryUsedBytes = host.MemoryUsedBytes, MemoryTotalBytes = host.MemoryTotalBytes,
                DiskUsedBytes = disk?.UsedBytes, DiskTotalBytes = disk?.TotalBytes,
                NetRxBytes = Clamp(physical.Sum(i => (decimal)i.RxBytes)), NetTxBytes = Clamp(physical.Sum(i => (decimal)i.TxBytes)),
                Load1 = host.Load1, Load5 = host.Load5, Load15 = host.Load15,
            });
        }

        foreach (var container in report.Containers)
        {
            if (container.ContainerId.Length == 0) continue;
            rows.Add(new MetricSample
            {
                ServerId = serverId, ContainerId = Cut(container.ContainerId, 128), WorkloadId = WorkloadOf(container.Labels, knownWorkloads), Timestamp = timestamp,
                Resolution = MetricResolution.Raw, CpuPercent = container.CpuPercent, MemoryUsedBytes = container.MemoryUsedBytes,
                MemoryTotalBytes = container.MemoryLimitBytes > 0 ? container.MemoryLimitBytes : null,
                NetRxBytes = Clamp(container.NetRxBytes), NetTxBytes = Clamp(container.NetTxBytes),
            });
        }

        return rows;
    }

    /// <summary>The workload a container belongs to, from the <c>aethera.*</c> labels, only when it is a workload of this server.</summary>
    public static Guid? WorkloadOf(IDictionary<string, string> labels, ISet<Guid>? known)
    {
        foreach (var key in (ReadOnlySpan<string>)["aethera.application.id", "aethera.service.id", "aethera.workload.id"])
            if (labels.TryGetValue(key, out var value) && Guid.TryParse(value, out var id) && known?.Contains(id) == true)
                return id;
        return null;
    }

    internal static async Task<ISet<Guid>> WorkloadIdsAsync(ServerAgentState state, AetheraDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.WorkloadIds is { } cached && now - state.WorkloadIdsLoadedAt < WorkloadCacheTtl) return cached;
        var ids = await db.Workloads.IgnoreQueryFilters().AsNoTracking().Where(w => w.ServerId == state.ServerId).Select(w => w.Id).ToListAsync(cancellationToken);
        state.WorkloadIds = [.. ids];
        state.WorkloadIdsLoadedAt = now;
        return state.WorkloadIds;
    }

    private static long Clamp(decimal value) => value >= long.MaxValue ? long.MaxValue : (long)value;

    private static long Clamp(ulong value) => value >= long.MaxValue ? long.MaxValue : (long)value;

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
