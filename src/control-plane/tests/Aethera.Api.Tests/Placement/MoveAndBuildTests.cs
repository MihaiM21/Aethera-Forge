using System.Net;
using System.Text.Json.Nodes;
using Aethera.Agent.V1;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Deployments;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Aethera.Api.Tests.Placement.PlacementTestSupport;

namespace Aethera.Api.Tests.Placement;

/// <summary>Moving a deployed workload and building on one server for another, end to end through the job system (ADR 0012).</summary>
[Collection(DeploymentsApiCollection.Name)]
public sealed class MoveAndBuildTests(DeploymentsFixture fixture)
{
    private FakeServerTransport Transport => fixture.Transport;

    private static DockerContainer Container(string id, ContainerRunState state = ContainerRunState.Running) =>
        new(id, id, "img", "sha", state, state.ToString(), ContainerHealthState.None, null, null, null, 0, false, 0,
            new Dictionary<string, string>(), [], [], [], null);

    private static BuildOutcome Built(BuildImageCommand c) =>
        new(c.Spec.BuildId, "sha256:img", "sha256:built", c.Spec.ImageTags, [], 4242, TimeSpan.FromSeconds(3), "dockerfile", "deadbeefcafe", c.Spec.Push, "linux/amd64", false);

    /// <summary>A healthy fleet: pulls and builds work, containers are created with sequential ids, nothing runs on the servers beforehand.</summary>
    private FakeServerTransport Healthy()
    {
        var n = 0;
        Transport.Commands.Clear();
        return Transport
            .On<ImagePullCommand, ImagePulled>(_ => new ImagePulled("sha256:img", "sha256:pulled", 1, [], false))
            .On<BuildImageCommand, BuildOutcome>(Built)
            .On<ImageInspectCommand, DockerImage>(c => new DockerImage("sha256:img", [c.Image], [], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0))
            .On<NetworkCreateCommand, DockerNetwork>(c => new DockerNetwork("n", c.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []))
            .On<ContainerCreateCommand, ContainerCreated>(c => new ContainerCreated($"cid-{Interlocked.Increment(ref n)}", c.Spec.Name, ContainerRunState.Running, []))
            .On<ContainerRemoveCommand, Unit>(_ => Unit.Value)
            .On<ContainerStopCommand, DockerContainer>(c => Container(c.Container, ContainerRunState.Exited))
            .On<ContainerStartCommand, DockerContainer>(c => Container(c.Container))
            .On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => [Container("old-on-a")])
            .On<ImageRemoveCommand, Unit>(_ => Unit.Value)
            .On<HealthProbeCommand, HealthProbeOutcome>(_ => new HealthProbeOutcome(true, 3, 0, TimeSpan.Zero, "running", null))
            .On<ProxyEnsureCommand, ProxyEnsured>(_ => new ProxyEnsured("traefik", "proxy-1", "v3.1", true, true, "abc"));
    }

    private async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    private Task<Deployment> LoadAsync(Guid id) => WithDbAsync(db => db.Deployments.AsNoTracking().Include(d => d.Steps).FirstAsync(d => d.Id == id));

    /// <summary>Waits for the deployment to end and for its job (and so the move's follow-up) to finish.</summary>
    private async Task<Deployment> FinishedAsync(string id)
    {
        var guid = Guid.Parse(id);
        await GatewayFixture.EventuallyAsync(async () => (await LoadAsync(guid)).Status is DeploymentStatus.Running or DeploymentStatus.Failed or DeploymentStatus.Cancelled, "the deployment to end", 20000);
        var jobId = (await LoadAsync(guid)).JobId;
        await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(db =>
            db.Jobs.AsNoTracking().AnyAsync(j => j.Id == jobId && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed))), "its job to end", 20000);
        return await LoadAsync(guid);
    }

    private async Task<Deployment> DeployAsync(Tenant t, string appId)
    {
        var response = await t.Developer.PostAsync($"/api/v1/applications/{appId}/deployments", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await FinishedAsync((await response.ReadAsync()).Id());
    }

    private static async Task<string> ServerOfAsync(Tenant t, string appId) =>
        (await t.Viewer.GetJsonAsync($"/api/v1/applications/{appId}"))["serverId"]!.GetValue<string>();

    // ---- moving ----------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Moving_a_running_application_deploys_on_the_target_then_removes_the_old_containers()
    {
        Healthy();
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("old");
        var b = await t.SeedServerAsync("new");
        var app = await t.CreateApplicationAsync(env, a.ToString());
        var first = await DeployAsync(t, app.Id());
        Assert.Equal(DeploymentStatus.Running, first.Status);
        Transport.Commands.Clear();

        var response = await t.Developer.PostAsync($"/api/v1/applications/{app.Id()}/move", new { serverId = b });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.ReadAsync();
        Assert.NotNull(body["jobId"]);
        var moved = await FinishedAsync(body["deploymentId"]!.GetValue<string>());
        Assert.Equal(DeploymentStatus.Running, moved.Status);
        Assert.Equal(b, moved.ServerId);
        Assert.Equal(b.ToString(), await ServerOfAsync(t, app.Id()));

        lock (Transport.Commands)
        {
            // The new deployment ran on the target and did not try to replace a container of the old server there.
            Assert.Contains(Transport.Commands, c => c.ServerId == b && c.Command is ImagePullCommand);
            Assert.Contains(Transport.Commands, c => c.ServerId == b && c.Command is ContainerCreateCommand);
            Assert.DoesNotContain(Transport.Commands, c => c.ServerId == b && c.Command is ContainerStopCommand);
        }

        // The follow-up: a teardown job removes what the workload left on the old server (and its image).
        await GatewayFixture.EventuallyAsync(async () =>
        {
            lock (Transport.Commands)
                return Transport.Commands.Any(c => c.ServerId == a && c.Command is ContainerRemoveCommand { Container: "old-on-a" })
                       && Transport.Commands.Any(c => c.ServerId == a && c.Command is ImageRemoveCommand);
        }, "the old server to be cleaned up", 20000);
        lock (Transport.Commands) Assert.DoesNotContain(Transport.Commands, c => c.ServerId == b && c.Command is ContainerRemoveCommand { Container: "old-on-a" });
    }

    [RequiresDatabaseFact]
    public async Task A_failed_move_leaves_the_application_on_the_old_server_which_is_not_touched()
    {
        Healthy();
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("old", publicIp: "203.0.113.10");
        var b = await t.SeedServerAsync("new", publicIp: "198.51.100.20");
        var app = await t.CreateApplicationAsync(env, a.ToString(), extra: new { autoDomain = true, runtime = new { ports = new[] { new { containerPort = 8080, isHttp = true } } } });
        Assert.Equal(DeploymentStatus.Running, (await DeployAsync(t, app.Id())).Status);
        var before = await WithDbAsync(db => db.Domains.AsNoTracking().SingleAsync(d => d.WorkloadId == Guid.Parse(app.Id())));
        Transport.Commands.Clear();
        Transport.Fail<ImagePullCommand, ImagePulled>(CommandErrorCode.ImagePullFailed, "manifest unknown");

        var body = await (await t.Developer.PostAsync($"/api/v1/applications/{app.Id()}/move", new { serverId = b })).ReadAsync();
        var failed = await FinishedAsync(body["deploymentId"]!.GetValue<string>());

        Assert.Equal(DeploymentStatus.Failed, failed.Status);
        await GatewayFixture.EventuallyAsync(async () => await ServerOfAsync(t, app.Id()) == a.ToString(), "the application to be back on the old server", 10000);
        var after = await WithDbAsync(db => db.Domains.AsNoTracking().SingleAsync(d => d.WorkloadId == Guid.Parse(app.Id())));
        Assert.Equal(a, after.ServerId);
        Assert.Equal(before.Hostname, after.Hostname); // renamed for the new server while moving, named for the old one again
        Assert.Equal("running", (await t.Viewer.GetJsonAsync($"/api/v1/applications/{app.Id()}"))["state"]!["status"]!.GetValue<string>());
        lock (Transport.Commands)
        {
            Assert.DoesNotContain(Transport.Commands, c => c.ServerId == a && c.Command is ContainerRemoveCommand or ContainerStopCommand);
            Assert.DoesNotContain(Transport.Commands, c => c.Command is ContainerListCommand);
        }
    }

    [RequiresDatabaseFact]
    public async Task A_move_waits_for_a_deployment_in_flight_to_finish()
    {
        Healthy();
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var a = await t.SeedServerAsync("old");
        var b = await t.SeedServerAsync("new");
        var app = await t.CreateApplicationAsync(env, a.ToString());
        await WithDbAsync(async db =>
        {
            var application = await db.Workloads.FirstAsync(w => w.Id == Guid.Parse(app.Id()));
            db.Deployments.Add(Deployment.Queue(application.Id, application.EnvironmentId, a, application.AllocateDeploymentNumber(), DeploymentTrigger.Manual, DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });

        await (await t.Developer.PostAsync($"/api/v1/applications/{app.Id()}/move", new { serverId = b })).AssertProblemAsync(409, "workload.deployment_in_progress");
    }

    // ---- build elsewhere -------------------------------------------------------------------------------------------------------------

    private async Task<(Tenant Tenant, string AppId, Guid Run, Guid Build, string RegistryId)> BuildElsewhereAppAsync(bool withRegistry = true)
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var run = await t.SeedServerAsync("runner", [ServerRole.Worker]);
        var build = await t.SeedServerAsync("builder", [ServerRole.Build]);
        var registry = await t.Admin.CreateAsync("/api/v1/registries", new { name = Tenant.Unique("ghcr"), url = "ghcr.io", username = "mihaim21", password = "ghp_s3cret-token", @namespace = "MihaiM21" });
        if (withRegistry) await t.Admin.PatchOkAsync($"/api/v1/servers/{build}", new { buildRegistryId = registry.Id() });
        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("api"), environmentId = env, serverId = run, buildServerId = build, sourceKind = "dockerfile", autoDomain = false,
            gitSource = new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" },
            build = new { engine = "dockerfile", context = ".", dockerfilePath = "Dockerfile" },
        });
        return (t, app.Id(), run, build, registry.Id());
    }

    [RequiresDatabaseFact]
    public async Task A_build_elsewhere_builds_and_pushes_on_the_build_server_and_the_run_server_pulls()
    {
        Healthy();
        var (t, appId, run, build, _) = await BuildElsewhereAppAsync();

        var d = await DeployAsync(t, appId);

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Equal(build, d.BuildServerId);
        Assert.Matches($@"^ghcr\.io/mihaim21/api-[0-9a-f]+:{d.Id}$", d.ImageRef);
        List<RecordedCommand> commands;
        lock (Transport.Commands) commands = [.. Transport.Commands];
        var buildCommand = Assert.Single(commands, c => c.Command is BuildImageCommand);
        Assert.Equal(build, buildCommand.ServerId);
        var spec = ((BuildImageCommand)buildCommand.Command).Spec;
        Assert.True(spec.Push);
        Assert.Equal([d.ImageRef!], spec.ImageTags);
        Assert.Equal("ghcr.io", spec.PushAuth!.Server);
        Assert.Equal("mihaim21", spec.PushAuth.Username);
        Assert.Equal("ghp_s3cret-token", spec.PushAuth.Password!.Value);
        var pull = Assert.Single(commands, c => c.Command is ImagePullCommand);
        Assert.Equal(run, pull.ServerId);
        Assert.Equal(d.ImageRef, ((ImagePullCommand)pull.Command).Reference);
        Assert.Equal("ghp_s3cret-token", ((ImagePullCommand)pull.Command).Auth!.Password!.Value);
        Assert.DoesNotContain(commands, c => c.ServerId == build && c.Command is ContainerCreateCommand);
        Assert.DoesNotContain(commands, c => c.ServerId == run && c.Command is BuildImageCommand);

        var logs = await WithDbAsync(db => db.LogChunks.AsNoTracking().Where(l => l.StreamId == "job:" + d.JobId).Select(l => l.Data).ToListAsync());
        Assert.Contains(logs, l => l.Contains("builder") && l.Contains("runner"));
        Assert.DoesNotContain(logs, l => l.Contains("ghp_s3cret-token"));
    }

    [RequiresDatabaseFact]
    public async Task Without_a_build_registry_the_deployment_fails_before_anything_is_built()
    {
        Healthy();
        var (t, appId, _, _, _) = await BuildElsewhereAppAsync(withRegistry: false);

        var d = await DeployAsync(t, appId);

        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal("build.registry_missing", d.FailureCode);
        Assert.Equal(DeploymentStep.Build, d.FailedStep);
        Assert.Contains("registry", d.FailureReason);
        lock (Transport.Commands) Assert.DoesNotContain(Transport.Commands, c => c.Command is BuildImageCommand or ImagePullCommand);
    }

    [RequiresDatabaseFact]
    public async Task A_rollback_of_a_build_elsewhere_runs_the_stored_reference_without_building()
    {
        Healthy();
        var (t, appId, run, _, _) = await BuildElsewhereAppAsync();
        var first = await DeployAsync(t, appId);
        await DeployAsync(t, appId);
        Transport.Commands.Clear();
        // The run server pruned the image meanwhile.
        Transport.Fail<ImageInspectCommand, DockerImage>(CommandErrorCode.NotFound, "no such image");

        var response = await t.Developer.PostAsync($"/api/v1/deployments/{first.Id}/rollback", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var rollback = await FinishedAsync((await response.ReadAsync()).Id());

        Assert.Equal(DeploymentStatus.Running, rollback.Status);
        Assert.Equal(first.ImageRef, rollback.ImageRef);
        lock (Transport.Commands)
        {
            Assert.DoesNotContain(Transport.Commands, c => c.Command is BuildImageCommand);
            var pull = Assert.Single(Transport.Commands, c => c.Command is ImagePullCommand);
            Assert.Equal(run, pull.ServerId);
            Assert.Equal(first.ImageRef, ((ImagePullCommand)pull.Command).Reference);
            Assert.Equal("ghp_s3cret-token", ((ImagePullCommand)pull.Command).Auth!.Password!.Value); // the registry is found by the reference's host
        }
    }

    [RequiresDatabaseFact]
    public async Task Images_built_elsewhere_are_cleaned_up_on_both_servers_beyond_the_rollback_points()
    {
        Healthy();
        var (t, appId, run, build, _) = await BuildElsewhereAppAsync();
        var deployments = new List<Deployment>();
        for (var i = 0; i < DeployJobHandler.KeepRollbackPoints + 2; i++) deployments.Add(await DeployAsync(t, appId));

        await GatewayFixture.EventuallyAsync(async () =>
        {
            lock (Transport.Commands)
                return Transport.Commands.Any(c => c.ServerId == build && c.Command is ImageRemoveCommand r && r.Image == deployments[0].ImageRef)
                       && Transport.Commands.Any(c => c.ServerId == run && c.Command is ImageRemoveCommand r && r.Image == deployments[0].ImageRef);
        }, "the oldest image to be removed from both servers", 20000);
        lock (Transport.Commands)
            Assert.DoesNotContain(Transport.Commands, c => c.Command is ImageRemoveCommand r && r.Image == deployments[^1].ImageRef); // the live one stays
    }

    // ---- build quota and slots -------------------------------------------------------------------------------------------------------

    private async Task<(Tenant Tenant, string AppId, Guid Server)> QuotaAppAsync()
    {
        var t = await fixture.NewTenantAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var server = await t.SeedServerAsync("solo");
        await t.Admin.PatchOkAsync($"/api/v1/servers/{server}", new { buildCpuLimit = 2, buildMemoryLimitBytes = 2 * Gib });
        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("api"), environmentId = env, serverId = server, sourceKind = "dockerfile", autoDomain = false,
            gitSource = new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" },
        });
        return (t, app.Id(), server);
    }

    [RequiresDatabaseFact]
    public async Task The_build_quota_is_not_sent_to_an_agent_that_cannot_enforce_it_and_the_log_says_so()
    {
        Healthy();
        var (t, appId, _) = await QuotaAppAsync();

        var d = await DeployAsync(t, appId);

        Assert.Equal(DeploymentStatus.Running, d.Status);
        lock (Transport.Commands) Assert.Null(((BuildImageCommand)Assert.Single(Transport.Commands, c => c.Command is BuildImageCommand).Command).Spec.BuilderLimits);
        var logs = await WithDbAsync(db => db.LogChunks.AsNoTracking().Where(l => l.StreamId == "job:" + d.JobId).Select(l => l.Data).ToListAsync());
        Assert.Contains(logs, l => l.Contains("build quota") && l.Contains("does not enforce"));
    }

    [RequiresDatabaseFact]
    public async Task The_build_quota_reaches_an_agent_that_advertises_build_quota()
    {
        Healthy();
        var (t, appId, server) = await QuotaAppAsync();
        var registry = fixture.Factory.Services.GetRequiredService<AgentSessionRegistry>();
        var hello = new Hello { AgentVersion = "0.2.0" };
        hello.Capabilities.Add("build.quota");
        var session = new AgentSession(server, new AgentIdentity(server, "01", "fp", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)), hello, TimeProvider.System);
        registry.State(server).Attach(session);
        try
        {
            var d = await DeployAsync(t, appId);

            Assert.Equal(DeploymentStatus.Running, d.Status);
            ResourceLimitsSpec? limits;
            lock (Transport.Commands) limits = ((BuildImageCommand)Assert.Single(Transport.Commands, c => c.Command is BuildImageCommand).Command).Spec.BuilderLimits;
            Assert.Equal(new ResourceLimitsSpec(CpuLimitCores: 2, MemoryLimitBytes: 2 * Gib), limits);
        }
        finally
        {
            registry.State(server).Detach(session);
        }
    }

    [RequiresDatabaseFact]
    public async Task A_server_builds_no_more_images_at_once_than_its_limit()
    {
        var gate = fixture.Factory.Services.GetRequiredService<BuildSlotGate>();
        gate.PollInterval = TimeSpan.FromMilliseconds(40);
        var server = Guid.NewGuid();
        var waiting = 0;

        var first = await gate.AcquireAsync(server, 1, null, default);
        var secondTask = gate.AcquireAsync(server, 1, _ => { Interlocked.Increment(ref waiting); return ValueTask.CompletedTask; }, default);
        await GatewayFixture.EventuallyAsync(() => Task.FromResult(Volatile.Read(ref waiting) == 1), "the second build to wait");
        Assert.False(secondTask.IsCompleted);

        // Another server has its own slots.
        await using (await gate.AcquireAsync(Guid.NewGuid(), 1, null, default)) { }

        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        await second.DisposeAsync();
        Assert.Equal(1, waiting); // announced once

        // Two slots let two builds run together.
        await using var one = await gate.AcquireAsync(server, 2, null, default);
        await using var two = await gate.AcquireAsync(server, 2, null, default);
    }
}
