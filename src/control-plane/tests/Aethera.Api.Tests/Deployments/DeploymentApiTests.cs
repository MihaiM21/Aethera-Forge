using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Tests.Deployments;

/// <summary>The API with fast job workers running and the real transports swapped for a <see cref="FakeServerTransport"/>.</summary>
public sealed class DeploymentsFixture : IDisposable
{
    public DeploymentsFixture()
    {
        Base = new AetheraApiFactory();
        Transport = new FakeServerTransport();
        Factory = Jobs.JobsApiFixture.Configure(Base).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IServerTransport>();
            services.AddSingleton<IServerTransport>(Transport);
        }));
    }

    public AetheraApiFactory Base { get; }

    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; }

    public FakeServerTransport Transport { get; }

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class DeploymentsApiCollection : ICollectionFixture<DeploymentsFixture>
{
    public const string Name = "deployments-api";
}

/// <summary>End to end: API -> deployment + job -> worker -> engine -> (fake) transport, against a real database.</summary>
[Collection(DeploymentsApiCollection.Name)]
public sealed class DeploymentApiTests(DeploymentsFixture fixture)
{
    private FakeServerTransport Transport => fixture.Transport;

    private static DockerContainer Container(string id, ContainerRunState state = ContainerRunState.Running) =>
        new(id, id, "img", "sha", state, state.ToString(), ContainerHealthState.None, null, null, null, 0, false, 0,
            new Dictionary<string, string>(), [], [], [], null);

    /// <summary>Scripts a healthy server: pulls work, containers are created with sequential ids.</summary>
    private FakeServerTransport Healthy()
    {
        var n = 0;
        Transport.Commands.Clear();
        return Transport
            .On<ImagePullCommand, ImagePulled>(_ => new ImagePulled("sha256:img", "sha256:dig", 1, [], false))
            .On<ImageInspectCommand, DockerImage>(c => new DockerImage("sha256:img", [c.Image], [], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0))
            .On<NetworkCreateCommand, DockerNetwork>(c => new DockerNetwork("n", c.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []))
            .On<ContainerCreateCommand, ContainerCreated>(c => new ContainerCreated($"cid-{Interlocked.Increment(ref n)}", c.Spec.Name, ContainerRunState.Running, []))
            .On<ContainerRemoveCommand, Unit>(_ => Unit.Value)
            .On<ContainerStopCommand, DockerContainer>(c => Container(c.Container, ContainerRunState.Exited))
            .On<ContainerStartCommand, DockerContainer>(c => Container(c.Container))
            .On<ContainerRestartCommand, DockerContainer>(c => Container(c.Container))
            .On<ImageRemoveCommand, Unit>(_ => Unit.Value)
            .On<ProxyEnsureCommand, ProxyEnsured>(_ => new ProxyEnsured("traefik", "proxy-1", "v3.1", true, true, "abc"));
    }

    private async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    private Task<Deployment> LoadAsync(string id) =>
        WithDbAsync(db => db.Deployments.AsNoTracking().Include(d => d.Steps).FirstAsync(d => d.Id == Guid.Parse(id)));

