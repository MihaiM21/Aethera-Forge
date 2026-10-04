using System.Text;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;
using Aethera.Infrastructure.Ssh.Metrics;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Tests.Ssh;

/// <summary>Typed commands through <see cref="SshTransport"/> onto a scripted SSH session: argv, stdin, uploads, results and failures.</summary>
public sealed class SshTransportTests : IAsyncLifetime
{
    private SshTestHost? _host;
    private SshTestHost Host => _host!;
    private FakeSshConnection Ssh => Host.Connector!.Connection;
    private Guid _server;

    public async Task InitializeAsync()
    {
        _host = await SshTestHost.CreateAsync();
        if (_host is not null) _server = await _host.SeedServerAsync("Pw-Secret-9d41");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private const string ContainerJson = """[{"Id":"aaaaaaaaaaaa","Name":"/web","Image":"sha256:1","State":{"Status":"running","Health":{"Status":"healthy"}},"Config":{"Image":"nginx:1.27"}}]""";
    private const string ImageJson = """[{"Id":"sha256:img1","RepoTags":["nginx:1.27"],"RepoDigests":["nginx@sha256:dig1"],"Size":1000,"Architecture":"amd64","Os":"linux","Config":{}}]""";

    private static RemoteResult Ok(string stdout = "") => new(0, stdout, "");

    private static RemoteResult Fail(string stderr, int exit = 1) => new(exit, "", stderr);

    private Task<CommandOutcome<TResult>> Run<TCommand, TResult>(TCommand command, CommandOptions? options = null) where TCommand : IServerCommand<TResult> =>
        Host.Transport.ExecuteAsync<TCommand, TResult>(_server, command, options ?? CommandOptions.For("job:step:0"), CancellationToken.None);

    // ---- capabilities -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Capabilities_are_honest_about_what_the_fallback_cannot_do()
    {
        var caps = SshTransport.SshCapabilities;
        Assert.DoesNotContain(TransportCapabilities.PushEvents, new[] { caps & TransportCapabilities.PushEvents });
        Assert.Equal(TransportCapabilities.None, caps & TransportCapabilities.PushEvents);
        Assert.Equal(TransportCapabilities.None, caps & TransportCapabilities.PushMetrics);
        Assert.Equal(TransportCapabilities.None, caps & TransportCapabilities.SelfUpdate);
        foreach (var supported in new[] { TransportCapabilities.ContainerOps, TransportCapabilities.ImageOps, TransportCapabilities.VolumeNetworkOps, TransportCapabilities.Compose,
                     TransportCapabilities.Builds, TransportCapabilities.LogFollow, TransportCapabilities.HealthProbe })
            Assert.Equal(supported, caps & supported);
    }

    // ---- containers -------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Starting_a_container_returns_its_inspected_state()
    {
        Ssh.Handler = c => c.Line.Contains(" inspect ") ? Ok(ContainerJson) : Ok();
        var outcome = await Run<ContainerStartCommand, DockerContainer>(new ContainerStartCommand("web"));

        Assert.True(outcome.Succeeded);
        Assert.Equal(ContainerRunState.Running, outcome.Result!.State);
        Assert.Equal(ContainerHealthState.Healthy, outcome.Result.Health);
        Assert.Equal(["docker container start -- web", "docker container inspect -- web"], Ssh.Lines());
    }

    [RequiresDatabaseFact]
    public async Task Listing_containers_lists_ids_then_inspects_them()
    {
        Ssh.Handler = c => c.Line.Contains(" ls ") ? Ok("aaaaaaaaaaaa\nbbbbbbbbbbbb\n") : Ok(ContainerJson);
        var outcome = await Run<ContainerListCommand, IReadOnlyList<DockerContainer>>(new ContainerListCommand(All: true, LabelFilters: ["app=web"]));

        Assert.Single(outcome.Result!);
        Assert.Equal("docker container ls --quiet --no-trunc --all --filter label=app=web", Ssh.Lines()[0]);
        Assert.Equal("docker container inspect -- aaaaaaaaaaaa bbbbbbbbbbbb", Ssh.Lines()[1]);
    }

    [RequiresDatabaseFact]
    public async Task A_missing_container_is_a_failed_outcome_with_a_stable_code()
    {
        Ssh.Handler = _ => Fail("Error response from daemon: No such container: web");
        var outcome = await Run<ContainerStartCommand, DockerContainer>(new ContainerStartCommand("web"));

        Assert.Equal(CommandStatus.Failed, outcome.Status);
        Assert.Equal(CommandErrorCode.NotFound, outcome.ErrorCode);
        Assert.Contains("No such container", outcome.ErrorMessage);
        Assert.Throws<ServerTransportException>(() => outcome.EnsureSucceeded());
    }

    [RequiresDatabaseFact]
    public async Task Creating_a_container_keeps_secrets_out_of_argv_and_out_of_error_messages()
    {
        var spec = new ContainerSpec("nginx:1.27", "web") { Env = [EnvVarSpec.OfSecret("DB_PASSWORD", "s3cr3t-Value-77")] };
        Ssh.Handler = c => c.Line.Contains("container create") ? Fail("create failed with --env DB_PASSWORD=s3cr3t-Value-77 in the output") : Ok();
        var outcome = await Run<ContainerCreateCommand, ContainerCreated>(new ContainerCreateCommand(spec));

        Assert.Equal(CommandStatus.Failed, outcome.Status);
        Assert.DoesNotContain("s3cr3t-Value-77", outcome.ErrorMessage);
        Assert.Contains("********", outcome.ErrorMessage);
        var create = Ssh.Commands.First(c => c.Line.Contains("container create"));
        Assert.DoesNotContain("s3cr3t-Value-77", create.Line);
        Assert.Equal("DB_PASSWORD=s3cr3t-Value-77\n", create.Stdin);
    }

    [RequiresDatabaseFact]
    public async Task A_container_that_fails_to_start_is_not_left_behind()
    {
        Ssh.Handler = c => c.Line.Contains("container start") ? Fail("Bind for 0.0.0.0:80 failed: port is already allocated") : Ok(c.Line.Contains("create") ? "id" : "");
        var outcome = await Run<ContainerCreateCommand, ContainerCreated>(new ContainerCreateCommand(new ContainerSpec("nginx:1.27", "web") { Ports = [new PortMappingSpec(80, 80)] }));

        Assert.Equal(CommandErrorCode.PortConflict, outcome.ErrorCode);
        Assert.Contains("docker container rm --force -- web", Ssh.Lines()); // cleaned up so a retry does not meet "name already in use"
    }

    [RequiresDatabaseFact]
    public async Task Replacing_an_existing_container_removes_it_first_and_tolerates_it_not_existing()
    {
        Ssh.Handler = c => c.Line.Contains("container rm") ? Fail("Error response from daemon: No such container: web") : c.Line.Contains("inspect") ? Ok(ContainerJson) : Ok("id");
        var outcome = await Run<ContainerCreateCommand, ContainerCreated>(new ContainerCreateCommand(new ContainerSpec("nginx:1.27", "web"), ReplaceExisting: true));

        Assert.True(outcome.Succeeded);
        Assert.Equal("docker container rm --force -- web", Ssh.Lines()[0]);
    }

    // ---- images, registry ------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Pulling_with_credentials_logs_in_through_stdin_and_reports_the_image()
    {
        var auth = new RegistryCredentials("ghcr.io", "octocat", new SecretValue("tok-AbC123xyz"));
        Ssh.Handler = c => c.Line.Contains("image pull") || c.Line.StartsWith("sh -c") ? Ok("Status: Image is up to date for nginx:1.27") : Ok(ImageJson);
        var outcome = await Run<ImagePullCommand, ImagePulled>(new ImagePullCommand("nginx:1.27", auth));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Result!.AlreadyPresent);
        Assert.Equal("sha256:dig1", outcome.Result.Digest);
        var pull = Ssh.Commands.First();
        Assert.DoesNotContain("tok-AbC123xyz", pull.Line);
        Assert.Equal("tok-AbC123xyz\n", pull.Stdin);
    }

