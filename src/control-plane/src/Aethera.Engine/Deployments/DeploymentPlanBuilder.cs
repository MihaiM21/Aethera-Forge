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

    /// <summary>Id of the build record; also the agent's log stream id (<c>build:&lt;id&gt;</c>).</summary>
    public Guid? BuildId { get; init; }

    public GitCredentialSpec? GitCredential { get; init; }
    public RegistryCredentials? Registry { get; init; }

    /// <summary>Rollback: run this image from the server's own store instead of building or pulling.</summary>
    public string? ExistingImage { get; init; }

    public required IProxyProvider Proxy { get; init; }
    public string ProxyNetwork { get; init; } = "aethera-proxy";
    public Action<LogEntry>? OnLog { get; init; }

    /// <summary>
    /// False when the server's proxy has no ACME email: HTTPS routes then name no certificate resolver (one that does not exist would make every
    /// HTTPS domain fail silently) and Traefik serves its default certificate.
    /// </summary>
    public bool AcmeAvailable { get; init; } = true;

    /// <summary>
    /// The services of the compose file, from <c>compose.inspect</c> (or the inline file), when known. With it the override puts the environment
    /// network and the runtime variables on every service and a domain's service is checked; without it only the routed services are named.
    /// </summary>
    public IReadOnlyList<ComposeServiceDefinition>? ComposeServices { get; init; }
}

/// <summary>Turns a <see cref="DeploymentSnapshot"/> into the <see cref="DeploymentPlan"/> the runner executes. Pure: no I/O.</summary>
public static class DeploymentPlanBuilder
{
    /// <summary>Stable container name of a workload on its server.</summary>
    public static string ContainerName(DeploymentSnapshot s) => $"{s.Slug}-{s.WorkloadId.ToString("N")[..8]}";

    public static string ComposeProjectName(DeploymentSnapshot s) => $"{s.Slug}-{s.WorkloadId.ToString("N")[..8]}";

    /// <summary>The network every workload of an environment joins, so workloads reach each other by name.</summary>
    public static string EnvironmentNetwork(Guid environmentId) => $"aethera-env-{environmentId.ToString("N")[..8]}";

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

        if (s.Compose is { } compose)
            return ComposePlan(s, c, compose, routes);

        foreach (var route in routes)
            foreach (var (k, v) in c.Proxy.RouteLabels(route.Route)) labels[k] = v;
        MergeCustomLabels(labels, s.CustomLabels);

        var env = s.Env.Where(e => e.Runtime).Select(e => ToSpec(e, c)).ToList();
        var networks = new List<NetworkAttachmentSpec>();
        if (routes.Count > 0) networks.Add(new NetworkAttachmentSpec(c.ProxyNetwork));
        foreach (var n in s.Networks) networks.Add(new NetworkAttachmentSpec(n.DockerName, n.Aliases.Count > 0 ? n.Aliases : null));

        var container = new ContainerSpec("", name)
        {
            Command = s.Image?.Command is { Count: > 0 } command ? command : null,
            Labels = labels,
            Env = env,
            Ports = s.Ports.Where(p => p.PublishedPort is > 0)
                .Select(p => new PortMappingSpec(p.ContainerPort, p.PublishedPort!.Value, null, p.Protocol == PortProtocol.Udp ? PortProtocolKind.Udp : PortProtocolKind.Tcp)).ToList(),
            // Named volumes live in the workload's own namespace (VolumeNames): prod and dev never share a Docker volume.
            Mounts = s.Volumes.Select(v => v.HostPath is { Length: > 0 }
                ? new VolumeMountSpec(MountKind.Bind, v.HostPath, v.MountPath, v.ReadOnly)
                : new VolumeMountSpec(MountKind.Volume, VolumeNames.DockerName(s.Slug, s.WorkloadId, v.Name), v.MountPath, v.ReadOnly)).ToList(),
            StopTimeout = TimeSpan.FromSeconds(Math.Max(1, s.Runtime.StopTimeoutSeconds)),
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
            InternalNetworks = s.Networks.Where(n => n.Internal).Select(n => n.DockerName).ToHashSet(),
            JobId = c.JobId, OrganizationId = c.OrganizationId, OnLog = c.OnLog,
            HealthFor = health, HealthTimeout = TimeSpan.FromSeconds(s.Runtime.HealthTimeoutSeconds),
            HealthInterval = TimeSpan.FromSeconds(s.Runtime.HealthIntervalSeconds), HealthRetries = Math.Max(1, s.Runtime.HealthRetries),
            HealthStartPeriod = TimeSpan.FromSeconds(s.Runtime.HealthStartPeriodSeconds),
            StopTimeout = TimeSpan.FromSeconds(Math.Max(1, s.Runtime.StopTimeoutSeconds)),
        };

