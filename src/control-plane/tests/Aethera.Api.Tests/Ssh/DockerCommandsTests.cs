using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh.Docker;

namespace Aethera.Api.Tests.Ssh;

public sealed class DockerCommandsTests
{
    private static readonly SshDockerPolicy Policy = new();

    private static List<string> Words(RemoteCommand command) => PosixShell.Split(command.Line);

    private static ServerTransportException Rejected(Action action)
    {
        var ex = Assert.Throws<ServerTransportException>(action);
        Assert.Equal(TransportErrors.CommandRejected, ex.Code);
        return ex;
    }

    // ---- fixed templates -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Container_lifecycle_commands_map_to_fixed_argv()
    {
        Assert.Equal(["docker", "container", "start", "--", "web"], Words(DockerCommands.ContainerStart("web")));
        Assert.Equal(["docker", "container", "stop", "--time", "20", "--", "web"], Words(DockerCommands.ContainerStop("web", TimeSpan.FromSeconds(20))));
        Assert.Equal(["docker", "container", "restart", "--", "web"], Words(DockerCommands.ContainerRestart("web", null)));
        Assert.Equal(["docker", "container", "rm", "--force", "--volumes", "--", "web"], Words(DockerCommands.ContainerRemove("web", true, true)));
        Assert.Equal(["docker", "container", "inspect", "--", "a", "b"], Words(DockerCommands.ContainerInspect(["a", "b"])));
    }

    [Fact]
    public void Image_and_resource_commands_map_to_fixed_argv()
    {
        Assert.Equal(["docker", "image", "pull", "--platform", "linux/arm64", "--", "nginx:1.27"], Words(DockerCommands.ImagePull("nginx:1.27", null, "linux/arm64")));
        Assert.Equal(["docker", "image", "rm", "--force", "--", "nginx:1.27"], Words(DockerCommands.ImageRemove("nginx:1.27", true)));
        Assert.Equal(["docker", "volume", "create", "--driver", "local", "--label", "a=b", "--", "data"],
            Words(DockerCommands.VolumeCreate("data", "local", null, new Dictionary<string, string> { ["a"] = "b" })));
        Assert.Equal(["docker", "network", "connect", "--alias", "api", "--", "net", "web"], Words(DockerCommands.NetworkConnect("net", "web", ["api"], null)));
        Assert.Equal(["docker", "network", "rm", "--", "net"], Words(DockerCommands.NetworkRemove("net")));
    }

    [Fact]
    public void Prune_commands_always_force_and_scope_themselves()
    {
        Assert.Equal(["docker", "image", "prune", "--force", "--all", "--filter", "until=3600s", "--filter", "label=keep=no"],
            Words(DockerCommands.ImagePrune(true, TimeSpan.FromHours(1), ["keep=no"])));
        Assert.Equal(["docker", "container", "prune", "--force"], Words(DockerCommands.ContainerPrune(null)));
        Assert.Equal(["docker", "builder", "prune", "--force"], Words(DockerCommands.BuilderPrune(null)));
    }

    [Fact]
    public void Logs_use_since_tail_and_follow()
    {
        var since = new DateTimeOffset(2026, 10, 4, 12, 30, 5, TimeSpan.Zero);
        var words = Words(DockerCommands.Logs(new LogStreamRequest("web", true, since, 200)));
        Assert.Equal(["docker", "container", "logs", "--timestamps", "--since", "2026-10-04T12:30:05.0000000Z", "--tail", "200", "--follow", "--", "web"], words);
        Assert.Equal(["docker", "container", "logs", "--timestamps", "--", "web"], Words(DockerCommands.Logs(new LogStreamRequest("web", false, null, 0))));
    }

    [Fact]
    public void A_user_supplied_name_never_becomes_an_option()
    {
        Rejected(() => DockerCommands.ContainerStart("--privileged"));
        Rejected(() => DockerCommands.ContainerRemove("-f", true, true));
        Rejected(() => DockerCommands.ContainerInspect(["ok", "-x"]));
    }

