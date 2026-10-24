using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Tests.Deployments;

/// <summary>Release 0.1 hardening, end to end: volume namespaces and permissions, teardown on delete, graceful stop, failures, retries, cancel, webhooks, pull policy.</summary>
public sealed partial class DeploymentApiTests
{
    private IEnumerable<T> Received<T>() where T : class
    {
        lock (Transport.Commands) return Transport.Commands.Select(c => c.Command).OfType<T>().ToList();
    }

    private static string Id8(string id) => id.Replace("-", "")[^8..];

    private static async Task<HttpResponseMessage> AddVolumeAsync(HttpClient client, string appId, string? name, string path) =>
        await client.PostAsync($"/api/v1/applications/{appId}/volumes", new { name, mountPath = path });

    // ---- volumes (6) ------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task The_same_volume_name_of_two_applications_is_two_docker_volumes()
    {
        Healthy();
        var (t1, app1) = await ImageAppAsync();
        var (t2, app2) = await ImageAppAsync();
        Assert.True((await AddVolumeAsync(t1.Developer, app1, "uploads", "/app/uploads")).IsSuccessStatusCode);
        Assert.True((await AddVolumeAsync(t2.Developer, app2, "uploads", "/app/uploads")).IsSuccessStatusCode);

        await WaitForAsync((await DeployAsync(t1, app1)).Id(), Finished, "first");
        await WaitForAsync((await DeployAsync(t2, app2)).Id(), Finished, "second");

        string SourceOf(string app) => Received<ContainerCreateCommand>().Single(c => c.Spec.Labels!["aethera.workload"] == app).Spec.Mounts!.Single().Source;
        Assert.Equal($"{(await t1.Developer.GetJsonAsync($"/api/v1/applications/{app1}"))["slug"]!.GetValue<string>()}-{Id8(app1)}-uploads", SourceOf(app1));
        Assert.NotEqual(SourceOf(app1), SourceOf(app2));
        Assert.EndsWith($"-{Id8(app2)}-uploads", SourceOf(app2));
    }

    [RequiresDatabaseFact]
    public async Task Installation_volumes_are_refused_for_everyone_and_foreign_ones_need_an_administrator()
    {
        var (t, appId) = await ImageAppAsync();
        var slug = (await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["slug"]!.GetValue<string>();

        foreach (var name in new[] { "aethera_pgdata", "aethera_aethera_data", "aethera-traefik-acme" })
        {
            foreach (var client in new[] { t.Developer, t.Admin, t.Owner })
                await (await AddVolumeAsync(client, appId, name, "/mnt/x")).AssertProblemAsync(422, "volume.name_reserved");
        }

        // Another workload's volume: <slug>-<8 hex>-<name>.
        const string foreign = "other-1a2b3c4d-data";
        await (await AddVolumeAsync(t.Developer, appId, foreign, "/mnt/foreign")).AssertProblemAsync(403, "volume.foreign_requires_admin");
        var admin = await AddVolumeAsync(t.Admin, appId, foreign, "/mnt/foreign");
        Assert.Equal(HttpStatusCode.Created, admin.StatusCode);

        // Plain names, and names in the workload's own namespace, are the Developer's.
        Assert.Equal(HttpStatusCode.Created, (await AddVolumeAsync(t.Developer, appId, "scratch", "/mnt/scratch")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddVolumeAsync(t.Developer, appId, $"{slug}-{Id8(appId)}-cache", "/mnt/cache")).StatusCode);

        // A rename is checked the same way.
        var volumes = await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}/volumes");
        var scratch = volumes["items"]!.AsArray().Single(v => v!["name"]!.GetValue<string>() == "scratch")!.Id();
        await (await t.Developer.PatchAsync($"/api/v1/volumes/{scratch}", new { name = "aethera_pgdata" })).AssertProblemAsync(422, "volume.name_reserved");
        await (await t.Developer.PatchAsync($"/api/v1/volumes/{scratch}", new { name = foreign + "2" })).AssertProblemAsync(403, "volume.foreign_requires_admin");
    }

    [RequiresDatabaseFact]
    public async Task An_administrator_mounts_a_foreign_volume_as_it_is()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        Assert.True((await AddVolumeAsync(t.Admin, appId, "shared-1a2b3c4d-data", "/shared")).IsSuccessStatusCode);

