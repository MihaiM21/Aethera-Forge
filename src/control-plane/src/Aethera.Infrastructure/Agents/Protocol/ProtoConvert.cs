using Aethera.Domain.Transport;
using Duration = Google.Protobuf.WellKnownTypes.Duration;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Protocol;

/// <summary>Conversions between the generated protocol types (<c>Aethera.Agent.V1</c>) and the domain mirrors in <c>Aethera.Domain.Transport</c>.</summary>
/// <remarks>
/// The domain enums use the protocol's numeric values on purpose (the protocol only evolves additively), so most enum conversions are casts;
/// a unit test pins that equality by name.
/// </remarks>
internal static class ProtoConvert
{
    // ---- primitives ------------------------------------------------------------------------------------------------------------------

    public static Duration? ToProto(this TimeSpan? value) => value is { } v ? Duration.FromTimeSpan(v) : null;

    public static Timestamp? ToProto(this DateTimeOffset? value) => value is { } v ? Timestamp.FromDateTimeOffset(v) : null;

    public static TimeSpan ToTimeSpan(this Duration? value) => value is null ? TimeSpan.Zero : value.ToTimeSpan();

    public static DateTimeOffset? ToDomain(this Timestamp? value) =>
        value is null || (value.Seconds == 0 && value.Nanos == 0) || value.Seconds < -62135596800L ? null : value.ToDateTimeOffset();

    public static T EnumOf<T>(int value) where T : struct, Enum => Enum.IsDefined(typeof(T), value) ? (T)(object)value : default;

    private static void AddAll<T>(Google.Protobuf.Collections.RepeatedField<T> target, IEnumerable<T>? source)
    {
        if (source is not null) target.Add(source);
    }

    private static void AddAll(Google.Protobuf.Collections.MapField<string, string> target, IReadOnlyDictionary<string, string>? source)
    {
        if (source is null) return;
        foreach (var (key, value) in source) target[key] = value;
    }

    private static IReadOnlyDictionary<string, string> ToDict(Google.Protobuf.Collections.MapField<string, string> map) =>
        map.ToDictionary(p => p.Key, p => p.Value);

    // ---- domain -> proto (commands) --------------------------------------------------------------------------------------------------

    public static P.SecretValue? ToProto(this SecretValue? value) => value is null ? null : new P.SecretValue { Value = value.Value };

    public static P.EnvVar ToProto(this EnvVarSpec env)
    {
        var message = new P.EnvVar { Name = env.Name };
        if (env.Secret is not null) message.Secret = env.Secret.ToProto();
        else message.Plain = env.Plain ?? "";
        return message;
    }

    public static IEnumerable<P.EnvVar> ToProto(this IEnumerable<EnvVarSpec>? env) => env?.Select(ToProto) ?? [];

    public static P.RegistryAuth? ToProto(this RegistryCredentials? auth) => auth is null ? null : new P.RegistryAuth
    {
        Server = auth.Server, Username = auth.Username, Password = auth.Password.ToProto(), IdentityToken = auth.IdentityToken.ToProto(),
    };

    public static P.PortMapping ToProto(this PortMappingSpec port) => new()
    {
        ContainerPort = (uint)port.ContainerPort, HostPort = (uint)port.HostPort, HostIp = port.HostIp ?? "", Protocol = (P.PortProtocol)(int)port.Protocol,
    };

    public static P.VolumeMount ToProto(this VolumeMountSpec mount) => new()
    {
        Type = (P.MountType)(int)mount.Type, Source = mount.Source, Target = mount.Target, ReadOnly = mount.ReadOnly, TmpfsSizeBytes = mount.TmpfsSizeBytes,
    };