    private async Task<Deployment> WaitForAsync(string id, Func<Deployment, bool> done, string what)
    {
        Deployment? last = null;
        try
        {
            await GatewayFixture.EventuallyAsync(async () => done(last = await LoadAsync(id)), what, timeoutMs: 15000);
        }
        catch (Xunit.Sdk.XunitException)
        {
            var job = last?.JobId is { } jid ? await WithDbAsync(db => db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jid)) : null;
            throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}. Deployment: {last?.Status}/{last?.FailureCode}; job: {job?.Status} {job?.ErrorJson}");
        }
        if (last?.JobId is { } jobId)
            await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(db => db.Jobs.AsNoTracking()
                .AnyAsync(j => j.Id == jobId && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled))),
                $"the job of {what} to finish", timeoutMs: 15000);
        return await LoadAsync(id);
    }

    private static bool Finished(Deployment d) => d.Status is DeploymentStatus.Running or DeploymentStatus.Failed or DeploymentStatus.Cancelled or DeploymentStatus.Superseded;

    private async Task<(Tenant Tenant, string AppId)> ImageAppAsync(object? extra = null)
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync(publicIp: "203.0.113.10");
        var (_, env) = await tenant.CreateProjectWithEnvironmentAsync();
        var app = await tenant.CreateApplicationAsync(env, server.Id(), extra: extra);
        return (tenant, app.Id());
    }

    private static async Task<JsonNode> DeployAsync(Tenant t, string appId)
    {
        var response = await t.Developer.PostAsync($"/api/v1/applications/{appId}/deployments", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.ReadAsync();
    }

    [RequiresDatabaseFact]
    public async Task Deploying_an_image_app_runs_the_pipeline_and_promotes_the_deployment()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();

        var queued = await DeployAsync(t, appId);
        Assert.Equal(1, queued["number"]!.GetValue<int>());
        var d = await WaitForAsync(queued.Id(), Finished, "the deployment to finish");

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.True(d.IsRollbackPoint);
        Assert.Equal("nginx:1.27", d.ImageRef);
        Assert.Single(d.ContainerIds);
        Assert.StartsWith("cid-", d.ContainerIds[0]);
        Assert.Equal(DeploymentStep.Running, d.CurrentStep);
        Assert.Equal(StepStatus.Skipped, d.Steps.Single(s => s.Step == DeploymentStep.Build).Status);

        var app = await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}");
        Assert.Equal("running", app["state"]!["status"]!.GetValue<string>());
        Assert.Equal(queued.Id(), app["state"]!["currentDeploymentId"]!.GetValue<string>());

        var detail = await t.Viewer.GetJsonAsync($"/api/v1/deployments/{queued.Id()}");
        Assert.Equal("running", detail["status"]!.GetValue<string>());
        Assert.Equal(9, detail["steps"]!.AsArray().Count);

        var list = await t.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/deployments");
        Assert.Single(list["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task A_second_deployment_supersedes_the_first_and_removes_its_container()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var first = await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "first deployment");
        var secondId = (await DeployAsync(t, appId)).Id();
        var second = await WaitForAsync(secondId, d => d.Status == DeploymentStatus.Running, "second deployment");

        Assert.Equal(2, second.Number);
        Assert.Equal(DeploymentStatus.Superseded, (await LoadAsync(first.Id.ToString())).Status);
        Assert.Contains(Transport.Commands, c => c.Command is ContainerRemoveCommand r && r.Container == first.ContainerIds[0]);
    }

    [RequiresDatabaseFact]
    public async Task A_failed_pull_fails_the_deployment_the_job_and_the_application()
    {
        Healthy().Fail<ImagePullCommand, ImagePulled>(CommandErrorCode.ImagePullFailed, "manifest unknown");
        var (t, appId) = await ImageAppAsync();

        var queued = await DeployAsync(t, appId);
        var d = await WaitForAsync(queued.Id(), Finished, "the deployment to fail");

        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal(DeploymentStep.Image, d.FailedStep);
        Assert.Equal("image.pull_failed", d.FailureCode);
        Assert.False(d.IsRollbackPoint);
        await GatewayFixture.EventuallyAsync(async () =>
            (await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["state"]!["status"]!.GetValue<string>() == "failed", "application status failed");
        await GatewayFixture.EventuallyAsync(async () =>
            await WithDbAsync(db => db.Jobs.AsNoTracking().AnyAsync(j => j.Id == d.JobId && j.Status == JobStatus.Failed)), "the job to fail");
    }

    [RequiresDatabaseFact]
    public async Task Rollback_restores_an_earlier_deployment_without_pulling_or_building()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var first = await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "first");
        await WaitForAsync((await DeployAsync(t, appId)).Id(), d => d.Status == DeploymentStatus.Running, "second");
        Transport.Commands.Clear();

        var response = await t.Developer.PostAsync($"/api/v1/deployments/{first.Id}/rollback", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var rollback = await WaitForAsync((await response.ReadAsync()).Id(), d => d.Status == DeploymentStatus.Running, "the rollback");

        Assert.Equal(DeploymentTrigger.Rollback, rollback.Trigger);
        Assert.Equal(first.Id, rollback.RollbackOfDeploymentId);
        Assert.Equal(3, rollback.Number);
        Assert.DoesNotContain(Transport.Commands, c => c.Command is ImagePullCommand or BuildImageCommand);
        Assert.Contains(Transport.Commands, c => c.Command is ImageInspectCommand);
    }

    [RequiresDatabaseFact]
    public async Task Rolling_back_to_a_failed_deployment_is_a_conflict()
    {
        Healthy().Fail<ImagePullCommand, ImagePulled>(CommandErrorCode.ImagePullFailed, "nope");
        var (t, appId) = await ImageAppAsync();
        var failed = await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "failure");

        var response = await t.Developer.PostAsync($"/api/v1/deployments/{failed.Id}/rollback", new { });
        await response.AssertProblemAsync(409, "deployment.not_rollback_point");
    }

    [RequiresDatabaseFact]
    public async Task Stop_and_start_move_the_deployment_and_the_desired_state()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var d = await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "deployment");

        Assert.Equal(HttpStatusCode.Accepted, (await t.Developer.PostAsync($"/api/v1/applications/{appId}/stop", new { })).StatusCode);
        await GatewayFixture.EventuallyAsync(async () =>
            (await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["state"]!["status"]!.GetValue<string>() == "stopped", "stopped");
        Assert.Equal(DeploymentStatus.Stopped, (await LoadAsync(d.Id.ToString())).Status);

        Assert.Equal(HttpStatusCode.Accepted, (await t.Developer.PostAsync($"/api/v1/applications/{appId}/start", new { })).StatusCode);
        await GatewayFixture.EventuallyAsync(async () =>
            (await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}"))["state"]!["status"]!.GetValue<string>() == "running", "running again");
        Assert.Equal(DeploymentStatus.Running, (await LoadAsync(d.Id.ToString())).Status);
    }

    [RequiresDatabaseFact]
    public async Task Stopping_an_application_that_was_never_deployed_is_a_conflict()
    {
        var (t, appId) = await ImageAppAsync();
        await (await t.Developer.PostAsync($"/api/v1/applications/{appId}/stop", new { })).AssertProblemAsync(409, "application.not_deployed");
    }

    [RequiresDatabaseFact]
    public async Task Viewers_cannot_deploy_and_other_tenants_cannot_see_deployments()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsync($"/api/v1/applications/{appId}/deployments", new { })).StatusCode);

        var d = await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "deployment");
        var other = await fixture.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync($"/api/v1/deployments/{d.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Developer.PostAsync($"/api/v1/applications/{appId}/deployments", new { })).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Routed_application_gets_proxy_ensured_and_traefik_labels()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync(new { runtime = new { ports = new[] { new { containerPort = 8080, isHttp = true } } } });
        var domain = await t.Developer.PostAsync($"/api/v1/applications/{appId}/domains", new { hostname = "app.example.com" });
        Assert.True(domain.IsSuccessStatusCode, await domain.Content.ReadAsStringAsync());

        await WaitForAsync((await DeployAsync(t, appId)).Id(), Finished, "deployment");

        var create = Transport.Commands.Select(c => c.Command).OfType<ContainerCreateCommand>().Single();
        Assert.Contains(create.Spec.Labels!, kv => kv.Key.EndsWith("-https.rule") && kv.Value == "Host(`app.example.com`)");
        Assert.Contains(create.Spec.Labels!, kv => kv.Key.EndsWith("-https.tls.certresolver"));
        Assert.Contains(create.Spec.Networks!, n => n.Network == "aethera-proxy");
        Assert.Contains(Transport.Commands, c => c.Command is ProxyEnsureCommand);
        Assert.Contains(Transport.Commands, c => c.Command is NetworkCreateCommand n && n.NetworkName == "aethera-proxy");
    }

    // ---- webhooks -------------------------------------------------------------------------------------------------------------------

    private async Task<(Tenant Tenant, string AppId, string EndpointPath, string Secret)> GitAppWithWebhookAsync(bool autoDeploy = true, string? branchFilter = null)
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var (_, env) = await tenant.CreateProjectWithEnvironmentAsync();
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("git"), environmentId = env, serverId = server.Id(), sourceKind = "git",
            gitSource = new { repositoryUrl = "https://github.com/acme/site.git", branch = "main", autoDeploy },
        });
        var response = await tenant.Developer.PutAsync($"/api/v1/applications/{app.Id()}/webhook", Json.Body(new { provider = "gitHub", branchFilter }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var hook = await response.ReadAsync();
        return (tenant, app.Id(), hook["path"]!.GetValue<string>(), hook["secret"]!.GetValue<string>());
    }

    private static HttpRequestMessage Push(string path, string secret, string branch, string delivery, string? sigSecret = null)
    {
        var body = "{\"ref\":\"refs/heads/" + branch + "\",\"after\":\"abc123def456\",\"head_commit\":{\"message\":\"ship it\",\"author\":{\"name\":\"Ann\"}}}";
        var sig = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(sigSecret ?? secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Event", "push");
        request.Headers.Add("X-GitHub-Delivery", delivery);
        request.Headers.Add("X-Hub-Signature-256", sig);
        return request;
    }

    [RequiresDatabaseFact]
    public async Task A_signed_push_to_the_deploy_branch_queues_a_webhook_deployment()
    {
        var (t, appId, path, secret) = await GitAppWithWebhookAsync();
        var anonymous = fixture.Factory.CreateAnonymousClient();

        var response = await anonymous.SendAsync(Push(path, secret, "main", "delivery-1"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.ReadAsync();
        Assert.Equal("accepted", result["outcome"]!.GetValue<string>().ToLowerInvariant());

        var d = await LoadAsync(result["deploymentId"]!.GetValue<string>());
        Assert.Equal(DeploymentTrigger.Webhook, d.Trigger);
        Assert.Equal("abc123def456", d.CommitSha);
        Assert.Equal("Ann", d.CommitAuthor);

        var deliveries = await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}/webhook/deliveries");
        Assert.Single(deliveries.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task Redelivery_is_a_duplicate_and_creates_no_second_deployment()
    {
        var (t, appId, path, secret) = await GitAppWithWebhookAsync();
        var anonymous = fixture.Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.SendAsync(Push(path, secret, "main", "same-id"))).StatusCode);
        var again = await anonymous.SendAsync(Push(path, secret, "main", "same-id"));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("duplicate", (await again.ReadAsync())["outcome"]!.GetValue<string>().ToLowerInvariant());
        Assert.Equal(1, await WithDbAsync(db => db.Deployments.CountAsync(d => d.WorkloadId == Guid.Parse(appId))));
    }

    [RequiresDatabaseFact]
    public async Task A_bad_signature_is_unauthorized_and_stores_nothing()
    {
        var (_, appId, path, secret) = await GitAppWithWebhookAsync();
        var anonymous = fixture.Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Push(path, secret, "main", "d1", sigSecret: "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/webhooks/git/{Guid.NewGuid()}", new StringContent("{}"))).StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.Deployments.CountAsync(d => d.WorkloadId == Guid.Parse(appId))));
        Assert.Equal(0, await WithDbAsync(db => db.WebhookDeliveries.CountAsync(d => d.Endpoint.WorkloadId == Guid.Parse(appId))));
    }

    [RequiresDatabaseFact]
    public async Task Pushes_to_other_branches_or_with_auto_deploy_off_are_ignored()
    {
        var (_, appId, path, secret) = await GitAppWithWebhookAsync();
        var anonymous = fixture.Factory.CreateAnonymousClient();
        var other = await anonymous.SendAsync(Push(path, secret, "feature/x", "d-branch"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal("ignored", (await other.ReadAsync())["outcome"]!.GetValue<string>().ToLowerInvariant());

        var (_, offApp, offPath, offSecret) = await GitAppWithWebhookAsync(autoDeploy: false);
        var off = await anonymous.SendAsync(Push(offPath, offSecret, "main", "d-off"));
        Assert.Equal("ignored", (await off.ReadAsync())["outcome"]!.GetValue<string>().ToLowerInvariant());

        Assert.Equal(0, await WithDbAsync(db => db.Deployments.CountAsync(d => d.WorkloadId == Guid.Parse(appId) || d.WorkloadId == Guid.Parse(offApp))));
    }

    [RequiresDatabaseFact]
    public async Task Setting_the_webhook_up_again_rotates_the_secret()
    {
        var (t, appId, path, oldSecret) = await GitAppWithWebhookAsync();
        var rotated = await (await t.Developer.PutAsync($"/api/v1/applications/{appId}/webhook", Json.Body(new { provider = "gitHub" }))).ReadAsync();
        var anonymous = fixture.Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Push(path, oldSecret, "main", "old"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.SendAsync(Push(path, rotated["secret"]!.GetValue<string>(), "main", "new"))).StatusCode);

        var read = await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}/webhook");
        Assert.Null(read["secret"]);
    }

    [RequiresDatabaseFact]
    public async Task The_organization_wide_list_filters_by_status_and_names_the_application()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var queued = await DeployAsync(t, appId);
        await WaitForAsync(queued.Id(), Finished, "the deployment to finish");

        var all = await t.Viewer.GetJsonAsync("/api/v1/deployments");
        var item = all["items"]!.AsArray().Single(i => i!["deployment"]!["id"]!.GetValue<string>() == queued.Id())!;
        Assert.Equal(appId, item["deployment"]!["applicationId"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(item["applicationName"]!.GetValue<string>()));

        var failed = await t.Viewer.GetJsonAsync($"/api/v1/deployments?status=failed&applicationId={appId}");
        Assert.Empty(failed["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Viewer.GetAsync("/api/v1/deployments?bogus=1")).StatusCode);

        var other = await fixture.NewTenantAsync();
        var foreign = await other.Viewer.GetJsonAsync("/api/v1/deployments");
        Assert.DoesNotContain(foreign["items"]!.AsArray(), i => i!["deployment"]!["id"]!.GetValue<string>() == queued.Id());
    }

    [RequiresDatabaseFact]
    public async Task Deployment_logs_expose_the_pipeline_stream_and_stay_inside_the_organization()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var queued = await DeployAsync(t, appId);
        await WaitForAsync(queued.Id(), Finished, "the deployment to finish");

        var page = await t.Viewer.GetJsonAsync($"/api/v1/deployments/{queued.Id()}/logs");
        Assert.Equal("deploy", page["source"]!.GetValue<string>());
        Assert.StartsWith("job:", page["streamId"]!.GetValue<string>());
        Assert.NotEmpty(page["items"]!.AsArray());
        Assert.True(page["ended"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.BadRequest, (await t.Viewer.GetAsync($"/api/v1/deployments/{queued.Id()}/logs?source=nope")).StatusCode);
        var download = await t.Viewer.GetAsync($"/api/v1/deployments/{queued.Id()}/logs?download=true");
        Assert.Equal("text/plain", download.Content.Headers.ContentType!.MediaType);

        var other = await fixture.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Viewer.GetAsync($"/api/v1/deployments/{queued.Id()}/logs")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Application_logs_tail_the_running_container()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var none = await t.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/logs");
        Assert.Empty(none["lines"]!.AsArray());

        var queued = await DeployAsync(t, appId);
        await WaitForAsync(queued.Id(), Finished, "the deployment to finish");
        Transport.LogLines(
            new LogEntry("c", 1, DateTimeOffset.UtcNow.AddSeconds(-2), LogSource.Container, LogStream.Stdout, "hello"),
            new LogEntry("c", 2, DateTimeOffset.UtcNow.AddSeconds(-1), LogSource.Container, LogStream.Stderr, "oops"));

        var logs = await t.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/logs?tail=50");
        // The scripted lines are shared by the fixture's transport: assert on ours, not on the count.
        var lines = logs["lines"]!.AsArray();
        Assert.Contains(lines, l => l!["text"]!.GetValue<string>() == "oops" && l["stream"]!.GetValue<string>() == "stderr");
        Assert.Contains(lines, l => l!["text"]!.GetValue<string>() == "hello" && l["stream"]!.GetValue<string>() == "stdout");
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Viewer.GetAsync($"/api/v1/applications/{appId}/logs?tail=0")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task The_audit_log_is_for_administrators_and_records_deployments()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        await DeployAsync(t, appId);

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.GetAsync("/api/v1/audit-log")).StatusCode);
        var log = await t.Admin.GetJsonAsync("/api/v1/audit-log?action=application.*");
        Assert.Contains(log["items"]!.AsArray(), e => e!["action"]!.GetValue<string>() == "application.deploy_requested" && e["resourceId"]!.GetValue<string>() == appId);
        var none = await t.Admin.GetJsonAsync("/api/v1/audit-log?action=nothing.here");
        Assert.Empty(none["items"]!.AsArray());
    }

    private async Task<(Tenant Tenant, string ServiceId)> ServiceAsync(string templateKey = "postgres")
    {
        Transport.On<HealthProbeCommand, HealthProbeOutcome>(_ => new HealthProbeOutcome(true, 1, 0, TimeSpan.Zero, "ok", null));
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync(publicIp: "203.0.113.10");
        var (_, env) = await tenant.CreateProjectWithEnvironmentAsync();
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name = "db", environmentId = env, serverId = server.Id(), templateKey });
        return (tenant, service.Id());
    }

    [RequiresDatabaseFact]
    public async Task A_service_deploys_through_the_same_engine_and_joins_the_environment_network()
    {
        Healthy();
        var (t, serviceId) = await ServiceAsync();

        var response = await t.Developer.PostAsync($"/api/v1/services/{serviceId}/deployments", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.ReadAsync();
        var d = await WaitForAsync(queued.Id(), Finished, "the service deployment to finish");

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.StartsWith("postgres:", d.ImageRef);
        var service = await t.Developer.GetJsonAsync($"/api/v1/services/{serviceId}");
        Assert.Equal("running", service["state"]!["status"]!.GetValue<string>());

        var create = Transport.Commands.Select(c => c.Command).OfType<ContainerCreateCommand>().Single();
        Assert.Contains(create.Spec.Networks!, n => n.Network.StartsWith("aethera-env-") && n.Aliases is { Count: > 0 });
        Assert.DoesNotContain(create.Spec.Ports!, p => p.HostPort is > 0);
        Assert.Contains(create.Spec.Env!, e => e.Name == "POSTGRES_USER");

        var list = await t.Viewer.GetJsonAsync($"/api/v1/services/{serviceId}/deployments");
        Assert.Single(list["items"]!.AsArray());
        var all = await t.Viewer.GetJsonAsync("/api/v1/deployments");
        Assert.Contains(all["items"]!.AsArray(), i => i!["deployment"]!["id"]!.GetValue<string>() == queued.Id());
    }

    [RequiresDatabaseFact]
    public async Task Service_routes_do_not_accept_an_application_and_the_other_way_round()
    {
        Healthy();
        var (t, serviceId) = await ServiceAsync("redis");
        Assert.Equal(HttpStatusCode.NotFound, (await t.Developer.PostAsync($"/api/v1/applications/{serviceId}/deployments", new { })).StatusCode);
        var (_, appId) = await ImageAppAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await t.Developer.PostAsync($"/api/v1/services/{appId}/deployments", new { })).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task A_service_can_be_stopped_and_its_logs_read()
    {
        Healthy();
        var (t, serviceId) = await ServiceAsync("redis");
        var queued = await (await t.Developer.PostAsync($"/api/v1/services/{serviceId}/deployments", new { })).ReadAsync();
        await WaitForAsync(queued.Id(), Finished, "the service deployment to finish");

        Transport.LogLines(new LogEntry("c", 1, DateTimeOffset.UtcNow, LogSource.Container, LogStream.Stdout, "ready"));
        var logs = await t.Viewer.GetJsonAsync($"/api/v1/services/{serviceId}/logs");
        Assert.Contains(logs["lines"]!.AsArray(), l => l!["text"]!.GetValue<string>() == "ready");

        var stop = await t.Developer.PostAsync($"/api/v1/services/{serviceId}/stop", new { });
        Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        await GatewayFixture.EventuallyAsync(async () =>
            (await t.Developer.GetJsonAsync($"/api/v1/services/{serviceId}"))["state"]!["status"]!.GetValue<string>() == "stopped", "the service to stop", timeoutMs: 15000);
    }

    [RequiresDatabaseFact]
    public async Task A_service_whose_image_has_no_default_command_gets_the_one_from_its_template()
    {
        Healthy();
        var (t, serviceId) = await ServiceAsync("minio");
        var queued = await (await t.Developer.PostAsync($"/api/v1/services/{serviceId}/deployments", new { })).ReadAsync();
        await WaitForAsync(queued.Id(), Finished, "the service deployment to finish");

        var create = Transport.Commands.Select(c => c.Command).OfType<ContainerCreateCommand>().Last();
        Assert.Equal(["server", "/data", "--console-address", ":9001"], create.Spec.Command);
    }

    [RequiresDatabaseFact]
    public async Task A_log_stream_ends_when_its_job_finishes_not_when_the_deployment_turns_running()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var queued = await DeployAsync(t, appId);
        await WaitForAsync(queued.Id(), Finished, "the deployment to finish");

        // The deployment is Running, but the job that narrates it is still writing: the stream must stay open.
        var slow = await (await t.Admin.PostAsync("/api/v1/jobs/echo", new { lines = new[] { "closing line" }, delayMs = 2500 })).ReadAsync();
        var jobId = Guid.Parse(slow.Id());
        await WithDbAsync(async db => await db.Deployments.Where(d => d.Id == Guid.Parse(queued.Id())).ExecuteUpdateAsync(u => u.SetProperty(d => d.JobId, jobId)));

        var open = await t.Viewer.GetJsonAsync($"/api/v1/deployments/{queued.Id()}/logs?source=deploy");
        Assert.False(open["ended"]!.GetValue<bool>(), "the job is still running");

        await GatewayFixture.EventuallyAsync(async () =>
            (await t.Viewer.GetJsonAsync($"/api/v1/deployments/{queued.Id()}/logs?source=deploy"))["ended"]!.GetValue<bool>(), "the stream to end with its job", timeoutMs: 15000);
        var done = await t.Viewer.GetJsonAsync($"/api/v1/deployments/{queued.Id()}/logs?source=deploy");
        Assert.Contains(done["items"]!.AsArray(), l => l!["text"]!.GetValue<string>().Contains("closing line"));
    }
}
