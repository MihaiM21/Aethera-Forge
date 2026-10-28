using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Tests;

/// <summary>Release 0.1 hardening of the deploy engine: volume namespaces, graceful stop, safe strategy choice, pull policy, transient failures.</summary>
public class HardeningTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Records commands (with their payload) and answers from a script; an unscripted command fails.</summary>
    private sealed class Transport : IServerTransport
    {
        public List<object> Commands { get; } = [];
        public Func<object, object?> Responder { get; set; } = _ => null;
        public Func<object, Exception?>? Throw { get; set; }
        public bool Available { get; set; } = true;
        public TransportKind Kind => TransportKind.Agent;
        public TransportCapabilities Capabilities => TransportCapabilities.AllAgent;

        public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken ct) =>
            ValueTask.FromResult(Available ? TransportStatus.Up(Kind) : TransportStatus.Down(Kind, "agent offline"));

        public Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(Guid serverId, TCommand command, CommandOptions options, CancellationToken ct)
            where TCommand : IServerCommand<TResult>
        {
            Commands.Add(command);
            Names.Add(command.Name);
            if (Throw?.Invoke(command) is { } ex) throw ex;
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(Responder(command) is TResult r
                ? new CommandOutcome<TResult>(CommandStatus.Succeeded, r, CommandErrorCode.None, null, 0, now, now, false)
                : new CommandOutcome<TResult>(CommandStatus.Failed, default, CommandErrorCode.NotFound, "no such object", 1, now, now, false));
        }

        public IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, CancellationToken ct) => throw new NotSupportedException();

        public List<string> Names { get; } = [];
    }

    private static DockerContainer Container(string id, ContainerRunState state = ContainerRunState.Exited) =>
        new(id, id, "i", "i", state, state.ToString(), ContainerHealthState.None, null, null, null, 0, false, 0, new Dictionary<string, string>(), [], [], [], null);

    private static object? Happy(object command) => command switch
    {
        ImagePullCommand => new ImagePulled("sha256:img", "sha256:dig", 1, [], false),
        ImageInspectCommand i => new DockerImage("sha256:img", [i.Image], ["x@sha256:dig"], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0),
        NetworkCreateCommand n => new DockerNetwork("n1", n.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []),
        ContainerStopCommand s => Container(s.Container),
        ContainerRemoveCommand => Unit.Value,
        ContainerCreateCommand c => new ContainerCreated("c-new", c.Spec.Name, ContainerRunState.Running, []),
        HealthProbeCommand => new HealthProbeOutcome(true, 1, 200, TimeSpan.Zero, "ok", null),
        _ => null,
    };

    private static Deployment NewDeployment(string strategy = DeploymentStrategies.Recreate)
    {
        var clock = new FixedClock();
        var d = Deployment.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DeploymentTrigger.Manual, clock.UtcNow, strategy);
        d.Start("{}", clock.UtcNow);
        return d;
    }

    private static DeploymentPlan Plan(
        ContainerSpec? container = null, ImagePullPolicy policy = ImagePullPolicy.Always, string? previous = "c-old", TimeSpan? stop = null,
        ProbeTarget? health = null) =>
        new()
        {
            ServerId = Guid.NewGuid(), ImageReference = "postgres:17", PullPolicy = policy, Container = container ?? new ContainerSpec("ignored", "db"),
            PreviousContainer = previous, Networks = ["aethera-env"], StopTimeout = stop ?? TimeSpan.FromSeconds(30), Health = health,
        };

    private static DeploymentRunner Runner(Transport t) =>
        new(t, [new RecreateStrategy(), new LowDowntimeStrategy()], new FixedClock());

    private static Task Noop(Deployment d, CancellationToken ct) => Task.CompletedTask;

    // ---- volumes (6) ------------------------------------------------------------------------------------------------------------------

    private static readonly Guid WorkloadA = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-111111111111");
    private static readonly Guid WorkloadB = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-222222222222");

    private static DeploymentSnapshot SnapshotOf(Guid id, string slug, params VolumeEntry[] volumes) => new()
    {
        WorkloadId = id, EnvironmentId = Guid.NewGuid(), Slug = slug, SourceKind = ApplicationSourceKind.DockerImage,
        Image = new ImageSnapshot("nginx", "1", ImagePullPolicy.IfNotPresent, null), Volumes = [.. volumes],
    };

    private static PlanContext Context() => new()
    {
        ServerId = Guid.NewGuid(), DeploymentId = Guid.NewGuid(), DeploymentNumber = 1, ResolveSecret = (_, _) => "x", Proxy = new TraefikProxyProvider(),
    };

    [Fact]
    public void The_same_volume_name_in_two_workloads_is_two_docker_volumes()
    {
        var prod = DeploymentPlanBuilder.Build(SnapshotOf(WorkloadA, "website", new VolumeEntry("uploads", "/app/uploads", null, false)), Context());
        var dev = DeploymentPlanBuilder.Build(SnapshotOf(WorkloadB, "website", new VolumeEntry("uploads", "/app/uploads", null, false)), Context());

        Assert.Equal("website-11111111-uploads", prod.Container.Mounts!.Single().Source);
        Assert.Equal("website-22222222-uploads", dev.Container.Mounts!.Single().Source);
        Assert.NotEqual(prod.Container.Mounts!.Single().Source, dev.Container.Mounts!.Single().Source);
    }

    [Fact]
    public void A_name_already_in_the_workloads_namespace_is_not_prefixed_twice()
    {
        var plan = DeploymentPlanBuilder.Build(SnapshotOf(WorkloadA, "db", new VolumeEntry("db-11111111-data", "/var/lib/data", null, false)), Context());

        Assert.Equal("db-11111111-data", plan.Container.Mounts!.Single().Source);
    }

    [Fact]
    public void A_user_chosen_name_never_addresses_an_installation_volume()
    {
        // The API refuses these names; the engine namespaces them as well, so even a stored one cannot reach aethera_pgdata.
        var plan = DeploymentPlanBuilder.Build(SnapshotOf(WorkloadA, "web", new VolumeEntry("aethera_pgdata", "/x", null, false)), Context());

        Assert.Equal("web-11111111-aethera_pgdata", plan.Container.Mounts!.Single().Source);
    }

    [Theory]
    [InlineData("data", "web", VolumeNameKind.Local)]
    [InlineData("web-11111111-data", "web", VolumeNameKind.Own)]
    [InlineData("web-aaaaaaaa-data", "web", VolumeNameKind.Own)]
    [InlineData("other-22222222-data", "web", VolumeNameKind.Foreign)]
    [InlineData("aethera_pgdata", "web", VolumeNameKind.Reserved)]
    [InlineData("aethera_aethera_data", "web", VolumeNameKind.Reserved)]
    [InlineData("aethera-traefik-acme", "web", VolumeNameKind.Reserved)]
    [InlineData("AETHERA_x", "web", VolumeNameKind.Reserved)]
    [InlineData("aethera-11111111-data", "aethera", VolumeNameKind.Own)]
    public void Volume_names_are_classified(string name, string slug, VolumeNameKind expected) =>
        Assert.Equal(expected, VolumeNames.Classify(name, slug, WorkloadA));

    // ---- graceful stop (10) ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DeploymentStrategies.Recreate)]
    [InlineData(DeploymentStrategies.LowDowntime)]
    public async Task The_old_container_is_stopped_with_the_stop_timeout_before_it_is_removed(string strategy)
    {
        var t = new Transport { Responder = Happy };
        var d = NewDeployment(strategy);

        var result = await Runner(t).RunAsync(d, Plan(stop: TimeSpan.FromSeconds(60)), Noop, default);

        Assert.Equal(DeploymentRunResult.Running, result);
        var names = t.Names.ToList();
        var stop = names.IndexOf("container.stop");
        var remove = names.IndexOf("container.remove");
        Assert.True(stop >= 0 && remove > stop, string.Join(",", names));
        Assert.Equal(TimeSpan.FromSeconds(60), t.Commands.OfType<ContainerStopCommand>().Single().Timeout);
        Assert.Equal("c-old", t.Commands.OfType<ContainerStopCommand>().Single().Container);
        // Low-downtime stops it first, so the removal does not have to kill it; recreate stopped it before the new one started and removes the
        // stopped container (forced: it may have been restarted by its restart policy meanwhile).
        Assert.Equal(strategy == DeploymentStrategies.Recreate, t.Commands.OfType<ContainerRemoveCommand>().Single(r => r.Container == "c-old").Force);
    }

    [Fact]
    public async Task Low_downtime_keeps_the_order_new_healthy_then_old_stopped()
    {
        var t = new Transport { Responder = Happy };

        await Runner(t).RunAsync(NewDeployment(DeploymentStrategies.LowDowntime), Plan(health: new TcpProbeTarget("db", 5432)), Noop, default);

        var names = t.Names.ToList();
        Assert.True(names.IndexOf("container.create") < names.IndexOf("health.probe"));
        Assert.True(names.IndexOf("health.probe") < names.IndexOf("container.stop"));
        Assert.True(names.IndexOf("container.stop") < names.IndexOf("container.remove"));
    }

    [Fact]
    public async Task A_stop_that_fails_falls_back_to_a_forced_removal()
    {
        var t = new Transport { Responder = c => c is ContainerStopCommand ? null : Happy(c) };

        var result = await Runner(t).RunAsync(NewDeployment(), Plan(), Noop, default);

        Assert.Equal(DeploymentRunResult.Running, result);
        Assert.True(t.Commands.OfType<ContainerRemoveCommand>().Single().Force);
    }

    [Fact]
    public void Databases_get_a_longer_default_than_applications_in_the_plan()
    {
        var snapshot = SnapshotOf(WorkloadA, "pg") with { Runtime = new RuntimeSnapshot { StopTimeoutSeconds = 60 } };

        var plan = DeploymentPlanBuilder.Build(snapshot, Context());

        Assert.Equal(TimeSpan.FromSeconds(60), plan.StopTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.Container.StopTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), DeploymentPlanBuilder.Build(SnapshotOf(WorkloadA, "app"), Context()).StopTimeout);
    }

    // ---- strategy choice (14) ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Low_downtime_with_a_published_port_runs_recreate_and_says_why()
    {
        var spec = new ContainerSpec("i", "api") { Ports = [new PortMappingSpec(5000, 5000)] };
        var t = new Transport { Responder = Happy };
        var d = NewDeployment(DeploymentStrategies.LowDowntime);

        await Runner(t).RunAsync(d, Plan(spec), Noop, default);

        Assert.Equal(DeploymentStatus.Running, d.Status);
        // Recreate: the old one is stopped (freeing the port) before the new one starts, and removed once the new one is up.
        Assert.Equal("api-1", t.Commands.OfType<ContainerCreateCommand>().Single().Spec.Name);
        var names = t.Names.ToList();
        Assert.True(names.IndexOf("container.stop") < names.IndexOf("container.create"));
        Assert.True(names.IndexOf("container.create") < names.IndexOf("container.remove"));
        var details = d.Steps.Single(s => s.Step == DeploymentStep.Container).DetailsJson!;
        Assert.Contains("strategyNote", details);
        Assert.Contains("host port 5000", details);
    }

    [Fact]
    public void Low_downtime_with_a_writable_volume_falls_back_but_a_read_only_one_does_not()
    {
        var writable = new ContainerSpec("i", "db") { Mounts = [new VolumeMountSpec(MountKind.Volume, "db-data", "/var/lib/db")] };
        var readOnly = new ContainerSpec("i", "web") { Mounts = [new VolumeMountSpec(MountKind.Volume, "web-cfg", "/etc/cfg", ReadOnly: true)] };

        var (name, note) = DeploymentRunner.ChooseStrategy(DeploymentStrategies.LowDowntime, Plan(writable));
        Assert.Equal(DeploymentStrategies.Recreate, name);
        Assert.Contains("writable volume (/var/lib/db)", note);

        Assert.Equal((DeploymentStrategies.LowDowntime, null), DeploymentRunner.ChooseStrategy(DeploymentStrategies.LowDowntime, Plan(readOnly)));
        Assert.Equal((DeploymentStrategies.LowDowntime, null), DeploymentRunner.ChooseStrategy(DeploymentStrategies.LowDowntime, Plan()));
        Assert.Equal((DeploymentStrategies.Recreate, null), DeploymentRunner.ChooseStrategy(DeploymentStrategies.Recreate, Plan(writable)));
    }

    // ---- pull policy (19) --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Always_pulls_without_inspecting()
    {
        var t = new Transport { Responder = Happy };

        await Runner(t).RunAsync(NewDeployment(), Plan(policy: ImagePullPolicy.Always, previous: null), Noop, default);

        Assert.Contains("image.pull", t.Names);
        Assert.DoesNotContain("image.inspect", t.Names);
    }

    [Fact]
    public async Task IfNotPresent_uses_the_image_that_is_on_the_server()
    {
        var t = new Transport { Responder = Happy };
        var d = NewDeployment();

        await Runner(t).RunAsync(d, Plan(policy: ImagePullPolicy.IfNotPresent, previous: null), Noop, default);

        Assert.Contains("image.inspect", t.Names);
        Assert.DoesNotContain("image.pull", t.Names);
        Assert.Equal("sha256:dig", d.ImageDigest);
        Assert.Equal("postgres:17", d.ImageRef);
    }

    [Fact]
    public async Task IfNotPresent_pulls_an_image_the_server_does_not_have()
    {
        var t = new Transport { Responder = c => c is ImageInspectCommand ? null : Happy(c) };

        var result = await Runner(t).RunAsync(NewDeployment(), Plan(policy: ImagePullPolicy.IfNotPresent, previous: null), Noop, default);

        Assert.Equal(DeploymentRunResult.Running, result);
        Assert.Equal(["image.inspect", "image.pull"], t.Names.Take(2));
    }

    [Fact]
    public async Task Never_uses_a_present_image_and_fails_clearly_when_it_is_missing()
    {
        var present = new Transport { Responder = Happy };
        Assert.Equal(DeploymentRunResult.Running, await Runner(present).RunAsync(NewDeployment(), Plan(policy: ImagePullPolicy.Never, previous: null), Noop, default));
        Assert.DoesNotContain("image.pull", present.Names);

        var missing = new Transport { Responder = c => c is ImageInspectCommand ? null : Happy(c) };
        var d = NewDeployment();
        Assert.Equal(DeploymentRunResult.Failed, await Runner(missing).RunAsync(d, Plan(policy: ImagePullPolicy.Never, previous: null), Noop, default));
        Assert.Equal(FailureCodes.ImageMissing, d.FailureCode);
        Assert.Equal(DeploymentStep.Image, d.FailedStep);
        Assert.Contains("pull policy is Never", d.FailureReason);
        Assert.DoesNotContain("image.pull", missing.Names);
    }

    [Fact]
    public void The_builder_takes_the_pull_policy_from_the_snapshot()
    {
        var snapshot = SnapshotOf(WorkloadA, "web") with { Image = new ImageSnapshot("nginx", "1", ImagePullPolicy.Never, null) };

        Assert.Equal(ImagePullPolicy.Never, DeploymentPlanBuilder.Build(snapshot, Context()).PullPolicy);
    }

    // ---- transient failures (13) -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_transient_failure_before_the_last_attempt_leaves_the_deployment_resumable()
    {
        var t = new Transport { Responder = Happy, Available = false };
        var d = NewDeployment();
        var runner = Runner(t);

        var first = await runner.RunAsync(d, Plan(previous: null), Noop, default, finalAttempt: false);

        Assert.Equal(DeploymentRunResult.Retry, first);
        Assert.Equal(DeploymentStatus.InProgress, d.Status);
        Assert.Equal(FailureCodes.ServerUnavailable, runner.TransientFailure!.Code);

        // The next attempt runs the same deployment to the end.
        t.Available = true;
        var second = await runner.RunAsync(d, Plan(previous: null), Noop, default, finalAttempt: false);
        Assert.Equal(DeploymentRunResult.Running, second);
        Assert.Equal(DeploymentStatus.Running, d.Status);
    }

    [Fact]
    public async Task The_last_attempt_marks_a_transient_failure_failed()
    {
        var t = new Transport { Responder = Happy, Available = false };
        var d = NewDeployment();

        Assert.Equal(DeploymentRunResult.Failed, await Runner(t).RunAsync(d, Plan(previous: null), Noop, default, finalAttempt: true));
        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal(FailureCodes.ServerUnavailable, d.FailureCode);
    }

    [Fact]
    public async Task A_transport_error_in_the_middle_of_the_pipeline_is_retried_too_and_other_failures_are_not()
    {
        var unreachable = new Transport
        {
            Responder = Happy, Throw = c => c is ContainerCreateCommand ? new ServerTransportException(TransportErrors.AgentUnavailable, "agent gone") : null,
        };
        var d = NewDeployment();
        Assert.Equal(DeploymentRunResult.Retry, await Runner(unreachable).RunAsync(d, Plan(previous: null), Noop, default, finalAttempt: false));
        Assert.Equal(DeploymentStatus.InProgress, d.Status);

        var broken = new Transport { Responder = c => c is ContainerCreateCommand ? null : Happy(c) };
        var d2 = NewDeployment();
        Assert.Equal(DeploymentRunResult.Failed, await Runner(broken).RunAsync(d2, Plan(previous: null), Noop, default, finalAttempt: false));
        Assert.Equal(DeploymentStatus.Failed, d2.Status);
    }
}
