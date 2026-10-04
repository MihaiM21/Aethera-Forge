using System.Text.Json;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Deployments;

/// <summary>Per-run inputs of <see cref="DeploymentPlanBuilder"/> that are not part of the frozen snapshot.</summary>
public sealed class PlanContext
{
    public required Guid ServerId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required int DeploymentNumber { get; init; }
    public Guid? JobId { get; init; }
    public Guid? OrganizationId { get; init; }

    /// <summary>The container (or compose project) of the live deployment that this one replaces.</summary>
    public string? PreviousContainer { get; init; }

    /// <summary>Returns the plaintext of a pinned secret version. The caller registers it for log redaction.</summary>
    public required Func<Guid, int, string> ResolveSecret { get; init; }

    public GitCredentialSpec? GitCredential { get; init; }
    public RegistryCredentials? Registry { get; init; }

    /// <summary>Rollback: run this image from the server's own store instead of building or pulling.</summary>
    public string? ExistingImage { get; init; }

    public required IProxyProvider Proxy { get; init; }
    public string ProxyNetwork { get; init; } = "aethera-proxy";
    public Action<LogEntry>? OnLog { get; init; }
}

/// <summary>Turns a <see cref="DeploymentSnapshot"/> into the <see cref="DeploymentPlan"/> the runner executes. Pure: no I/O.</summary>
public static class DeploymentPlanBuilder
{
    /// <summary>Stable container name of a workload on its server.</summary>
    public static string ContainerName(DeploymentSnapshot s) => $"{s.Slug}-{s.WorkloadId.ToString("N")[..8]}";

    public static string ComposeProjectName(DeploymentSnapshot s) => $"{s.Slug}-{s.WorkloadId.ToString("N")[..8]}";

    public static DeploymentPlan Build(DeploymentSnapshot s, PlanContext c)
    {
        var name = ContainerName(s);
        var routes = Routes(s, c);
        var labels = new Dictionary<string, string>
        {
            ["aethera.managed"] = "true",
            ["aethera.workload"] = s.WorkloadId.ToString(),
            ["aethera.environment"] = s.EnvironmentId.ToString(),
            ["aethera.deployment"] = c.DeploymentId.ToString(),
        };
        foreach (var route in routes)
            foreach (var (k, v) in c.Proxy.RouteLabels(route)) labels[k] = v;

        if (s.Compose is { } compose)
            return ComposePlan(s, c, compose, routes);

        var env = s.Env.Where(e => e.Runtime).Select(e => ToSpec(e, c)).ToList();
        var networks = new List<NetworkAttachmentSpec>();
        if (routes.Count > 0) networks.Add(new NetworkAttachmentSpec(c.ProxyNetwork));
        foreach (var n in s.Networks) networks.Add(new NetworkAttachmentSpec(n.DockerName, n.Aliases.Count > 0 ? n.Aliases : null));

        var container = new ContainerSpec("", name)
        {
            Labels = labels,
            Env = env,
            Ports = s.Ports.Where(p => p.PublishedPort is > 0)
                .Select(p => new PortMappingSpec(p.ContainerPort, p.PublishedPort!.Value, null, p.Protocol == PortProtocol.Udp ? PortProtocolKind.Udp : PortProtocolKind.Tcp)).ToList(),
            Mounts = s.Volumes.Select(v => v.HostPath is { Length: > 0 }
                ? new VolumeMountSpec(MountKind.Bind, v.HostPath, v.MountPath, v.ReadOnly)
                : new VolumeMountSpec(MountKind.Volume, v.Name, v.MountPath, v.ReadOnly)).ToList(),
            Networks = networks,
            Resources = new ResourceLimitsSpec(
                s.Runtime.CpuLimit ?? 0, s.Runtime.CpuReservation ?? 0, s.Runtime.MemoryLimitBytes ?? 0, s.Runtime.MemoryReservationBytes ?? 0, 0, s.Runtime.PidsLimit ?? 0),
            RestartPolicy = new RestartPolicySpec(s.Runtime.RestartPolicy switch
            {
                RestartPolicy.No => RestartPolicyKind.No,
                RestartPolicy.Always => RestartPolicyKind.Always,
                RestartPolicy.OnFailure => RestartPolicyKind.OnFailure,
                _ => RestartPolicyKind.UnlessStopped,
            }),
        };

        var allNetworks = s.Networks.Select(n => n.DockerName).Concat(routes.Count > 0 ? [c.ProxyNetwork] : []).Distinct().ToList();
        var health = HealthTargets(s, name);
        var common = new DeploymentPlan
        {
            ServerId = c.ServerId, Container = container, PreviousContainer = c.PreviousContainer, Networks = allNetworks,
            JobId = c.JobId, OrganizationId = c.OrganizationId, OnLog = c.OnLog,
            HealthFor = health, HealthTimeout = TimeSpan.FromSeconds(s.Runtime.HealthTimeoutSeconds),
            HealthInterval = TimeSpan.FromSeconds(s.Runtime.HealthIntervalSeconds), HealthRetries = Math.Max(1, s.Runtime.HealthRetries),
            HealthStartPeriod = TimeSpan.FromSeconds(s.Runtime.HealthStartPeriodSeconds),
        };

        if (c.ExistingImage is { Length: > 0 } existing)
            return Copy(common, imageReference: existing, local: true);

        if (s.Image is { } image)
        {
            var reference = image.Tag.StartsWith('@') ? image.Image + image.Tag : $"{image.Image}:{image.Tag}";
            return Copy(common, imageReference: reference, pullAuth: c.Registry);
        }

        return Copy(common, build: BuildSpec(s, c));
    }

