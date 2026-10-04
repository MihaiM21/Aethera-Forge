using System.Globalization;
using System.Text.Json;
using Aethera.Domain;
using Google.Protobuf.WellKnownTypes;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Ssh.Metrics;

/// <summary>The read-only commands one poll runs (ADR 0002: <c>/proc/stat</c>, <c>meminfo</c>, <c>loadavg</c>, <c>df -P</c>, <c>docker stats</c>, <c>docker ps</c>), as one fixed script.</summary>
public static class SshMetricsScript
{
    public const string Cpu = "##cpu", Mem = "##mem", Load = "##load", Uptime = "##uptime", Net = "##net", Df = "##df", Nproc = "##nproc", Docker = "##docker", Stats = "##stats", Ps = "##ps";

    /// <summary>
    /// A fixed script without any variable part. Two samples of <c>/proc/stat</c> one second apart give the CPU utilisation without keeping
    /// state between polls. Docker problems never fail the poll: its state is reported in the <c>##docker</c> section.
    /// </summary>
    public const string Line = """
        sh -c 'echo "##cpu"; head -n 1 /proc/stat; sleep 1; head -n 1 /proc/stat; echo "##mem"; cat /proc/meminfo; echo "##load"; cat /proc/loadavg; echo "##uptime"; cat /proc/uptime; echo "##net"; cat /proc/net/dev; echo "##df"; df -P -k 2>/dev/null; echo "##nproc"; getconf _NPROCESSORS_ONLN 2>/dev/null || nproc 2>/dev/null; echo "##docker"; docker info --format "{{.ServerVersion}}" 2>&1 | head -n 3; echo "##stats"; docker stats --no-stream --no-trunc --format "{{json .}}" 2>/dev/null; echo "##ps"; docker ps --no-trunc --format "{{json .}}" 2>/dev/null; true'
        """;
}

public sealed record PolledContainer(string Id, string Name, double CpuPercent, long MemoryUsedBytes, long MemoryLimitBytes, ulong NetRxBytes, ulong NetTxBytes,
    ulong BlockReadBytes, ulong BlockWriteBytes, uint Pids, IReadOnlyDictionary<string, string> Labels, string State);

public sealed record PolledDisk(string MountPoint, string Device, long TotalBytes, long UsedBytes);

public sealed record PolledMetrics(
    double CpuPercent, int Cores, double Load1, double Load5, double Load15, long MemoryTotalBytes, long MemoryUsedBytes, long MemoryAvailableBytes,
    long SwapTotalBytes, long SwapUsedBytes, IReadOnlyList<PolledDisk> Disks, ulong NetRxBytes, ulong NetTxBytes, TimeSpan Uptime,
    DockerStatus DockerStatus, string? DockerVersion, IReadOnlyList<PolledContainer> Containers);

/// <summary>Parsers for the output of <see cref="SshMetricsScript"/>; pure functions over text, tested with fixtures.</summary>
public static class SshMetricsParsers
{
    private static CultureInfo Inv => CultureInfo.InvariantCulture;