    [RequiresDatabaseFact]
    public async Task A_failed_pull_is_classified_and_never_echoes_the_password()
    {
        var auth = new RegistryCredentials("ghcr.io", "octocat", new SecretValue("tok-AbC123xyz"));
        Ssh.Handler = _ => Fail("Error response from daemon: Head \"https://ghcr.io\": unauthorized: tok-AbC123xyz rejected");
        var outcome = await Run<ImagePullCommand, ImagePulled>(new ImagePullCommand("ghcr.io/octocat/app:1", auth));

        Assert.Equal(CommandErrorCode.RegistryAuthFailed, outcome.ErrorCode);
        Assert.DoesNotContain("tok-AbC123xyz", outcome.ErrorMessage);
    }

    [RequiresDatabaseFact]
    public async Task Pruning_all_unused_images_with_a_keep_list_never_removes_kept_or_used_images()
    {
        // table: id<TAB>repo:tag
        var table = "aaaaaaaaaaaa\tapp:rollback\nbbbbbbbbbbbb\tapp:current\ncccccccccccc\tapp:old\ndddddddddddd\t<none>:<none>\n";
        Ssh.Handler = c =>
            c.Line.Contains("image ls") ? Ok(table)
            : c.Line.Contains("container ls") ? Ok("bbbbbbbbbbbb\n")
            : c.Line.Contains("image inspect") ? Ok("[" + string.Join(",", c.Line.Split(' ').Where(w => w.StartsWith("cccc") || w.StartsWith("dddd")).Select(id => "{\"Id\":\"" + id + "\",\"Size\":100,\"Config\":{}}")) + "]")
            : c.Line.Contains("image rm") ? Ok("Deleted: " + c.Line.Split(' ')[^1])
            : Ok();
        var outcome = await Run<ImagePruneCommand, PruneOutcome>(new ImagePruneCommand(AllUnused: true, Keep: ["app:rollback"]));

        Assert.True(outcome.Succeeded);
        var removed = Ssh.Lines().Where(l => l.Contains("image rm")).ToList();
        Assert.Equal(2, removed.Count);
        Assert.All(removed, l => Assert.True(l.EndsWith("cccccccccccc") || l.EndsWith("dddddddddddd")));
        Assert.DoesNotContain(Ssh.Lines(), l => l.Contains("aaaaaaaaaaaa") && l.Contains("rm"));
        Assert.DoesNotContain(Ssh.Lines(), l => l.Contains("bbbbbbbbbbbb") && l.Contains("rm"));
        Assert.Equal(200, outcome.Result!.SpaceReclaimedBytes);
    }