    private static DeploymentPlan Copy(DeploymentPlan p, string? imageReference = null, bool local = false, RegistryCredentials? pullAuth = null, BuildSpec? build = null) =>
        new()
        {
            ServerId = p.ServerId, Container = p.Container, PreviousContainer = p.PreviousContainer, Networks = p.Networks, JobId = p.JobId,
            OrganizationId = p.OrganizationId, OnLog = p.OnLog, HealthFor = p.HealthFor, HealthTimeout = p.HealthTimeout, HealthInterval = p.HealthInterval,
            HealthRetries = p.HealthRetries, HealthStartPeriod = p.HealthStartPeriod,
            ImageReference = imageReference, LocalImage = local, PullAuth = pullAuth, Build = build,
        };

    private static BuildSpec BuildSpec(DeploymentSnapshot s, PlanContext c)
    {
        var git = s.Git ?? throw new InvalidOperationException("A source build needs a git source.");
        var b = s.Build ?? new BuildSnapshot(BuildEngines.Dockerfile, ".", null, null, null, null, null, true, null);
        var engine = b.Engine switch
        {
            BuildEngines.Nixpacks => BuildEngineKind.Nixpacks,
            BuildEngines.Static => BuildEngineKind.Static,
            BuildEngines.Image => BuildEngineKind.Image,
            _ => BuildEngineKind.Dockerfile,
        };
        var buildVars = s.Env.Where(e => e.Build).Select(e => ToSpec(e, c)).ToList();
        return new BuildSpec(c.DeploymentId.ToString(), engine, [ImagePolicy.Tag(s.Slug, c.DeploymentId)])
        {
            Git = new GitSourceSpec(git.Url, git.Branch, git.CommitPin, Depth: git.CommitPin is null ? 1 : 0, Credentials: c.GitCredential),
            ContextPath = b.Context,
            DockerfilePath = b.DockerfilePath,
            BuildArgs = engine == BuildEngineKind.Dockerfile ? buildVars : null,
            Env = engine == BuildEngineKind.Dockerfile ? null : buildVars,
            InstallCommand = b.InstallCommand,
            BuildCommand = b.BuildCommand,
            StartCommand = b.StartCommand,
            OutputDir = b.OutputDirectory,
            SpaFallback = engine == BuildEngineKind.Static,
            Cache = new BuildCacheSpec(b.CacheEnabled, 1, CacheKey: $"{s.WorkloadId:N}"),
            TargetPlatforms = b.TargetPlatform is { Length: > 0 } ? [b.TargetPlatform] : null,
            ImageLabels = new Dictionary<string, string>
            {
                ["aethera.workload"] = s.WorkloadId.ToString(), ["aethera.deployment"] = c.DeploymentId.ToString(),
            },
        };
    }

