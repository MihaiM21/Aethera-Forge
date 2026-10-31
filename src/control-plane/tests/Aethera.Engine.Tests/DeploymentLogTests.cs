using System.Runtime.CompilerServices;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;

namespace Aethera.Engine.Tests;

/// <summary>What a deployment's log shows of the agent's work: command output live, and why a compose project did not start.</summary>
public class DeploymentLogTests
{
    private static readonly Guid Server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Answers each command from a script; its output goes to <see cref="CommandOptions.OnLog"/> like the agent's live chunks do.</summary>
    private sealed class ScriptedTransport(
        Func<object, object?> responder, Dictionary<string, string[]>? output = null, Dictionary<string, string[]>? containerLogs = null) : IServerTransport
    {
        public List<(string Command, CommandOptions Options)> Commands { get; } = [];
        public TransportKind Kind => TransportKind.Agent;
        public TransportCapabilities Capabilities => TransportCapabilities.AllAgent;

        public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken ct) => ValueTask.FromResult(TransportStatus.Up(Kind));

        public Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(Guid serverId, TCommand command, CommandOptions options, CancellationToken ct)
            where TCommand : IServerCommand<TResult>
        {
            Commands.Add((command.Name, options));
            var sequence = 0;
            foreach (var line in output?.GetValueOrDefault(command.Name) ?? [])
            {
                var stderr = line.StartsWith("!", StringComparison.Ordinal);
                options.OnLog?.Invoke(new LogEntry(options.LogStreamId ?? "", ++sequence, DateTimeOffset.UnixEpoch, LogSource.Container,
                    stderr ? LogStream.Stderr : LogStream.Stdout, (stderr ? line[1..] : line) + "\n"));
            }

            var result = responder(command);
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(result is TResult r
                ? new CommandOutcome<TResult>(CommandStatus.Succeeded, r, CommandErrorCode.None, null, 0, now, now, false)
                : new CommandOutcome<TResult>(CommandStatus.Failed, default, CommandErrorCode.Internal, "compose up: exit status 1", 1, now, now, false));
        }

