using System.Globalization;
using System.Text;
using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Ssh.Docker;

/// <summary>Host-side rules the SSH transport enforces itself, mirroring the agent's local policy (<c>agent.yaml</c>).</summary>
public sealed class SshDockerPolicy
{
    /// <summary>Host directories bind mounts may live under. Default: only <c>/var/lib/aethera</c>, as the agent.</summary>
    public IReadOnlyList<string> AllowedBindPrefixes { get; init; } = ["/var/lib/aethera"];

    /// <summary>Directory below which Compose projects are stored (ADR 0002).</summary>
    public string ProjectsDirectory { get; init; } = "/var/lib/aethera/projects";

    /// <summary>Paths that are never mounted, whatever the allowlist says.</summary>
    public static readonly IReadOnlyList<string> AlwaysDenied =
        ["/", "/var/run/docker.sock", "/run/docker.sock", "/etc", "/root", "/proc", "/sys", "/dev", "/boot", "/var/lib/docker", "/var/lib/containerd", "/var/lib/aethera/agent", "/var/lib/aethera/bin", "/var/lib/aethera/pki"];

    public string BindSource(string source)
    {
        var path = SshValidators.AbsolutePath(source, "A bind mount source").TrimEnd('/');
        if (path.Length == 0) path = "/";
        foreach (var denied in AlwaysDenied)
        {
            if (path == denied || (denied != "/" && path.StartsWith(denied + "/", StringComparison.Ordinal)))
                throw new ServerTransportException(TransportErrors.CommandRejected, "A bind mount source is not allowed.");
        }

        if (!AllowedBindPrefixes.Any(prefix => path == prefix || path.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal)))
            throw new ServerTransportException(TransportErrors.CommandRejected, "A bind mount source is outside the directories this server allows.");
        return path;
    }
}

/// <summary>A container create request turned into the Docker CLI calls that implement it.</summary>
public sealed record ContainerCreatePlan(RemoteCommand Create, IReadOnlyList<RemoteCommand> ConnectNetworks, RemoteCommand? Start, IReadOnlyList<string> Warnings);

/// <summary>
/// The allowlisted Docker CLI mapping (ADR 0002 "SshTransport"): every typed command becomes a fixed argv template whose variable parts
/// pass <see cref="SshValidators"/> and <see cref="ShellQuote"/>. There is no generic "run this string" entry point.
/// </summary>
public static class DockerCommands
{
    private static CultureInfo Inv => CultureInfo.InvariantCulture;

    // ---- containers ----------------------------------------------------------------------------------------------------------------

    public static RemoteCommand ContainerStart(string container) =>
        new Cmd("docker").Lit("container").Lit("start").Lit("--").Arg(SshValidators.Name(container, "The container")).ToRemote();

    public static RemoteCommand ContainerStop(string container, TimeSpan? timeout) =>
        new Cmd("docker").Lit("container").Lit("stop").OptIf(timeout is not null, "--time", () => SshValidators.Seconds(timeout!.Value, "The stop timeout")).Lit("--")
            .Arg(SshValidators.Name(container, "The container")).ToRemote();

    public static RemoteCommand ContainerRestart(string container, TimeSpan? timeout) =>
        new Cmd("docker").Lit("container").Lit("restart").OptIf(timeout is not null, "--time", () => SshValidators.Seconds(timeout!.Value, "The restart timeout")).Lit("--")
            .Arg(SshValidators.Name(container, "The container")).ToRemote();

    public static RemoteCommand ContainerRemove(string container, bool force, bool removeVolumes) =>
        new Cmd("docker").Lit("container").Lit("rm").If(force, "--force").If(removeVolumes, "--volumes").Lit("--")
            .Arg(SshValidators.Name(container, "The container")).ToRemote();

    /// <summary><c>docker container inspect</c> of one or more containers: a JSON array on standard output.</summary>
    public static RemoteCommand ContainerInspect(IEnumerable<string> containers)
    {
        var cmd = new Cmd("docker").Lit("container").Lit("inspect").Lit("--");
        foreach (var container in containers) cmd.Arg(SshValidators.Name(container, "The container"));
        return cmd.ToRemote();
    }

