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

    /// <inheritdoc cref="DeploymentPlan.WriteLog"/>
    public Func<LogStream, string, CancellationToken, ValueTask>? WriteLog { get; init; }

    /// <inheritdoc cref="DeploymentPlan.Narrate"/>
    public Func<string, CancellationToken, ValueTask>? Narrate { get; init; }

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

    /// <summary>The run server's address in the private mesh (ADR 0012); ports with <see cref="PortBindScope.Private"/> bind to it.</summary>
    public string? MeshAddress { get; init; }

    // ---- build elsewhere and build quota (ADR 0012) -----------------------------------------------------------------------------------

    /// <summary>Names of the build server (the snapshot's <see cref="BuildSnapshot.BuildServerId"/>) and the run server, for the log.</summary>
    public string? BuildServerName { get; init; }

    public string? RunServerName { get; init; }

    /// <summary>
    /// <c>&lt;registry host&gt;/&lt;namespace&gt;</c> the build server pushes to; null when it has no build registry, which fails a build elsewhere
    /// with <see cref="FailureCodes.BuildRegistryMissing"/> before anything is built. Credentials are in <see cref="BuildRegistry"/> (push) and
    /// <see cref="Registry"/> (pull) and are the same ones.
    /// </summary>
    public string? BuildRegistryPrefix { get; init; }

    public RegistryCredentials? BuildRegistry { get; init; }

    /// <summary>The build server's quota, set only when its agent can enforce it; null = the daemon's builder.</summary>
    public ResourceLimitsSpec? BuilderLimits { get; init; }

    /// <inheritdoc cref="DeploymentPlan.AcquireBuildSlot"/>
    public Func<CancellationToken, Task<IAsyncDisposable>>? AcquireBuildSlot { get; init; }

    /// <summary>
    /// <see cref="ExistingImage"/> is a registry reference (the rollback target was built elsewhere): it is pulled with <see cref="Registry"/> when
    /// the run server does not have it, instead of being expected locally.
    /// </summary>
    public bool ExistingImageIsRemote { get; init; }
}

/// <summary>Turns a <see cref="DeploymentSnapshot"/> into the <see cref="DeploymentPlan"/> the runner executes. Pure: no I/O.</summary>
public static class DeploymentPlanBuilder
{
    /// <summary>
    /// The host address a published port binds to (ADR 0012): every interface, the server's mesh address, or loopback. A private port on a server
    /// without a mesh address fails the Network step instead of quietly listening on the internet.
    /// </summary>
    public static string? BindAddress(PortEntry port, PlanContext c) => port.BindScope switch
    {
        PortBindScope.Loopback => "127.0.0.1",
        PortBindScope.Private => c.MeshAddress is { Length: > 0 } mesh
            ? mesh
            : throw new DeploymentFailure(DeploymentStep.Network, FailureCodes.NoMeshAddress,
                $"Port {port.PublishedPort} is published on the private network, but this server has no mesh address. Turn on the private network in Settings > Network or publish the port publicly."),
        _ => null,
    };

    /// <summary>
    /// The container's limits with its priority applied (ADR 0012): High doubles the CPU shares that come from the reservation (one core's worth
    /// without one) and shields it from the OOM killer, Low halves them and offers it first.
    /// </summary>
    public static ResourceLimitsSpec Resources(RuntimeSnapshot r)
    {
        var reservation = r.CpuReservation ?? 0;
        var (cpuReservation, oom) = r.Priority switch
        {
            WorkloadPriority.High => ((reservation > 0 ? reservation : 1) * 2, -500),
            WorkloadPriority.Low => ((reservation > 0 ? reservation : 1) / 2, 500),
            _ => (reservation, 0),
        };
        return new ResourceLimitsSpec(r.CpuLimit ?? 0, cpuReservation, r.MemoryLimitBytes ?? 0, r.MemoryReservationBytes ?? 0, 0, r.PidsLimit ?? 0, OomScoreAdj: oom);
    }