        await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "the deployment");

        Assert.Equal("shared-1a2b3c4d-data", Received<ContainerCreateCommand>().Last().Spec.Mounts!.Single().Source);
    }

    // ---- delete tears down (7) ---------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Deleting_an_application_stops_and_removes_its_containers_and_keeps_the_volumes()
    {
        Healthy().On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => [Container("cid-orphan", ContainerRunState.Running)]);
        var (t, appId) = await ImageAppAsync();
        Assert.True((await AddVolumeAsync(t.Developer, appId, "data", "/data")).IsSuccessStatusCode);
        var slug = (await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["slug"]!.GetValue<string>();
        var d = await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "the deployment");
        Transport.Commands.Clear();

        var response = await t.Developer.DeleteAsync($"/api/v1/applications/{appId}?confirm={slug}");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.ReadAsync();
        Assert.Equal([$"{slug}-{Id8(appId)}-data"], body["volumesKept"]!.AsArray().Select(v => v!.GetValue<string>()));
        var jobId = body["jobId"]!.GetValue<string>();
        await GatewayFixture.EventuallyAsync(async () =>
            (await t.Developer.GetJsonAsync($"/api/v1/jobs/{jobId}"))["status"]!.GetValue<string>() == "succeeded", "the teardown job", timeoutMs: 15000);

        var job = await t.Developer.GetJsonAsync($"/api/v1/jobs/{jobId}");
        Assert.Equal("application.teardown", job["type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await t.Developer.GetAsync($"/api/v1/applications/{appId}")).StatusCode);

        foreach (var container in new[] { d.ContainerIds[0], "cid-orphan" })
        {
            var names = Transport.Commands.Select(c => c.Command).Select(c => c switch
            {
                ContainerStopCommand s when s.Container == container => "stop",
                ContainerRemoveCommand r when r.Container == container => "remove",
                _ => null,
            }).Where(n => n is not null).ToList();
            Assert.Equal(["stop", "remove"], names);
        }

        Assert.DoesNotContain(Transport.Commands, c => c.Command is VolumeRemoveCommand or ComposeDownCommand { RemoveVolumes: true });
    }

    [RequiresDatabaseFact]
    public async Task Deleting_a_service_removes_its_container_but_not_its_data_volume()
    {
        Healthy().On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => []);
        var (t, serviceId) = await ServiceAsync();
        var queued = await (await t.Developer.PostAsync($"/api/v1/services/{serviceId}/deployments", new { })).ReadAsync();
        var d = await WaitForAsync(queued.Id(), Finished, "the service deployment");
        Transport.Commands.Clear();

        var response = await t.Developer.DeleteAsync($"/api/v1/services/{serviceId}?confirm=db");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.ReadAsync();
        Assert.Single(body["volumesKept"]!.AsArray());
        Assert.EndsWith("-data", body["volumesKept"]![0]!.GetValue<string>());
        await GatewayFixture.EventuallyAsync(async () =>
            (await t.Developer.GetJsonAsync($"/api/v1/jobs/{body["jobId"]!.GetValue<string>()}"))["status"]!.GetValue<string>() == "succeeded", "the teardown job", timeoutMs: 15000);

        Assert.Contains(Received<ContainerRemoveCommand>(), r => r.Container == d.ContainerIds[0]);
        // A database gets the longer grace period.
        Assert.Equal(TimeSpan.FromSeconds(60), Received<ContainerStopCommand>().Single(s => s.Container == d.ContainerIds[0]).Timeout);
        Assert.Empty(Received<VolumeRemoveCommand>());
    }

    [RequiresDatabaseFact]
    public async Task Deleting_a_workload_that_never_ran_has_nothing_to_tear_down()
    {
        var (t, appId) = await ImageAppAsync();
        var slug = (await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["slug"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.NoContent, (await t.Developer.DeleteAsync($"/api/v1/applications/{appId}?confirm={slug}")).StatusCode);
    }

    // ---- graceful stop on redeploy (10) ------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_redeploy_stops_the_old_container_gracefully_before_removing_it()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var first = await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "first");
        Transport.Commands.Clear();

        await WaitForAsync((await DeployAsync(t, appId)).Id(), d => d.Status == DeploymentStatus.Running, "second");

        var order = Transport.Commands.Select(c => c.Command).Where(c => c is ContainerStopCommand or ContainerRemoveCommand)
            .Select(c => c switch { ContainerStopCommand s => $"stop {s.Container} {s.Timeout!.Value.TotalSeconds}", ContainerRemoveCommand r => $"remove {r.Container}", _ => "" }).ToList();
        Assert.Equal([$"stop {first.ContainerIds[0]} 30", $"remove {first.ContainerIds[0]}"], order);
    }

    // ---- failures end the deployment (12) ----------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task An_unexpected_exception_ends_the_deployment_and_the_application_failed_without_leaking_it()
    {
        Healthy().On<ImageInspectCommand, DockerImage>(_ => throw new InvalidOperationException("connection string password=hunter2"));
        var (t, appId) = await ImageAppAsync();

        var d = await WaitForAsync((await DeployAsync(t, appId)).Id(), x => x.IsTerminal, "the deployment to end");

        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal("internal.error", d.FailureCode);
        Assert.DoesNotContain("hunter2", d.FailureReason);
        Assert.Contains(d.Steps, s => s.Status == StepStatus.Failed && s.ErrorCode == "internal.error");
        await GatewayFixture.EventuallyAsync(async () =>
            (await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["state"]!["status"]!.GetValue<string>() == "failed", "the application to be failed");
    }

    [RequiresDatabaseFact]
    public async Task A_lost_secret_fails_the_deployment_instead_of_leaving_it_in_progress()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var secret = await t.CreateSecretAsync();
        var variable = await t.Admin.PostAsync($"/api/v1/applications/{appId}/env-vars", new { key = "TOKEN", secretId = secret.Id() });
        Assert.True(variable.IsSuccessStatusCode, await variable.Content.ReadAsStringAsync());
        await WithDbAsync(async db => await db.SecretVersions.Where(v => v.SecretId == Guid.Parse(secret.Id())).ExecuteDeleteAsync());

        var d = await WaitForAsync((await DeployAsync(t, appId)).Id(), x => x.IsTerminal, "the deployment to end");

        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal("secret.version_missing", d.FailureCode);
    }

    [RequiresDatabaseFact]
    public async Task Cancelling_a_running_deployment_ends_it_cancelled_and_the_application_is_not_deploying()
    {
        using var gate = new ManualResetEventSlim();
        Healthy().On<ProxyEnsureCommand, ProxyEnsured>(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return new ProxyEnsured("traefik", "proxy-1", "v3.1", true, true, "abc");
        });
        try
        {
            var (t, appId) = await ImageAppAsync(new { runtime = new { ports = new[] { new { containerPort = 8080, isHttp = true } } } });
            Assert.True((await t.Developer.PostAsync($"/api/v1/applications/{appId}/domains", new { hostname = $"{Tenant.Unique("c")}.example.com" })).IsSuccessStatusCode);
            var queued = await DeployAsync(t, appId);
            await GatewayFixture.EventuallyAsync(() => Task.FromResult(Received<ProxyEnsureCommand>().Any()), "the worker to reach the proxy step", timeoutMs: 15000);
            var jobId = (await LoadAsync(queued.Id())).JobId ?? (await WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.ResourceId == Guid.Parse(appId) && j.Type == "application.deploy").Select(j => j.Id).FirstAsync()));

            Assert.Equal(HttpStatusCode.Accepted, (await t.Developer.PostAsync($"/api/v1/jobs/{jobId}/cancel", new { })).StatusCode);
            await Task.Delay(500); // the cancel reaches the running job
            gate.Set();

            var d = await WaitForAsync(queued.Id(), x => x.IsTerminal, "the deployment to end");
            Assert.Equal(DeploymentStatus.Cancelled, d.Status);
            await GatewayFixture.EventuallyAsync(async () =>
                (await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["state"]!["status"]!.GetValue<string>() != "deploying", "the application to leave deploying");
        }
        finally
        {
            gate.Set();
        }
    }

    // ---- transient failures are retried for real (13) ----------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_deployment_that_met_an_unreachable_server_is_redeployed_by_the_retry()
    {
        var failures = 0;
        Healthy().On<ImageInspectCommand, DockerImage>(c =>
            Interlocked.Increment(ref failures) <= 1
                ? throw new ServerTransportException(TransportErrors.AgentUnavailable, "agent reconnecting")
                : new DockerImage("sha256:img", [c.Image], [], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0));
        var (t, appId) = await ImageAppAsync();

        var queued = await DeployAsync(t, appId);
        var d = await WaitForAsync(queued.Id(), x => x.IsTerminal || x.Status == DeploymentStatus.Running, "the retry to deploy it", 30000);

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.True(failures >= 2, "the first attempt failed and a retry ran the pipeline again");
        var job = await WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == d.JobId));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, job.Attempt);
        Assert.Single(Received<ContainerCreateCommand>());
    }

    [RequiresDatabaseFact]
    public async Task A_server_that_stays_unreachable_ends_the_deployment_failed_on_the_last_attempt()
    {
        Healthy().On<ImageInspectCommand, DockerImage>(_ => throw new ServerTransportException(TransportErrors.AgentUnavailable, "agent gone"));
        var (t, appId) = await ImageAppAsync();

        var queued = await DeployAsync(t, appId);
        var d = await WaitForAsync(queued.Id(), x => x.IsTerminal, "the deployment to fail", 30000);

        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal("server.unavailable", d.FailureCode);
        var job = await WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == d.JobId));
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(DeploymentService_DeployMaxAttempts, job.Attempt);
    }

    private const int DeploymentService_DeployMaxAttempts = Aethera.Infrastructure.Deployments.DeploymentService.DeployMaxAttempts;

    // ---- low-downtime safety (14) ------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Low_downtime_with_a_published_port_or_a_writable_volume_runs_recreate_and_says_so()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync(new { runtime = new { strategy = "low-downtime", ports = new[] { new { containerPort = 5000, publishedPort = 5000 } } } });
        Assert.True((await AddVolumeAsync(t.Developer, appId, "data", "/data")).IsSuccessStatusCode);

        var first = await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "first");
        Transport.Commands.Clear();
        var second = await WaitForAsync((await DeployAsync(t, appId)).Id(), d => d.Status == DeploymentStatus.Running, "second");

        Assert.Equal(DeploymentStatus.Running, second.Status);
        var order = Transport.Commands.Select(c => c.Command).Where(c => c is ContainerRemoveCommand or ContainerCreateCommand).Select(c => c.GetType().Name).ToList();
        Assert.Equal(["ContainerRemoveCommand", "ContainerCreateCommand"], order); // old one first: recreate, not side by side
        Assert.DoesNotContain("-2", Received<ContainerCreateCommand>().Single().Spec.Name); // no per-deployment name of low-downtime
        var details = second.Steps.Single(s => s.Step == DeploymentStep.Container).DetailsJson!;
        Assert.Contains("strategyNote", details);
        Assert.Contains("writable volume", details);
        Assert.NotNull(first);
    }

    // ---- queued deployments can be cancelled (17) --------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Cancelling_a_queued_deploy_job_ends_its_deployment_cancelled()
    {
        using var gate = new ManualResetEventSlim();
        Healthy().On<ImageInspectCommand, DockerImage>(c =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return new DockerImage("sha256:img", [c.Image], [], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0);
        });
        try
        {
            var (t, appId) = await ImageAppAsync();
            var running = await DeployAsync(t, appId);
            await GatewayFixture.EventuallyAsync(() => Task.FromResult(Received<ImageInspectCommand>().Any()), "the first deployment to start", timeoutMs: 15000);
            var queued = await DeployAsync(t, appId); // waits behind the first one (same lock)
            var queuedJob = await WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.ResourceId == Guid.Parse(appId) && j.Status == JobStatus.Queued).Select(j => j.Id).FirstAsync());

            Assert.Equal(HttpStatusCode.Accepted, (await t.Developer.PostAsync($"/api/v1/jobs/{queuedJob}/cancel", new { })).StatusCode);

            Assert.Equal(DeploymentStatus.Cancelled, (await LoadAsync(queued.Id())).Status);
            gate.Set();
            await WaitForAsync(running.Id(), Finished, "the first deployment");
            Assert.Equal(DeploymentStatus.Cancelled, (await LoadAsync(queued.Id())).Status);
        }
        finally
        {
            gate.Set();
        }
    }

    // ---- webhooks deploy the pushed branch (18) ----------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_push_to_a_branch_the_filter_matches_builds_that_branch_at_the_pushed_commit()
    {
        Healthy().On<BuildImageCommand, BuildOutcome>(b => new BuildOutcome(b.Spec.BuildId, "sha256:img", "sha256:dig", b.Spec.ImageTags, [], 1, TimeSpan.Zero, "dockerfile", "abc123def456", false, "linux/amd64", false));
        var (t, appId, path, secret) = await GitAppWithWebhookAsync(branchFilter: "release/*");
        var anonymous = fixture.Factory.CreateAnonymousClient();

        var response = await anonymous.SendAsync(Push(path, secret, "release/1.2", "d-release"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var deploymentId = (await response.ReadAsync())["deploymentId"]!.GetValue<string>();
        var d = await WaitForAsync(deploymentId, Finished, "the webhook deployment");

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Equal("refs/heads/release/1.2", d.Ref);
        var git = Received<BuildImageCommand>().Single().Spec.Git!;
        Assert.Equal("release/1.2", git.Ref);
        Assert.Equal("abc123def456", git.Commit);

        // A push to a branch the filter does not match still does nothing.
        var ignored = await anonymous.SendAsync(Push(path, secret, "feature/x", "d-feature"));
        Assert.Equal("ignored", (await ignored.ReadAsync())["outcome"]!.GetValue<string>().ToLowerInvariant());
        Assert.Equal(1, await WithDbAsync(db => db.Deployments.CountAsync(x => x.WorkloadId == Guid.Parse(appId))));
        Assert.NotNull(t);
    }

    // ---- pull policy (19) --------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task The_image_pull_policy_decides_whether_a_deployment_pulls()
    {
        Healthy();
        var (t, ifNotPresent) = await ImageAppAsync();
        await WaitForAsync((await DeployAsync(t, ifNotPresent)).Id(), Finished, "if-not-present");
        Assert.Empty(Received<ImagePullCommand>());
        Assert.NotEmpty(Received<ImageInspectCommand>());

        Healthy();
        var (t2, always) = await ImageAppAsync(new { image = new { image = "nginx", tag = "1.27", pullPolicy = "always" } });
        await WaitForAsync((await DeployAsync(t2, always)).Id(), Finished, "always");
        Assert.Single(Received<ImagePullCommand>());

        Healthy().Fail<ImageInspectCommand, DockerImage>(CommandErrorCode.NotFound, "no such image");
        var (t3, never) = await ImageAppAsync(new { image = new { image = "nginx", tag = "1.27", pullPolicy = "never" } });
        var d = await WaitForAsync((await DeployAsync(t3, never)).Id(), Finished, "never");
        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal("image.missing", d.FailureCode);
        Assert.Empty(Received<ImagePullCommand>());
    }
}

/// <summary>Pure rules of the snapshot factory that the hardening relies on.</summary>
public sealed class DeploymentSnapshotRulesTests
{
    private static Aethera.Engine.Deployments.DeploymentSnapshot Git(string branch = "main", string? pin = null) => new()
    {
        WorkloadId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), Slug = "site", SourceKind = ApplicationSourceKind.Git,
        Git = new Aethera.Engine.Deployments.GitSnapshot("https://github.com/acme/site.git", branch, pin, null),
    };

    [Fact]
    public void A_webhook_deployment_builds_the_pushed_branch_at_the_pushed_commit()
    {
        var snapshot = Aethera.Infrastructure.Deployments.DeploymentSnapshotFactory.ApplyTrigger(Git(), DeploymentTrigger.Webhook, "refs/heads/release/1.2", "abc123def456");

        Assert.Equal("release/1.2", snapshot.Git!.Branch);
        Assert.Equal("abc123def456", snapshot.Git.CommitPin);
    }

    [Fact]
    public void Other_triggers_and_bad_refs_leave_the_configured_source_alone_or_refuse()
    {
        var configured = Git();
        Assert.Same(configured, Aethera.Infrastructure.Deployments.DeploymentSnapshotFactory.ApplyTrigger(configured, DeploymentTrigger.Manual, "refs/heads/other", "abc123def456"));
        Assert.Same(configured, Aethera.Infrastructure.Deployments.DeploymentSnapshotFactory.ApplyTrigger(configured, DeploymentTrigger.Webhook, null, null));

        foreach (var bad in new[] { "refs/heads/--upload-pack=x", "refs/heads/a..b", "refs/heads/a b", "refs/heads/x;rm", "refs/heads/-f" })
            Assert.Throws<Aethera.Infrastructure.Deployments.UnsupportedSourceException>(() =>
                Aethera.Infrastructure.Deployments.DeploymentSnapshotFactory.ApplyTrigger(configured, DeploymentTrigger.Webhook, bad, "abc123def456"));

        // A malformed commit id is not pinned.
        Assert.Null(Aethera.Infrastructure.Deployments.DeploymentSnapshotFactory.ApplyTrigger(configured, DeploymentTrigger.Webhook, "refs/heads/main", "not-a-sha; x").Git!.CommitPin);
    }
}