    /// <summary>Full ids of the containers matching the filters, one per line.</summary>
    public static RemoteCommand ContainerIds(bool all, IReadOnlyList<string>? labelFilters, string? nameFilter)
    {
        var cmd = new Cmd("docker").Lit("container").Lit("ls").Lit("--quiet").Lit("--no-trunc").If(all, "--all");
        foreach (var label in labelFilters ?? []) cmd.Lit("--filter").Arg("label=" + SshValidators.LabelFilter(label));
        if (nameFilter is { Length: > 0 }) cmd.Lit("--filter").Arg("name=" + SshValidators.Name(nameFilter, "The name filter"));
        return cmd.ToRemote();
    }

    /// <summary>Plans <c>docker create</c> (+ network connects + start) for a full container spec.</summary>
    public static ContainerCreatePlan ContainerCreate(ContainerSpec spec, bool start, bool pullIfMissing, RegistryCredentials? pullAuth, SshDockerPolicy policy)
    {
        var warnings = new List<string>();
        var name = SshValidators.Name(spec.Name, "The container name");
        var image = SshValidators.NotOptionImage(spec.Image);
        var cmd = new Cmd("docker").Lit("container").Lit("create").Opt("--name", name).Opt("--pull", pullIfMissing ? "missing" : "never");

        if (spec.Hostname is not null) cmd.Opt("--hostname", SshValidators.Hostname(spec.Hostname));
        if (spec.WorkingDir is not null) cmd.Opt("--workdir", SshValidators.AbsolutePath(spec.WorkingDir, "The working directory"));
        if (spec.User is not null) cmd.Opt("--user", SshValidators.User(spec.User));
        if (spec.StopSignal is not null) cmd.Opt("--stop-signal", SshValidators.Signal(spec.StopSignal));
        if (spec.StopTimeout is { } stopTimeout) cmd.Opt("--stop-timeout", SshValidators.Seconds(stopTimeout, "The stop timeout"));
        cmd.If(spec.ReadOnlyRootFs, "--read-only").If(spec.Init, "--init");
        foreach (var cap in spec.CapDrop ?? []) cmd.Opt("--cap-drop", SshValidators.Capability(cap));
        foreach (var host in spec.ExtraHosts ?? []) cmd.Opt("--add-host", SshValidators.ExtraHost(host));

        foreach (var (key, value) in (spec.Labels ?? new Dictionary<string, string>()).OrderBy(l => l.Key, StringComparer.Ordinal))
            cmd.Opt("--label", $"{SshValidators.LabelKey(key)}={SshValidators.LabelValue(key, value)}");

        foreach (var port in spec.Ports ?? []) cmd.Opt("--publish", PortSpec(port));

        foreach (var mount in spec.Mounts ?? []) cmd.Opt("--mount", MountSpec(mount, policy));

        var networks = spec.Networks ?? [];
        var primary = networks.Count > 0 ? networks[0] : null;
        if (primary is not null)
        {
            cmd.Opt("--network", SshValidators.Name(primary.Network, "The network"));
            foreach (var alias in primary.Aliases ?? []) cmd.Opt("--network-alias", SshValidators.Name(alias, "A network alias"));
            if (primary.Ipv4Address is { } ip) cmd.Opt("--ip", Ipv4(ip));
        }

        ApplyResources(cmd, spec.Resources, warnings);
        if (spec.RestartPolicy is { } restart) cmd.Opt("--restart", RestartPolicy(restart));
        if (spec.Healthcheck is { } health) ApplyHealthcheck(cmd, health);
        if (spec.LogConfig is { } log)
        {
            cmd.Opt("--log-driver", SshValidators.LogDriver(log.Driver));
            foreach (var (key, value) in (log.Options ?? new Dictionary<string, string>()).OrderBy(o => o.Key, StringComparer.Ordinal))
                cmd.Opt("--log-opt", SshValidators.KeyValueOption(key, value, "A log option"));
        }

        // Environment: always through an env-file in a private temp dir (values can be secret and argv is world-readable in `ps`).
        var envLines = new StringBuilder();
        var secrets = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var env in spec.Env ?? [])
        {
            var envName = SshValidators.EnvName(env.Name);
            if (!seen.Add(envName)) throw new ServerTransportException(TransportErrors.CommandRejected, $"The environment variable {envName} is set twice.");
            var value = SshValidators.EnvValue(envName, env.Secret?.Value ?? env.Plain);
            envLines.Append(envName).Append('=').Append(value).Append('\n');
            if (env.Secret is not null) secrets.Add(value);
        }