    public static P.ContainerSpec ToProto(this ContainerSpec spec)
    {
        var message = new P.ContainerSpec
        {
            Image = spec.Image, Name = spec.Name, Hostname = spec.Hostname ?? "", WorkingDir = spec.WorkingDir ?? "", User = spec.User ?? "",
            StopSignal = spec.StopSignal ?? "", StopTimeout = spec.StopTimeout.ToProto(), ReadOnlyRootFs = spec.ReadOnlyRootFs, Init = spec.Init,
        };
        AddAll(message.Entrypoint, spec.Entrypoint);
        AddAll(message.Command, spec.Command);
        message.Env.Add(spec.Env.ToProto());
        AddAll(message.Labels, spec.Labels);
        if (spec.Ports is not null) message.Ports.Add(spec.Ports.Select(ToProto));
        if (spec.Mounts is not null) message.Mounts.Add(spec.Mounts.Select(ToProto));
        if (spec.Networks is not null)
            message.Networks.Add(spec.Networks.Select(n =>
            {
                var attachment = new P.NetworkAttachment { Network = n.Network, Ipv4Address = n.Ipv4Address ?? "" };
                AddAll(attachment.Aliases, n.Aliases);
                return attachment;
            }));
        if (spec.Resources is { } r)
            message.Resources = new P.ResourceLimits
            {
                CpuLimitCores = r.CpuLimitCores, CpuReservationCores = r.CpuReservationCores, MemoryLimitBytes = r.MemoryLimitBytes,
                MemoryReservationBytes = r.MemoryReservationBytes, MemorySwapLimitBytes = r.MemorySwapLimitBytes, PidsLimit = r.PidsLimit, CpusetCpus = r.CpusetCpus ?? "",
            };
        if (spec.RestartPolicy is { } rp) message.RestartPolicy = new P.RestartPolicy { Name = (P.RestartPolicyName)(int)rp.Name, MaximumRetryCount = rp.MaximumRetryCount };
        if (spec.Healthcheck is { } h)
        {
            message.Healthcheck = new P.HealthcheckSpec
            {
                Interval = h.Interval.ToProto(), Timeout = h.Timeout.ToProto(), Retries = h.Retries, StartPeriod = h.StartPeriod.ToProto(), StartInterval = h.StartInterval.ToProto(),
            };
            message.Healthcheck.Test.Add(h.Test);
        }

        if (spec.LogConfig is { } log)
        {
            message.LogConfig = new P.LogConfig { Driver = log.Driver };
            AddAll(message.LogConfig.Options, log.Options);
        }

        AddAll(message.CapDrop, spec.CapDrop);
        AddAll(message.ExtraHosts, spec.ExtraHosts);
        return message;
    }

    public static P.ComposeProject ToProto(this ComposeProjectSpec project)
    {
        var message = new P.ComposeProject { ProjectName = project.ProjectName, ComposeFile = project.ComposeFile, OverrideFile = project.OverrideFile ?? "" };
        message.Env.Add(project.Env.ToProto());
        AddAll(message.Profiles, project.Profiles);
        if (project.RegistryAuths is not null) message.RegistryAuths.Add(project.RegistryAuths.Select(a => a.ToProto()!));
        return message;
    }

    public static P.GitSource ToProto(this GitSourceSpec git)
    {
        var message = new P.GitSource { Url = git.Url, Ref = git.Ref ?? "", Commit = git.Commit ?? "", Depth = git.Depth, Submodules = git.Submodules };
        if (git.Credentials is { } c)
        {
            message.Credentials = new P.GitCredentials { CredentialsRef = c.CredentialsRef };
            if (c.SshPrivateKey is not null)
                message.Credentials.SshKey = new P.SshKeyCredential { PrivateKey = c.SshPrivateKey.ToProto(), KnownHosts = c.KnownHosts ?? "" };
            else if (c.HttpsToken is not null)
                message.Credentials.HttpsToken = new P.HttpsTokenCredential { Username = c.HttpsUsername ?? "", Token = c.HttpsToken.ToProto() };
        }

        return message;
    }

    public static P.BuildRequest ToProto(this BuildSpec spec)
    {
        var message = new P.BuildRequest
        {
            BuildId = spec.BuildId, Engine = (P.BuildEngine)(int)spec.Engine, EngineName = spec.EngineName ?? "", Git = spec.Git?.ToProto(),
            ImageReference = spec.ImageReference ?? "", ImagePullAuth = spec.ImagePullAuth.ToProto(), ContextPath = spec.ContextPath ?? "",
            DockerfilePath = spec.DockerfilePath ?? "", TargetStage = spec.TargetStage ?? "", InstallCommand = spec.InstallCommand ?? "",
            BuildCommand = spec.BuildCommand ?? "", StartCommand = spec.StartCommand ?? "", OutputDir = spec.OutputDir ?? "", SpaFallback = spec.SpaFallback,
            Push = spec.Push,
        };
        message.BuildArgs.Add(spec.BuildArgs.ToProto());
        message.Env.Add(spec.Env.ToProto());
        if (spec.Secrets is not null) message.Secrets.Add(spec.Secrets.Select(s => new P.BuildSecret { Id = s.Id, Value = s.Value.ToProto() }));
        if (spec.Cache is { } cache)
        {
            message.Cache = new P.BuildCacheSettings
            {
                Enabled = cache.Enabled, Mode = (P.CacheMode)cache.Mode, CacheTo = cache.CacheTo ?? "", CacheKey = cache.CacheKey ?? "", NoCache = cache.NoCache, MaxSizeBytes = cache.MaxSizeBytes,
            };
            AddAll(message.Cache.CacheFrom, cache.CacheFrom);
        }

        AddAll(message.TargetPlatforms, spec.TargetPlatforms);
        message.ImageTags.Add(spec.ImageTags);
        AddAll(message.ImageLabels, spec.ImageLabels);
        if (spec.PushAuth is not null) message.PushOptions = new P.PushOptions { Auth = spec.PushAuth.ToProto() };
        return message;
    }