    [RequiresDatabaseFact]
    public async Task System_prune_needs_a_scope_and_unlabelled_volumes_are_never_removed()
    {
        var none = await Run<SystemPruneCommand, PruneOutcome>(new SystemPruneCommand());
        Assert.Equal(CommandErrorCode.InvalidArgument, none.ErrorCode);
        Assert.Empty(Ssh.Commands);

        Ssh.Handler = c =>
            c.Line.Contains("volume ls") ? Ok("labelled\nbare\n")
            : c.Line.Contains("volume inspect") ? Ok("""[{"Name":"labelled","Labels":{"app":"x"}},{"Name":"bare","Labels":{}}]""")
            : Ok();
        var outcome = await Run<SystemPruneCommand, PruneOutcome>(new SystemPruneCommand(Volumes: true));
        Assert.Equal(["labelled"], outcome.Result!.Deleted);
        Assert.Contains("docker volume rm -- labelled", Ssh.Lines());
        Assert.DoesNotContain("docker volume rm -- bare", Ssh.Lines());

        var unlabelled = await Run<VolumePruneCommand, PruneOutcome>(new VolumePruneCommand(IncludeUnlabelled: true));
        Assert.Equal(["labelled", "bare"], unlabelled.Result!.Deleted);
    }

    // ---- compose ----------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Compose_up_uploads_the_files_privately_then_runs_compose_in_the_project_directory()
    {
        var project = new ComposeProjectSpec("demo", "services:\n  web:\n    image: nginx\n") { Env = [EnvVarSpec.OfSecret("API_KEY", "key-with-'quote")] };
        Ssh.Handler = c => c.Line.Contains(" ps ") ? Ok("""{"ID":"e1","Name":"demo-web-1","Service":"web","Image":"nginx","State":"running","Health":"","ExitCode":0,"Publishers":[]}""") : Ok();
        var outcome = await Run<ComposeUpCommand, ComposeOutcome>(new ComposeUpCommand(project));

        Assert.True(outcome.Succeeded);
        Assert.Equal("web", Assert.Single(outcome.Result!.Services).Service);
        Assert.Equal("mkdir -p -m 0700 -- /var/lib/aethera/projects/demo", Ssh.Lines()[0]);
        var uploads = Ssh.Uploads.ToList();
        Assert.Equal(["/var/lib/aethera/projects/demo/compose.yaml", "/var/lib/aethera/projects/demo/.env"], uploads.Select(u => u.Path));
        Assert.All(uploads, u => Assert.Equal("0600", u.Mode));
        Assert.Equal("services:\n  web:\n    image: nginx\n", uploads[0].Content);
        Assert.Equal("API_KEY=\"key-with-'quote\"\n", uploads[1].Content);
        Assert.StartsWith("cd /var/lib/aethera/projects/demo && docker compose --project-name demo --file compose.yaml up --detach", Ssh.Lines()[1]);
        Assert.All(Ssh.Lines(), l => Assert.DoesNotContain("key-with", l));
    }