        var script = new SecretScript();
        if (pullAuth is not null) script.Login(pullAuth);
        if (envLines.Length > 0)
        {
            script.WithFile(envLines.ToString(), secrets);
            cmd.Lit("--env-file").Lit(SecretScript.FileWord);
        }

        // Entrypoint: Docker takes one word; the rest of a longer list becomes leading arguments.
        var commandArgs = new List<string>(spec.Command ?? []);
        if (spec.Entrypoint is { } entrypoint)
        {
            cmd.Opt("--entrypoint", entrypoint.Count > 0 ? NoNul(entrypoint[0]) : "");
            if (entrypoint.Count > 1) commandArgs.InsertRange(0, entrypoint.Skip(1));
        }

        cmd.Lit("--").Arg(image);
        foreach (var arg in commandArgs) cmd.Arg(NoNul(arg));

        var connects = new List<RemoteCommand>();
        foreach (var extra in networks.Skip(1))
        {
            var connect = new Cmd("docker").Lit("network").Lit("connect");
            foreach (var alias in extra.Aliases ?? []) connect.Opt("--alias", SshValidators.Name(alias, "A network alias"));
            if (extra.Ipv4Address is { } ip) connect.Opt("--ip", Ipv4(ip));
            connects.Add(connect.Lit("--").Arg(SshValidators.Name(extra.Network, "The network")).Arg(name).ToRemote());
        }