    public static P.HealthProbe ToProto(this HealthProbeCommand command)
    {
        var message = new P.HealthProbe
        {
            Timeout = command.Timeout.ToProto(), Interval = command.Interval.ToProto(), Retries = command.Retries, StartPeriod = command.StartPeriod.ToProto(),
            SuccessThreshold = command.SuccessThreshold,
        };
        switch (command.Target)
        {
            case HttpProbeTarget http:
                message.Http = new P.HttpProbe
                {
                    Url = http.Url, Method = http.Method ?? "", BodyContains = http.BodyContains ?? "", FollowRedirects = http.FollowRedirects, InsecureSkipVerify = http.InsecureSkipVerify,
                };
                AddAll(message.Http.Headers, http.Headers);
                if (http.ExpectedStatus is not null) message.Http.ExpectedStatus.Add(http.ExpectedStatus);
                break;
            case TcpProbeTarget tcp:
                message.Tcp = new P.TcpProbe { Host = tcp.Host, Port = (uint)tcp.Port };
                break;
            case ContainerHealthProbeTarget container:
                message.Container = new P.ContainerHealthProbe { Container = container.Container };
                break;
        }

        return message;
    }

    private static P.IpamConfig ToProto(IpamEntry entry) => new() { Subnet = entry.Subnet, Gateway = entry.Gateway };

    public static P.NetworkCreate ToProto(this NetworkCreateCommand c)
    {
        var message = new P.NetworkCreate
        {
            Name = c.NetworkName, Driver = c.Driver ?? "", Internal = c.Internal, Attachable = c.Attachable, Ipv6 = c.Ipv6, IfNotExists = c.IfNotExists,
        };
        if (c.Ipam is not null) message.Ipam.Add(c.Ipam.Select(ToProto));
        AddAll(message.Options, c.Options);
        AddAll(message.Labels, c.Labels);
        return message;
    }

    // ---- proto -> domain (results) ---------------------------------------------------------------------------------------------------

    public static PortMappingSpec ToDomain(this P.PortMapping p) =>
        new((int)p.ContainerPort, (int)p.HostPort, p.HostIp.Length == 0 ? null : p.HostIp,
            Enum.IsDefined(typeof(PortProtocolKind), (int)p.Protocol) ? (PortProtocolKind)(int)p.Protocol : PortProtocolKind.Tcp);

    public static VolumeMountSpec ToDomain(this P.VolumeMount m) =>
        new(EnumOf<MountKind>((int)m.Type), m.Source, m.Target, m.ReadOnly, m.TmpfsSizeBytes);

    public static DockerContainer ToDomain(this P.ContainerInfo c) => new(
        c.Id, c.Name, c.Image, c.ImageId, EnumOf<ContainerRunState>((int)c.State), c.Status, EnumOf<ContainerHealthState>((int)c.Health), c.CreatedAt.ToDomain(),
        c.StartedAt.ToDomain(), c.FinishedAt.ToDomain(), c.ExitCode, c.OomKilled, (int)c.RestartCount, ToDict(c.Labels), c.Ports.Select(ToDomain).ToList(),
        c.Mounts.Select(ToDomain).ToList(), c.Networks.Select(n => new ContainerNetworkInfo(n.Network, n.IpAddress, n.MacAddress, n.Aliases.ToList())).ToList(),
        c.InspectJson.Length == 0 ? null : c.InspectJson.ToStringUtf8());

