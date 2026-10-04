using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Docker;
using Aethera.Infrastructure.Ssh.Metrics;

namespace Aethera.Api.Tests.Ssh;

/// <summary>Parsers over fixture output of the read-only commands (captured from real hosts; no SSH involved).</summary>
public sealed class ParserTests
{
    // ---- /proc and df ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cpu_utilisation_is_busy_over_total_between_two_samples()
    {
        // 110 ticks passed; 25 of them idle (idle + iowait) -> 85 of 110 busy. guest columns (9, 10) are ignored: they are inside user.
        const string first = "cpu  1000 100 500 8000 400 0 50 0 300 0";
        const string second = "cpu  1050 110 520 8020 405 0 55 0 350 0";
        Assert.Equal(85.0 / 110 * 100, SshMetricsParsers.CpuPercent(first, second), 6);
        Assert.Equal(0, SshMetricsParsers.CpuPercent(first, first)); // no time passed
        Assert.Equal(0, SshMetricsParsers.CpuPercent("garbage", second));
    }

    [Fact]
    public void Meminfo_gives_total_available_and_swap()
    {
        var (total, available, swapTotal, swapFree) = SshMetricsParsers.ParseMemInfo(
        [
            "MemTotal:        2035516 kB", "MemFree:          200000 kB", "MemAvailable:    1200000 kB", "Buffers:           50000 kB", "Cached:           900000 kB",
            "SwapTotal:       1048572 kB", "SwapFree:         1000000 kB",
        ]);
        Assert.Equal(2035516L * 1024, total);
        Assert.Equal(1200000L * 1024, available);
        Assert.Equal(1048572L * 1024, swapTotal);
        Assert.Equal(1000000L * 1024, swapFree);
    }

    [Fact]
    public void Meminfo_of_an_old_kernel_without_MemAvailable_falls_back_to_free_buffers_cache()
    {
        var (total, available, _, _) = SshMetricsParsers.ParseMemInfo(["MemTotal: 1000 kB", "MemFree: 100 kB", "Buffers: 50 kB", "Cached: 150 kB"]);
        Assert.Equal(1000L * 1024, total);
        Assert.Equal(300L * 1024, available);
    }

    [Fact]
    public void Loadavg_and_net_dev_are_summed_without_loopback_and_container_bridges()
    {
        Assert.Equal((0.52, 0.30, 0.11), SshMetricsParsers.ParseLoadAvg("0.52 0.30 0.11 1/234 5678"));
        Assert.Equal((0.0, 0.0, 0.0), SshMetricsParsers.ParseLoadAvg(null));

        var (rx, tx) = SshMetricsParsers.ParseNetDev(
        [
            "Inter-|   Receive                                                |  Transmit",
            " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed",
            "    lo: 1000 10 0 0 0 0 0 0 1000 10 0 0 0 0 0 0",
            "  eth0: 5000000 4000 0 0 0 0 0 0 2500000 3000 0 0 0 0 0 0",
            "docker0: 99 1 0 0 0 0 0 0 77 1 0 0 0 0 0 0",
            "veth1a2b: 55 1 0 0 0 0 0 0 66 1 0 0 0 0 0 0",
            "  ens3:100 1 0 0 0 0 0 0 200 1 0 0 0 0 0 0",
        ]);
        Assert.Equal(5000100UL, rx);
        Assert.Equal(2500200UL, tx);
    }

    [Fact]
    public void Df_keeps_real_devices_and_network_file_systems_only()
    {
        var disks = SshMetricsParsers.ParseDf(
        [
            "Filesystem     1024-blocks     Used Available Capacity Mounted on",
            "/dev/vda1         20134592  8123456  11999999      41% /",
            "tmpfs               203552      800    202752       1% /run",
            "udev                982000        0    982000       0% /dev",
            "/dev/vdb1        103081248 12345678  85500000      13% /mnt/data disk",
            "nas.local:/export 500000000 1000000 499000000      1% /mnt/nas",
            "/dev/vda1         20134592  8123456  11999999      41% /var/lib/docker/overlay2/x/merged",
            "overlay           20134592  8123456  11999999      41% /var/lib/docker/overlay2",
        ]);
        Assert.Equal(["/", "/mnt/data disk", "/mnt/nas"], disks.Select(d => d.MountPoint));
        Assert.Equal(20134592L * 1024, disks[0].TotalBytes);
        Assert.Equal(8123456L * 1024, disks[0].UsedBytes);
    }

