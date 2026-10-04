using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;

namespace Aethera.Engine.Tests;

public class DeploymentRunnerTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Records commands; a responder returns the result, or null to fail the command.</summary>
    private sealed class FakeTransport(Func<object, object?> responder, bool available = true) : IServerTransport
    {
        public List<string> Commands { get; } = [];
        public TransportKind Kind => TransportKind.Agent;
        public TransportCapabilities Capabilities => TransportCapabilities.AllAgent;

        public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken ct) =>
            ValueTask.FromResult(available ? TransportStatus.Up(Kind) : TransportStatus.Down(Kind, "agent offline"));

        public Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            Guid serverId, TCommand command, CommandOptions options, CancellationToken ct)
            where TCommand : IServerCommand<TResult>
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command.Name);
            var result = responder(command);
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(result is TResult r
                ? new CommandOutcome<TResult>(CommandStatus.Succeeded, r, CommandErrorCode.None, null, 0, now, now, false)
                : new CommandOutcome<TResult>(CommandStatus.Failed, default, CommandErrorCode.BuildFailed, "boom", 1, now, now, false));
        }

        public IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static object? Happy(object command) => command switch
    {
        BuildImageCommand b => new BuildOutcome(b.Spec.BuildId, "sha256:img", "sha256:dig", b.Spec.ImageTags, [], 1, TimeSpan.Zero, "dockerfile", "abc123", false, "linux/amd64", false),
        ImagePullCommand => new ImagePulled("sha256:img", "sha256:dig", 1, [], false),
        NetworkCreateCommand n => new DockerNetwork("n1", n.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []),
        ContainerRemoveCommand => Unit.Value,
        ContainerCreateCommand => new ContainerCreated("c-new", "app", ContainerRunState.Running, []),
        HealthProbeCommand => new HealthProbeOutcome(true, 1, 200, TimeSpan.Zero, "ok", null),
        _ => null,
    };

    private static (Deployment, DeploymentPlan) Setup(bool build, ProbeTarget? health = null, string? previous = null)
    {
        var clock = new FixedClock();
        var d = Deployment.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DeploymentTrigger.Manual, clock.UtcNow);
        d.Start("{}", clock.UtcNow);
        var plan = new DeploymentPlan
        {
            ServerId = d.ServerId,
            Build = build ? new BuildSpec("b1", BuildEngineKind.Dockerfile, ["aethera/app:1"]) : null,
            ImageReference = build ? null : "nginx:latest",
            Container = new ContainerSpec("ignored", "app"),
            Health = health,
            PreviousContainer = previous,
            Networks = ["aethera-proj"],
        };
        return (d, plan);
    }

    private static DeploymentRunner Runner(FakeTransport t) => new(t, [new RecreateStrategy()], new FixedClock());

    private static Task Noop(Deployment d, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public async Task Image_source_runs_to_Running_and_skips_build()
    {
        var (d, plan) = Setup(build: false, new TcpProbeTarget("h", 80), previous: "c-old");
        var t = new FakeTransport(Happy);

        var result = await Runner(t).RunAsync(d, plan, Noop, default);

        Assert.Equal(DeploymentRunResult.Running, result);
        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.True(d.IsRollbackPoint);
        Assert.Equal("nginx:latest", d.ImageRef);
        Assert.Equal(["c-new"], d.ContainerIds);
        Assert.Equal(["image.pull", "network.create", "container.remove", "container.create", "health.probe"], t.Commands);
        Assert.Equal(StepStatus.Skipped, d.Steps.Single(s => s.Step == DeploymentStep.Build).Status);
    }

    [Fact]
    public async Task Source_build_tags_image_and_records_commit()
    {
        var (d, plan) = Setup(build: true);
        var t = new FakeTransport(Happy);

        Assert.Equal(DeploymentRunResult.Running, await Runner(t).RunAsync(d, plan, Noop, default));

        Assert.Equal("aethera/app:1", d.ImageRef);
        Assert.Equal("abc123", d.CommitSha);
        Assert.Equal(StepStatus.Skipped, d.Steps.Single(s => s.Step == DeploymentStep.HealthCheck).Status);
    }

    [Fact]
    public async Task Failed_build_fails_the_Build_step_and_stops()
    {
        var (d, plan) = Setup(build: true);
        var t = new FakeTransport(c => c is BuildImageCommand ? null : Happy(c));

        Assert.Equal(DeploymentRunResult.Failed, await Runner(t).RunAsync(d, plan, Noop, default));

        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal(DeploymentStep.Build, d.FailedStep);
        Assert.Equal(FailureCodes.BuildFailed, d.FailureCode);
        Assert.DoesNotContain("container.create", t.Commands);
    }

    [Fact]
    public async Task Unhealthy_probe_fails_the_HealthCheck_step()
    {
        var (d, plan) = Setup(build: false, new TcpProbeTarget("h", 80));
        var t = new FakeTransport(c => c is HealthProbeCommand ? new HealthProbeOutcome(false, 15, 0, TimeSpan.Zero, "refused", null) : Happy(c));

        Assert.Equal(DeploymentRunResult.Failed, await Runner(t).RunAsync(d, plan, Noop, default));

        Assert.Equal(DeploymentStep.HealthCheck, d.FailedStep);
        Assert.Equal(FailureCodes.HealthTimeout, d.FailureCode);
        Assert.False(d.IsRollbackPoint);
    }

    [Fact]
    public async Task Unavailable_server_fails_the_TargetServer_step()
    {
        var (d, plan) = Setup(build: false);
        var t = new FakeTransport(Happy, available: false);

        Assert.Equal(DeploymentRunResult.Failed, await Runner(t).RunAsync(d, plan, Noop, default));

        Assert.Equal(DeploymentStep.TargetServer, d.FailedStep);
        Assert.Equal(FailureCodes.ServerUnavailable, d.FailureCode);
    }

    [Fact]
    public async Task Cancellation_marks_the_deployment_cancelled()
    {
        var (d, plan) = Setup(build: false);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Equal(DeploymentRunResult.Cancelled, await Runner(new FakeTransport(Happy)).RunAsync(d, plan, Noop, cts.Token));
        Assert.Equal(DeploymentStatus.Cancelled, d.Status);
    }

    [Fact]
    public async Task Unknown_strategy_fails_the_deployment()
    {
        var clock = new FixedClock();
        var d = Deployment.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DeploymentTrigger.Manual, clock.UtcNow, strategy: "nope");
        d.Start("{}", clock.UtcNow);
        var (_, plan) = Setup(build: false);

        Assert.Equal(DeploymentRunResult.Failed, await Runner(new FakeTransport(Happy)).RunAsync(d, plan, Noop, default));
        Assert.Equal(FailureCodes.UnknownStrategy, d.FailureCode);
    }
}