    public static DockerImage ToDomain(this P.ImageInfo i) => new(
        i.Id, i.RepoTags.ToList(), i.RepoDigests.ToList(), i.SizeBytes, i.CreatedAt.ToDomain(), ToDict(i.Labels), i.Architecture, i.Os, i.ContainersUsing);

    public static DockerVolume ToDomain(this P.VolumeInfo v) =>
        new(v.Name, v.Driver, v.Mountpoint, ToDict(v.Labels), v.CreatedAt.ToDomain(), v.SizeBytes, v.RefCount);

    public static DockerNetwork ToDomain(this P.NetworkInfo n) => new(
        n.Id, n.Name, n.Driver, n.Scope, n.Internal, n.Attachable, n.Ipv6, n.Ipam.Select(i => new IpamEntry(i.Subnet, i.Gateway)).ToList(), ToDict(n.Labels),
        n.CreatedAt.ToDomain(), n.Endpoints.Select(e => new NetworkEndpointInfo(e.ContainerId, e.ContainerName, e.Ipv4Address)).ToList());

    public static PruneOutcome ToDomain(this P.PruneResult r) => new(r.Deleted.ToList(), r.SpaceReclaimedBytes);

    public static ComposeOutcome ToDomain(this P.ComposeResult r) => new(r.ProjectName, r.Services.Select(s => new ComposeServiceInfo(
        s.Service, s.ContainerId, s.ContainerName, s.Image, EnumOf<ContainerRunState>((int)s.State), EnumOf<ContainerHealthState>((int)s.Health), s.ExitCode,
        s.Ports.Select(ToDomain).ToList())).ToList());

    public static BuildOutcome ToDomain(this P.BuildResult r) => new(
        r.BuildId, r.ImageId, r.Digest, r.Tags.ToList(), r.RepoDigests.ToList(), r.SizeBytes, r.Duration.ToTimeSpan(), r.EngineUsed, r.CommitSha, r.Pushed, r.Platform, r.CacheHit);

    public static BuildDetection ToDomain(this P.BuildDetectResult r) => new(r.Candidates.Select(c => new BuildCandidateInfo(
        EnumOf<BuildEngineKind>((int)c.Engine), c.EngineName, c.Confidence, c.Reason, c.DockerfilePath, c.InstallCommand, c.BuildCommand, c.StartCommand, c.OutputDir,
        c.Language, c.SuggestedPorts.Select(p => (int)p).ToList())).ToList(), r.CommitSha);

    public static HealthProbeOutcome ToDomain(this P.HealthProbeResult r) =>
        new(r.Healthy, r.Attempts, r.HttpStatus, r.Latency.ToTimeSpan(), r.Detail, r.CheckedAt.ToDomain());

    public static DiscoveryInfo ToDomain(this P.DiscoveryReport r)
    {
        var h = r.Host ?? new P.HostFacts();
        var d = r.Docker ?? new P.DockerInfo();
        return new DiscoveryInfo(
            r.CollectedAt.ToDomain(),
            new HostFactsInfo(h.Hostname, h.OsName, h.OsVersion, h.KernelVersion, h.Architecture, h.CpuModel, (int)h.CpuCoresPhysical, (int)h.CpuCoresLogical,
                h.MemoryTotalBytes, h.SwapTotalBytes, h.Virtualization, h.IpAddresses.ToList(), h.Timezone, h.BootTime.ToDomain()),
            new DockerDaemonInfo(AgentFacts.ToDomain(d.Status), d.Version, d.ApiVersion, d.StorageDriver, d.CgroupVersion, d.DockerRootDir, d.ComposeVersion, d.BuildxVersion,
                d.Rootless, d.SwarmActive, (int)d.ContainersRunning, (int)d.ContainersStopped, (int)d.ImageCount, d.Error),
            r.Disks.Select(x => new DiskInfo(x.MountPoint, x.Device, x.FsType, x.TotalBytes, x.UsedBytes, x.InodesTotal, x.InodesUsed)).ToList(),
            r.Interfaces.Select(x => new NetworkInterfaceInfo(x.Name, x.MacAddress, x.Addresses.ToList(), (int)x.Mtu, x.Up)).ToList(),
            r.Containers.Select(ToDomain).ToList(), r.Networks.Select(ToDomain).ToList(), r.Volumes.Select(ToDomain).ToList(),
            r.Tools.Select(t => new ToolInfo(t.Name, t.Version, t.Path)).ToList());
    }
}
