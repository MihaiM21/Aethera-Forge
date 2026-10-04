using System.Globalization;
using System.Text.Json;
using Aethera.Domain;
using Google.Protobuf.WellKnownTypes;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Ssh.Metrics;

/// <summary>The read-only facts script of <c>discovery.refresh</c> over SSH, and its parser. Fixed text, no variable parts.</summary>
public static class SshDiscoveryScript
{
    public const string Line = """
        sh -c 'echo "##os"; cat /etc/os-release 2>/dev/null; echo "##uname"; uname -srm; echo "##host"; hostname; echo "##cpu"; grep -m1 "model name" /proc/cpuinfo 2>/dev/null; echo "##nproc"; getconf _NPROCESSORS_ONLN 2>/dev/null || nproc 2>/dev/null; echo "##mem"; cat /proc/meminfo; echo "##df"; df -P -k 2>/dev/null; echo "##docker"; docker info --format "{{json .}}" 2>&1; echo "##dockerv"; docker version --format "{{.Server.APIVersion}}" 2>/dev/null; echo "##compose"; docker compose version --short 2>/dev/null; echo "##buildx"; docker buildx version 2>/dev/null | head -n 1; true'
        """;

    public static P.DiscoveryReport Parse(string output, DateTimeOffset at)
    {
        var sections = SshMetricsParsers.Sections(output);
        List<string> Get(string key) => sections.TryGetValue(key, out var list) ? list : [];

        var osRelease = Get("##os").Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim('"'), StringComparer.Ordinal);
        var uname = (Get("##uname").FirstOrDefault() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mem = SshMetricsParsers.ParseMemInfo(Get("##mem"));
        var cpuModel = Get("##cpu").FirstOrDefault() is { } cpuLine && cpuLine.Contains(':') ? cpuLine[(cpuLine.IndexOf(':') + 1)..].Trim() : "";

        var host = new P.HostFacts
        {
            Hostname = Get("##host").FirstOrDefault() ?? "",
            OsName = osRelease.GetValueOrDefault("NAME", ""),
            OsVersion = osRelease.GetValueOrDefault("VERSION_ID", ""),
            KernelVersion = uname.Length > 1 ? uname[1] : "",
            Architecture = Arch(uname.Length > 2 ? uname[2] : ""),
            CpuModel = cpuModel,
            CpuCoresLogical = uint.TryParse(Get("##nproc").FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var cores) ? cores : 0,
            MemoryTotalBytes = mem.Total,
            SwapTotalBytes = mem.SwapTotal,
        };

        var report = new P.DiscoveryReport { CollectedAt = Timestamp.FromDateTimeOffset(at), Host = host, Docker = Docker(Get("##docker"), Get("##dockerv"), Get("##compose"), Get("##buildx")) };
        foreach (var disk in SshMetricsParsers.ParseDf(Get("##df")))
            report.Disks.Add(new P.DiskUsage { MountPoint = disk.MountPoint, Device = disk.Device, TotalBytes = disk.TotalBytes, UsedBytes = disk.UsedBytes });
        return report;
    }

    public static string Arch(string uname) => uname switch
    {
        "x86_64" or "amd64" => "amd64",
        "aarch64" or "arm64" => "arm64",
        "armv7l" => "arm",
        _ => uname,
    };

    private static P.DockerInfo Docker(List<string> info, List<string> apiVersion, List<string> compose, List<string> buildx)
    {
        var result = new P.DockerInfo { ApiVersion = apiVersion.FirstOrDefault() ?? "", ComposeVersion = compose.FirstOrDefault() ?? "", BuildxVersion = buildx.FirstOrDefault() ?? "" };
        var text = string.Join('\n', info);
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                result.Status = P.DockerStatus.Running;
                result.Version = Str(root, "ServerVersion");
                result.StorageDriver = Str(root, "Driver");
                result.CgroupVersion = Str(root, "CgroupVersion");
                result.DockerRootDir = Str(root, "DockerRootDir");
                result.ContainersRunning = (uint)Num(root, "ContainersRunning");
                result.ContainersStopped = (uint)Num(root, "ContainersStopped");
                result.ImageCount = (uint)Num(root, "Images");
                result.SwarmActive = root.TryGetProperty("Swarm", out var swarm) && Str(swarm, "LocalNodeState") == "active";
                result.Rootless = root.TryGetProperty("SecurityOptions", out var options) && options.ValueKind == JsonValueKind.Array
                    && options.EnumerateArray().Any(o => o.ValueKind == JsonValueKind.String && o.GetString()!.Contains("rootless", StringComparison.Ordinal));
                return result;
            }
            catch (JsonException)
            {
                // Falls through to the text classification.
            }
        }

        var (status, _) = SshMetricsParsers.ParseDockerState(info);
        result.Status = status switch
        {
            DockerStatus.Running => P.DockerStatus.Running,
            DockerStatus.Stopped => P.DockerStatus.Stopped,
            DockerStatus.NotInstalled => P.DockerStatus.NotInstalled,
            DockerStatus.PermissionDenied => P.DockerStatus.PermissionDenied,
            _ => P.DockerStatus.Unreachable,
        };
        result.Error = text.Length > 300 ? text[..300] : text;
        return result;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