    public static IReadOnlyDictionary<string, List<string>> Sections(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("##", StringComparison.Ordinal) && line.Length < 12)
            {
                current = [];
                sections[line] = current;
            }
            else if (line.Length > 0)
            {
                current?.Add(line);
            }
        }

        return sections;
    }

    public static PolledMetrics Parse(string output)
    {
        var sections = Sections(output);
        List<string> Get(string key) => sections.TryGetValue(key, out var list) ? list : [];

        var cpu = Get(SshMetricsScript.Cpu);
        var mem = ParseMemInfo(Get(SshMetricsScript.Mem));
        var load = ParseLoadAvg(Get(SshMetricsScript.Load).FirstOrDefault());
        var (rx, tx) = ParseNetDev(Get(SshMetricsScript.Net));
        var cores = int.TryParse(Get(SshMetricsScript.Nproc).FirstOrDefault(), NumberStyles.None, Inv, out var n) ? n : 0;
        var uptime = double.TryParse(Get(SshMetricsScript.Uptime).FirstOrDefault()?.Split(' ')[0], NumberStyles.Float, Inv, out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        var (dockerStatus, dockerVersion) = ParseDockerState(Get(SshMetricsScript.Docker));

        var labelsByContainer = ParsePs(Get(SshMetricsScript.Ps));
        var containers = ParseStats(Get(SshMetricsScript.Stats), labelsByContainer);

        return new PolledMetrics(
            cpu.Count >= 2 ? CpuPercent(cpu[0], cpu[1]) : 0, cores, load.Item1, load.Item2, load.Item3,
            mem.Total, Math.Max(0, mem.Total - mem.Available), mem.Available, mem.SwapTotal, Math.Max(0, mem.SwapTotal - mem.SwapFree),
            ParseDf(Get(SshMetricsScript.Df)), rx, tx, uptime, dockerStatus, dockerVersion, containers);
    }

    /// <summary>Utilisation between two <c>cpu</c> lines of <c>/proc/stat</c>: busy time over total time, 0 to 100.</summary>
    public static double CpuPercent(string first, string second)
    {
        var a = CpuTimes(first);
        var b = CpuTimes(second);
        if (a is null || b is null) return 0;
        var total = b.Value.Total - a.Value.Total;
        var idle = b.Value.Idle - a.Value.Idle;
        if (total <= 0) return 0;
        return Math.Clamp((total - idle) / (double)total * 100, 0, 100);
    }

    private static (long Total, long Idle)? CpuTimes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[0] != "cpu") return null;
        var values = parts.Skip(1).Take(8).Select(p => long.TryParse(p, NumberStyles.None, Inv, out var v) ? v : 0).ToArray();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0); // idle + iowait
        return (values.Sum(), idle); // guest time is already counted in user, so only the first 8 columns are summed
    }

    public static (long Total, long Available, long SwapTotal, long SwapFree) ParseMemInfo(IEnumerable<string> lines)
    {
        long total = 0, available = -1, free = 0, buffers = 0, cached = 0, swapTotal = 0, swapFree = 0;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon];
            var number = line[(colon + 1)..].Trim().Split(' ')[0];
            if (!long.TryParse(number, NumberStyles.None, Inv, out var kb)) continue;
            var bytes = kb * 1024;
            switch (key)
            {
                case "MemTotal": total = bytes; break;
                case "MemAvailable": available = bytes; break;
                case "MemFree": free = bytes; break;
                case "Buffers": buffers = bytes; break;
                case "Cached": cached = bytes; break;
                case "SwapTotal": swapTotal = bytes; break;
                case "SwapFree": swapFree = bytes; break;
            }
        }

        if (available < 0) available = free + buffers + cached; // kernels before 3.14
        return (total, Math.Min(available, total), swapTotal, swapFree);
    }

    public static (double, double, double) ParseLoadAvg(string? line)
    {
        var parts = (line ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double At(int i) => parts.Length > i && double.TryParse(parts[i], NumberStyles.Float, Inv, out var v) ? v : 0;
        return (At(0), At(1), At(2));
    }

    /// <summary>Sum of the received and transmitted bytes of every interface but loopback and virtual container bridges.</summary>
    public static (ulong Rx, ulong Tx) ParseNetDev(IEnumerable<string> lines)
    {
        ulong rx = 0, tx = 0;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            if (name == "lo" || name.StartsWith("veth", StringComparison.Ordinal) || name.StartsWith("docker", StringComparison.Ordinal) || name.StartsWith("br-", StringComparison.Ordinal)) continue;
            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9) continue;
            if (ulong.TryParse(fields[0], NumberStyles.None, Inv, out var r)) rx += r;
            if (ulong.TryParse(fields[8], NumberStyles.None, Inv, out var t)) tx += t;
        }

        return (rx, tx);
    }

    /// <summary><c>df -P -k</c>: real devices and network file systems only (pseudo file systems report no useful capacity).</summary>
    public static IReadOnlyList<PolledDisk> ParseDf(IEnumerable<string> lines)
    {
        var result = new List<PolledDisk>();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || parts[0] == "Filesystem") continue;
            if (!parts[0].StartsWith('/') && !parts[0].Contains(':')) continue;
            if (!long.TryParse(parts[1], NumberStyles.None, Inv, out var total) || !long.TryParse(parts[2], NumberStyles.None, Inv, out var used)) continue;
            var mount = string.Join(' ', parts.Skip(5));
            if (mount.StartsWith("/var/lib/docker/", StringComparison.Ordinal) || mount.StartsWith("/snap/", StringComparison.Ordinal)) continue;
            result.Add(new PolledDisk(mount, parts[0], total * 1024, used * 1024));
        }

        return result;
    }

    public static (DockerStatus Status, string? Version) ParseDockerState(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return (DockerStatus.Unknown, null);
        var text = string.Join('\n', lines);
        if (text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)) return (DockerStatus.PermissionDenied, null);
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) && text.Contains("docker", StringComparison.OrdinalIgnoreCase)) return (DockerStatus.NotInstalled, null);
        if (text.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase) || text.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase)) return (DockerStatus.Stopped, null);
        var version = lines[0].Trim();
        return version.Length is > 0 and < 40 && char.IsDigit(version[0]) ? (DockerStatus.Running, version) : (DockerStatus.Unreachable, null);
    }

    public static Dictionary<string, (string Name, string State, IReadOnlyDictionary<string, string> Labels)> ParsePs(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, (string, string, IReadOnlyDictionary<string, string>)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = Str(root, "ID");
                if (id.Length == 0) continue;
                result[id] = (Str(root, "Names"), Str(root, "State"), ParseLabels(Str(root, "Labels")));
            }
            catch (JsonException)
            {
                // A damaged line only loses that container's labels.
            }
        }

        return result;
    }

    /// <summary><c>docker ps</c> prints labels as <c>k=v,k=v</c>; a comma inside a value splits it (a documented limitation of the text format).</summary>
    public static IReadOnlyDictionary<string, string> ParseLabels(string text)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index > 0) labels[pair[..index]] = pair[(index + 1)..];
        }

        return labels;
    }

    public static List<PolledContainer> ParseStats(IEnumerable<string> lines, IReadOnlyDictionary<string, (string Name, string State, IReadOnlyDictionary<string, string> Labels)> ps)
    {
        var result = new List<PolledContainer>();
        foreach (var line in lines)
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = Str(root, "ID").Length > 0 ? Str(root, "ID") : Str(root, "Container");
                if (id.Length == 0) continue;
                var (memUsed, memLimit) = Pair(Str(root, "MemUsage"));
                var (netRx, netTx) = Pair(Str(root, "NetIO"));
                var (blockRead, blockWrite) = Pair(Str(root, "BlockIO"));
                ps.TryGetValue(id, out var info);
                result.Add(new PolledContainer(
                    id, info.Name ?? Str(root, "Name"), Percent(Str(root, "CPUPerc")), memUsed, memLimit, (ulong)Math.Max(0, netRx), (ulong)Math.Max(0, netTx),
                    (ulong)Math.Max(0, blockRead), (ulong)Math.Max(0, blockWrite), uint.TryParse(Str(root, "PIDs"), NumberStyles.None, Inv, out var pids) ? pids : 0,
                    info.Labels ?? new Dictionary<string, string>(), info.State ?? "running"));
            }
            catch (JsonException)
            {
                // Skip a damaged line.
            }
        }

        return result;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static double Percent(string text) =>
        double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, Inv, out var value) ? value : 0;

    private static (long, long) Pair(string text)
    {
        var parts = text.Split('/', 2);
        return (ParseSize(parts[0]), parts.Length > 1 ? ParseSize(parts[1]) : 0);
    }

    /// <summary>Sizes as Docker prints them: <c>12.5MiB</c>, <c>1.2kB</c>, <c>0B</c>, <c>--</c>. Decimal (kB, MB) and binary (KiB, MiB) units.</summary>
    public static long ParseSize(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text == "--") return 0;
        var index = 0;
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] is '.' or ',')) index++;
        if (!double.TryParse(text[..index].Replace(',', '.'), NumberStyles.Float, Inv, out var value)) return 0;
        var multiplier = text[index..].Trim().ToLowerInvariant() switch
        {
            "b" or "" => 1d,
            "kb" => 1e3, "mb" => 1e6, "gb" => 1e9, "tb" => 1e12, "pb" => 1e15,
            "kib" => 1024d, "mib" => 1024d * 1024, "gib" => 1024d * 1024 * 1024, "tib" => 1024d * 1024 * 1024 * 1024, "pib" => Math.Pow(1024, 5),
            _ => 1d,
        };
        return (long)Math.Min(value * multiplier, long.MaxValue / 2d);
    }

    /// <summary>The same <c>MetricsReport</c> an agent sends, so the SSH poller feeds the existing ingestion path unchanged.</summary>
    public static P.MetricsReport ToReport(PolledMetrics m, DateTimeOffset at)
    {
        var host = new P.HostMetrics
        {
            CpuPercent = m.CpuPercent, CpuCores = (uint)Math.Max(0, m.Cores), Load1 = m.Load1, Load5 = m.Load5, Load15 = m.Load15,
            MemoryTotalBytes = m.MemoryTotalBytes, MemoryUsedBytes = m.MemoryUsedBytes, MemoryAvailableBytes = m.MemoryAvailableBytes,
            SwapTotalBytes = m.SwapTotalBytes, SwapUsedBytes = m.SwapUsedBytes, Uptime = Duration.FromTimeSpan(m.Uptime),
        };
        foreach (var disk in m.Disks) host.Disks.Add(new P.DiskUsage { MountPoint = disk.MountPoint, Device = disk.Device, TotalBytes = disk.TotalBytes, UsedBytes = disk.UsedBytes });
        host.Interfaces.Add(new P.NetworkInterfaceStats { Name = "total", RxBytes = m.NetRxBytes, TxBytes = m.NetTxBytes });

        var report = new P.MetricsReport { CollectedAt = Timestamp.FromDateTimeOffset(at), Host = host };
        foreach (var c in m.Containers)
        {
            var container = new P.ContainerMetrics
            {
                ContainerId = c.Id, Name = c.Name, CpuPercent = c.CpuPercent, MemoryUsedBytes = c.MemoryUsedBytes, MemoryLimitBytes = c.MemoryLimitBytes,
                NetRxBytes = c.NetRxBytes, NetTxBytes = c.NetTxBytes, BlockReadBytes = c.BlockReadBytes, BlockWriteBytes = c.BlockWriteBytes, Pids = c.Pids,
            };
            foreach (var (key, value) in c.Labels) container.Labels[key] = value;
            report.Containers.Add(container);
        }

        return report;
    }
}
