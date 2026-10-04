using System.Text.Json;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Tests.Ssh;

/// <summary>
/// <see cref="SshTransport"/> with the real SSH client and real shells against the throw-away sshd (<c>Ssh/sshd</c>), whose <c>docker</c>
/// stand-in records its argv, env-file and stdin. This is where the secret-handling scripts (private temp directory, env-file, stdin logins,
/// cleanup) and the argv templates meet a real <c>sh</c>.
/// </summary>
[Collection("sshd")]
public sealed class SshTransportSshdTests : IAsyncLifetime
{
    private SshTestHost? _host;
    private Guid _server;

    public async Task InitializeAsync()
    {
        if (!SshdEndpoint.IsConfigured || !TestDatabase.IsConfigured) return;
        _host = await SshTestHost.CreateAsync(fakeConnector: false);
        _server = await _host!.SeedServerAsync(SshdEndpoint.Password, SshdEndpoint.Host, SshdEndpoint.Port, "root");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<List<JsonElement>> CallsAsync()
    {
        using var lease = await _host!.Get<SshConnectionPool>().AcquireAsync(_server, CancellationToken.None);
        var result = await lease.Connection.ExecuteAsync(new RemoteCommand("cat /tmp/docker-calls.jsonl 2>/dev/null; true"), new RemoteExecOptions(), CancellationToken.None);
        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private async Task ResetAsync()
    {
        using var lease = await _host!.Get<SshConnectionPool>().AcquireAsync(_server, CancellationToken.None);
        await lease.Connection.ExecuteAsync(new RemoteCommand("rm -f /tmp/docker-calls.jsonl; rm -rf /var/lib/aethera/projects"), new RemoteExecOptions(), CancellationToken.None);
    }

    private static List<string> Argv(JsonElement call) => call.GetProperty("argv").EnumerateArray().Select(e => e.GetString()!).ToList();

    private Task<CommandOutcome<TResult>> Run<TCommand, TResult>(TCommand command) where TCommand : IServerCommand<TResult> =>
        _host!.Transport.ExecuteAsync<TCommand, TResult>(_server, command, CommandOptions.For("job:step:0"), CancellationToken.None);

    [RequiresSshdFact]
    public async Task A_container_create_with_hostile_values_arrives_as_exactly_the_requested_argv()
    {
        if (_host is null) return;
        await ResetAsync();
        var hostile = "'; touch /tmp/pwned; echo '\"$(id)`id` && more\nsecond line";
        var oneLine = "'; touch /tmp/pwned; echo '\"$(id)`id` && more second line"; // label values may not contain line breaks
        var spec = new ContainerSpec("ghcr.io/o/app:1.0", "web")
        {
            Labels = new Dictionary<string, string> { ["note"] = oneLine },
            Command = ["sh", "-c", hostile],
            Healthcheck = new HealthcheckConfig(["CMD", "curl", hostile]),
            Ports = [new PortMappingSpec(80, 8080, "127.0.0.1")],
            Mounts = [new VolumeMountSpec(MountKind.Volume, "data", "/data")],
            Resources = new ResourceLimitsSpec(CpuLimitCores: 0.5, MemoryLimitBytes: 268435456),
        };
        var outcome = await Run<ContainerCreateCommand, ContainerCreated>(new ContainerCreateCommand(spec));
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);

        var create = (await CallsAsync()).First(c => Argv(c)[..2].SequenceEqual(["container", "create"]));
        var argv = Argv(create);
        Assert.Contains("note=" + oneLine, argv);
        Assert.Contains("127.0.0.1:8080:80/tcp", argv);
        Assert.Contains("type=volume,source=data,target=/data", argv);
        var afterImage = argv.Skip(argv.IndexOf("ghcr.io/o/app:1.0") + 1).ToList();
        Assert.Equal(["sh", "-c", hostile], afterImage);
        Assert.Contains(ShellQuote.Join(["curl", hostile]), argv); // quoted word by word for the container's shell, one word for Docker

        using var lease = await _host.Get<SshConnectionPool>().AcquireAsync(_server, CancellationToken.None);
        var pwned = await lease.Connection.ExecuteAsync(new RemoteCommand("ls /tmp/pwned 2>&1; true"), new RemoteExecOptions(), CancellationToken.None);
        Assert.Contains("No such file", pwned.Stdout + pwned.Stderr); // nothing was executed
    }

    [RequiresSshdFact]
    public async Task Environment_secrets_reach_docker_through_a_private_env_file_that_is_removed_afterwards()
    {
        if (_host is null) return;
        await ResetAsync();
        var spec = new ContainerSpec("nginx:1.27", "web") { Env = [EnvVarSpec.OfPlain("MODE", "prod"), EnvVarSpec.OfSecret("DB_PASSWORD", "p@ss 'w\"rd $(id) #x")] };
        var outcome = await Run<ContainerCreateCommand, ContainerCreated>(new ContainerCreateCommand(spec));
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);

        var create = (await CallsAsync()).First(c => Argv(c)[..2].SequenceEqual(["container", "create"]));
        Assert.Equal("MODE=prod\nDB_PASSWORD=p@ss 'w\"rd $(id) #x\n", create.GetProperty("envFile").GetString());
        Assert.Equal("0o600", create.GetProperty("envFileMode").GetString()); // never readable by other users
        Assert.DoesNotContain(create.GetProperty("argv").EnumerateArray().Select(e => e.GetString()), a => a!.Contains("p@ss"));

        // The temp directory with the env-file is gone once the command ended.
        using var lease = await _host.Get<SshConnectionPool>().AcquireAsync(_server, CancellationToken.None);
        var leftovers = await lease.Connection.ExecuteAsync(new RemoteCommand("ls -d /tmp/tmp.* 2>/dev/null | wc -l"), new RemoteExecOptions(), CancellationToken.None);
        Assert.Equal("0", leftovers.Stdout.Trim());
    }