    private static EnvVarSpec ToSpec(EnvEntry e, PlanContext c) =>
        e.SecretId is { } id
            ? EnvVarSpec.OfSecret(e.Key, c.ResolveSecret(id, e.SecretVersion ?? 0))
            : EnvVarSpec.OfPlain(e.Key, e.Value ?? "");

    private static Func<string, ProbeTarget?>? HealthTargets(DeploymentSnapshot s, string baseName)
    {
        var r = s.Runtime;
        if (r.HealthType == HealthCheckType.None) return null;
        var port = r.HealthPort ?? s.Ports.FirstOrDefault(p => p.IsHttp)?.ContainerPort ?? s.Ports.FirstOrDefault()?.ContainerPort;
        return container => r.HealthType switch
        {
            HealthCheckType.Container => new ContainerHealthProbeTarget(container),
            HealthCheckType.Tcp when port is { } p => new TcpProbeTarget(container, p),
            HealthCheckType.Http when port is { } p =>
                new HttpProbeTarget($"http://{container}:{p}{(r.HealthPath is { Length: > 0 } path ? (path.StartsWith('/') ? path : "/" + path) : "/")}"),
            _ => null,
        };
    }

    /// <summary>Groups domains by target port and path prefix; hosts of one group share a router.</summary>
    private static List<RouteSpec> Routes(DeploymentSnapshot s, PlanContext c)
    {
        var defaultPort = s.Ports.FirstOrDefault(p => p.IsHttp)?.ContainerPort ?? s.Ports.FirstOrDefault()?.ContainerPort;
        var groups = s.Domains
            .Select(d => (Domain: d, Port: d.TargetPort ?? defaultPort))
            .Where(x => x.Port is not null)
            .GroupBy(x => (x.Port, Prefix: string.IsNullOrEmpty(x.Domain.PathPrefix) ? "/" : x.Domain.PathPrefix, x.Domain.Https))
            .ToList();
        var routes = new List<RouteSpec>();
        foreach (var (g, i) in groups.Select((g, i) => (g, i)))
            routes.Add(new RouteSpec(i == 0 ? s.Slug : $"{s.Slug}-{i + 1}", g.Select(x => x.Domain.Hostname).Distinct().ToList(), g.Key.Port!.Value, c.ProxyNetwork)
            {
                Https = g.Key.Https, RedirectToHttps = g.Key.Https, PathPrefix = g.Key.Prefix == "/" ? null : g.Key.Prefix,
            });
        return routes;
    }

    private static DeploymentPlan ComposePlan(DeploymentSnapshot s, PlanContext c, ComposeSnapshot compose, List<RouteSpec> routes)
    {
        var env = s.Env.Where(e => e.Runtime || e.Build).Select(e => ToSpec(e, c)).ToList();
        string? overrideFile = null;
        if (routes.Count > 0 && compose.RoutedService is { Length: > 0 } svc)
        {
            var labels = routes.SelectMany(r => c.Proxy.RouteLabels(r)).ToDictionary(kv => kv.Key, kv => kv.Value);
            overrideFile = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["services"] = new Dictionary<string, object> { [svc] = new Dictionary<string, object> { ["labels"] = labels, ["networks"] = new[] { "default", "aethera-proxy" } } },
                ["networks"] = new Dictionary<string, object> { ["aethera-proxy"] = new Dictionary<string, object> { ["external"] = true, ["name"] = c.ProxyNetwork } },
            });
        }
        return new DeploymentPlan
        {
            ServerId = c.ServerId, JobId = c.JobId, OrganizationId = c.OrganizationId, OnLog = c.OnLog, PreviousContainer = c.PreviousContainer,
            Compose = new ComposeProjectSpec(ComposeProjectName(s), compose.Content) { OverrideFile = overrideFile, Env = env, RegistryAuths = c.Registry is null ? null : [c.Registry] },
        };
    }
}
