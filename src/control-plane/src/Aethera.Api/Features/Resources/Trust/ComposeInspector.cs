using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Aethera.Api.Features.Resources.Trust;

/// <summary>One offending place of a compose document: a JSON Pointer into the document (<c>/services/web/privileged</c>) and why it matters.</summary>
public sealed record ComposeFinding(string Pointer, string Reason);

/// <summary>A bind source found in a compose document (host directory or file), with the pointer of the key that holds it.</summary>
public sealed record ComposeBindSource(string Pointer, string Source);

/// <summary>What <see cref="ComposeInspector"/> found in a compose document.</summary>
public sealed record ComposeAnalysis(IReadOnlyList<ComposeFinding> RestrictedOptions, IReadOnlyList<ComposeBindSource> BindSources);

/// <summary>The compose document cannot be read (not YAML, not a compose shape, too large or too complex).</summary>
public sealed class ComposeInvalidException(string message) : Exception(message);

/// <summary>
/// Reads inline compose content into a plain YAML node tree (no typed deserialization, no object construction, tags are ignored) and
/// lists what is root-equivalent on a host (ADR 0006). The document is bounded: size, nesting, node count and aliases are capped, and
/// every traversal has a visit budget, so a YAML bomb (many aliases of aliases) is refused instead of expanded.
/// <para>
/// The inspection is deliberately conservative. A value it cannot judge (an unresolved <c>${VARIABLE}</c> where a host path or namespace
/// would matter) counts as restricted. Merge keys (<c>&lt;&lt;: *anchor</c>) are followed. It is an API-level gate only: the engine
/// re-checks the effective configuration before it starts anything (ADR 0006).
/// </para>
/// </summary>
public static class ComposeInspector
{
    public const int MaxChars = 256 * 1024;
    public const int MaxBytes = 256 * 1024;
    public const int MaxAliases = 50;
    public const int MaxDepth = 32;
    public const int MaxEvents = 50_000;
    public const int MaxVisits = 100_000;

    /// <summary>Namespace options whose values <c>host</c> and <c>container:...</c> join the host's or another container's namespace.</summary>
    private static readonly string[] NamespaceKeys = ["network_mode", "pid", "ipc", "userns_mode", "uts", "cgroup"];

    public static ComposeAnalysis Analyze(string content, IReadOnlyCollection<int> reservedPorts)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
            throw new ComposeInvalidException($"The compose document is larger than {MaxBytes / 1024} KiB.");