        if (c.ExistingImage is { Length: > 0 } existing)
            return Copy(common, imageReference: existing, local: true);

        if (s.Image is { } image)
        {
            var reference = image.Tag.StartsWith('@') ? image.Image + image.Tag : $"{image.Image}:{image.Tag}";
            return Copy(common, imageReference: reference, pullAuth: c.Registry, pullPolicy: image.PullPolicy);
        }

        return Copy(common, build: BuildSpec(s, c));
    }

    private static DeploymentPlan Copy(
        DeploymentPlan p, string? imageReference = null, bool local = false, RegistryCredentials? pullAuth = null, BuildSpec? build = null,
        ImagePullPolicy pullPolicy = ImagePullPolicy.Always) =>
        new()
        {
            ServerId = p.ServerId, Container = p.Container, PreviousContainer = p.PreviousContainer, Networks = p.Networks, InternalNetworks = p.InternalNetworks, JobId = p.JobId,
            OrganizationId = p.OrganizationId, OnLog = p.OnLog, HealthFor = p.HealthFor, HealthTimeout = p.HealthTimeout, HealthInterval = p.HealthInterval,
            HealthRetries = p.HealthRetries, HealthStartPeriod = p.HealthStartPeriod, StopTimeout = p.StopTimeout,
            ImageReference = imageReference, LocalImage = local, PullAuth = pullAuth, Build = build, PullPolicy = pullPolicy,
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
        return new BuildSpec((c.BuildId ?? c.DeploymentId).ToString(), engine, [ImagePolicy.Tag(s.Slug, c.DeploymentId)])
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

    /// <summary>Merges the workload's custom labels last. The protected keys (<see cref="CustomLabelRules.IsProtected"/>) are dropped even if one got stored.</summary>
    private static void MergeCustomLabels(IDictionary<string, string> labels, IReadOnlyDictionary<string, string>? custom)
    {
        if (custom is null) return;
        foreach (var (k, v) in custom)
            if (!CustomLabelRules.IsProtected(k)) labels[k] = v;
    }

    /// <summary>A router of the plan and the compose service its labels go on (null for a plain container).</summary>
    private sealed record PlannedRoute(RouteSpec Route, string? Service);

    private sealed record RouteGroupKey(
        string? Service, int Port, string Prefix, bool Https, string? RedirectTo, bool Strip, Guid? AuthId, int? AuthVersion, Guid? DnsProviderId);

    /// <summary>
    /// Groups domains by (service, port, path prefix, options); hosts of one group share a router. Every router and service is named
    /// <c>&lt;slug&gt;-&lt;workload&gt;</c> (<see cref="RouteNames"/>) because the proxy's names are shared by every container of the server.
    /// </summary>
    private static List<PlannedRoute> Routes(DeploymentSnapshot s, PlanContext c)
    {
        var compose = s.Compose;
        var known = compose is not null ? c.ComposeServices : null;
        var defaultPort = s.Ports.FirstOrDefault(p => p.IsHttp)?.ContainerPort ?? s.Ports.FirstOrDefault()?.ContainerPort;

        var resolved = new List<(DomainEntry Domain, RouteGroupKey Key)>();
        foreach (var d in s.Domains)
        {
            string? service = null;
            if (compose is not null)
            {
                service = d.ComposeService ?? compose.RoutedService;
                if (string.IsNullOrEmpty(service))
                    throw new ProxyRoutingException($"The domain {d.Hostname} names no compose service; choose the service that serves it.");
                if (known is not null && known.All(k => k.Name != service))
                    throw new ProxyRoutingException($"The domain {d.Hostname} routes to the service {service}, which is not in the compose file.");
            }

            var redirect = d.RedirectTo is { Length: > 0 };
            int? port = redirect ? 0
                : d.TargetPort ?? (compose is not null ? known?.FirstOrDefault(k => k.Name == service)?.Ports.Cast<int?>().FirstOrDefault() : null) ?? defaultPort;
            if (port is null)
            {
                if (compose is not null)
                    throw new ProxyRoutingException($"The domain {d.Hostname} needs a target port: the service {service} declares none.");
                continue;
            }

            if (d.Hostname.StartsWith("*.", StringComparison.Ordinal) && d.DnsProviderId is null)
                throw new ProxyRoutingException($"The wildcard domain {d.Hostname} needs a DNS provider (DNS-01 certificate).");

            var prefix = string.IsNullOrEmpty(d.PathPrefix) ? "/" : d.PathPrefix;
            resolved.Add((d, new RouteGroupKey(service, port.Value, prefix, d.Https, redirect ? d.RedirectTo : null,
                !redirect && d.StripPathPrefix && prefix != "/", redirect ? null : d.BasicAuthSecretId, redirect ? null : d.BasicAuthSecretVersion,
                d.Https && c.AcmeAvailable ? d.DnsProviderId : null)));
        }

        var baseName = RouteNames.ForWorkload(s.Slug, s.WorkloadId);
        var routes = new List<PlannedRoute>();
        var ordered = resolved.GroupBy(x => x.Key)
            .OrderBy(g => g.Key.Service, StringComparer.Ordinal).ThenBy(g => g.Key.Port).ThenBy(g => g.Key.Prefix, StringComparer.Ordinal)
            .ThenBy(g => g.Key.RedirectTo, StringComparer.Ordinal).ThenBy(g => g.Key.Https).ThenBy(g => g.Key.Strip)
            .ThenBy(g => g.Key.AuthId).ThenBy(g => g.Key.DnsProviderId);
        foreach (var (g, i) in ordered.Select((g, i) => (g, i)))
        {
            var key = g.Key;
            var hosts = g.Select(x => x.Domain.Hostname).Distinct().ToList();
            var resolver = !key.Https || !c.AcmeAvailable ? null
                : key.DnsProviderId is not null ? TraefikProxyProvider.CloudflareResolver : TraefikProxyProvider.CertResolver;
            IReadOnlyList<string>? users = key.AuthId is { } authId
                ? c.ResolveSecret(authId, key.AuthVersion ?? 0).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;
            routes.Add(new PlannedRoute(new RouteSpec(RouteNames.Group(baseName, i), hosts, key.Port, c.ProxyNetwork)
            {
                Https = key.Https, RedirectToHttps = key.Https, PathPrefix = key.Prefix == "/" ? null : key.Prefix, CertResolver = resolver,
                RedirectTo = key.RedirectTo, StripPathPrefix = key.Strip, BasicAuthUsers = users,
            }, key.Service));
        }

        return routes;
    }

    private static readonly System.Text.Json.JsonSerializerOptions OverrideJson = new()
    {
        // The file is read by docker compose, never embedded in HTML: keep backticks and non-ASCII characters readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Compose interpolates <c>$</c> in the override file too (<c>${1}</c> of a redirect, a regular expression, a bcrypt hash, a variable value): <c>$$</c> is a literal one.</summary>
    private static string Escape(string value) => value.Replace("$", "$$", StringComparison.Ordinal);

    private static DeploymentPlan ComposePlan(DeploymentSnapshot s, PlanContext c, ComposeSnapshot compose, List<PlannedRoute> routes)
    {
        var interpolation = s.Env.Where(e => e.Runtime || e.Build).Select(e => ToSpec(e, c)).ToList();
        var buildArgs = s.Env.Where(e => e.Build).Select(e => ToSpec(e, c)).ToList();
        var runtimeEnv = s.Env.Where(e => e.Runtime).Select(e => ToSpec(e, c)).ToList();

        // Services the override names: every service of the file when known, else the routed ones.
        var known = c.ComposeServices?.Select(x => x.Name).ToList();
        var services = known is { Count: > 0 } ? known : routes.Select(r => r.Service!).Distinct().ToList();
        var routed = routes.Where(r => r.Service is not null).GroupBy(r => r.Service!).ToDictionary(g => g.Key, g => g.Select(r => r.Route).ToList());
        var primary = routes.FirstOrDefault()?.Service ?? services.FirstOrDefault();

        string? overrideFile = null;
        if (services.Count > 0)
        {
            var envNetwork = EnvironmentNetwork(s.EnvironmentId);
            var serviceBlocks = new Dictionary<string, object>();
            foreach (var service in services)
            {
                var block = new Dictionary<string, object>();
                var labels = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["aethera.managed"] = "true", ["aethera.workload"] = s.WorkloadId.ToString(), ["aethera.environment"] = s.EnvironmentId.ToString(),
                };
                if (routed.TryGetValue(service, out var serviceRoutes))
                    foreach (var route in serviceRoutes)
                        foreach (var (k, v) in c.Proxy.RouteLabels(route)) labels[k] = v;
                if (service == primary) MergeCustomLabels(labels, s.CustomLabels);
                block["labels"] = labels.ToDictionary(kv => kv.Key, kv => Escape(kv.Value));

                if (runtimeEnv.Count > 0)
                    block["environment"] = runtimeEnv.OrderBy(e => e.Name, StringComparer.Ordinal)
                        .ToDictionary(e => e.Name, e => Escape(e.Secret?.Value ?? e.Plain ?? ""));

                // Networks: the project's own (a service that lists any network no longer joins `default` implicitly), the proxy for routed
                // services, and the managed ones. The environment network carries the service name (and a name unique to this workload) as alias.
                var networks = new Dictionary<string, object> { ["default"] = new Dictionary<string, object>() };
                if (routed.ContainsKey(service)) networks[c.ProxyNetwork] = new Dictionary<string, object>();
                foreach (var n in s.Networks)
                {
                    var aliases = new List<string>();
                    if (n.DockerName == envNetwork)
                    {
                        aliases.Add(service);
                        aliases.Add($"{s.Slug}-{service}");
                    }
                    if (service == primary) aliases.AddRange(n.Aliases);
                    networks[n.DockerName] = aliases.Count > 0
                        ? new Dictionary<string, object> { ["aliases"] = aliases.Distinct().ToList() }
                        : new Dictionary<string, object>();
                }
                block["networks"] = networks;
                serviceBlocks[service] = block;
            }

            var topNetworks = new Dictionary<string, object>();
            if (routed.Count > 0) topNetworks[c.ProxyNetwork] = new Dictionary<string, object> { ["external"] = true, ["name"] = c.ProxyNetwork };
            foreach (var n in s.Networks) topNetworks[n.DockerName] = new Dictionary<string, object> { ["external"] = true, ["name"] = n.DockerName };
            overrideFile = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object> { ["services"] = serviceBlocks, ["networks"] = topNetworks }, OverrideJson);
        }

        ComposeProjectSpec project;
        if (compose.FilePath is { Length: > 0 } filePath)
        {
            var git = s.Git ?? throw new InvalidOperationException("A compose file in the repository needs a git source.");
            project = new ComposeProjectSpec(ComposeProjectName(s), "")
            {
                Git = new GitSourceSpec(git.Url, git.Branch, git.CommitPin, Depth: git.CommitPin is null ? 1 : 0, Credentials: c.GitCredential),
                ComposePath = filePath, BuildArgs = buildArgs.Count > 0 ? buildArgs : null,
            };
        }
        else
        {
            project = new ComposeProjectSpec(ComposeProjectName(s), compose.Content);
        }

        var allNetworks = s.Networks.Select(n => n.DockerName).Concat(routes.Count > 0 ? [c.ProxyNetwork] : []).Distinct().ToList();
        return new DeploymentPlan
        {
            ServerId = c.ServerId, JobId = c.JobId, OrganizationId = c.OrganizationId, OnLog = c.OnLog, PreviousContainer = c.PreviousContainer,
            Networks = allNetworks, InternalNetworks = s.Networks.Where(n => n.Internal).Select(n => n.DockerName).ToHashSet(),
            Compose = project with { OverrideFile = overrideFile, Env = interpolation, RegistryAuths = c.Registry is null ? null : [c.Registry] },
        };
    }
}
