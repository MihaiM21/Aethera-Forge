using Aethera.Agent.V1;
using Aethera.Domain;
using DomainDockerStatus = Aethera.Domain.DockerStatus;
using ProtoDockerStatus = Aethera.Agent.V1.DockerStatus;

namespace Aethera.Infrastructure.Agents;

/// <summary>Copies what an agent reports about its machine (spec section 55) into the server's discovered-facts columns.</summary>
public static class AgentFacts
{
    public static void Apply(Server server, HostFacts? host, DateTimeOffset now)
    {
        if (host is null) return;
        var facts = server.Facts;
        if (host.OsName.Length > 0) facts.Os = Cut(host.OsName, 100);
        if (host.OsVersion.Length > 0) facts.OsVersion = Cut(host.OsVersion, 100);
        if (host.KernelVersion.Length > 0) facts.Kernel = Cut(host.KernelVersion, 100);
        if (host.Architecture.Length > 0) facts.Architecture = Cut(host.Architecture, 32);
        if (host.CpuModel.Length > 0) facts.CpuModel = Cut(host.CpuModel, 200);
        if (host.CpuCoresLogical > 0) facts.CpuCores = (int)Math.Min(host.CpuCoresLogical, 4096);
        if (host.MemoryTotalBytes > 0) facts.MemoryBytes = host.MemoryTotalBytes;
        facts.DiscoveredAt = now;
    }

    public static void Apply(Server server, DiscoveryReport report, DateTimeOffset now)
    {
        Apply(server, report.Host, now);
        if (report.Docker is { Version.Length: > 0 } docker) server.Facts.DockerVersion = Cut(docker.Version, 64);
        // Capacity of the disk that matters: the root file system, else the largest one.
        var disk = report.Disks.FirstOrDefault(d => d.MountPoint == "/") ?? report.Disks.OrderByDescending(d => d.TotalBytes).FirstOrDefault();
        if (disk is { TotalBytes: > 0 }) server.Facts.DiskBytes = disk.TotalBytes;
    }

    public static DomainDockerStatus ToDomain(ProtoDockerStatus status) => status switch
    {
        ProtoDockerStatus.Running => DomainDockerStatus.Running,
        ProtoDockerStatus.Stopped => DomainDockerStatus.Stopped,
        ProtoDockerStatus.Unreachable => DomainDockerStatus.Unreachable,
        ProtoDockerStatus.NotInstalled => DomainDockerStatus.NotInstalled,
        ProtoDockerStatus.PermissionDenied => DomainDockerStatus.PermissionDenied,
        _ => DomainDockerStatus.Unknown,
    };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