        Prescan(content);

        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(content));
        }
        catch (Exception e) when (e is YamlException or ArgumentException)
        {
            throw new ComposeInvalidException("Not valid YAML: " + Clean(e.Message));
        }

        if (stream.Documents.Count != 1) throw new ComposeInvalidException("Expected exactly one YAML document.");
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ComposeInvalidException("A compose document is a YAML mapping with a 'services' section.");

        var findings = new List<ComposeFinding>();
        var binds = new List<ComposeBindSource>();
        var budget = new Budget();

        var services = Find(root, "services", budget) as YamlMappingNode
            ?? throw new ComposeInvalidException("A compose document needs a 'services' mapping.");

        foreach (var (nameNode, serviceNode) in Entries(services, budget))
        {
            if (nameNode is not YamlScalarNode name) throw new ComposeInvalidException("Service names must be plain strings.");
            if (serviceNode is not YamlMappingNode service) throw new ComposeInvalidException($"Service '{name.Value}' must be a mapping.");
            InspectService(service, "/services/" + Escape(name.Value ?? ""), reservedPorts, findings, binds, budget);
        }

        if (Find(root, "include", budget) is not null)
            findings.Add(new ComposeFinding("/include", "includes other compose files from the host"));

        InspectVolumes(Find(root, "volumes", budget), findings, binds, budget);
        foreach (var section in new[] { "secrets", "configs" })
            InspectFileSources(Find(root, section, budget), "/" + section, findings, binds, budget);

        return new ComposeAnalysis(findings, binds);
    }

    // ---- bounds ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Walks the parser events once: caps nesting, node count and alias count before any tree is built.</summary>
    private static void Prescan(string content)
    {
        var parser = new Parser(new StringReader(content));
        var depth = 0;
        var events = 0;
        var aliases = 0;
        var documents = 0;
        try
        {
            while (parser.MoveNext())
            {
                if (++events > MaxEvents) throw new ComposeInvalidException("The compose document is too complex.");
                switch (parser.Current)
                {
                    case DocumentStart: documents++; break;
                    case AnchorAlias:
                        if (++aliases > MaxAliases) throw new ComposeInvalidException($"The compose document uses more than {MaxAliases} YAML aliases.");
                        break;
                    case MappingStart or SequenceStart:
                        if (++depth > MaxDepth) throw new ComposeInvalidException($"The compose document is nested deeper than {MaxDepth} levels.");
                        break;
                    case MappingEnd or SequenceEnd: depth--; break;
                }
            }
        }
        catch (YamlException e)
        {
            throw new ComposeInvalidException("Not valid YAML: " + Clean(e.Message));
        }

        if (documents > 1) throw new ComposeInvalidException("Expected exactly one YAML document.");
    }

    private sealed class Budget
    {
        private int _visits;

        public void Spend()
        {
            if (++_visits > MaxVisits) throw new ComposeInvalidException("The compose document is too complex (aliases expand too far).");
        }
    }

    // ---- services -------------------------------------------------------------------------------------------------------------------

    private static void InspectService(
        YamlMappingNode service, string pointer, IReadOnlyCollection<int> reservedPorts, List<ComposeFinding> findings, List<ComposeBindSource> binds,
        Budget budget)
    {
        foreach (var (keyNode, value) in Entries(service, budget))
        {
            if (keyNode is not YamlScalarNode { Value: { } key }) continue;
            var at = pointer + "/" + Escape(key);

            switch (key)
            {
                case "privileged":
                    if (!IsFalse(value)) findings.Add(new(at, "privileged containers have full access to the host"));
                    break;

                case "cap_add":
                    if (!IsEmpty(value)) findings.Add(new(at, "added Linux capabilities"));
                    break;

                case "devices" or "device_cgroup_rules":
                    if (!IsEmpty(value)) findings.Add(new(at, "host devices"));
                    break;

                case "cgroup_parent":
                    findings.Add(new(at, "places the container in a host cgroup"));
                    break;

                case "security_opt":
                    foreach (var (index, item) in Items(value, budget))
                    {
                        var text = Text(item).ToLowerInvariant();
                        if (text.Contains("unconfined", StringComparison.Ordinal) || text.Contains("disable", StringComparison.Ordinal))
                            findings.Add(new($"{at}/{index}", "disables a security profile"));
                    }

                    break;

                case "env_file":
                    findings.Add(new(at, "reads files from the host"));
                    break;

                case "extends":
                    if (value is YamlMappingNode extends && Find(extends, "file", budget) is not null)
                        findings.Add(new(at + "/file", "extends a service from another file"));
                    break;

                case "build":
                    InspectBuild(value, at, findings, budget);
                    break;

                case "volumes":
                    InspectServiceVolumes(value, at, findings, binds, budget);
                    break;

                case "volumes_from":
                    foreach (var (index, item) in Items(value, budget))
                    {
                        if (Text(item).StartsWith("container:", StringComparison.Ordinal))
                            findings.Add(new($"{at}/{index}", "mounts the volumes of an arbitrary host container"));
                    }

                    break;

                case "ports":
                    InspectPorts(value, at, reservedPorts, findings, budget);
                    break;

                default:
                    if (Array.IndexOf(NamespaceKeys, key) >= 0 && value is YamlScalarNode scalar && JoinsForeignNamespace(scalar.Value))
                        findings.Add(new(at, $"shares the {NamespaceName(key)} namespace of the host or another container"));
                    break;
            }
        }
    }

    private static string NamespaceName(string key) => key switch { "network_mode" => "network", "userns_mode" => "user", "uts" => "UTS", "cgroup" => "cgroup", var k => k };

    private static bool JoinsForeignNamespace(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        return text == "host" || text.StartsWith("container:", StringComparison.Ordinal) || text.Contains('$', StringComparison.Ordinal);
    }

    private static void InspectBuild(YamlNode value, string pointer, List<ComposeFinding> findings, Budget budget)
    {
        // build: ./dir  or  build: { context: ..., dockerfile: ... }
        var context = value is YamlScalarNode scalar ? scalar : value is YamlMappingNode map ? Find(map, "context", budget) as YamlScalarNode : null;
        var contextPointer = value is YamlScalarNode ? pointer : pointer + "/context";
        if (context?.Value is { } path && EscapesProjectDirectory(path))
            findings.Add(new(contextPointer, "builds from a directory outside the project"));
        if (value is YamlMappingNode m && Find(m, "dockerfile", budget) is YamlScalarNode { Value: { } dockerfile } && EscapesProjectDirectory(dockerfile))
            findings.Add(new(pointer + "/dockerfile", "reads a Dockerfile from outside the project"));
    }

    private static bool EscapesProjectDirectory(string path)
    {
        var text = path.Trim();
        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("git@", StringComparison.Ordinal)) return false; // a remote context
        return text.StartsWith('/') || text.StartsWith('~') || text.Contains('$') || text.Contains('\\')
            || text.Split('/').Contains("..");
    }

    // ---- volumes and mounts ---------------------------------------------------------------------------------------------------------

    private static void InspectServiceVolumes(
        YamlNode value, string pointer, List<ComposeFinding> findings, List<ComposeBindSource> binds, Budget budget)
    {
        foreach (var (index, item) in Items(value, budget))
        {
            var at = $"{pointer}/{index}";
            switch (item)
            {
                case YamlScalarNode { Value: { } spec }:
                {
                    // [SOURCE:]TARGET[:MODE]. Without a colon it is an anonymous volume.
                    var colon = spec.IndexOf(':');
                    if (colon < 0) break;
                    var source = spec[..colon];
                    if (!IsNamedVolume(source)) AddBind(at, source, findings, binds);
                    break;
                }

                case YamlMappingNode mount:
                {
                    var type = (Find(mount, "type", budget) as YamlScalarNode)?.Value?.Trim().ToLowerInvariant();
                    var source = (Find(mount, "source", budget) as YamlScalarNode)?.Value ?? "";
                    var isBind = type switch
                    {
                        "bind" => true,
                        "volume" => source.Length > 0 && !IsNamedVolume(source),
                        "tmpfs" or "npipe" or "image" or "cluster" => false,
                        _ => source.Length > 0 && !IsNamedVolume(source),
                    };
                    if (isBind) AddBind(at + "/source", source, findings, binds);
                    break;
                }
            }
        }
    }

    private static void AddBind(string pointer, string source, List<ComposeFinding> findings, List<ComposeBindSource> binds)
    {
        findings.Add(new(pointer, "mounts a directory of the host"));
        binds.Add(new(pointer, source));
    }

    /// <summary>A named volume is a bare name: no slash, not relative (<c>.</c>, <c>..</c>), not home (<c>~</c>), no variable.</summary>
    private static bool IsNamedVolume(string source)
    {
        if (source.Length == 0) return false;
        var first = source[0];
        if (!(char.IsAsciiLetterOrDigit(first))) return false;
        return source.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
    }

    private static void InspectVolumes(YamlNode? volumes, List<ComposeFinding> findings, List<ComposeBindSource> binds, Budget budget)
    {
        // A "named" volume with driver_opts can still be a bind mount of a host directory (type: none, o: bind, device: /etc).
        if (volumes is not YamlMappingNode map) return;
        foreach (var (keyNode, definition) in Entries(map, budget))
        {
            if (keyNode is not YamlScalarNode { Value: { } name } || definition is not YamlMappingNode volume) continue;
            if (Find(volume, "driver_opts", budget) is not YamlMappingNode options || options.Children.Count == 0) continue;
            var at = $"/volumes/{Escape(name)}/driver_opts";
            findings.Add(new(at, "a volume with driver options can bind a directory of the host"));
            if (Find(options, "device", budget) is YamlScalarNode { Value: { } device } && device.Length > 0) binds.Add(new(at + "/device", device));
        }
    }

    private static void InspectFileSources(YamlNode? section, string pointer, List<ComposeFinding> findings, List<ComposeBindSource> binds, Budget budget)
    {
        // Top-level secrets/configs may be read from a file on the host.
        if (section is not YamlMappingNode map) return;
        foreach (var (keyNode, definition) in Entries(map, budget))
        {
            if (keyNode is not YamlScalarNode { Value: { } name } || definition is not YamlMappingNode item) continue;
            if (Find(item, "file", budget) is YamlScalarNode { Value: { } file })
                AddBind($"{pointer}/{Escape(name)}/file", file, findings, binds);
        }
    }

    // ---- ports ----------------------------------------------------------------------------------------------------------------------

    private static void InspectPorts(
        YamlNode value, string pointer, IReadOnlyCollection<int> reservedPorts, List<ComposeFinding> findings, Budget budget)
    {
        foreach (var (index, item) in Items(value, budget))
        {
            var at = $"{pointer}/{index}";
            string? published = item switch
            {
                YamlMappingNode map => (Find(map, "published", budget) as YamlScalarNode)?.Value,
                YamlScalarNode { Value: { } spec } => PublishedPartOfShortSyntax(spec),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(published)) continue;

            if (published.Contains('$'))
            {
                findings.Add(new(at, "publishes a host port that is set by a variable"));
                continue;
            }

            var range = published.Split('-', 2);
            if (!int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)) continue;
            var last = range.Length == 2 && int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : first;
            if (first < TrustPolicy.PrivilegedPortLimit)
                findings.Add(new(at, $"publishes the privileged host port {first}"));
            else if (reservedPorts.FirstOrDefault(p => p >= first && p <= last) is var reserved and > 0)
                findings.Add(new(at, $"publishes a host port reserved by Aethera ({reserved})"));
        }
    }

    /// <summary>"80", "8080:80", "127.0.0.1:8080:80", "[::1]:8080:80/tcp" -> the host part, or null when only a container port is given.</summary>
    private static string? PublishedPartOfShortSyntax(string spec)
    {
        var withoutProtocol = spec.Split('/', 2)[0];
        var parts = withoutProtocol.Split(':');
        return parts.Length < 2 ? null : parts[^2];
    }

    // ---- tree helpers ---------------------------------------------------------------------------------------------------------------

    /// <summary>The entries of a mapping, followed through YAML merge keys (<c>&lt;&lt;: *anchor</c>). Overridden keys are still returned.</summary>
    private static IEnumerable<(YamlNode Key, YamlNode Value)> Entries(YamlMappingNode map, Budget budget)
    {
        foreach (var (key, value) in map.Children)
        {
            budget.Spend();
            if (key is YamlScalarNode { Value: "<<" })
            {
                foreach (var merged in value is YamlSequenceNode sequence ? sequence.Children : [value])
                {
                    if (merged is not YamlMappingNode mergedMap) continue;
                    foreach (var entry in Entries(mergedMap, budget)) yield return entry;
                }

                continue;
            }

            yield return (key, value);
        }
    }

    private static YamlNode? Find(YamlMappingNode map, string key, Budget budget)
    {
        foreach (var (candidate, value) in Entries(map, budget))
        {
            if (candidate is YamlScalarNode scalar && scalar.Value == key) return value;
        }

        return null;
    }

    private static IEnumerable<(int Index, YamlNode Item)> Items(YamlNode node, Budget budget)
    {
        if (node is not YamlSequenceNode sequence) yield break;
        var index = 0;
        foreach (var item in sequence.Children)
        {
            budget.Spend();
            yield return (index++, item);
        }
    }

    private static string Text(YamlNode node) => node is YamlScalarNode { Value: { } value } ? value : "";

    private static bool IsFalse(YamlNode node) => node is YamlScalarNode { Value: { } value } && value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmpty(YamlNode node) => node switch
    {
        YamlSequenceNode sequence => sequence.Children.Count == 0,
        YamlMappingNode map => map.Children.Count == 0,
        YamlScalarNode { Value: null or "" or "~" or "null" } => true,
        _ => false,
    };

    private static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    private static string Clean(string message) => message.Length > 200 ? message[..200] + "..." : message;
}