    // ---- docker ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("1.5kB", 1500L)]
    [InlineData("3MB", 3_000_000L)]
    [InlineData("20MiB", 20L * 1024 * 1024)]
    [InlineData("1.944GiB", 2087354105L)]
    [InlineData("2GB", 2_000_000_000L)]
    [InlineData("--", 0L)]
    [InlineData("", 0L)]
    [InlineData("12", 12L)]
    public void Docker_sizes_distinguish_decimal_and_binary_units(string text, long expected) => Assert.InRange(SshMetricsParsers.ParseSize(text), expected - 2, expected + 2);

    [Fact]
    public void Docker_stats_and_ps_become_container_metrics_with_labels()
    {
        var id = new string('a', 64);
        var other = new string('b', 64);
        var stats = new[]
        {
            $$"""{"BlockIO":"1MB / 2MB","CPUPerc":"12.50%","Container":"{{id[..12]}}","ID":"{{id}}","MemPerc":"1.00%","MemUsage":"20MiB / 1.944GiB","Name":"web","NetIO":"1.2kB / 3.4kB","PIDs":"7"}""",
            $$"""{"CPUPerc":"--","ID":"{{other}}","MemUsage":"0B / 0B","NetIO":"0B / 0B","BlockIO":"0B / 0B","Name":"idle","PIDs":"0"}""",
            "not json",
        };
        var ps = new[]
        {
            $$"""{"ID":"{{id}}","Names":"web","State":"running","Labels":"aethera.application.id=11111111-1111-1111-1111-111111111111,other=x"}""",
        };

        var containers = SshMetricsParsers.ParseStats(stats, SshMetricsParsers.ParsePs(ps));
        Assert.Equal(2, containers.Count);
        var web = containers[0];
        Assert.Equal(id, web.Id);
        Assert.Equal(12.5, web.CpuPercent);
        Assert.Equal(20L * 1024 * 1024, web.MemoryUsedBytes);
        Assert.Equal(1200UL, web.NetRxBytes);
        Assert.Equal(3400UL, web.NetTxBytes);
        Assert.Equal(7u, web.Pids);
        Assert.Equal("11111111-1111-1111-1111-111111111111", web.Labels["aethera.application.id"]);
        Assert.Equal(0, containers[1].CpuPercent);
        Assert.Empty(containers[1].Labels);
    }