    // ---- injection --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hostile_values_for_validated_fields_are_rejected_not_escaped()
    {
        foreach (var value in PosixShell.HostileStrings().Where(v => v.Length > 0))
        {
            Rejected(() => DockerCommands.ContainerStart(value));
            Rejected(() => DockerCommands.ContainerStop(value, null));
            Rejected(() => DockerCommands.ImagePull(value, null, null));
            Rejected(() => DockerCommands.VolumeCreate(value, null, null, null));
            Rejected(() => DockerCommands.NetworkRemove(value));
            Rejected(() => DockerCommands.Logs(new LogStreamRequest(value, false, null, 0)));
        }
    }

    [Fact]
    public void Free_text_fields_are_quoted_so_each_stays_exactly_one_word()
    {
        foreach (var value in PosixShell.HostileStrings().Concat(PosixShell.RandomHostileStrings(400)).Where(v => !v.Contains('\0') && !v.Any(char.IsControl) && v.Length > 0))
        {
            var spec = new ContainerSpec("nginx:1.27", "web")
            {
                Labels = new Dictionary<string, string> { ["note"] = value },
                Command = [value, "--flag", value],
                Entrypoint = [value],
                LogConfig = new LogConfigSpec("json-file", new Dictionary<string, string> { ["tag"] = value }),
                Healthcheck = new HealthcheckConfig(["CMD", "curl", value]),
            };

            var plan = DockerCommands.ContainerCreate(spec, true, false, null, Policy);
            var words = PosixShell.Split(plan.Create.Line);
            Assert.Contains("note=" + value, words);
            Assert.Contains("tag=" + value, words);
            Assert.Contains(ShellQuote.Join(["curl", value]), words); // the health command is one word for Docker and quoted word by word for the container's shell
            var afterImage = words.Skip(words.IndexOf("nginx:1.27") + 1).ToList();
            Assert.Equal([value, "--flag", value], afterImage);
        }
    }

    [Fact]
    public void Hostile_environment_variable_names_and_multiline_values_are_refused()
    {
        foreach (var name in new[] { "A B", "A=B", "1A", "A;id", "", "A$(id)", "A\nB" })
            Rejected(() => DockerCommands.ContainerCreate(new ContainerSpec("nginx", "web") { Env = [EnvVarSpec.OfPlain(name, "x")] }, true, false, null, Policy));
        Rejected(() => DockerCommands.ContainerCreate(new ContainerSpec("nginx", "web") { Env = [EnvVarSpec.OfPlain("A", "line1\nB=injected")] }, true, false, null, Policy));
        Rejected(() => DockerCommands.ContainerCreate(new ContainerSpec("nginx", "web") { Env = [EnvVarSpec.OfPlain("A", "x"), EnvVarSpec.OfPlain("A", "y")] }, true, false, null, Policy));
    }

    // ---- secrets ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Environment_values_travel_in_a_private_env_file_on_stdin_never_in_argv()
    {
        var spec = new ContainerSpec("nginx:1.27", "web")
        {
            Env = [EnvVarSpec.OfPlain("MODE", "prod"), EnvVarSpec.OfSecret("DB_PASSWORD", "p@ss 'word\" $(id)")],
        };
        var plan = DockerCommands.ContainerCreate(spec, true, false, null, Policy);

        Assert.DoesNotContain("p@ss", plan.Create.Line);
        Assert.DoesNotContain("prod", plan.Create.Line);
        Assert.DoesNotContain("DB_PASSWORD", plan.Create.Line);
        Assert.Contains("umask 077", plan.Create.Line);
        Assert.Contains("trap", plan.Create.Line); // removed afterwards
        Assert.Contains("--env-file", plan.Create.Line);
        Assert.Equal("MODE=prod\nDB_PASSWORD=p@ss 'word\" $(id)\n", plan.Create.Stdin);
        Assert.Contains("p@ss 'word\" $(id)", plan.Create.Secrets!);
        Assert.DoesNotContain("p@ss", plan.Create.ToString());
    }