    /// <summary>Stable container name of a workload on its server.</summary>
    public static string ContainerName(DeploymentSnapshot s) => $"{s.Slug}-{s.WorkloadId.ToString("N")[..8]}";

    public static string ComposeProjectName(DeploymentSnapshot s) => $"{s.Slug}-{s.WorkloadId.ToString("N")[..8]}";

    /// <summary>The network every workload of an environment joins, so workloads reach each other by name.</summary>
    public static string EnvironmentNetwork(Guid environmentId) => $"aethera-env-{environmentId.ToString("N")[..8]}";

    /// <summary>
    /// The plan before the image is known. For a container workload it carries <see cref="DeploymentPlan.WithImage"/>: the runner rebuilds it with the
    /// image's facts after the Image step, which settles the port of a workload that declares none and the liveness check.
    /// </summary>
    public static DeploymentPlan Build(DeploymentSnapshot s, PlanContext c) => Build(s, c, null);

    /// <param name="facts">The image about to run; null = not known yet (domains without a port are then left for the rebuild).</param>
    public static DeploymentPlan Build(DeploymentSnapshot s, PlanContext c, ImageFacts? facts)
    {
        var name = ContainerName(s);
        var originalSnapshot = s;

        // A workload that declares no port gets the one its image exposes (or its build engine's), like Coolify reads EXPOSE.
        int? detected = null;
        if (s.Compose is null && facts is not null && s.Ports.Count == 0 && (facts.PreferredPort ?? EngineDefaultPort(s)) is { } port)
        {
            detected = port;
            s = s with { Ports = [new PortEntry(port, PortProtocol.Tcp, null, true)] };
        }

        var routes = Routes(s, c, portKnown: s.Compose is not null || facts is not null);
        var labels = new Dictionary<string, string>
        {
            ["aethera.managed"] = "true",
            ["aethera.workload"] = s.WorkloadId.ToString(),
            ["aethera.environment"] = s.EnvironmentId.ToString(),
            ["aethera.deployment"] = c.DeploymentId.ToString(),
        };
        if (s.Preview is { } preview) labels["aethera.preview"] = preview.PreviewId.ToString();

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
                .Select(p => new PortMappingSpec(p.ContainerPort, p.PublishedPort!.Value, BindAddress(p, c), p.Protocol == PortProtocol.Udp ? PortProtocolKind.Udp : PortProtocolKind.Tcp))
                .ToList(),
            // Named volumes live in the workload's own namespace (VolumeNames): prod and dev never share a Docker volume.
            Mounts = s.Volumes.Select(v => v.HostPath is { Length: > 0 }
                ? new VolumeMountSpec(MountKind.Bind, v.HostPath, v.MountPath, v.ReadOnly)
                : new VolumeMountSpec(MountKind.Volume, VolumeNames.DockerName(s.Slug, s.WorkloadId, v.Name), v.MountPath, v.ReadOnly)).ToList(),
            StopTimeout = TimeSpan.FromSeconds(Math.Max(1, s.Runtime.StopTimeoutSeconds)),
            Networks = networks,
            Resources = Resources(s.Runtime),
            RestartPolicy = new RestartPolicySpec(s.Runtime.RestartPolicy switch
            {
                RestartPolicy.No => RestartPolicyKind.No,
                RestartPolicy.Always => RestartPolicyKind.Always,
                RestartPolicy.OnFailure => RestartPolicyKind.OnFailure,
                _ => RestartPolicyKind.UnlessStopped,
            }),
        };