    [RequiresSshdFact]
    public async Task Registry_logins_use_stdin_and_a_temporary_docker_config_never_the_users_own()
    {
        if (_host is null) return;
        await ResetAsync();
        var auth = new RegistryCredentials("ghcr.io", "octo cat", new SecretValue("tok-S3cret-77"));
        var outcome = await Run<ImagePullCommand, ImagePulled>(new ImagePullCommand("ghcr.io/o/app:1", auth));
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);

        var calls = await CallsAsync();
        var login = calls.First(c => Argv(c)[0] == "login");
        Assert.Equal(["login", "-u", "octo cat", "--password-stdin", "ghcr.io"], Argv(login));
        Assert.Equal("tok-S3cret-77\n", login.GetProperty("stdin").GetString());
        var config = login.GetProperty("dockerConfig").GetString()!;
        Assert.StartsWith("/tmp/tmp.", config); // a throw-away directory, not ~/.docker
        Assert.DoesNotContain(calls.SelectMany(Argv), a => a.Contains("tok-S3cret"));

        using var lease = await _host.Get<SshConnectionPool>().AcquireAsync(_server, CancellationToken.None);
        var gone = await lease.Connection.ExecuteAsync(new RemoteCommand("ls -d " + ShellQuote.Quote(config) + " 2>&1; true"), new RemoteExecOptions(), CancellationToken.None);
        Assert.Contains("No such file", gone.Stdout + gone.Stderr);
    }

    [RequiresSshdFact]
    public async Task Compose_files_are_uploaded_privately_and_compose_runs_in_the_project_directory()
    {
        if (_host is null) return;
        await ResetAsync();
        var project = new ComposeProjectSpec("demo", "services:\n  web:\n    image: nginx:1.27\n") { Env = [EnvVarSpec.OfSecret("API_KEY", "k-'quoted'-$x")] };
        var outcome = await Run<ComposeUpCommand, ComposeOutcome>(new ComposeUpCommand(project));
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.Equal("web", Assert.Single(outcome.Result!.Services).Service);
        Assert.Equal(8081, Assert.Single(outcome.Result.Services[0].Ports).HostPort);

        using var lease = await _host!.Get<SshConnectionPool>().AcquireAsync(_server, CancellationToken.None);
        var files = await lease.Connection.ExecuteAsync(new RemoteCommand("sh -c 'stat -c \"%a %n\" /var/lib/aethera/projects/demo/compose.yaml /var/lib/aethera/projects/demo/.env /var/lib/aethera/projects/demo; cat /var/lib/aethera/projects/demo/.env'"), new RemoteExecOptions(), CancellationToken.None);
        Assert.Contains("600 /var/lib/aethera/projects/demo/compose.yaml", files.Stdout);
        Assert.Contains("600 /var/lib/aethera/projects/demo/.env", files.Stdout);
        Assert.Contains("700 /var/lib/aethera/projects/demo\n", files.Stdout);
        Assert.Contains("API_KEY=\"k-'quoted'-\\$x\"", files.Stdout);

        var compose = (await CallsAsync()).First(c => Argv(c)[0] == "compose" && Argv(c).Contains("up"));
        Assert.Equal(["compose", "--project-name", "demo", "--file", "compose.yaml", "up", "--detach", "--pull", "missing", "--wait", "--wait-timeout", "300"], Argv(compose));
    }

    [RequiresSshdFact]
    public async Task A_secret_build_argument_comes_from_the_environment_of_a_sourced_private_file()
    {
        if (_host is null) return;
        await ResetAsync();
        var spec = new BuildSpec("b1", BuildEngineKind.Dockerfile, ["app:1"])
        {
            Git = new GitSourceSpec("https://github.com/o/r.git", "main"),
            BuildArgs = [EnvVarSpec.OfSecret("NPM_TOKEN", "it's a 'secret' $(id)")],
        };
        var outcome = await Run<BuildImageCommand, BuildOutcome>(new BuildImageCommand(spec));
        Assert.True(outcome.Succeeded, outcome.ErrorMessage);

        var build = (await CallsAsync()).First(c => Argv(c)[0] == "build");
        Assert.Contains("NPM_TOKEN", Argv(build)); // the name only
        Assert.DoesNotContain(Argv(build), a => a.Contains("secret"));
        Assert.Equal("it's a 'secret' $(id)", build.GetProperty("env").GetProperty("NPM_TOKEN").GetString()); // exported by the sourced file, byte for byte
    }

    [RequiresSshdFact]
    public async Task Inventory_commands_parse_what_the_cli_prints()
    {
        if (_host is null) return;
        await ResetAsync();
        var containers = await Run<ContainerListCommand, IReadOnlyList<DockerContainer>>(new ContainerListCommand());
        var web = Assert.Single(containers.Result!);
        Assert.Equal(("web", ContainerRunState.Running, ContainerHealthState.Healthy), (web.Name, web.State, web.Health));
        Assert.Contains(web.Ports, p => p is { ContainerPort: 80, HostPort: 8080 });

        var health = await Run<HealthProbeCommand, HealthProbeOutcome>(new HealthProbeCommand(new ContainerHealthProbeTarget("web")));
        Assert.True(health.Result!.Healthy);
    }
}
