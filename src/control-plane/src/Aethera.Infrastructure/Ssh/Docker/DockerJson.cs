using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Ssh.Docker;

/// <summary>Maps <c>docker ... inspect</c> JSON (the engine API's shapes) to the transport's domain types.</summary>
public static class DockerJson
{
    public static List<JsonElement> Array(string json)
    {
        var trimmed = json.Trim();
        if (trimmed.Length == 0) return [];
        using var document = JsonDocument.Parse(trimmed);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
            : [document.RootElement.Clone()];
    }

    /// <summary>One JSON document per line (<c>--format json</c> of Compose before v2.21 prints one array instead; both are accepted).</summary>
    public static List<JsonElement> Lines(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0) return [];
        if (trimmed[0] == '[') return Array(trimmed);
        var result = new List<JsonElement>();
        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            using var document = JsonDocument.Parse(line);
            result.Add(document.RootElement.Clone());
        }

        return result;
    }

    // ---- containers ----------------------------------------------------------------------------------------------------------------

    public static DockerContainer Container(JsonElement e, bool includeRaw)
    {
        var state = Obj(e, "State");
        var config = Obj(e, "Config");
        var status = Str(state, "Status");
        var health = Str(Obj(state, "Health"), "Status");
        return new DockerContainer(
            Id: Str(e, "Id"),
            Name: Str(e, "Name").TrimStart('/'),
            Image: Str(config, "Image"),
            ImageId: Str(e, "Image"),
            State: RunState(status),
            Status: status,
            Health: health switch
            {
                "starting" => ContainerHealthState.Starting,
                "healthy" => ContainerHealthState.Healthy,
                "unhealthy" => ContainerHealthState.Unhealthy,
                _ => ContainerHealthState.None,
            },
            CreatedAt: Time(Str(e, "Created")),
            StartedAt: Time(Str(state, "StartedAt")),
            FinishedAt: Time(Str(state, "FinishedAt")),
            ExitCode: (int)Num(state, "ExitCode"),
            OomKilled: Bool(state, "OOMKilled"),
            RestartCount: (int)Num(e, "RestartCount"),
            Labels: Labels(config),
            Ports: Ports(Obj(Obj(e, "NetworkSettings"), "Ports")),
            Mounts: Mounts(e),
            Networks: Networks(Obj(Obj(e, "NetworkSettings"), "Networks")),
            InspectJson: includeRaw ? StripEnv(e) : null);
    }

    public static ContainerRunState RunState(string status) => status switch
    {
        "created" => ContainerRunState.Created,
        "running" => ContainerRunState.Running,
        "paused" => ContainerRunState.Paused,
        "restarting" => ContainerRunState.Restarting,
        "removing" => ContainerRunState.Removing,
        "exited" => ContainerRunState.Exited,
        "dead" => ContainerRunState.Dead,
        _ => ContainerRunState.Unspecified,
    };

    /// <summary>Raw inspect output with the environment values removed (names stay), as the agent does.</summary>
    public static string StripEnv(JsonElement e)
    {
        var node = JsonNode.Parse(e.GetRawText());
        if (node?["Config"]?["Env"] is JsonArray env)
        {
            for (var i = 0; i < env.Count; i++)
            {
                var text = env[i]?.GetValue<string>() ?? "";
                var index = text.IndexOf('=');
                env[i] = index >= 0 ? text[..(index + 1)] : text;
            }
        }

        return node?.ToJsonString() ?? "{}";
    }

    private static IReadOnlyList<PortMappingSpec> Ports(JsonElement ports)
    {
        var result = new List<PortMappingSpec>();
        if (ports.ValueKind != JsonValueKind.Object) return result;
        foreach (var entry in ports.EnumerateObject())
        {
            var parts = entry.Name.Split('/');
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort)) continue;
            var protocol = parts.Length > 1 ? parts[1] switch { "udp" => PortProtocolKind.Udp, "sctp" => PortProtocolKind.Sctp, _ => PortProtocolKind.Tcp } : PortProtocolKind.Tcp;
            if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() == 0)
            {
                result.Add(new PortMappingSpec(containerPort, 0, null, protocol));
                continue;
            }

            foreach (var binding in entry.Value.EnumerateArray())
            {
                _ = int.TryParse(Str(binding, "HostPort"), NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort);
                var hostIp = Str(binding, "HostIp");
                result.Add(new PortMappingSpec(containerPort, hostPort, hostIp.Length > 0 ? hostIp : null, protocol));
            }
        }

        return result;
    }

    private static IReadOnlyList<VolumeMountSpec> Mounts(JsonElement e)
    {
        var result = new List<VolumeMountSpec>();
        if (!e.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array) return result;
        foreach (var mount in mounts.EnumerateArray())
        {
            var type = Str(mount, "Type") switch { "bind" => MountKind.Bind, "tmpfs" => MountKind.Tmpfs, _ => MountKind.Volume };
            var source = type == MountKind.Volume ? Str(mount, "Name") : Str(mount, "Source");
            result.Add(new VolumeMountSpec(type, source, Str(mount, "Destination"), !(mount.TryGetProperty("RW", out var rw) && rw.ValueKind == JsonValueKind.True)));
        }

        return result;
    }

    private static IReadOnlyList<ContainerNetworkInfo> Networks(JsonElement networks)
    {
        var result = new List<ContainerNetworkInfo>();
        if (networks.ValueKind != JsonValueKind.Object) return result;
        foreach (var network in networks.EnumerateObject())
        {
            var aliases = new List<string>();
            if (network.Value.TryGetProperty("Aliases", out var list) && list.ValueKind == JsonValueKind.Array)
                aliases.AddRange(list.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0));
            result.Add(new ContainerNetworkInfo(network.Name, Str(network.Value, "IPAddress"), Str(network.Value, "MacAddress"), aliases));
        }

        return result;
    }

    // ---- images, volumes, networks -------------------------------------------------------------------------------------------------

    public static DockerImage Image(JsonElement e, int containersUsing = 0) => new(
        Str(e, "Id"), StrList(e, "RepoTags"), StrList(e, "RepoDigests"), (long)Num(e, "Size"), Time(Str(e, "Created")),
        Labels(Obj(e, "Config")), Str(e, "Architecture"), Str(e, "Os"), containersUsing);

    public static DockerVolume Volume(JsonElement e)
    {
        var usage = Obj(e, "UsageData");
        var size = (long)Num(usage, "Size");
        var refCount = (int)Num(usage, "RefCount");
        return new DockerVolume(Str(e, "Name"), Str(e, "Driver"), Str(e, "Mountpoint"), Labels(e), Time(Str(e, "CreatedAt")), size < 0 ? 0 : size, refCount < 0 ? 0 : refCount);
    }

    public static DockerNetwork Network(JsonElement e)
    {
        var ipam = new List<IpamEntry>();
        if (Obj(e, "IPAM").TryGetProperty("Config", out var configs) && configs.ValueKind == JsonValueKind.Array)
            ipam.AddRange(configs.EnumerateArray().Select(c => new IpamEntry(Str(c, "Subnet"), Str(c, "Gateway"))));
        var endpoints = new List<NetworkEndpointInfo>();
        if (Obj(e, "Containers") is { ValueKind: JsonValueKind.Object } containers)
            endpoints.AddRange(containers.EnumerateObject().Select(c => new NetworkEndpointInfo(c.Name, Str(c.Value, "Name"), Str(c.Value, "IPv4Address"))));
        return new DockerNetwork(
            Str(e, "Id"), Str(e, "Name"), Str(e, "Driver"), Str(e, "Scope"), Bool(e, "Internal"), Bool(e, "Attachable"), Bool(e, "EnableIPv6"),
            ipam, Labels(e), Time(Str(e, "Created")), endpoints);
    }

    // ---- compose -------------------------------------------------------------------------------------------------------------------

    public static ComposeServiceInfo ComposeService(JsonElement e)
    {
        var ports = new List<PortMappingSpec>();
        if (e.TryGetProperty("Publishers", out var publishers) && publishers.ValueKind == JsonValueKind.Array)
        {
            foreach (var publisher in publishers.EnumerateArray())
            {
                var target = (int)Num(publisher, "TargetPort");
                if (target <= 0) continue;
                var protocol = Str(publisher, "Protocol") switch { "udp" => PortProtocolKind.Udp, "sctp" => PortProtocolKind.Sctp, _ => PortProtocolKind.Tcp };
                var url = Str(publisher, "URL");
                ports.Add(new PortMappingSpec(target, (int)Num(publisher, "PublishedPort"), url.Length > 0 && url != "0.0.0.0" && url != "::" ? url : null, protocol));
            }
        }

        var health = Str(e, "Health");
        return new ComposeServiceInfo(
            Str(e, "Service"), Str(e, "ID"), Str(e, "Name"), Str(e, "Image"), RunState(Str(e, "State")),
            health switch { "starting" => ContainerHealthState.Starting, "healthy" => ContainerHealthState.Healthy, "unhealthy" => ContainerHealthState.Unhealthy, _ => ContainerHealthState.None },
            (int)Num(e, "ExitCode"), ports);
    }

    // ---- primitives ----------------------------------------------------------------------------------------------------------------

    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : Empty;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> StrList(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];

    private static IReadOnlyDictionary<string, string> Labels(JsonElement holder)
    {
        var result = new Dictionary<string, string>();
        if (holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labels.EnumerateObject()) result[label.Name] = label.Value.ValueKind == JsonValueKind.String ? label.Value.GetString() ?? "" : "";
        }

        return result;
    }

    private static DateTimeOffset? Time(string text) =>
        text.Length > 0 && !text.StartsWith("0001-01-01", StringComparison.Ordinal) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUniversalTime()
            : null;
}