        return new ContainerCreatePlan(script.Build(cmd.ToString()), connects, start ? ContainerStart(name) : null, warnings);
    }

    private static string NoNul(string value) => value.Contains('\0') ? throw new ServerTransportException(TransportErrors.CommandRejected, "A value contains a NUL character.") : value;

    private static string Ipv4(string value) =>
        System.Net.IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? ip.ToString()
            : throw new ServerTransportException(TransportErrors.CommandRejected, "An IPv4 address is not valid.");

    private static string PortSpec(PortMappingSpec port)
    {
        var container = SshValidators.Port(port.ContainerPort, "The container port");
        var protocol = port.Protocol switch { PortProtocolKind.Udp => "udp", PortProtocolKind.Sctp => "sctp", _ => "tcp" };
        var host = port.HostPort == 0 ? "" : SshValidators.Port(port.HostPort, "The host port").ToString(Inv) + ":";
        var ip = port.HostIp is { Length: > 0 } hostIp ? SshValidators.HostIp(hostIp) + ":" : "";
        if (ip.Length > 0 && host.Length == 0) host = ":"; // ip::container (random host port)
        return $"{ip}{host}{container}/{protocol}";
    }

    private static string MountSpec(VolumeMountSpec mount, SshDockerPolicy policy)
    {
        var target = SshValidators.AbsolutePath(mount.Target, "A mount target");
        var readOnly = mount.ReadOnly ? ",readonly" : "";
        return mount.Type switch
        {
            MountKind.Volume => $"type=volume,source={SshValidators.Name(mount.Source, "A volume name")},target={target}{readOnly}",
            MountKind.Bind => $"type=bind,source={policy.BindSource(mount.Source)},target={target}{readOnly}",
            MountKind.Tmpfs => $"type=tmpfs,target={target}" + (mount.TmpfsSizeBytes > 0 ? $",tmpfs-size={mount.TmpfsSizeBytes.ToString(Inv)}" : ""),
            _ => throw new ServerTransportException(TransportErrors.CommandRejected, "The mount type is not supported."),
        };
    }

    private static void ApplyResources(Cmd cmd, ResourceLimitsSpec? r, List<string> warnings)
    {
        if (r is null) return;
        if (r.CpuLimitCores > 0) cmd.Opt("--cpus", Math.Round(r.CpuLimitCores, 3).ToString("0.###", Inv));
        if (r.MemoryLimitBytes > 0) cmd.Opt("--memory", r.MemoryLimitBytes.ToString(Inv));
        if (r.MemoryReservationBytes > 0) cmd.Opt("--memory-reservation", r.MemoryReservationBytes.ToString(Inv));
        if (r.MemorySwapLimitBytes != 0) cmd.Opt("--memory-swap", r.MemorySwapLimitBytes.ToString(Inv));
        if (r.PidsLimit != 0) cmd.Opt("--pids-limit", r.PidsLimit.ToString(Inv));
        if (r.CpusetCpus is { Length: > 0 } cpuset) cmd.Opt("--cpuset-cpus", SshValidators.Cpuset(cpuset));
        if (r.CpuReservationCores > 0) warnings.Add("A CPU reservation is not supported by the Docker CLI and was ignored.");
    }

    private static string RestartPolicy(RestartPolicySpec policy) => policy.Name switch
    {
        RestartPolicyKind.No => "no",
        RestartPolicyKind.Always => "always",
        RestartPolicyKind.UnlessStopped => "unless-stopped",
        RestartPolicyKind.OnFailure => policy.MaximumRetryCount > 0 ? $"on-failure:{policy.MaximumRetryCount.ToString(Inv)}" : "on-failure",
        _ => throw new ServerTransportException(TransportErrors.CommandRejected, "The restart policy is not valid."),
    };

    private static void ApplyHealthcheck(Cmd cmd, HealthcheckConfig health)
    {
        var test = health.Test;
        if (test.Count == 0) return;
        switch (test[0])
        {
            case "NONE":
                cmd.Lit("--no-healthcheck");
                return;
            case "CMD-SHELL" when test.Count == 2:
                cmd.Opt("--health-cmd", NoNul(test[1]));
                break;
            case "CMD" when test.Count >= 2:
                cmd.Opt("--health-cmd", ShellQuote.Join(test.Skip(1).Select(NoNul))); // runs through the container's shell: quoted word by word
                break;
            default:
                throw new ServerTransportException(TransportErrors.CommandRejected, "The health check test must start with CMD, CMD-SHELL or NONE.");
        }

        if (health.Interval is { } interval) cmd.Opt("--health-interval", Millis(interval));
        if (health.Timeout is { } timeout) cmd.Opt("--health-timeout", Millis(timeout));
        if (health.Retries > 0) cmd.Opt("--health-retries", health.Retries.ToString(Inv));
        if (health.StartPeriod is { } period) cmd.Opt("--health-start-period", Millis(period));
        if (health.StartInterval is { } startInterval) cmd.Opt("--health-start-interval", Millis(startInterval));
    }

    private static string Millis(TimeSpan value) => ((long)Math.Max(0, value.TotalMilliseconds)).ToString(Inv) + "ms";

    // ---- images --------------------------------------------------------------------------------------------------------------------

    public static RemoteCommand ImagePull(string reference, RegistryCredentials? auth, string? platform)
    {
        var cmd = new Cmd("docker").Lit("image").Lit("pull");
        if (platform is not null) cmd.Opt("--platform", SshValidators.Platform(platform));
        cmd.Lit("--").Arg(SshValidators.NotOptionImage(reference));
        var script = new SecretScript();
        if (auth is not null) script.Login(auth);
        return script.Build(cmd.ToString());
    }

    public static RemoteCommand ImageInspect(IEnumerable<string> images)
    {
        var cmd = new Cmd("docker").Lit("image").Lit("inspect").Lit("--");
        foreach (var image in images) cmd.Arg(SshValidators.NotOptionImage(image));
        return cmd.ToRemote();
    }

    public static RemoteCommand ImageIds(bool includeIntermediate, IReadOnlyList<string>? labelFilters, string? referenceFilter)
    {
        var cmd = new Cmd("docker").Lit("image").Lit("ls").Lit("--quiet").Lit("--no-trunc").If(includeIntermediate, "--all");
        foreach (var label in labelFilters ?? []) cmd.Lit("--filter").Arg("label=" + SshValidators.LabelFilter(label));
        if (referenceFilter is { Length: > 0 }) cmd.Lit("--filter").Arg("reference=" + SshValidators.ImageRef(referenceFilter, "The reference filter"));
        return cmd.ToRemote();
    }

    public static RemoteCommand ImageRemove(string image, bool force) =>
        new Cmd("docker").Lit("image").Lit("rm").If(force, "--force").Lit("--").Arg(SshValidators.NotOptionImage(image)).ToRemote();

    public static RemoteCommand ImagePrune(bool all, TimeSpan? olderThan, IReadOnlyList<string>? labelFilters)
    {
        var cmd = new Cmd("docker").Lit("image").Lit("prune").Lit("--force").If(all, "--all");
        AddPruneFilters(cmd, olderThan, labelFilters);
        return cmd.ToRemote();
    }

    /// <summary><c>docker image ls --all</c> with id and tag, tab separated, to pick images to remove while keeping some.</summary>
    public static RemoteCommand ImageTable() =>
        new Cmd("docker").Lit("image").Lit("ls").Lit("--all").Lit("--no-trunc").Lit("--format").Lit("'{{.ID}}\t{{.Repository}}:{{.Tag}}'").ToRemote();

    /// <summary>Image ids of every container (running or not), one per line.</summary>
    public static RemoteCommand ContainerImageIds() =>
        new Cmd("docker").Lit("container").Lit("ls").Lit("--all").Lit("--no-trunc").Lit("--format").Lit("'{{.ImageID}}'").ToRemote();

    // ---- volumes, networks ---------------------------------------------------------------------------------------------------------

    public static RemoteCommand VolumeCreate(string name, string? driver, IReadOnlyDictionary<string, string>? options, IReadOnlyDictionary<string, string>? labels)
    {
        var cmd = new Cmd("docker").Lit("volume").Lit("create");
        if (driver is not null) cmd.Opt("--driver", SshValidators.VolumeDriver(driver));
        foreach (var (key, value) in (options ?? new Dictionary<string, string>()).OrderBy(o => o.Key, StringComparer.Ordinal))
            cmd.Opt("--opt", SshValidators.KeyValueOption(key, value, "A volume option"));
        foreach (var (key, value) in (labels ?? new Dictionary<string, string>()).OrderBy(o => o.Key, StringComparer.Ordinal))
            cmd.Opt("--label", $"{SshValidators.LabelKey(key)}={SshValidators.LabelValue(key, value)}");
        return cmd.Lit("--").Arg(SshValidators.Name(name, "The volume name")).ToRemote();
    }

    public static RemoteCommand VolumeNames(IReadOnlyList<string>? labelFilters, bool danglingOnly)
    {
        var cmd = new Cmd("docker").Lit("volume").Lit("ls").Lit("--quiet");
        foreach (var label in labelFilters ?? []) cmd.Lit("--filter").Arg("label=" + SshValidators.LabelFilter(label));
        if (danglingOnly) cmd.Lit("--filter").Lit("dangling=true");
        return cmd.ToRemote();
    }

    public static RemoteCommand VolumeInspect(IEnumerable<string> names)
    {
        var cmd = new Cmd("docker").Lit("volume").Lit("inspect").Lit("--");
        foreach (var name in names) cmd.Arg(SshValidators.Name(name, "The volume name"));
        return cmd.ToRemote();
    }

    public static RemoteCommand VolumeRemove(string name, bool force) =>
        new Cmd("docker").Lit("volume").Lit("rm").If(force, "--force").Lit("--").Arg(SshValidators.Name(name, "The volume name")).ToRemote();

    public static RemoteCommand NetworkCreate(NetworkCreateCommand c)
    {
        var cmd = new Cmd("docker").Lit("network").Lit("create");
        if (c.Driver is not null) cmd.Opt("--driver", SshValidators.NetworkDriver(c.Driver));
        cmd.If(c.Internal, "--internal").If(c.Attachable, "--attachable").If(c.Ipv6, "--ipv6");
        foreach (var ipam in c.Ipam ?? [])
        {
            cmd.Opt("--subnet", Cidr(ipam.Subnet));
            if (!string.IsNullOrEmpty(ipam.Gateway)) cmd.Opt("--gateway", IpAny(ipam.Gateway));
        }

        foreach (var (key, value) in (c.Options ?? new Dictionary<string, string>()).OrderBy(o => o.Key, StringComparer.Ordinal))
            cmd.Opt("--opt", SshValidators.KeyValueOption(key, value, "A network option"));
        foreach (var (key, value) in (c.Labels ?? new Dictionary<string, string>()).OrderBy(o => o.Key, StringComparer.Ordinal))
            cmd.Opt("--label", $"{SshValidators.LabelKey(key)}={SshValidators.LabelValue(key, value)}");
        return cmd.Lit("--").Arg(SshValidators.Name(c.NetworkName, "The network name")).ToRemote();
    }

    private static string IpAny(string value) =>
        System.Net.IPAddress.TryParse(value, out var ip) ? ip.ToString() : throw new ServerTransportException(TransportErrors.CommandRejected, "An IP address is not valid.");

    private static string Cidr(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, Inv, out var bits) || bits is < 0 or > 128)
            throw new ServerTransportException(TransportErrors.CommandRejected, "A subnet must look like 10.0.0.0/24.");
        return IpAny(parts[0]) + "/" + bits.ToString(Inv);
    }

    public static RemoteCommand NetworkIds(IReadOnlyList<string>? labelFilters)
    {
        var cmd = new Cmd("docker").Lit("network").Lit("ls").Lit("--quiet").Lit("--no-trunc");
        foreach (var label in labelFilters ?? []) cmd.Lit("--filter").Arg("label=" + SshValidators.LabelFilter(label));
        return cmd.ToRemote();
    }

    public static RemoteCommand NetworkInspect(IEnumerable<string> names)
    {
        var cmd = new Cmd("docker").Lit("network").Lit("inspect").Lit("--");
        foreach (var name in names) cmd.Arg(SshValidators.Name(name, "The network"));
        return cmd.ToRemote();
    }

    public static RemoteCommand NetworkRemove(string name) =>
        new Cmd("docker").Lit("network").Lit("rm").Lit("--").Arg(SshValidators.Name(name, "The network")).ToRemote();

    public static RemoteCommand NetworkConnect(string network, string container, IReadOnlyList<string>? aliases, string? ipv4)
    {
        var cmd = new Cmd("docker").Lit("network").Lit("connect");
        foreach (var alias in aliases ?? []) cmd.Opt("--alias", SshValidators.Name(alias, "A network alias"));
        if (ipv4 is not null) cmd.Opt("--ip", Ipv4(ipv4));
        return cmd.Lit("--").Arg(SshValidators.Name(network, "The network")).Arg(SshValidators.Name(container, "The container")).ToRemote();
    }

    public static RemoteCommand NetworkDisconnect(string network, string container, bool force) =>
        new Cmd("docker").Lit("network").Lit("disconnect").If(force, "--force").Lit("--")
            .Arg(SshValidators.Name(network, "The network")).Arg(SshValidators.Name(container, "The container")).ToRemote();

    public static RemoteCommand NetworkPrune(TimeSpan? olderThan, IReadOnlyList<string>? labelFilters)
    {
        var cmd = new Cmd("docker").Lit("network").Lit("prune").Lit("--force");
        AddPruneFilters(cmd, olderThan, labelFilters);
        return cmd.ToRemote();
    }

    public static RemoteCommand ContainerPrune(TimeSpan? olderThan)
    {
        var cmd = new Cmd("docker").Lit("container").Lit("prune").Lit("--force");
        AddPruneFilters(cmd, olderThan, null);
        return cmd.ToRemote();
    }

    public static RemoteCommand BuilderPrune(TimeSpan? olderThan)
    {
        var cmd = new Cmd("docker").Lit("builder").Lit("prune").Lit("--force");
        if (olderThan is { } age) cmd.Lit("--filter").Arg("until=" + SshValidators.Seconds(age, "The age") + "s");
        return cmd.ToRemote();
    }

    private static void AddPruneFilters(Cmd cmd, TimeSpan? olderThan, IReadOnlyList<string>? labelFilters)
    {
        if (olderThan is { } age && age > TimeSpan.Zero) cmd.Lit("--filter").Arg("until=" + SshValidators.Seconds(age, "The age") + "s");
        foreach (var label in labelFilters ?? []) cmd.Lit("--filter").Arg("label=" + SshValidators.LabelFilter(label));
    }

    // ---- compose -------------------------------------------------------------------------------------------------------------------

    /// <summary>Directory of a project on the server (<c>/var/lib/aethera/projects/&lt;name&gt;</c>).</summary>
    public static string ProjectDirectory(SshDockerPolicy policy, string project) => policy.ProjectsDirectory.TrimEnd('/') + "/" + SshValidators.ComposeProject(project);

    public static RemoteCommand MakeProjectDirectory(string directory) =>
        new Cmd("mkdir").Lit("-p").Lit("-m").Lit("0700").Lit("--").Arg(directory).ToRemote();

    public static RemoteCommand RemoveProjectDirectory(string directory) =>
        new Cmd("rm").Lit("-rf").Lit("--").Arg(directory).ToRemote();

    private static Cmd Compose(SshDockerPolicy policy, ComposeProjectSpec project, bool withFiles)
    {
        var name = SshValidators.ComposeProject(project.ProjectName);
        var dir = ProjectDirectory(policy, name);
        var cmd = new Cmd("cd").Arg(dir).Lit("&&").Lit("docker").Lit("compose").Opt("--project-name", name);
        if (withFiles)
        {
            cmd.Opt("--file", "compose.yaml");
            if (project.OverrideFile is { Length: > 0 }) cmd.Opt("--file", "compose.override.yaml");
        }

        foreach (var profile in project.Profiles ?? []) cmd.Opt("--profile", SshValidators.Name(profile, "A Compose profile"));
        return cmd;
    }

    public static RemoteCommand ComposeUp(SshDockerPolicy policy, ComposeUpCommand c)
    {
        var cmd = Compose(policy, c.Project, withFiles: true).Lit("up").Lit("--detach").Opt("--pull", c.PullPolicy switch
        {
            ComposePullPolicyKind.Always => "always",
            ComposePullPolicyKind.Never => "never",
            _ => "missing",
        });
        cmd.If(c.Build, "--build").If(c.ForceRecreate, "--force-recreate").If(c.RemoveOrphans, "--remove-orphans");
        if (c.Wait) cmd.Lit("--wait").Opt("--wait-timeout", SshValidators.Seconds(c.WaitTimeout ?? TimeSpan.FromMinutes(5), "The wait timeout"));
        if (c.Services is { Count: > 0 } services)
        {
            cmd.Lit("--");
            foreach (var service in services) cmd.Arg(SshValidators.ServiceName(service));
        }

        return WithRegistryLogins(c.Project, cmd);
    }

    public static RemoteCommand ComposeDown(SshDockerPolicy policy, ComposeDownCommand c)
    {
        var cmd = Compose(policy, c.Project, withFiles: false).Lit("down").If(c.RemoveVolumes, "--volumes").If(c.RemoveOrphans, "--remove-orphans");
        if (c.Timeout is { } timeout) cmd.Opt("--timeout", SshValidators.Seconds(timeout, "The timeout"));
        return cmd.ToRemote();
    }

    public static RemoteCommand ComposePs(SshDockerPolicy policy, ComposePsCommand c) =>
        Compose(policy, c.Project, withFiles: false).Lit("ps").If(c.All, "--all").Opt("--format", "json").ToRemote();

    public static RemoteCommand ComposePull(SshDockerPolicy policy, ComposePullCommand c)
    {
        var cmd = Compose(policy, c.Project, withFiles: true).Lit("pull");
        if (c.Services is { Count: > 0 } services)
        {
            cmd.Lit("--");
            foreach (var service in services) cmd.Arg(SshValidators.ServiceName(service));
        }

        return WithRegistryLogins(c.Project, cmd);
    }

    private static RemoteCommand WithRegistryLogins(ComposeProjectSpec project, Cmd cmd)
    {
        var script = new SecretScript();
        foreach (var auth in project.RegistryAuths ?? []) script.Login(auth);
        return script.Build(cmd.ToString());
    }

    // ---- builds --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>docker build</c> of a public git URL (the only build the SSH transport runs besides image pulls). Secrets, credentials, pushes,
    /// caches and multi-platform builds need the agent.
    /// </summary>
    public static RemoteCommand BuildFromGit(BuildSpec spec)
    {
        var git = spec.Git ?? throw new ServerTransportException(TransportErrors.Unsupported, Hint);
        if (spec.Engine != BuildEngineKind.Dockerfile || git.Credentials is not null || git.Submodules || spec.Push || spec.PushAuth is not null
            || spec.Secrets is { Count: > 0 } || spec.Cache is { Enabled: true } || spec.TargetPlatforms is { Count: > 1 } || spec.ImagePullAuth is not null)
            throw new ServerTransportException(TransportErrors.Unsupported, Hint);

        var url = SshValidators.PublicGitUrl(git.Url);
        var gitRef = git.Commit is { Length: > 0 } ? SshValidators.GitRef(git.Commit) : git.Ref is { Length: > 0 } ? SshValidators.GitRef(git.Ref) : "";
        var contextDir = spec.ContextPath is { Length: > 0 } and not "." ? ":" + SshValidators.RelativePath(spec.ContextPath, "The build context") : "";
        var context = url + (gitRef.Length > 0 || contextDir.Length > 0 ? "#" + gitRef + contextDir : "");

        var cmd = new Cmd("docker").Lit("build").Lit("--pull");
        foreach (var tag in spec.ImageTags) cmd.Opt("--tag", SshValidators.ImageTag(tag));
        if (spec.DockerfilePath is { Length: > 0 } dockerfile) cmd.Opt("--file", SshValidators.RelativePath(dockerfile, "The Dockerfile path"));
        if (spec.TargetStage is { Length: > 0 } stage) cmd.Opt("--target", SshValidators.Name(stage, "The build target"));
        if (spec.Cache?.NoCache == true) cmd.Lit("--no-cache");
        if (spec.TargetPlatforms is { Count: 1 } platforms) cmd.Opt("--platform", SshValidators.Platform(platforms[0]));
        foreach (var (key, value) in (spec.ImageLabels ?? new Dictionary<string, string>()).OrderBy(l => l.Key, StringComparer.Ordinal))
            cmd.Opt("--label", $"{SshValidators.LabelKey(key)}={SshValidators.LabelValue(key, value)}");

        // Build args: plain ones are visible metadata; a secret value is taken from the environment (`--build-arg NAME`) of a shell that
        // sourced a 0600 temp file, never from argv.
        var secretLines = new StringBuilder();
        var secretValues = new List<string>();
        foreach (var arg in spec.BuildArgs ?? [])
        {
            var name = SshValidators.EnvName(arg.Name);
            if (arg.Secret is { } secret)
            {
                SshValidators.EnvValue(name, secret.Value);
                secretLines.Append(name).Append('=').Append(ShellQuote.Quote(secret.Value)).Append('\n');
                secretValues.Add(secret.Value);
                cmd.Opt("--build-arg", name);
            }
            else
            {
                cmd.Opt("--build-arg", $"{name}={SshValidators.EnvValue(name, arg.Plain)}");
            }
        }

        cmd.Lit("--").Arg(context);
        if (secretLines.Length == 0) return cmd.ToRemote();
        // `set -a; . file` exports the quoted assignments; the file is private and removed on exit.
        var script = new SecretScript().WithFile(secretLines.ToString(), secretValues);
        return script.Build("set -a\n. " + SecretScript.FileWord + "\nset +a\n" + cmd);
    }

    public const string Hint = "Enable the agent on this server or use a build server.";

    // ---- logs, probes --------------------------------------------------------------------------------------------------------------

    public static RemoteCommand Logs(LogStreamRequest request)
    {
        var cmd = new Cmd("docker").Lit("container").Lit("logs").Lit("--timestamps");
        if (request.Since is { } since) cmd.Opt("--since", since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", Inv));
        if (request.Tail > 0) cmd.Opt("--tail", request.Tail.ToString(Inv));
        cmd.If(request.Follow, "--follow");
        return cmd.Lit("--").Arg(SshValidators.Name(request.Container, "The container")).ToRemote();
    }

    public static RemoteCommand ContainerHealth(string container) =>
        new Cmd("docker").Lit("container").Lit("inspect").Lit("--format").Lit("'{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}'").Lit("--")
            .Arg(SshValidators.Name(container, "The container")).ToRemote();

    // ---- discovery and polling (read only) -----------------------------------------------------------------------------------------

    public static RemoteCommand DockerVersionJson() =>
        new Cmd("docker").Lit("version").Lit("--format").Lit("'{{json .}}'").ToRemote();

    public static RemoteCommand DockerInfoJson() =>
        new Cmd("docker").Lit("info").Lit("--format").Lit("'{{json .}}'").ToRemote();
}