        var allNetworks = s.Networks.Select(n => n.DockerName).Concat(routes.Count > 0 ? [c.ProxyNetwork] : []).Distinct().ToList();
        var health = HealthTargets(s, facts);
        // Without a configured check the container must still be up a few seconds after it started (a crash at start fails the deployment
        // instead of reporting it running), and an image with its own HEALTHCHECK must report healthy.
        var liveness = s.Runtime.HealthType == HealthCheckType.None && facts is not null;
        var common = new DeploymentPlan
        {
            ServerId = c.ServerId, Container = container, PreviousContainer = c.PreviousContainer, Networks = allNetworks,
            InternalNetworks = s.Networks.Where(n => n.Internal).Select(n => n.DockerName).ToHashSet(),
            JobId = c.JobId, OrganizationId = c.OrganizationId, OnLog = c.OnLog,
            HealthFor = health,
            HealthTimeout = TimeSpan.FromSeconds(liveness ? 5 : s.Runtime.HealthTimeoutSeconds),
            HealthInterval = TimeSpan.FromSeconds(liveness ? 2 : s.Runtime.HealthIntervalSeconds),
            HealthRetries = liveness ? (facts!.HasHealthcheck ? LivenessRetriesWithHealthcheck : LivenessRetries) : Math.Max(1, s.Runtime.HealthRetries),
            HealthStartPeriod = TimeSpan.FromSeconds(liveness ? 2 : s.Runtime.HealthStartPeriodSeconds),
            HealthSuccessThreshold = liveness ? LivenessSuccesses : 1,
            StopTimeout = TimeSpan.FromSeconds(Math.Max(1, s.Runtime.StopTimeoutSeconds)),
            DetectedPort = detected, WriteLog = c.WriteLog, Narrate = c.Narrate,
            WithImage = facts is null ? f => Build(originalSnapshot, c, f) : null,
            // Only a plan that builds uses these: a rollback or an image source has nothing to build.
            BuildServerId = c.ExistingImage is null && s.Image is null && BuildsElsewhere(s, c) ? s.Build!.BuildServerId : null,
            BuildServerName = c.BuildServerName, RunServerName = c.RunServerName, AcquireBuildSlot = c.AcquireBuildSlot,
        };

        if (c.ExistingImage is { Length: > 0 } existing)
            // A rollback to an image that came through a registry pulls it when the server no longer has it (ADR 0012); others expect it locally.
            return c.ExistingImageIsRemote
                ? Copy(common, imageReference: existing, pullAuth: c.Registry, pullPolicy: ImagePullPolicy.IfNotPresent)
                : Copy(common, imageReference: existing, local: true);

        if (s.Image is { } image)
        {
            var reference = image.Tag.StartsWith('@') ? image.Image + image.Tag : $"{image.Image}:{image.Tag}";
            return Copy(common, imageReference: reference, pullAuth: c.Registry, pullPolicy: image.PullPolicy);
        }