        public async IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var sequence = 0;
            foreach (var line in containerLogs?.GetValueOrDefault(request.Container) ?? [])
                yield return new LogEntry(request.Container, ++sequence, DateTimeOffset.UnixEpoch, LogSource.Container, LogStream.Stderr, line + "\n");
            await Task.CompletedTask;
        }

        public IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static ComposeServiceInfo Service(string name, ContainerRunState state, ContainerHealthState health = ContainerHealthState.None, int exitCode = 0) =>
        new(name, "c-" + name, "turn-one-" + name + "-1", name + ":latest", state, health, exitCode, []);

    private static (DeploymentRun Run, List<string> Log) NewRun(ScriptedTransport transport)
    {
        var clock = new FixedClock();
        var d = Deployment.Queue(Guid.NewGuid(), Guid.NewGuid(), Server, 1, DeploymentTrigger.Manual, clock.UtcNow);
        d.Start("{}", clock.UtcNow);
        var log = new List<string>();
        var plan = new DeploymentPlan
        {
            ServerId = Server, JobId = Guid.NewGuid(),
            Compose = new ComposeProjectSpec("turn-one-1234abcd", "") { Git = new GitSourceSpec("https://github.com/acme/turn-one.git", "dev", null) },
            WriteLog = (stream, line, _) => { lock (log) log.Add(stream == LogStream.Stderr ? "stderr: " + line : line); return ValueTask.CompletedTask; },
            Narrate = (line, _) => { lock (log) log.Add("[aethera] " + line); return ValueTask.CompletedTask; },
        };
        return (new DeploymentRun(d, plan, "", transport, clock, _ => Task.CompletedTask), log);
    }

    [Fact]
    public async Task A_failed_compose_up_shows_its_output_each_service_state_and_the_output_of_the_service_that_stopped()
    {
        var transport = new ScriptedTransport(
            command => command switch
            {
                ComposePsCommand => new ComposeOutcome("turn-one-1234abcd",
                [
                    Service("influxdb", ContainerRunState.Exited, exitCode: 1),
                    Service("api", ContainerRunState.Created),
                    Service("client", ContainerRunState.Created),
                ]),
                _ => null,
            },
            output: new()
            {
                ["compose.up"] = ["#12 [api build 5/9] RUN dotnet restore", "!dependency failed to start: container turn-one-influxdb-1 exited (1)"],
            },
            containerLogs: new() { ["c-influxdb"] = ["Error: password must be at least 8 characters long"] });
        var (run, log) = NewRun(transport);

        var failure = await Assert.ThrowsAsync<DeploymentFailure>(() => new ComposeStrategy().ExecuteAsync(run, default));

        Assert.Equal(FailureCodes.ContainerFailed, failure.Code);
        Assert.Equal("The command failed on the server. compose up: exit status 1. influxdb: exited with code 1; api: created but not started; client: created but not started",
            failure.Message);
        // the agent's output, live, then the state of every service and the output of the ones that did not start
        Assert.Equal(
        [
            "#12 [api build 5/9] RUN dotnet restore",
            "stderr: dependency failed to start: container turn-one-influxdb-1 exited (1)",
            "[aethera] Service api: created but not started.",
            "[aethera] Service client: created but not started.",
            "[aethera] Service influxdb: exited with code 1.",
            $"[aethera] The last {DeploymentRun.FailureLogLines} lines of the output of service influxdb:",
            "stderr: Error: password must be at least 8 characters long",
        ], log);
        var up = transport.Commands.Single(c => c.Command == "compose.up");
        Assert.Equal($"deploy:{run.Deployment.Id}", up.Options.LogStreamId);
    }

    [Fact]
    public async Task An_unhealthy_service_after_a_successful_up_is_named_with_its_output()
    {
        var transport = new ScriptedTransport(
            command => command switch
            {
                ComposeUpCommand => new ComposeOutcome("turn-one-1234abcd",
                    [Service("api", ContainerRunState.Running, ContainerHealthState.Unhealthy), Service("influxdb", ContainerRunState.Running, ContainerHealthState.Healthy)]),
                _ => null,
            },
            containerLogs: new() { ["c-api"] = ["Unhandled exception. Npgsql.NpgsqlException: Failed to connect"] });
        var (run, log) = NewRun(transport);

        var failure = await Assert.ThrowsAsync<DeploymentFailure>(() => new ComposeStrategy().ExecuteAsync(run, default));

        Assert.Equal(DeploymentStep.HealthCheck, failure.Step);
        Assert.Contains("[aethera] Service api: running, unhealthy (its health check fails).", log);
        Assert.Contains("[aethera] Service influxdb: running, healthy.", log);
        Assert.Contains("stderr: Unhandled exception. Npgsql.NpgsqlException: Failed to connect", log);
        Assert.DoesNotContain(transport.Commands, c => c.Command == "compose.ps"); // the outcome of up already has the states
    }

    [Fact]
    public async Task When_the_state_cannot_be_read_the_failure_of_compose_up_stays_as_it_was()
    {
        var (run, log) = NewRun(new ScriptedTransport(_ => null));

        var failure = await Assert.ThrowsAsync<DeploymentFailure>(() => new ComposeStrategy().ExecuteAsync(run, default));

        Assert.Equal("The command failed on the server. compose up: exit status 1", failure.Message);
        Assert.Contains("[aethera] The state of the services could not be read.", log);
    }

    [Fact]
    public async Task An_image_build_writes_to_its_own_stream_and_is_not_copied_into_the_deployment_log()
    {
        var spec = new BuildSpec("0190f3c2-0000-7000-8000-00000000b001", BuildEngineKind.Dockerfile, ["api:1"]);
        var transport = new ScriptedTransport(
            command => command is BuildImageCommand b
                ? new BuildOutcome(b.Spec.BuildId, "sha256:img", "sha256:built", b.Spec.ImageTags, [], 1, TimeSpan.Zero, "dockerfile", "abc", false, "linux/amd64", false)
                : null,
            output: new() { ["build.run"] = ["#5 [2/4] RUN npm ci"] });
        var (run, log) = NewRun(transport);

        await run.ExecuteAsync<BuildImageCommand, BuildOutcome>(new BuildImageCommand(spec), "build", DeploymentStep.Build, FailureCodes.BuildFailed, default);

        var build = transport.Commands.Single();
        Assert.Null(build.Options.LogStreamId); // the dispatcher keeps it under build:<build id>, the deployment's Build log
        Assert.Empty(log);
    }
}