    [Fact]
    public void Registry_passwords_go_over_stdin_into_a_temporary_docker_config()
    {
        var auth = new RegistryCredentials("ghcr.io", "octocat", new SecretValue("tok-123456"));
        var pull = DockerCommands.ImagePull("ghcr.io/octocat/app:1", auth, null);

        Assert.DoesNotContain("tok-123456", pull.Line);
        Assert.Contains("--password-stdin", pull.Line);
        Assert.Contains("DOCKER_CONFIG=\"$d/dc\"", pull.Line); // never the user's ~/.docker/config.json
        Assert.StartsWith("tok-123456\n", pull.Stdin);
        Assert.Contains("tok-123456", pull.Secrets!);
        Assert.Contains("rm -rf", pull.Line);
    }

    [Fact]
    public void Registry_identity_tokens_and_multiline_passwords_are_refused()
    {
        var ex = Assert.Throws<ServerTransportException>(() => DockerCommands.ImagePull("ghcr.io/o/a:1", new RegistryCredentials("ghcr.io", "u", null, new SecretValue("t")), null));
        Assert.Equal(TransportErrors.Unsupported, ex.Code);
        Rejected(() => DockerCommands.ImagePull("ghcr.io/o/a:1", new RegistryCredentials("ghcr.io", "u", new SecretValue("a\nb")), null));
        Rejected(() => DockerCommands.ImagePull("ghcr.io/o/a:1", new RegistryCredentials("ghcr.io;id", "u", new SecretValue("abc")), null));
    }

    [Fact]
    public void Secret_build_arguments_are_taken_from_a_sourced_private_file()
    {
        var git = new GitSourceSpec("https://github.com/owner/repo.git", "main");
        var spec = new BuildSpec("b1", BuildEngineKind.Dockerfile, ["app:1"])
        {
            Git = git,
            BuildArgs = [EnvVarSpec.OfPlain("MODE", "prod"), EnvVarSpec.OfSecret("NPM_TOKEN", "it's a secret")],
        };
        var command = DockerCommands.BuildFromGit(spec);

        Assert.DoesNotContain("it's a secret", command.Line);
        Assert.DoesNotContain("secret", command.Line.Replace("--build-arg NPM_TOKEN", ""));
        Assert.Contains("--build-arg MODE=prod", command.Line);
        Assert.Contains("--build-arg NPM_TOKEN", command.Line);
        Assert.Equal("NPM_TOKEN='it'\\''s a secret'\n", command.Stdin);
        Assert.Contains("set -a", command.Line);
    }

    // ---- container spec details -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_full_container_spec_maps_to_its_options()
    {
        var spec = new ContainerSpec("ghcr.io/o/app:1.0", "web")
        {
            Hostname = "web", WorkingDir = "/app", User = "1000:1000",
            Ports = [new PortMappingSpec(80, 8080, "127.0.0.1"), new PortMappingSpec(53, 0, null, PortProtocolKind.Udp)],
            Mounts = [new VolumeMountSpec(MountKind.Volume, "data", "/data"), new VolumeMountSpec(MountKind.Bind, "/var/lib/aethera/cfg", "/cfg", true), new VolumeMountSpec(MountKind.Tmpfs, "", "/tmp", false, 1024)],
            Networks = [new NetworkAttachmentSpec("front", ["web"]), new NetworkAttachmentSpec("back", ["api"])],
            Resources = new ResourceLimitsSpec(CpuLimitCores: 1.5, MemoryLimitBytes: 536870912, PidsLimit: 100),
            RestartPolicy = new RestartPolicySpec(RestartPolicyKind.OnFailure, 3),
            Healthcheck = new HealthcheckConfig(["CMD-SHELL", "curl -f localhost"], TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), 3),
            LogConfig = new LogConfigSpec("json-file", new Dictionary<string, string> { ["max-size"] = "10m" }),
            StopSignal = "SIGTERM", StopTimeout = TimeSpan.FromSeconds(15), ReadOnlyRootFs = true, Init = true, CapDrop = ["ALL"],
            Entrypoint = ["/entry", "a"], Command = ["serve"],
        };
        var plan = DockerCommands.ContainerCreate(spec, true, true, null, Policy);
        var w = Words(plan.Create);

