using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Placement;

/// <summary>Servers with known facts, status and roles, straight in the database (the API has no way to say a server's agent is connected).</summary>
internal static class PlacementTestSupport
{
    public const long Gib = 1L << 30;

    private static int _next = 10;

    public static async Task<Guid> SeedServerAsync(
        this Tenant tenant, string name, ServerRole[]? roles = null, long memoryGib = 8, int cores = 4, long diskGib = 100,
        ServerLifecycle lifecycle = ServerLifecycle.Active, bool connected = true, string? publicIp = null, long? diskUsedGib = null,
        double? cpuPercent = null, long? memoryUsedGib = null)
    {
        await using var scope = tenant.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var server = new Server
        {
            OrganizationId = tenant.Identity.OrganizationId, Name = name, Host = $"{name}.example.com", Roles = roles?.ToList() ?? [],
            Lifecycle = lifecycle, PublicIp = publicIp ?? $"203.0.113.{Interlocked.Increment(ref _next)}",
            Facts = new ServerFacts { CpuCores = cores, MemoryBytes = memoryGib * Gib, DiskBytes = diskGib * Gib },
        };
        if (connected) server.RecordHeartbeat(now);
        db.Servers.Add(server);
        if (diskUsedGib is not null || cpuPercent is not null || memoryUsedGib is not null)
            db.MetricSamples.Add(new MetricSample
            {
                ServerId = server.Id, Timestamp = now, CpuPercent = cpuPercent, MemoryUsedBytes = memoryUsedGib * Gib, MemoryTotalBytes = memoryGib * Gib,
                DiskUsedBytes = diskUsedGib * Gib, DiskTotalBytes = diskGib * Gib,
            });
        await db.SaveChangesAsync();
        return server.Id;
    }

    /// <summary>An application with a memory reservation (and optional CPU) on the given server.</summary>
    public static async Task<string> ReservedApplicationAsync(this Tenant tenant, string environmentId, Guid serverId, long memoryGib, double? cpu = null, long? limitGib = null) =>
        (await tenant.CreateApplicationAsync(environmentId, serverId.ToString(), extra: new
        {
            runtime = new
            {
                resources = new { memoryReservationBytes = memoryGib * Gib, memoryLimitBytes = limitGib * Gib, cpuReservationCores = cpu },
            },
        })).Id();

    public static string Suggest(string kind, long? memoryBytes = null, double? cpu = null, Guid? workloadId = null) =>
        $"/api/v1/placement/suggest?kind={kind}" + (memoryBytes is null ? "" : $"&memory={memoryBytes}") + (cpu is null ? "" : $"&cpu={cpu}")
        + (workloadId is null ? "" : $"&workloadId={workloadId}");
}
