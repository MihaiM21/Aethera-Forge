using Aethera.Api.Features.Resources.Workloads;
using Aethera.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aethera.Api.Features.Resources.Services;

public sealed record ServiceTemplateVersion(string Version, string Image, bool IsDefault);

public sealed record ServiceTemplatePort(int ContainerPort, string Protocol, bool IsHttp);

public sealed record ServiceTemplateVolume(string Name, string MountPath);

public sealed record ServiceTemplateHealthCheck(
    string Type, string? Path, int? Port, int IntervalSeconds, int TimeoutSeconds, int Retries, int StartPeriodSeconds);

/// <summary>
/// An environment variable of the template. <c>Generate</c> is <c>"password"</c> for a random strong secret (stored as a
/// <em>secret</em> and linked as a secret-backed variable), or <c>null</c> for a plain variable with <c>Value</c>.
/// </summary>
public sealed record ServiceTemplateEnvVar(string Key, string? Value, string? Generate, int Length = 32);

public sealed record ServiceTemplate(
    string Key,
    string Name,
    string Description,
    string Category,
    string DefaultVersion,
    string DefaultImage,
    IReadOnlyList<ServiceTemplateVersion> Versions,
    IReadOnlyList<ServiceTemplatePort> Ports,
    IReadOnlyList<ServiceTemplateVolume> Volumes,
    ServiceTemplateHealthCheck HealthCheck,
    IReadOnlyList<ServiceTemplateEnvVar> Env,
    IReadOnlyList<string>? Command = null);

/// <summary>A template file could not be read; the message names the file and the field.</summary>
public sealed class ServiceTemplateFormatException(string message) : Exception(message);

/// <summary>
/// The catalogue of service templates (<c>GET /service-templates</c>). Each template is a YAML file embedded in this assembly
/// (<c>Features/Resources/Services/Templates/NN-key.yaml</c>, ordered by file name, format in docs/architecture/0008-product-ui.md): adding a database is adding a file.
/// </summary>
public static class ServiceTemplates
{
    public const string Password = "password";

    // Declared before All: static initializers run in textual order and the catalogue is parsed with this.
    private static readonly IDeserializer Yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public static IReadOnlyList<ServiceTemplate> All { get; } = LoadEmbedded();