        Assert.Equal(["docker", "container", "create"], w.Take(3));
        foreach (var expected in new[]
                 {
                     "--publish", "127.0.0.1:8080:80/tcp", "53/udp", "--mount", "type=volume,source=data,target=/data", "type=bind,source=/var/lib/aethera/cfg,target=/cfg,readonly",
                     "type=tmpfs,target=/tmp,tmpfs-size=1024", "--network", "front", "--network-alias", "web", "--cpus", "1.5", "--memory", "536870912", "--pids-limit", "100",
                     "on-failure:3", "--health-cmd", "curl -f localhost", "--health-interval", "10000ms", "--health-retries", "3", "--log-driver", "json-file", "max-size=10m",
                     "--stop-signal", "TERM", "--stop-timeout", "15", "--read-only", "--init", "--cap-drop", "ALL", "--entrypoint", "/entry", "--user", "1000:1000", "--workdir", "/app",
                 })
            Assert.Contains(expected, w);
        Assert.Equal(["--", "ghcr.io/o/app:1.0", "a", "serve"], w.Skip(w.IndexOf("--")).ToList());
        Assert.Equal("missing", w[w.IndexOf("--pull") + 1]);

        // The second network is joined after creation; the container is started last.
        var connect = Assert.Single(plan.ConnectNetworks);
        Assert.Equal(["docker", "network", "connect", "--alias", "api", "--", "back", "web"], Words(connect));
        Assert.Equal(["docker", "container", "start", "--", "web"], Words(plan.Start!));
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/var/lib/aethera/../../etc")]
    [InlineData("/etc/shadow")]
    [InlineData("/")]
    [InlineData("/root")]
    [InlineData("/home/deploy")]
    [InlineData("/var/lib/docker/volumes")]
    [InlineData("/var/lib/aethera/bin")]
    [InlineData("relative/path")]
    [InlineData("/var/lib/aethera/a,b")]
    public void Bind_mounts_outside_the_allowed_directories_are_refused(string source) =>
        Rejected(() => DockerCommands.ContainerCreate(new ContainerSpec("nginx", "web") { Mounts = [new VolumeMountSpec(MountKind.Bind, source, "/x")] }, true, false, null, Policy));

    [Fact]
    public void Bind_mount_prefixes_are_configurable()
    {
        var policy = new SshDockerPolicy { AllowedBindPrefixes = ["/srv/data"] };
        DockerCommands.ContainerCreate(new ContainerSpec("nginx", "web") { Mounts = [new VolumeMountSpec(MountKind.Bind, "/srv/data/x", "/x")] }, true, false, null, policy);
        Rejected(() => DockerCommands.ContainerCreate(new ContainerSpec("nginx", "web") { Mounts = [new VolumeMountSpec(MountKind.Bind, "/srv/other", "/x")] }, true, false, null, policy));
    }

    [Fact]
    public void Unsupported_resource_requests_surface_as_warnings_not_silence()
    {
        var plan = DockerCommands.ContainerCreate(new ContainerSpec("nginx", "web") { Resources = new ResourceLimitsSpec(CpuReservationCores: 0.5) }, false, false, null, Policy);
        Assert.Single(plan.Warnings);
        Assert.Null(plan.Start);
    }

    // ---- compose ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compose_commands_run_inside_the_project_directory_with_a_validated_name()
    {
        var project = new ComposeProjectSpec("demo", "services: {}") { Profiles = ["prod"] };
        var up = Words(DockerCommands.ComposeUp(Policy, new ComposeUpCommand(project, ["web"], ComposePullPolicyKind.Always, Build: false, ForceRecreate: true, RemoveOrphans: true)));
        Assert.Equal(["cd", "/var/lib/aethera/projects/demo", "&&", "docker", "compose", "--project-name", "demo", "--file", "compose.yaml", "--profile", "prod",
            "up", "--detach", "--pull", "always", "--force-recreate", "--remove-orphans", "--wait", "--wait-timeout", "300", "--", "web"], up);

        var down = Words(DockerCommands.ComposeDown(Policy, new ComposeDownCommand(project, RemoveVolumes: true)));
        Assert.Contains("--volumes", down);
        Assert.Contains("demo", down);
        Assert.Equal(["docker", "compose"], down.Skip(3).Take(2));

        foreach (var bad in new[] { "../x", "Demo", "a b", "a;b", "-x", "", "a/b" })
            Rejected(() => DockerCommands.ComposeUp(Policy, new ComposeUpCommand(new ComposeProjectSpec(bad, "x"))));
    }