    [Fact]
    public void The_whole_poll_output_parses_into_the_report_the_agent_would_send()
    {
        var id = new string('c', 64);
        var output = string.Join('\n',
            "##cpu", "cpu  1000 0 500 8000 0 0 0 0 0 0", "cpu  1050 0 520 8020 0 0 0 0 0 0",
            "##mem", "MemTotal: 2000000 kB", "MemAvailable: 500000 kB", "SwapTotal: 0 kB", "SwapFree: 0 kB",
            "##load", "1.50 1.00 0.50 2/100 123",
            "##uptime", "86400.55 170000.10",
            "##net", "  eth0: 1000 1 0 0 0 0 0 0 2000 1 0 0 0 0 0 0",
            "##df", "Filesystem 1024-blocks Used Available Capacity Mounted on", "/dev/vda1 10000 4000 6000 40% /",
            "##nproc", "4",
            "##docker", "27.3.1",
            "##stats", $$"""{"ID":"{{id}}","Name":"web","CPUPerc":"2.00%","MemUsage":"10MiB / 100MiB","NetIO":"1kB / 2kB","BlockIO":"0B / 0B","PIDs":"3"}""",
            "##ps", $$"""{"ID":"{{id}}","Names":"web","State":"running","Labels":"aethera.application.id=22222222-2222-2222-2222-222222222222"}""");

        var polled = SshMetricsParsers.Parse(output);
        Assert.Equal(DockerStatus.Running, polled.DockerStatus);
        Assert.Equal("27.3.1", polled.DockerVersion);
        Assert.Equal(4, polled.Cores);
        Assert.Equal(TimeSpan.FromSeconds(86400.55), polled.Uptime);

        var now = DateTimeOffset.UtcNow;
        var report = SshMetricsParsers.ToReport(polled, now);
        Assert.Equal(1.5, report.Host.Load1);
        Assert.Equal(2000000L * 1024, report.Host.MemoryTotalBytes);
        Assert.Equal(1500000L * 1024, report.Host.MemoryUsedBytes);
        Assert.Equal(4u, report.Host.CpuCores);
        Assert.Equal("/", Assert.Single(report.Host.Disks).MountPoint);
        Assert.Equal(1000UL, Assert.Single(report.Host.Interfaces).RxBytes);
        var container = Assert.Single(report.Containers);
        Assert.Equal(id, container.ContainerId);
        Assert.Equal(2.0, container.CpuPercent);

        // The very mapping the agent path uses accepts it: one host row plus one row per container, workload resolved from the label.
        var workload = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var rows = Aethera.Infrastructure.Agents.Ingest.AgentMetricsIngestor.Map(Guid.NewGuid(), report, now, new HashSet<Guid> { workload });
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].ContainerId);
        Assert.Equal(4000L * 1024, rows[0].DiskUsedBytes);
        Assert.Equal(workload, rows[1].WorkloadId);
    }

    [Theory]
    [InlineData("27.3.1", DockerStatus.Running)]
    [InlineData("permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock", DockerStatus.PermissionDenied)]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", DockerStatus.Stopped)]
    [InlineData("sh: 1: docker: not found", DockerStatus.NotInstalled)]
    public void Docker_state_is_classified_from_docker_info(string line, DockerStatus expected) =>
        Assert.Equal(expected, SshMetricsParsers.ParseDockerState([line]).Status);

    [Fact]
    public void A_poll_with_a_missing_docker_and_empty_sections_does_not_throw()
    {
        var polled = SshMetricsParsers.Parse("##cpu\n##mem\n##load\n##docker\nsh: 1: docker: not found\n##stats\n##ps\n");
        Assert.Equal(DockerStatus.NotInstalled, polled.DockerStatus);
        Assert.Empty(polled.Containers);
        Assert.Equal(0, polled.CpuPercent);
    }

    [Fact]
    public void The_poll_script_is_one_fixed_read_only_line()
    {
        var line = SshMetricsScript.Line;
        Assert.DoesNotContain("\n", line);
        foreach (var forbidden in new[] { " rm ", "kill", "docker run", "docker rm", "docker stop", "tee ", "chmod", "sudo" })
            Assert.DoesNotContain(forbidden, line);
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(line, @">(?!\s*/dev/null|&1)")); // the only redirections discard errors or merge them into the output
        foreach (var expected in new[] { "/proc/stat", "/proc/meminfo", "/proc/loadavg", "df -P", "docker stats --no-stream", "docker ps" })
            Assert.Contains(expected, line);
    }

    // ---- inspect JSON ----------------------------------------------------------------------------------------------------------------

    private const string ContainerJson = """
        [{
          "Id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Name": "/web", "Created": "2026-10-01T10:00:00.123456789Z", "Image": "sha256:bbbb", "RestartCount": 2,
          "State": {"Status": "running", "ExitCode": 0, "OOMKilled": false, "StartedAt": "2026-10-01T10:00:01.5Z", "FinishedAt": "0001-01-01T00:00:00Z", "Health": {"Status": "unhealthy"}},
          "Config": {"Image": "nginx:1.27", "Labels": {"a": "b"}, "Env": ["SECRET=hunter2", "PLAIN=1"]},
          "NetworkSettings": {"Ports": {"80/tcp": [{"HostIp": "0.0.0.0", "HostPort": "8080"}], "53/udp": null},
                              "Networks": {"bridge": {"IPAddress": "172.17.0.2", "MacAddress": "02:42", "Aliases": ["web", "w"]}}},
          "Mounts": [{"Type": "volume", "Name": "data", "Destination": "/data", "RW": true}, {"Type": "bind", "Source": "/srv", "Destination": "/s", "RW": false}]
        }]
        """;

    [Fact]
    public void Container_inspect_maps_state_health_ports_mounts_and_networks()
    {
        var c = DockerJson.Container(DockerJson.Array(ContainerJson).Single(), includeRaw: false);
        Assert.Equal("web", c.Name);
        Assert.Equal("nginx:1.27", c.Image);
        Assert.Equal(ContainerRunState.Running, c.State);
        Assert.Equal(ContainerHealthState.Unhealthy, c.Health);
        Assert.Equal(2, c.RestartCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 1, 500, TimeSpan.Zero), c.StartedAt);
        Assert.Null(c.FinishedAt); // Docker's zero time
        Assert.NotNull(c.CreatedAt); // nanosecond precision parses
        Assert.Equal("b", c.Labels["a"]);
        Assert.Contains(c.Ports, p => p is { ContainerPort: 80, HostPort: 8080, Protocol: PortProtocolKind.Tcp });
        Assert.Contains(c.Ports, p => p is { ContainerPort: 53, HostPort: 0, Protocol: PortProtocolKind.Udp });
        Assert.Equal(MountKind.Volume, c.Mounts[0].Type);
        Assert.Equal("data", c.Mounts[0].Source);
        Assert.True(c.Mounts[1].ReadOnly);
        Assert.Equal(["web", "w"], c.Networks.Single().Aliases);
        Assert.Null(c.InspectJson);
    }

    [Fact]
    public void Raw_inspect_output_keeps_environment_names_but_not_values()
    {
        var c = DockerJson.Container(DockerJson.Array(ContainerJson).Single(), includeRaw: true);
        Assert.NotNull(c.InspectJson);
        Assert.DoesNotContain("hunter2", c.InspectJson);
        Assert.Contains("SECRET=", c.InspectJson);
    }

    [Fact]
    public void Volume_network_image_and_compose_json_map()
    {
        var volume = DockerJson.Volume(DockerJson.Array("""[{"Name":"data","Driver":"local","Mountpoint":"/var/lib/docker/volumes/data/_data","Labels":{"x":"y"},"CreatedAt":"2026-10-01T10:00:00Z","UsageData":{"Size":-1,"RefCount":-1}}]""").Single());
        Assert.Equal(("data", "local", 0L, 0), (volume.Name, volume.Driver, volume.SizeBytes, volume.RefCount));

        var network = DockerJson.Network(DockerJson.Array("""[{"Id":"n1","Name":"front","Driver":"bridge","Scope":"local","Internal":true,"EnableIPv6":false,"IPAM":{"Config":[{"Subnet":"10.0.0.0/24","Gateway":"10.0.0.1"}]},"Containers":{"c1":{"Name":"web","IPv4Address":"10.0.0.2/24"}}}]""").Single());
        Assert.True(network.Internal);
        Assert.Equal("10.0.0.0/24", network.Ipam.Single().Subnet);
        Assert.Equal("web", network.Endpoints.Single().ContainerName);

        var image = DockerJson.Image(DockerJson.Array("""[{"Id":"sha256:abc","RepoTags":["nginx:1.27"],"RepoDigests":["nginx@sha256:def"],"Size":12345,"Architecture":"arm64","Os":"linux","Config":{"Labels":{"k":"v"}}}]""").Single());
        Assert.Equal(("sha256:abc", 12345L, "arm64"), (image.Id, image.SizeBytes, image.Architecture));
        Assert.Equal("v", image.Labels["k"]);

        // Compose before v2.21 prints one JSON array, later versions one object per line.
        const string row = """{"ID":"e1","Name":"demo-web-1","Service":"web","Image":"nginx","State":"running","Health":"healthy","ExitCode":0,"Publishers":[{"URL":"0.0.0.0","TargetPort":80,"PublishedPort":8081,"Protocol":"tcp"},{"URL":"","TargetPort":9,"PublishedPort":0,"Protocol":"tcp"}]}""";
        foreach (var output in new[] { "[" + row + "]", row + "\n" + row.Replace("web", "db") })
        {
            var services = DockerJson.Lines(output).Select(DockerJson.ComposeService).ToList();
            Assert.Equal("web", services[0].Service);
            Assert.Equal(ContainerHealthState.Healthy, services[0].Health);
            Assert.Contains(services[0].Ports, p => p is { ContainerPort: 80, HostPort: 8081 });
        }
    }

    [Fact]
    public void Prune_output_yields_the_deleted_objects_and_reclaimed_space()
    {
        var outcome = DockerErrors.ParsePrune("""
            Deleted Containers:
            abc123
            def456

            Deleted Images:
            untagged: nginx:old
            deleted: sha256:111

            Total reclaimed space: 1.5GB
            """);
        Assert.Equal(["abc123", "def456", "untagged: nginx:old", "deleted: sha256:111"], outcome.Deleted);
        Assert.Equal(1_500_000_000L, outcome.SpaceReclaimedBytes);
        Assert.Empty(DockerErrors.ParsePrune("Total reclaimed space: 0B").Deleted);
    }

    [Theory]
    [InlineData("Error response from daemon: No such container: web", 1, CommandErrorCode.Internal, CommandErrorCode.NotFound)]
    [InlineData("Error response from daemon: Conflict. The container name \"/web\" is already in use by container \"abc\".", 1, CommandErrorCode.Internal, CommandErrorCode.AlreadyExists)]
    [InlineData("Bind for 0.0.0.0:80 failed: port is already allocated", 1, CommandErrorCode.Internal, CommandErrorCode.PortConflict)]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", 1, CommandErrorCode.Internal, CommandErrorCode.DockerUnavailable)]
    [InlineData("sh: 1: docker: not found", 127, CommandErrorCode.Internal, CommandErrorCode.DockerUnavailable)]
    [InlineData("write /var/lib/docker/x: no space left on device", 1, CommandErrorCode.Internal, CommandErrorCode.OutOfDisk)]
    [InlineData("Error response from daemon: pull access denied for x, repository does not exist or may require authentication", 1, CommandErrorCode.ImagePullFailed, CommandErrorCode.RegistryAuthFailed)]
    [InlineData("Error response from daemon: manifest for x:9 not found: manifest unknown", 1, CommandErrorCode.ImagePullFailed, CommandErrorCode.ImagePullFailed)]
    [InlineData("something unexpected", 2, CommandErrorCode.BuildFailed, CommandErrorCode.BuildFailed)]
    public void Docker_errors_map_to_stable_codes(string stderr, int exit, CommandErrorCode fallback, CommandErrorCode expected) =>
        Assert.Equal(expected, DockerErrors.Classify(stderr, exit, fallback));

    // ---- discovery -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Discovery_output_becomes_host_facts_and_docker_info()
    {
        var output = string.Join('\n',
            "##os", "PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"", "NAME=\"Ubuntu\"", "VERSION_ID=\"24.04\"",
            "##uname", "Linux 6.8.0-45-generic x86_64", "##host", "prod-1", "##cpu", "model name\t: AMD EPYC 7B13", "##nproc", "8",
            "##mem", "MemTotal: 16000000 kB", "MemAvailable: 8000000 kB", "SwapTotal: 2000000 kB", "SwapFree: 2000000 kB",
            "##df", "Filesystem 1024-blocks Used Available Capacity Mounted on", "/dev/sda1 100000 20000 80000 20% /",
            "##docker", """{"ServerVersion":"27.3.1","Driver":"overlay2","CgroupVersion":"2","DockerRootDir":"/var/lib/docker","ContainersRunning":3,"ContainersStopped":1,"Images":9,"Swarm":{"LocalNodeState":"inactive"},"SecurityOptions":["name=seccomp","name=rootless"]}""",
            "##dockerv", "1.47", "##compose", "2.29.7", "##buildx", "github.com/docker/buildx v0.17.1");
        var report = SshDiscoveryScript.Parse(output, DateTimeOffset.UtcNow);

        Assert.Equal(("prod-1", "Ubuntu", "24.04", "6.8.0-45-generic", "amd64", "AMD EPYC 7B13"),
            (report.Host.Hostname, report.Host.OsName, report.Host.OsVersion, report.Host.KernelVersion, report.Host.Architecture, report.Host.CpuModel));
        Assert.Equal(8u, report.Host.CpuCoresLogical);
        Assert.Equal(16000000L * 1024, report.Host.MemoryTotalBytes);
        Assert.Equal(Aethera.Agent.V1.DockerStatus.Running, report.Docker.Status);
        Assert.Equal("27.3.1", report.Docker.Version);
        Assert.True(report.Docker.Rootless);
        Assert.Equal(3u, report.Docker.ContainersRunning);
        Assert.Equal("2.29.7", report.Docker.ComposeVersion);
        Assert.Equal("/", Assert.Single(report.Disks).MountPoint);
    }

    [Fact]
    public void Discovery_of_a_host_where_the_daemon_is_down_reports_it_instead_of_failing()
    {
        // Newer Docker prints the client part of `info` as JSON even without a daemon, then the error.
        var output = "##docker\n{\"ServerVersion\":\"\",\"ClientInfo\":{}}\nfailed to connect: Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?\n";
        var report = SshDiscoveryScript.Parse(output, DateTimeOffset.UtcNow);
        Assert.Equal(Aethera.Agent.V1.DockerStatus.Stopped, report.Docker.Status);
        Assert.NotEmpty(report.Docker.Error);
    }

    // ---- deploy/agent stays in sync ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_embedded_unit_and_config_equal_the_files_under_deploy_agent()
    {
        var root = FindRepositoryRoot();
        if (root is null) return; // not running from a checkout
        Assert.Equal(Normalize(File.ReadAllText(Path.Combine(root, "deploy", "agent", "aethera-agent.service"))), Normalize(Aethera.Infrastructure.Ssh.Bootstrap.SshInstallAssets.UnitFile));
        Assert.Equal(Normalize(File.ReadAllText(Path.Combine(root, "deploy", "agent", "agent.yaml"))), Normalize(Aethera.Infrastructure.Ssh.Bootstrap.SshInstallAssets.AgentYaml));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "agent", "aethera-agent.service"))) return dir.FullName;
        return null;
    }
}
