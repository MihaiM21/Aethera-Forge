using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Tests;

/// <summary>Build elsewhere, the build quota and build slots in the plan and the pipeline (ADR 0012).</summary>
public class BuildElsewhereTests
{
    private static readonly Guid RunServer = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid BuildServer = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Did = Guid.Parse("0190f3c2-0000-7000-8000-000000000009");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    }

    private static DeploymentSnapshot Snapshot(Guid? buildServer, string engine = BuildEngines.Dockerfile) => new()
    {
        WorkloadId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), Slug = "api", SourceKind = ApplicationSourceKind.Git,
        Git = new GitSnapshot("https://github.com/acme/api.git", "main", null, null),
        Build = new BuildSnapshot(engine, ".", null, null, null, null, null, true, null, null, buildServer),
        Ports = [new(8080, PortProtocol.Tcp, null, true)],
    };

    private static readonly RegistryCredentials Credentials = new("ghcr.io", "mihaim21", new SecretValue("token-value"));

    private static PlanContext Context(
        string? prefix = "ghcr.io/mihaim21", string? existing = null, bool remote = false, ResourceLimitsSpec? limits = null,
        Func<CancellationToken, Task<IAsyncDisposable>>? slot = null) => new()
    {
        ServerId = RunServer, DeploymentId = Did, DeploymentNumber = 1, ResolveSecret = (_, _) => throw new InvalidOperationException("no secrets"),
        Proxy = new TraefikProxyProvider(), BuildServerName = "builder", RunServerName = "runner", BuildRegistryPrefix = prefix,
        BuildRegistry = prefix is null ? null : Credentials, Registry = prefix is null ? null : Credentials, BuilderLimits = limits,
        ExistingImage = existing, ExistingImageIsRemote = remote, AcquireBuildSlot = slot,
    };

    // ---- the plan --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_build_on_another_server_pushes_to_the_registry_and_the_run_server_pulls_with_the_same_credentials()
    {
        var plan = DeploymentPlanBuilder.Build(Snapshot(BuildServer), Context());

        Assert.Equal(BuildServer, plan.BuildServerId);
        Assert.Equal("builder", plan.BuildServerName);
        Assert.Equal("runner", plan.RunServerName);
        var build = plan.Build!;
        Assert.Equal([$"ghcr.io/mihaim21/api:{Did}"], build.ImageTags);
        Assert.True(build.Push);
        Assert.Same(Credentials, build.PushAuth);
        Assert.Same(Credentials, plan.PullAuth);
    }

    [Fact]
    public void A_build_on_the_run_server_stays_local()
    {
        foreach (var snapshot in new[] { Snapshot(null), Snapshot(RunServer) })
        {
            var plan = DeploymentPlanBuilder.Build(snapshot, Context());
            Assert.Null(plan.BuildServerId);
            Assert.Equal([$"aethera/api:{Did}"], plan.Build!.ImageTags);
            Assert.False(plan.Build.Push);
            Assert.Null(plan.Build.PushAuth);
            Assert.Null(plan.PullAuth);
        }
    }

    [Fact]
    public void A_missing_build_registry_fails_the_Build_step_before_anything_is_built()
    {
        var failure = Assert.Throws<DeploymentFailure>(() => DeploymentPlanBuilder.Build(Snapshot(BuildServer), Context(prefix: null)));
        Assert.Equal(DeploymentStep.Build, failure.Step);
        Assert.Equal(FailureCodes.BuildRegistryMissing, failure.Code);
        Assert.Contains("builder", failure.Message);
        Assert.Contains("runner", failure.Message);
        Assert.DoesNotContain("token-value", failure.Message);
    }

    [Fact]
    public void A_build_registry_is_not_needed_when_nothing_is_built_elsewhere()
    {
        Assert.NotNull(DeploymentPlanBuilder.Build(Snapshot(RunServer), Context(prefix: null)).Build);
        var image = Snapshot(BuildServer) with { Git = null, Build = null, Image = new ImageSnapshot("nginx", "1", ImagePullPolicy.Always, null), SourceKind = ApplicationSourceKind.DockerImage };
        var plan = DeploymentPlanBuilder.Build(image, Context(prefix: null));
        Assert.Null(plan.Build);
        Assert.Null(plan.BuildServerId);
    }

    [Fact]
    public void The_build_quota_reaches_the_build_except_for_nixpacks()
    {
        var limits = new ResourceLimitsSpec(CpuLimitCores: 2, MemoryLimitBytes: 2L << 30);
        Assert.Equal(limits, DeploymentPlanBuilder.Build(Snapshot(null), Context(limits: limits)).Build!.BuilderLimits);
        Assert.Equal(limits, DeploymentPlanBuilder.Build(Snapshot(null, BuildEngines.Static), Context(limits: limits)).Build!.BuilderLimits);
        Assert.Null(DeploymentPlanBuilder.Build(Snapshot(null, BuildEngines.Nixpacks), Context(limits: limits)).Build!.BuilderLimits);
        Assert.Null(DeploymentPlanBuilder.Build(Snapshot(null), Context()).Build!.BuilderLimits);
    }

    [Fact]
    public void A_rollback_to_an_image_from_a_registry_pulls_it_when_missing_instead_of_expecting_it_locally()
    {
        var reference = $"ghcr.io/mihaim21/api:{Guid.NewGuid()}";
        var remote = DeploymentPlanBuilder.Build(Snapshot(BuildServer), Context(existing: reference, remote: true));
        Assert.Null(remote.Build);
        Assert.Null(remote.BuildServerId);
        Assert.False(remote.LocalImage);
        Assert.Equal(reference, remote.ImageReference);
        Assert.Equal(ImagePullPolicy.IfNotPresent, remote.PullPolicy);
        Assert.Same(Credentials, remote.PullAuth);

        var local = DeploymentPlanBuilder.Build(Snapshot(null), Context(existing: "aethera/api:old"));
        Assert.True(local.LocalImage);
        Assert.Null(local.PullAuth);
    }

    [Fact]
    public void The_registry_tag_has_the_documented_shape()
    {
        Assert.Equal($"ghcr.io/mihaim21/api:{Did}", ImagePolicy.RegistryTag("ghcr.io/mihaim21/", "api", Did));
        Assert.Equal($"registry.example.com:5000/team/api:{Did}", ImagePolicy.RegistryTag("registry.example.com:5000/team", "api", Did));
    }

    // ---- the pipeline ----------------------------------------------------------------------------------------------------------------

    private sealed class RecordingTransport(Func<object, object?> responder, List<string>? events = null) : IServerTransport
    {
        public List<(Guid Server, string Command)> Commands { get; } = [];
        public TransportKind Kind => TransportKind.Agent;
        public TransportCapabilities Capabilities => TransportCapabilities.AllAgent;

        public ValueTask<TransportStatus> GetStatusAsync(Guid serverId, CancellationToken ct) => ValueTask.FromResult(TransportStatus.Up(Kind));

        public Task<CommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(Guid serverId, TCommand command, CommandOptions options, CancellationToken ct)
            where TCommand : IServerCommand<TResult>
        {
            Commands.Add((serverId, command.Name));
            events?.Add(command.Name);
            var result = responder(command);
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(result is TResult r
                ? new CommandOutcome<TResult>(CommandStatus.Succeeded, r, CommandErrorCode.None, null, 0, now, now, false)
                : new CommandOutcome<TResult>(CommandStatus.Failed, default, CommandErrorCode.ImagePullFailed, "boom", 1, now, now, false));
        }

        public IAsyncEnumerable<LogEntry> StreamLogsAsync(Guid serverId, LogStreamRequest request, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<ServerEvent> SubscribeEventsAsync(Guid serverId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static object? Happy(object command) => command switch
    {
        BuildImageCommand b => new BuildOutcome(b.Spec.BuildId, "sha256:img", "sha256:built", b.Spec.ImageTags, [], 1, TimeSpan.Zero, "dockerfile", "abc123", b.Spec.Push, "linux/amd64", false),
        ImagePullCommand => new ImagePulled("sha256:img", "sha256:pulled", 1, [], false),
        NetworkCreateCommand n => new DockerNetwork("n1", n.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []),
        ContainerCreateCommand => new ContainerCreated("c-new", "api", ContainerRunState.Running, []),
        ImageInspectCommand i => new DockerImage("sha256:img", [i.Image], [], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0),
        ContainerRemoveCommand => Unit.Value,
        HealthProbeCommand => new HealthProbeOutcome(true, 1, 200, TimeSpan.Zero, "ok", null),
        _ => null,
    };

    private static (Deployment Deployment, DeploymentPlan Plan, List<string> Log) Run(DeploymentSnapshot snapshot, PlanContext context)
    {
        var clock = new FixedClock();
        var d = Deployment.Queue(Guid.NewGuid(), snapshot.EnvironmentId, RunServer, 1, DeploymentTrigger.Manual, clock.UtcNow);
        d.Start("{}", clock.UtcNow);
        var log = new List<string>();
        var plan = DeploymentPlanBuilder.Build(snapshot, new PlanContext
        {
            ServerId = context.ServerId, DeploymentId = context.DeploymentId, DeploymentNumber = 1, ResolveSecret = context.ResolveSecret, Proxy = context.Proxy,
            BuildServerName = context.BuildServerName, RunServerName = context.RunServerName, BuildRegistryPrefix = context.BuildRegistryPrefix, BuildRegistry = context.BuildRegistry,
            Registry = context.Registry, BuilderLimits = context.BuilderLimits, ExistingImage = context.ExistingImage, ExistingImageIsRemote = context.ExistingImageIsRemote, AcquireBuildSlot = context.AcquireBuildSlot,
            Narrate = (line, _) => { log.Add(line); return ValueTask.CompletedTask; },
        });
        return (d, plan, log);
    }

    private static Task Noop(Deployment d, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public async Task A_build_elsewhere_builds_on_the_build_server_and_the_run_server_pulls_the_pushed_image()
    {
        var (d, plan, log) = Run(Snapshot(BuildServer), Context());
        var transport = new RecordingTransport(Happy);

        var result = await new DeploymentRunner(transport, [new RecreateStrategy()], new FixedClock()).RunAsync(d, plan, Noop, default);

        Assert.Equal(DeploymentRunResult.Running, result);
        var build = transport.Commands.Single(c => c.Command == "build.run");
        var pull = transport.Commands.Single(c => c.Command == "image.pull");
        Assert.Equal(BuildServer, build.Server);
        Assert.Equal(RunServer, pull.Server);
        Assert.True(transport.Commands.IndexOf(build) < transport.Commands.IndexOf(pull));
        Assert.DoesNotContain(transport.Commands, c => c.Command == "container.create" && c.Server == BuildServer);

        var reference = $"ghcr.io/mihaim21/api:{Did}";
        Assert.Equal(reference, d.ImageRef);
        Assert.Equal(BuildServer, d.BuildServerId);
        Assert.Equal("sha256:pulled", d.ImageDigest);
        Assert.Equal("abc123", d.CommitSha);
        Assert.Contains(log, l => l.Contains("builder") && l.Contains("runner") && l.Contains(reference));
        Assert.DoesNotContain(log, l => l.Contains("token-value"));
        Assert.Equal(StepStatus.Succeeded, d.Steps.Single(s => s.Step == DeploymentStep.Image).Status);
    }

    [Fact]
    public async Task A_local_build_runs_on_the_run_server_and_records_no_build_server()
    {
        var (d, plan, _) = Run(Snapshot(null), Context());
        var transport = new RecordingTransport(Happy);

        Assert.Equal(DeploymentRunResult.Running, await new DeploymentRunner(transport, [new RecreateStrategy()], new FixedClock()).RunAsync(d, plan, Noop, default));

        Assert.All(transport.Commands, c => Assert.Equal(RunServer, c.Server));
        Assert.DoesNotContain(transport.Commands, c => c.Command == "image.pull");
        Assert.Null(d.BuildServerId);
        Assert.Equal($"aethera/api:{Did}", d.ImageRef);
    }

    [Fact]
    public async Task A_failed_build_elsewhere_fails_the_Build_step_and_never_touches_the_run_server()
    {
        var (d, plan, _) = Run(Snapshot(BuildServer), Context());
        var transport = new RecordingTransport(c => c is BuildImageCommand ? null : Happy(c));

        Assert.Equal(DeploymentRunResult.Failed, await new DeploymentRunner(transport, [new RecreateStrategy()], new FixedClock()).RunAsync(d, plan, Noop, default));

        Assert.Equal(DeploymentStep.Build, d.FailedStep);
        Assert.Equal(FailureCodes.BuildFailed, d.FailureCode);
        Assert.All(transport.Commands, c => Assert.Equal(BuildServer, c.Server));
    }

    [Fact]
    public async Task A_failed_pull_on_the_run_server_fails_the_Image_step_with_the_pull_code()
    {
        var (d, plan, _) = Run(Snapshot(BuildServer), Context());
        var transport = new RecordingTransport(c => c is ImagePullCommand ? null : Happy(c));

        Assert.Equal(DeploymentRunResult.Failed, await new DeploymentRunner(transport, [new RecreateStrategy()], new FixedClock()).RunAsync(d, plan, Noop, default));

        Assert.Equal(DeploymentStep.Image, d.FailedStep);
        Assert.Equal(FailureCodes.ImagePullFailed, d.FailureCode);
        Assert.Equal(BuildServer, d.BuildServerId); // where it was built is still on record
    }

    [Fact]
    public async Task The_build_slot_is_held_for_the_build_only()
    {
        var events = new List<string>();
        var slot = new SlotProbe(events);
        var (d, plan, _) = Run(Snapshot(BuildServer), Context(slot: _ => { events.Add("acquired"); return Task.FromResult<IAsyncDisposable>(slot); }));
        var transport = new RecordingTransport(Happy, events);

        await new DeploymentRunner(transport, [new RecreateStrategy()], new FixedClock()).RunAsync(d, plan, Noop, default);

        var acquired = events.IndexOf("acquired");
        var released = events.IndexOf("released");
        Assert.True(acquired >= 0 && released > acquired);
        Assert.True(events.IndexOf("build.run") > acquired && events.IndexOf("build.run") < released);
        Assert.True(events.IndexOf("image.pull") > released); // the pull on the run server happens after the slot is free
    }

    [Fact]
    public async Task The_build_slot_is_released_when_the_build_fails()
    {
        var events = new List<string>();
        var (d, plan, _) = Run(Snapshot(null), Context(slot: _ => Task.FromResult<IAsyncDisposable>(new SlotProbe(events))));
        var transport = new RecordingTransport(c => c is BuildImageCommand ? null : Happy(c));

        await new DeploymentRunner(transport, [new RecreateStrategy()], new FixedClock()).RunAsync(d, plan, Noop, default);

        Assert.Equal(["released"], events);
    }

    [Fact]
    public async Task A_rollback_to_a_registry_image_pulls_it_when_the_run_server_pruned_it()
    {
        var reference = $"ghcr.io/mihaim21/api:{Guid.NewGuid()}";
        var (d, plan, _) = Run(Snapshot(BuildServer), Context(existing: reference, remote: true));
        var transport = new RecordingTransport(c => c is ImageInspectCommand ? null : Happy(c));

        var result = await new DeploymentRunner(transport, [new RecreateStrategy()], new FixedClock()).RunAsync(d, plan, Noop, default);

        Assert.Equal(DeploymentRunResult.Running, result);
        Assert.Contains(transport.Commands, c => c.Command == "image.pull" && c.Server == RunServer);
        Assert.DoesNotContain(transport.Commands, c => c.Command == "build.run");
        Assert.Equal(reference, d.ImageRef);
    }

    [Fact]
    public async Task A_rollback_to_a_registry_image_the_server_still_has_does_not_pull()
    {
        var reference = $"ghcr.io/mihaim21/api:{Guid.NewGuid()}";
        var (d, plan, _) = Run(Snapshot(BuildServer), Context(existing: reference, remote: true));
        var transport = new RecordingTransport(Happy);

        Assert.Equal(DeploymentRunResult.Running, await new DeploymentRunner(transport, [new RecreateStrategy()], new FixedClock()).RunAsync(d, plan, Noop, default));

        Assert.DoesNotContain(transport.Commands, c => c.Command == "image.pull");
    }

    private sealed class SlotProbe(List<string> events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            events.Add("released");
            return ValueTask.CompletedTask;
        }
    }
}