    [Fact]
    public void Compose_environment_is_written_to_a_quoted_dotenv_file_never_to_argv()
    {
        var env = Aethera.Infrastructure.Ssh.SshCommandRunner.DotEnv([EnvVarSpec.OfPlain("A", "x y #z $HOME"), EnvVarSpec.OfSecret("S", "it's \"q\" $x \\")]);
        Assert.Equal("A='x y #z $HOME'\nS=\"it's \\\"q\\\" \\$x \\\\\"\n", env);
        Rejected(() => Aethera.Infrastructure.Ssh.SshCommandRunner.DotEnv([EnvVarSpec.OfPlain("A", "a\nB=injected")]));
        Rejected(() => Aethera.Infrastructure.Ssh.SshCommandRunner.DotEnv([EnvVarSpec.OfPlain("A B", "x")]));
    }

    // ---- builds -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_public_git_build_maps_to_docker_build_with_a_git_context()
    {
        var spec = new BuildSpec("b1", BuildEngineKind.Dockerfile, ["app:1", "app:latest"])
        {
            Git = new GitSourceSpec("https://github.com/owner/repo.git", "release/1.2") { },
            ContextPath = "services/api", DockerfilePath = "Dockerfile.prod", TargetStage = "runtime",
            BuildArgs = [EnvVarSpec.OfPlain("MODE", "prod")],
        };
        var w = Words(DockerCommands.BuildFromGit(spec));
        Assert.Equal(["docker", "build", "--pull", "--tag", "app:1", "--tag", "app:latest", "--file", "Dockerfile.prod", "--target", "runtime", "--build-arg", "MODE=prod", "--",
            "https://github.com/owner/repo.git#release/1.2:services/api"], w);
    }

    [Fact]
    public void Builds_beyond_image_pull_and_public_git_say_unsupported_with_the_hint()
    {
        var git = new GitSourceSpec("https://github.com/owner/repo.git", "main");
        var baseline = new BuildSpec("b1", BuildEngineKind.Dockerfile, ["app:1"]) { Git = git };
        var cases = new[]
        {
            baseline with { },
            new BuildSpec("b", BuildEngineKind.Nixpacks, ["a:1"]) { Git = git },
            new BuildSpec("b", BuildEngineKind.Static, ["a:1"]) { Git = git },
            new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]) { Git = git with { Credentials = new GitCredentialSpec("ref", "u", new SecretValue("t")) } },
            new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]) { Git = git, Secrets = [new BuildSecretSpec("id", new SecretValue("v"))] },
            new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]) { Git = git, Push = true },
            new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]) { Git = git, TargetPlatforms = ["linux/amd64", "linux/arm64"] },
            new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]) { Git = git with { Submodules = true } },
            new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]),
        };
        Assert.NotNull(DockerCommands.BuildFromGit(cases[0])); // the baseline itself is fine
        foreach (var spec in cases.Skip(1))
        {
            var ex = Assert.Throws<ServerTransportException>(() => DockerCommands.BuildFromGit(spec));
            Assert.Equal(TransportErrors.Unsupported, ex.Code);
            Assert.Contains("Enable the agent on this server or use a build server", ex.Message + DockerCommands.Hint);
        }
    }

    [Fact]
    public void A_private_or_non_https_repository_is_refused_before_anything_is_sent()
    {
        foreach (var url in new[] { "git@github.com:o/r.git", "http://github.com/o/r", "https://u:p@github.com/o/r", "ssh://git@github.com/o/r", "https://github.com/o/r.git#--upload-pack=id" })
            Rejected(() => DockerCommands.BuildFromGit(new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]) { Git = new GitSourceSpec(url, "main") }));
    }
}