    [RequiresDatabaseFact]
    public async Task Compose_down_of_a_project_that_was_never_deployed_over_ssh_does_nothing()
    {
        Ssh.Handler = c => c.Line.StartsWith("test -d") ? Fail("", 1) : Ok();
        var outcome = await Run<ComposeDownCommand, ComposeOutcome>(new ComposeDownCommand(new ComposeProjectSpec("demo", "x")));
        Assert.True(outcome.Succeeded);
        Assert.Single(Ssh.Commands);
    }

    // ---- builds -----------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task An_image_build_pulls_and_tags()
    {
        Ssh.Handler = c => c.Line.Contains("image inspect") ? Ok(ImageJson) : Ok();
        var spec = new BuildSpec("b1", BuildEngineKind.Image, ["registry.example.com/app:1"]) { ImageReference = "nginx:1.27" };
        var outcome = await Run<BuildImageCommand, BuildOutcome>(new BuildImageCommand(spec));

        Assert.True(outcome.Succeeded);
        Assert.Equal("image", outcome.Result!.EngineUsed);
        Assert.Equal("linux/amd64", outcome.Result.Platform);
        Assert.Equal(["docker image pull -- nginx:1.27", "docker image tag -- nginx:1.27 registry.example.com/app:1", "docker image inspect -- registry.example.com/app:1"], Ssh.Lines());
    }

    [RequiresDatabaseFact]
    public async Task A_public_git_build_streams_its_output_to_the_log_stream_and_the_callback()
    {
        Ssh.Handler = c => c.Line.StartsWith("docker build") ? Ok("Step 1/3 : FROM node\nStep 2/3 : RUN npm ci\nSuccessfully built abc") : Ok(ImageJson);
        var spec = new BuildSpec("b2", BuildEngineKind.Dockerfile, ["app:1"]) { Git = new GitSourceSpec("https://github.com/o/r.git", "main") };
        var logged = new List<string>();
        var progress = new List<string>();
        var options = new CommandOptions { IdempotencyKey = "k", LogStreamId = "build:" + Guid.NewGuid(), OnLog = e => logged.Add(e.Text), OnProgress = p => progress.Add(p.Step) };
        var outcome = await Run<BuildImageCommand, BuildOutcome>(new BuildImageCommand(spec), options);

        Assert.True(outcome.Succeeded);
        Assert.Equal("dockerfile", outcome.Result!.EngineUsed);
        Assert.Equal(["Step 1/3 : FROM node", "Step 2/3 : RUN npm ci", "Successfully built abc"], logged);
        Assert.Equal(["started", "finished"], progress);
    }