    public static ServiceTemplate? Find(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The image for a version of a template (<c>null</c> version = the default).</summary>
    public static ServiceTemplateVersion? Version(ServiceTemplate template, string? version) =>
        version is null
            ? template.Versions.First(v => v.IsDefault)
            : template.Versions.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));

    internal static HealthCheckType ToHealthCheckType(string type) => type switch
    {
        "http" => HealthCheckType.Http,
        "tcp" => HealthCheckType.Tcp,
        "container" => HealthCheckType.Container,
        _ => HealthCheckType.None,
    };

    private static IReadOnlyList<ServiceTemplate> LoadEmbedded()
    {
        var assembly = typeof(ServiceTemplates).Assembly;
        var templates = new List<ServiceTemplate>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".yaml", StringComparison.Ordinal) && n.Contains(".Templates.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            templates.Add(Parse(reader.ReadToEnd(), name));
        }

        var duplicate = templates.GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ServiceTemplateFormatException($"Template key '{duplicate.Key}' is defined twice.");
        return templates; // file order: the numeric prefix of the file names is the display order
    }

    /// <summary>Parses and validates one template file. <paramref name="source"/> only names the file in error messages.</summary>
    /// <exception cref="ServiceTemplateFormatException">The YAML is malformed or a field is missing or invalid.</exception>
    public static ServiceTemplate Parse(string yaml, string source = "template")
    {
        TemplateFile file;
        try
        {
            file = Yaml.Deserialize<TemplateFile>(yaml) ?? throw new ServiceTemplateFormatException($"{source}: the file is empty.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ServiceTemplateFormatException($"{source}: {ex.Message}");
        }

        string Need(string? value, string field) =>
            string.IsNullOrWhiteSpace(value) ? throw new ServiceTemplateFormatException($"{source}: '{field}' is required.") : value.Trim();

        var key = Need(file.Key, "key");
        if (!Slug.IsValid(key)) throw new ServiceTemplateFormatException($"{source}: key '{key}' must be lowercase letters, digits and hyphens.");
        var image = Need(file.Image, "image");
        if (!ApplicationRules.IsImage(image)) throw new ServiceTemplateFormatException($"{source}: image '{image}' is not a valid image name.");
        var versions = (file.Versions ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (versions.Count == 0) throw new ServiceTemplateFormatException($"{source}: at least one version is required (the first is the default).");

        var ports = (file.Ports ?? []).Select(p =>
            p.Port is < 1 or > 65535 ? throw new ServiceTemplateFormatException($"{source}: port {p.Port} is out of range.") : new ServiceTemplatePort(p.Port, "tcp", p.Http)).ToList();
        var volumes = (file.Volumes ?? []).Select(v =>
            string.IsNullOrWhiteSpace(v.Name) || !(v.Path?.StartsWith('/') ?? false)
                ? throw new ServiceTemplateFormatException($"{source}: every volume needs a name and an absolute path.")
                : new ServiceTemplateVolume(v.Name.Trim(), v.Path)).ToList();

        var hc = file.HealthCheck ?? new HealthFile { Type = "none" };
        var type = (hc.Type ?? "none").Trim().ToLowerInvariant();
        if (type is not ("none" or "http" or "tcp" or "container")) throw new ServiceTemplateFormatException($"{source}: healthCheck.type must be none, http, tcp or container.");
        if (type == "http" && !(hc.Path?.StartsWith('/') ?? false)) throw new ServiceTemplateFormatException($"{source}: an http health check needs a path starting with /.");
        var http = type == "http";
        var health = new ServiceTemplateHealthCheck(
            type, hc.Path, hc.Port ?? (type is "http" or "tcp" ? ports.FirstOrDefault()?.ContainerPort : null),
            hc.IntervalSeconds ?? (http ? 15 : 10), hc.TimeoutSeconds ?? 5, hc.Retries ?? 5, hc.StartPeriodSeconds ?? (http ? 30 : 20));

        var env = new List<ServiceTemplateEnvVar>();
        foreach (var e in file.Env ?? [])
        {
            var envKey = Need(e.Key, "env[].key");
            if (e.Generate is not null && e.Generate != Password) throw new ServiceTemplateFormatException($"{source}: env '{envKey}': generate must be '{Password}'.");
            if (e.Generate is null && e.Value is null) throw new ServiceTemplateFormatException($"{source}: env '{envKey}' needs a value or generate.");
            env.Add(new ServiceTemplateEnvVar(envKey, e.Generate is null ? e.Value : null, e.Generate, e.Length ?? 32));
        }

        return new ServiceTemplate(
            key, Need(file.Name, "name"), Need(file.Description, "description"), Need(file.Category, "category"), versions[0], $"{image}:{versions[0]}",
            versions.Select((v, i) => new ServiceTemplateVersion(v, $"{image}:{v}", i == 0)).ToList(), ports, volumes, health, env,
            file.Command is { Count: > 0 } ? file.Command : null);
    }

    // YamlDotNet fills mutable classes; the public records above stay immutable.
    private sealed class TemplateFile
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? Image { get; set; }
        public List<string>? Versions { get; set; }
        public List<string>? Command { get; set; }
        public List<PortFile>? Ports { get; set; }
        public List<VolumeFile>? Volumes { get; set; }
        public HealthFile? HealthCheck { get; set; }
        public List<EnvFile>? Env { get; set; }
    }

    private sealed class PortFile
    {
        public int Port { get; set; }
        public bool Http { get; set; }
    }

    private sealed class VolumeFile
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
    }

    private sealed class HealthFile
    {
        public string? Type { get; set; }
        public string? Path { get; set; }
        public int? Port { get; set; }
        public int? IntervalSeconds { get; set; }
        public int? TimeoutSeconds { get; set; }
        public int? Retries { get; set; }
        public int? StartPeriodSeconds { get; set; }
    }

    private sealed class EnvFile
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
        public string? Generate { get; set; }
        public int? Length { get; set; }
    }
}