        // Built elsewhere: the run server pulls what the build server pushed, with the same registry credentials.
        return Copy(common, build: BuildSpec(s, c), pullAuth: BuildsElsewhere(s, c) ? c.Registry : null);
    }

    /// <summary>True when the snapshot names a build server other than the run server (ADR 0012).</summary>
    public static bool BuildsElsewhere(DeploymentSnapshot s, PlanContext c) => s.Build?.BuildServerId is { } id && id != c.ServerId;

    private static DeploymentPlan Copy(
        DeploymentPlan p, string? imageReference = null, bool local = false, RegistryCredentials? pullAuth = null, BuildSpec? build = null,
        ImagePullPolicy pullPolicy = ImagePullPolicy.Always) =>
        new()
        {
            BuildServerId = p.BuildServerId, BuildServerName = p.BuildServerName, RunServerName = p.RunServerName, AcquireBuildSlot = p.AcquireBuildSlot,
            ServerId = p.ServerId, Container = p.Container, PreviousContainer = p.PreviousContainer, Networks = p.Networks, InternalNetworks = p.InternalNetworks, JobId = p.JobId,
            OrganizationId = p.OrganizationId, OnLog = p.OnLog, HealthFor = p.HealthFor, HealthTimeout = p.HealthTimeout, HealthInterval = p.HealthInterval,
            HealthRetries = p.HealthRetries, HealthStartPeriod = p.HealthStartPeriod, HealthSuccessThreshold = p.HealthSuccessThreshold, StopTimeout = p.StopTimeout,
            DetectedPort = p.DetectedPort, WriteLog = p.WriteLog, Narrate = p.Narrate, WithImage = p.WithImage,
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
        // The build configuration's own arguments first; a build variable of the same name (it may be a secret) replaces one.
        var configured = (b.BuildArgs ?? new Dictionary<string, string>())
            .Where(kv => buildVars.All(v => v.Name != kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => EnvVarSpec.OfPlain(kv.Key, kv.Value));
        var buildArgs = configured.Concat(buildVars).ToList();

        // Built for another server (ADR 0012): tagged for the build server's registry and pushed; without one nothing is built.
        var elsewhere = BuildsElsewhere(s, c);
        if (elsewhere && c.BuildRegistryPrefix is not { Length: > 0 })
            throw new DeploymentFailure(DeploymentStep.Build, FailureCodes.BuildRegistryMissing,
                $"The application is built on {c.BuildServerName ?? "another server"} but runs on {c.RunServerName ?? "this one"}, and the image has to travel through a registry. " +
                "Choose a build registry in the build server's settings (Servers > the build server > Settings), or build on the run server.");
        var tag = elsewhere ? ImagePolicy.RegistryTag(c.BuildRegistryPrefix!, s.Slug, c.DeploymentId) : ImagePolicy.Tag(s.Slug, c.DeploymentId);

        return new BuildSpec((c.BuildId ?? c.DeploymentId).ToString(), engine, [tag])
        {
            Push = elsewhere, PushAuth = elsewhere ? c.BuildRegistry : null,
            // The quota applies to the engines that build with `docker build`; Nixpacks drives Docker itself and stays on the daemon's builder.
            BuilderLimits = engine == BuildEngineKind.Nixpacks ? null : c.BuilderLimits,
            Git = new GitSourceSpec(git.Url, git.Branch, git.CommitPin, Depth: git.CommitPin is null ? 1 : 0, Credentials: c.GitCredential),
            ContextPath = b.Context,
            DockerfilePath = b.DockerfilePath,
            BuildArgs = engine == BuildEngineKind.Dockerfile ? buildArgs : null,
            Env = engine == BuildEngineKind.Dockerfile ? null : buildVars,
            InstallCommand = b.InstallCommand,
            BuildCommand = b.BuildCommand,
            StartCommand = b.StartCommand,
            OutputDir = b.OutputDirectory,
            SpaFallback = engine == BuildEngineKind.Static,
            // A preview builds code from a pull request: its layers never feed the application's own cache (ADR 0012).
            Cache = new BuildCacheSpec(b.CacheEnabled, 1, CacheKey: s.Preview is { } preview ? $"{s.WorkloadId:N}-pr-{preview.Number}" : $"{s.WorkloadId:N}"),
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

    /// <summary>Probes 2 s apart of the liveness check: about 20 s for a plain image, about 3 minutes for one whose HEALTHCHECK must pass.</summary>
    internal const int LivenessRetries = 10, LivenessRetriesWithHealthcheck = 90, LivenessSuccesses = 3;

    /// <summary>The port of the build engine's image when the image exposes none: the static engine serves nginx on 80, Nixpacks apps conventionally 3000.</summary>
    private static int? EngineDefaultPort(DeploymentSnapshot s) => s.Image is not null ? null : s.Build?.Engine switch
    {
        BuildEngines.Static => 80,
        BuildEngines.Nixpacks => 3000,
        _ => null,
    };

    private static Func<string, ProbeTarget?>? HealthTargets(DeploymentSnapshot s, ImageFacts? facts)
    {
        var r = s.Runtime;
        if (r.HealthType == HealthCheckType.None)
            return facts is null ? null : container => new ContainerHealthProbeTarget(container);
        var port = r.HealthPort ?? s.Ports.FirstOrDefault(p => p.IsHttp)?.ContainerPort ?? s.Ports.FirstOrDefault()?.ContainerPort;
        // The primary domain as Host header: frameworks with a host allowlist (Django ALLOWED_HOSTS, Rails hosts) answer 400 to an IP.
        var host = s.Domains.FirstOrDefault(d => d.RedirectTo is null && !d.Hostname.StartsWith("*.", StringComparison.Ordinal))?.Hostname;
        return container => r.HealthType switch
        {
            HealthCheckType.Container => new ContainerHealthProbeTarget(container),
            HealthCheckType.Tcp when port is { } p => new TcpProbeTarget(container, p),
            HealthCheckType.Http when port is { } p => HttpTarget(container, p, r.HealthPath, host),
            // A check that needs a port the workload does not have still makes sure the container runs.
            _ => facts is null ? null : new ContainerHealthProbeTarget(container),
        };
    }

    private static HttpProbeTarget HttpTarget(string container, int port, string? path, string? host)
    {
        var target = new HttpProbeTarget($"http://{container}:{port}{(path is { Length: > 0 } ? (path.StartsWith('/') ? path : "/" + path) : "/")}");
        return host is null ? target : target with { Headers = new Dictionary<string, string> { ["Host"] = host } };
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
    /// <param name="portKnown">False before the image is inspected: a domain of a workload without a port is then left for the rebuild.</param>
    private static List<PlannedRoute> Routes(DeploymentSnapshot s, PlanContext c, bool portKnown)
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
                if (portKnown)
                    throw new ProxyRoutingException(
                        $"The domain {d.Hostname} needs a port: the image exposes none. Set the port your application listens on (Configuration > Ports).");
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
                if (s.Preview is { } preview) labels["aethera.preview"] = preview.PreviewId.ToString();
                if (routed.TryGetValue(service, out var serviceRoutes))
                    foreach (var route in serviceRoutes)
                        foreach (var (k, v) in c.Proxy.RouteLabels(route)) labels[k] = v;
                if (service == primary) MergeCustomLabels(labels, s.CustomLabels);
                block["labels"] = labels.ToDictionary(kv => kv.Key, kv => Escape(kv.Value));

                if (runtimeEnv.Count > 0)
                    block["environment"] = runtimeEnv.OrderBy(e => e.Name, StringComparer.Ordinal)
                        .ToDictionary(e => e.Name, e => Escape(e.Secret?.Value ?? e.Plain ?? ""));

                // A service in another one's network namespace (network_mode: service:db) cannot list networks: compose refuses both. It is
                // reached through the service that owns the namespace, so it cannot carry a route of its own either.
                var networkMode = c.ComposeServices?.FirstOrDefault(x => x.Name == service)?.NetworkMode;
                if (networkMode is { Length: > 0 } && networkMode is not ("bridge" or "default"))
                {
                    if (routed.ContainsKey(service))
                        throw new ProxyRoutingException(
                            $"The service {service} uses network_mode {networkMode}: route its domain to the service that owns that network instead.");
                    serviceBlocks[service] = block;
                    continue;
                }

                // Networks: the project's own (a service that lists any network no longer joins `default` implicitly), the proxy for routed
                // services, and the managed ones. The environment network carries the service name (and a name unique to this workload) as alias.
                var networks = new Dictionary<string, object> { ["default"] = new Dictionary<string, object>() };
                if (routed.ContainsKey(service)) networks[c.ProxyNetwork] = new Dictionary<string, object>();
                foreach (var n in s.Networks)
                {
                    var aliases = new List<string>();
                    if (n.DockerName == envNetwork)
                    {
                        // A preview answers to its own name only: the plain service name belongs to the application it previews.
                        if (s.Preview is null) aliases.Add(service);
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