    [RequiresDatabaseFact]
    public async Task Unsupported_builds_fail_fast_with_the_hint_and_run_nothing()
    {
        var git = new GitSourceSpec("https://github.com/o/r.git", "main");
        foreach (var spec in new[]
                 {
                     new BuildSpec("b", BuildEngineKind.Nixpacks, ["a:1"]) { Git = git },
                     new BuildSpec("b", BuildEngineKind.Static, ["a:1"]) { Git = git },
                     new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]) { Git = git, Push = true },
                     new BuildSpec("b", BuildEngineKind.Dockerfile, ["a:1"]) { Git = git with { Credentials = new GitCredentialSpec("c", "u", new SecretValue("t")) } },
                 })
        {
            var ex = await Assert.ThrowsAsync<ServerTransportException>(() => Run<BuildImageCommand, BuildOutcome>(new BuildImageCommand(spec)));
            Assert.Equal(TransportErrors.Unsupported, ex.Code);
            Assert.Contains("Enable the agent on this server or use a build server", ex.Message);
        }

        Assert.Empty(Ssh.Commands);
    }

    [RequiresDatabaseFact]
    public async Task Commands_the_cli_cannot_carry_say_unsupported_with_the_hint()
    {
        async Task Expect<TCommand, TResult>(TCommand command) where TCommand : IServerCommand<TResult>
        {
            var ex = await Assert.ThrowsAsync<ServerTransportException>(() => Run<TCommand, TResult>(command));
            Assert.Equal(TransportErrors.Unsupported, ex.Code);
            Assert.Contains("Enable the agent on this server or use a build server", ex.Message);
        }

        await Expect<ProxyEnsureCommand, ProxyEnsured>(new ProxyEnsureCommand("caddy", [1]));
        await Expect<AgentSelfUpdateCommand, SelfUpdateOutcome>(new AgentSelfUpdateCommand("https://x/a", "00", "1.0"));
        await Expect<BuildDetectCommand, BuildDetection>(new BuildDetectCommand(new GitSourceSpec("https://github.com/o/r.git")));
        await Expect<LogStreamStartCommand, LogStreamStarted>(new LogStreamStartCommand("s", "web"));
        Assert.Empty(Ssh.Commands);
    }

    [RequiresDatabaseFact]
    public async Task Invalid_arguments_are_a_rejected_outcome_before_anything_is_sent()
    {
        var outcome = await Run<ContainerStartCommand, DockerContainer>(new ContainerStartCommand("web; reboot"));
        Assert.Equal(CommandStatus.Failed, outcome.Status);
        Assert.Equal(CommandErrorCode.InvalidArgument, outcome.ErrorCode);
        Assert.Empty(Ssh.Commands);
    }

    // ---- time, cancellation, audit ----------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task The_deadline_turns_a_slow_command_into_a_timed_out_outcome()
    {
        Ssh.Delay = TimeSpan.FromSeconds(5);
        var options = new CommandOptions { IdempotencyKey = "k", Deadline = DateTimeOffset.UtcNow.AddMilliseconds(300) };
        var outcome = await Run<ContainerRemoveCommand, Unit>(new ContainerRemoveCommand("web"), options);
        Assert.Equal(CommandStatus.TimedOut, outcome.Status);
        Assert.Equal(CommandErrorCode.Timeout, outcome.ErrorCode);
    }

    [RequiresDatabaseFact]
    public async Task Cancelling_gives_a_cancelled_outcome()
    {
        Ssh.Delay = TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(200);
        var outcome = await Host.Transport.ExecuteAsync<ContainerRemoveCommand, Unit>(_server, new ContainerRemoveCommand("web"), CommandOptions.For("k"), cts.Token);
        Assert.Equal(CommandStatus.Cancelled, outcome.Status);
    }

    [RequiresDatabaseFact]
    public async Task Every_command_is_audited_by_name_and_outcome_without_payload()
    {
        Ssh.Handler = c => c.Line.Contains(" inspect ") ? Ok(ContainerJson) : Ok();
        await Run<ContainerCreateCommand, ContainerCreated>(new ContainerCreateCommand(new ContainerSpec("nginx", "web") { Env = [EnvVarSpec.OfSecret("TOKEN", "audit-Secret-5567")] }),
            new CommandOptions { IdempotencyKey = "job:deploy:0", JobId = Guid.NewGuid() });

        var events = await Host.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.ResourceId == _server && e.Action == "ssh.command").ToListAsync());
        var audit = Assert.Single(events);
        Assert.Contains("container.create", audit.MetadataJson);
        Assert.Contains("ssh", audit.MetadataJson);
        Assert.DoesNotContain("audit-Secret-5567", audit.MetadataJson);
        Assert.DoesNotContain("nginx", audit.MetadataJson);
    }

    // ---- logs and events --------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Logs_follow_over_an_exec_channel_and_map_to_entries()
    {
        Ssh.StreamHandler = _ =>
        [
            new RemoteLine(false, "2026-10-04T12:00:01.123456789Z started"),
            new RemoteLine(true, "2026-10-04T12:00:02.5Z warning: slow"),
            new RemoteLine(false, "no timestamp here"),
        ];
        var entries = new List<LogEntry>();
        await foreach (var entry in Host.Transport.StreamLogsAsync(_server, new LogStreamRequest("web", true, null, 100), CancellationToken.None)) entries.Add(entry);

        Assert.Equal(4, entries.Count);
        Assert.Equal("started", entries[0].Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 0, 1, TimeSpan.Zero).AddTicks(1234567), entries[0].Timestamp);
        Assert.Equal(LogStream.Stderr, entries[1].Stream);
        Assert.Equal(LogSource.Container, entries[1].Source);
        Assert.Equal("no timestamp here", entries[2].Text);
        Assert.True(entries[3].Eof);
        Assert.Equal([1L, 2, 3, 4], entries.Select(e => e.Sequence));
        Assert.Equal("docker container logs --timestamps --tail 100 --follow -- web", Ssh.Lines()[0]);

        var stdoutOnly = new List<LogEntry>();
        await foreach (var entry in Host.Transport.StreamLogsAsync(_server, new LogStreamRequest("web", false, null, 0, IncludeStderr: false), CancellationToken.None)) stdoutOnly.Add(entry);
        Assert.DoesNotContain(stdoutOnly, e => e.Stream == LogStream.Stderr && !e.Eof);
    }

    [RequiresDatabaseFact]
    public async Task There_are_no_pushed_events_over_ssh()
    {
        var events = new List<ServerEvent>();
        await foreach (var e in Host.Transport.SubscribeEventsAsync(_server, CancellationToken.None)) events.Add(e);
        Assert.Empty(events);
    }

    // ---- discovery and health --------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Discovery_refresh_stores_facts_and_the_docker_axis_like_an_agent_report()
    {
        var output = string.Join('\n',
            "##os", "NAME=\"Debian GNU/Linux\"", "VERSION_ID=\"12\"", "##uname", "Linux 6.1.0-18-amd64 x86_64", "##host", "vps-1", "##cpu", "model name : Test CPU", "##nproc", "2",
            "##mem", "MemTotal: 4000000 kB", "SwapTotal: 0 kB", "SwapFree: 0 kB",
            "##df", "Filesystem 1024-blocks Used Available Capacity Mounted on", "/dev/vda1 50000000 1 49999999 1% /",
            "##docker", """{"ServerVersion":"27.3.1","Driver":"overlay2","ContainersRunning":1,"Images":2}""", "##dockerv", "1.47");
        Ssh.Handler = c =>
            c.Line == SshDiscoveryScript.Line ? Ok(output)
            : c.Line.Contains("container ls") || c.Line.Contains("network ls") || c.Line.Contains("volume ls") ? Ok()
            : Ok();
        var outcome = await Run<DiscoveryRefreshCommand, DiscoveryInfo>(new DiscoveryRefreshCommand());

        Assert.True(outcome.Succeeded);
        Assert.Equal("vps-1", outcome.Result!.Host.Hostname);
        Assert.Equal(DockerStatus.Running, outcome.Result.Docker.Status);
        var server = await Host.LoadServerAsync(_server);
        Assert.Equal("Debian GNU/Linux", server.Facts.Os);
        Assert.Equal("amd64", server.Facts.Architecture);
        Assert.Equal(2, server.Facts.CpuCores);
        Assert.Equal("27.3.1", server.Facts.DockerVersion);
        Assert.Equal(DockerStatus.Running, server.DockerStatus);
        Assert.NotNull(await Host.Get<AgentDiscoveryStore>().LoadAsync(_server, CancellationToken.None));
    }

    [RequiresDatabaseFact]
    public async Task A_container_health_probe_reads_dockers_status()
    {
        Ssh.Handler = _ => Ok("healthy\n");
        var outcome = await Run<HealthProbeCommand, HealthProbeOutcome>(new HealthProbeCommand(new ContainerHealthProbeTarget("web")));
        Assert.True(outcome.Result!.Healthy);

        Ssh.Handler = _ => Ok("unhealthy\n");
        var bad = await Run<HealthProbeCommand, HealthProbeOutcome>(new HealthProbeCommand(new ContainerHealthProbeTarget("web"), Retries: 2, Interval: TimeSpan.FromMilliseconds(10)));
        Assert.False(bad.Result!.Healthy);
        Assert.Equal(2, bad.Result.Attempts);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("metadata.google.internal")]
    [InlineData("fe80::1")]
    public void Link_local_and_metadata_addresses_can_never_be_probed(string host) =>
        Assert.Throws<ServerTransportException>(() => SshHealthProber.ValidateTarget(host, 80));
}
