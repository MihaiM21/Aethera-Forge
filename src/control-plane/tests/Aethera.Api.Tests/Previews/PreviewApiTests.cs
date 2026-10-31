using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Previews;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Previews;

/// <summary>
/// Pull request previews end to end (R2.3, ADR 0012): signed webhook -> preview row + deployment + job -> worker -> engine -> (fake) agent, against a real database.
/// </summary>
[Collection(PreviewsApiCollection.Name)]
public sealed class PreviewApiTests(PreviewsFixture fixture)
{
    private readonly PreviewHarness _h = new(fixture);

    private FakeServerTransport Transport => fixture.Transport;

    private static Guid DeploymentIdOf(JsonNode result) => Guid.Parse(result["deploymentId"]!.GetValue<string>());

    private static string Outcome(JsonNode result) => result["outcome"]!.GetValue<string>().ToLowerInvariant();

    // ---- lifecycle ------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task An_opened_pull_request_gets_a_preview_of_its_own_and_the_application_is_untouched()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync(withCredential: true);

        var accepted = await app.SendOkAsync("opened", 7);
        Assert.Equal("accepted", Outcome(accepted));
        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(accepted));

        Assert.Equal(DeploymentStatus.Running, deployment.Status);
        Assert.Equal(DeploymentTrigger.PullRequest, deployment.Trigger);
        Assert.Equal("feature/login", deployment.Ref);
        Assert.Equal(PreviewApp.HeadSha, deployment.CommitSha);
        Assert.Equal(Guid.Parse(app.ServerId), deployment.ServerId);
        Assert.False(deployment.CanRollbackTo);

        var preview = await _h.WaitForPreviewAsync(app, 7, p => p.Status == PreviewStatus.Running, "the preview to run");
        Assert.Equal(deployment.Id, preview.CurrentDeploymentId);
        Assert.Equal(deployment.PreviewId, preview.Id);
        Assert.False(preview.FromFork);
        Assert.Equal("Add the login page", preview.Title);
        Assert.Matches(@"^pr-7-site-[0-9a-f]+-[0-9a-f]{6}\.203-0-113-10\.sslip\.io$", preview.Hostname!);

        // The container: the preview's names, its label, its own route, none of the application's.
        var create = Transport.Commands.Select(c => c.Command).OfType<ContainerCreateCommand>().Single();
        Assert.Contains("-pr-7-", create.Spec.Name);
        Assert.Equal(preview.Id.ToString(), create.Spec.Labels!["aethera.preview"]);
        Assert.Contains(create.Spec.Labels, kv => kv.Value.Contains($"Host(`{preview.Hostname}`)"));
        Assert.Contains(create.Spec.Networks!, n => n.Network == "aethera-proxy");
        var build = Transport.Commands.Select(c => c.Command).OfType<BuildImageCommand>().Single();
        Assert.Equal("feature/login", build.Spec.Git!.Ref);
        Assert.Equal(PreviewApp.HeadSha, build.Spec.Git.Commit);
        Assert.Equal(PreviewApp.RepositoryUrl, build.Spec.Git.Url);

        // The application: no status change, no current deployment, no deployment in its history.
        var json = await app.Tenant.Developer.GetJsonAsync($"/api/v1/applications/{app.AppId}");
        Assert.NotEqual("running", json["state"]!["status"]!.GetValue<string>());
        Assert.Null(json["state"]!["currentDeploymentId"]);
        Assert.Empty((await app.Tenant.Viewer.GetJsonAsync($"/api/v1/applications/{app.AppId}/deployments"))["items"]!.AsArray());
        var previewDeployments = await app.Tenant.Viewer.GetJsonAsync($"/api/v1/applications/{app.AppId}/deployments?previewId={preview.Id}");
        Assert.Single(previewDeployments["items"]!.AsArray());
        Assert.Equal(preview.Id.ToString(), previewDeployments["items"]![0]!["previewId"]!.GetValue<string>());

        // The job took the preview's lock, not the application's.
        var job = await _h.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == deployment.JobId));
        Assert.Equal($"preview:{preview.Id}", job.LockKey);

        // The API.
        var list = await app.Tenant.Viewer.GetJsonAsync($"/api/v1/applications/{app.AppId}/previews");
        var item = Assert.Single(list["items"]!.AsArray())!;
        Assert.Equal(7, item["number"]!.GetValue<int>());
        Assert.Equal("running", item["status"]!.GetValue<string>());
        Assert.Equal($"http://{preview.Hostname}", item["url"]!.GetValue<string>());
        Assert.Equal(deployment.Id.ToString(), item["lastDeployment"]!["id"]!.GetValue<string>());

        // The comment: created while building, edited when running, with the URL and without the token.
        await GatewayFixture.EventuallyAsync(() => Task.FromResult(_h.Commenter.For(app.Ref(7)).Any(c => c.Markdown.Contains("Running"))), "the comment to say running");
        var calls = _h.Commenter.For(app.Ref(7));
        Assert.Contains("Building and deploying", calls[0].Markdown);
        Assert.Null(calls[0].ExistingCommentId);
        Assert.NotNull(calls[^1].ExistingCommentId);
        Assert.Contains($"http://{preview.Hostname}", calls[^1].Markdown);
        Assert.DoesNotContain("ghp_PREVIEWTOKEN123", string.Join("\n", calls.Select(c => c.Markdown)));
        Assert.Equal("ghp_PREVIEWTOKEN123", calls[0].Token);
        Assert.NotNull((await _h.PreviewAsync(app, 7)).ProviderCommentId);
    }

    [RequiresDatabaseFact]
    public async Task A_push_to_the_pull_request_deploys_again_and_replaces_the_previous_container()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();
        var first = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 3)));
        Assert.Equal(DeploymentStatus.Running, first.Status);

        var newSha = "cafebabecafebabecafebabecafebabecafebabe";
        var second = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("synchronize", 3, sha: newSha)));

        Assert.Equal(DeploymentStatus.Running, second.Status);
        Assert.Equal(newSha, second.CommitSha);
        Assert.Equal(DeploymentStatus.Superseded, (await _h.DeploymentAsync(first.Id)).Status);
        Assert.Contains(Transport.Commands, c => c.Command is ContainerRemoveCommand r && r.Container == first.ContainerIds[0]);
        var preview = await _h.WaitForPreviewAsync(app, 3, p => p.CurrentDeploymentId == second.Id && p.Status == PreviewStatus.Running, "the preview to serve the new commit");
        Assert.Equal(newSha, preview.HeadSha);
        Assert.Equal(1, await _h.WithDbAsync(db => db.Previews.CountAsync(p => p.ApplicationId == app.AppGuid)));
    }

    [RequiresDatabaseFact]
    public async Task A_failed_preview_deployment_fails_the_preview_not_the_application_and_the_comment_says_where()
    {
        _h.Scripted().Fail<BuildImageCommand, BuildOutcome>(CommandErrorCode.BuildFailed, "npm ERR! missing script: build");
        var app = await _h.NewAppAsync(withCredential: true);

        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 9)));

        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        Assert.Equal(DeploymentStep.Build, deployment.FailedStep);
        var preview = await _h.WaitForPreviewAsync(app, 9, p => p.Status == PreviewStatus.Failed, "the preview to fail");
        Assert.Contains("missing script", preview.StatusReason);
        var json = await app.Tenant.Developer.GetJsonAsync($"/api/v1/applications/{app.AppId}");
        Assert.NotEqual("failed", json["state"]!["status"]!.GetValue<string>());
        await GatewayFixture.EventuallyAsync(() => Task.FromResult(_h.Commenter.For(app.Ref(9)).Any(c => c.Markdown.Contains("Failed at the build step"))), "the failure to be commented");
        Assert.DoesNotContain("npm ERR", string.Join("\n", _h.Commenter.For(app.Ref(9)).Select(c => c.Markdown)));
    }

    [RequiresDatabaseFact]
    public async Task A_server_without_a_domain_base_fails_the_preview_with_a_clear_reason()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync(withDomainBase: false);

        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 2)));

        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        Assert.Equal("preview.no_domain", deployment.FailureCode);
        Assert.Contains("wildcard domain", deployment.FailureReason);
        Assert.DoesNotContain(Transport.Commands, c => c.Command is BuildImageCommand);
        var preview = await _h.WaitForPreviewAsync(app, 2, p => p.Status == PreviewStatus.Failed, "the preview to fail");
        Assert.NotNull(preview.StatusReason);
    }

    // ---- closing --------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_closed_pull_request_tears_the_preview_down_idempotently()
    {
        var app = await _h.NewAppAsync(withCredential: true);
        _h.Scripted();
        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 5)));
        var preview = await _h.WaitForPreviewAsync(app, 5, p => p.Status == PreviewStatus.Running, "the preview to run");
        var containerId = deployment.ContainerIds[0];
        // The agent lists what carries the preview's label; the volume of the preview is named after its snapshot.
        Transport.On<ContainerListCommand, IReadOnlyList<DockerContainer>>(c =>
            c.LabelFilters!.Contains($"aethera.preview={preview.Id}") ? [PreviewHarness.Container(containerId)] : []);
        Transport.Commands.Clear();

        var closed = await app.SendOkAsync("closed", 5);
        Assert.Equal("accepted", Outcome(closed));
        await _h.WaitForRemovedAsync(app, 5);

        var removed = await _h.PreviewAsync(app, 5);
        Assert.NotNull(removed.RemovedAt);
        Assert.NotNull(removed.ClosedAt);
        var commands = Transport.Commands.Select(c => c.Command).ToList();
        Assert.Contains(commands, c => c is ContainerListCommand l && l.LabelFilters!.Contains($"aethera.preview={preview.Id}"));
        Assert.Contains(commands, c => c is ContainerStopCommand s && s.Container == containerId);
        Assert.Contains(commands, c => c is ContainerRemoveCommand r && r.Container == containerId);
        Assert.Contains(commands, c => c is ImageRemoveCommand i && i.Image == deployment.ImageRef);
        Assert.DoesNotContain(commands, c => c is ContainerRemoveCommand r && !r.Container.Equals(containerId));
        Assert.Equal(DeploymentStatus.Stopped, (await _h.DeploymentAsync(deployment.Id)).Status);
        await GatewayFixture.EventuallyAsync(() => Task.FromResult(_h.Commenter.For(app.Ref(5)).Any(c => c.Markdown.Contains("removed"))), "the removal to be commented");

        // The job ran under the preview's lock; a second closed event changes nothing.
        var job = (await TeardownJobsAsync(preview.Id)).Single();
        Assert.Equal($"preview:{preview.Id}", job.LockKey);
        var again = await app.SendOkAsync("closed", 5, HttpStatusCode.OK);
        Assert.Equal("ignored", Outcome(again));
        Assert.Single(await TeardownJobsAsync(preview.Id));

        // A preview that is gone is gone from the default list, and its URL with it.
        Assert.Empty((await app.Tenant.Viewer.GetJsonAsync($"/api/v1/applications/{app.AppId}/previews"))["items"]!.AsArray());
        var withRemoved = await app.Tenant.Viewer.GetJsonAsync($"/api/v1/applications/{app.AppId}/previews?includeRemoved=true");
        Assert.Null(withRemoved["items"]![0]!["url"]);
    }

    [RequiresDatabaseFact]
    public async Task A_merged_pull_request_is_removed_too_and_the_teardown_can_be_resumed()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();
        await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 6)));
        await _h.WaitForPreviewAsync(app, 6, p => p.Status == PreviewStatus.Running, "the preview to run");

        // The first attempt cannot reach the server; the retry does.
        var failures = 0;
        Transport.On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ =>
        {
            if (Interlocked.Increment(ref failures) == 1) throw new ServerTransportException(TransportErrors.AgentUnavailable, "The agent is down.");
            return [];
        });

        await app.SendOkAsync("closed", 6, merged: true);
        await _h.WaitForRemovedAsync(app, 6);

        var preview = await _h.PreviewAsync(app, 6);
        Assert.Equal(PreviewStatus.Removed, preview.Status);
        Assert.True(failures >= 2, "the teardown was attempted again after the transient failure");
    }

    // ---- webhook rules --------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Applications_without_previews_and_pull_requests_to_other_branches_are_ignored()
    {
        _h.Scripted();
        var off = await _h.NewAppAsync(previews: false);
        var offResult = await off.SendOkAsync("opened", 1, HttpStatusCode.OK);
        Assert.Equal("ignored", Outcome(offResult));
        Assert.Contains("turned off", offResult["detail"]!.GetValue<string>());
        Assert.Equal(0, await _h.WithDbAsync(db => db.Previews.CountAsync(p => p.ApplicationId == off.AppGuid)));
        Assert.Equal(0, await _h.WithDbAsync(db => db.Deployments.CountAsync(d => d.WorkloadId == off.AppGuid)));

        var app = await _h.NewAppAsync();
        var response = await app.SendAsync("opened", 4, branch: "feature/x", target: "develop");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadAsync();
        Assert.Equal("ignored", Outcome(result));
        Assert.Contains("develop", result["detail"]!.GetValue<string>());
        Assert.Equal(0, await _h.WithDbAsync(db => db.Previews.CountAsync(p => p.ApplicationId == app.AppGuid)));

        // Events that change nothing for a preview.
        Assert.Equal("ignored", Outcome(await app.SendOkAsync("labeled", 4, HttpStatusCode.OK)));
    }

    [RequiresDatabaseFact]
    public async Task A_bad_signature_is_unauthorized_and_a_redelivery_is_a_duplicate()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync("opened", 1, sigSecret: "wrong")).StatusCode);
        Assert.Equal(0, await _h.WithDbAsync(db => db.Previews.CountAsync(p => p.ApplicationId == app.AppGuid)));

        Assert.Equal(HttpStatusCode.Accepted, (await app.SendAsync("opened", 1, delivery: "same-delivery")).StatusCode);
        var again = await app.SendAsync("opened", 1, delivery: "same-delivery");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("duplicate", Outcome(await again.ReadAsync()));
        Assert.Equal(1, await _h.WithDbAsync(db => db.Deployments.CountAsync(d => d.WorkloadId == app.AppGuid)));

        var deliveries = await app.Tenant.Developer.GetJsonAsync($"/api/v1/applications/{app.AppId}/webhook/deliveries");
        Assert.Contains(deliveries.AsArray(), d => d!["eventType"]!.GetValue<string>() == "pull_request" && d["ref"]!.GetValue<string>() == "pull/1");
    }

    // ---- forks ----------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Pull_requests_from_forks_are_ignored_unless_allowed_and_only_an_administrator_allows_them()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();

        var ignored = await app.SendOkAsync("opened", 8, HttpStatusCode.OK, headRepo: "octocat/site");
        Assert.Equal("ignored", Outcome(ignored));
        Assert.Contains("fork", ignored["detail"]!.GetValue<string>());
        Assert.Equal(0, await _h.WithDbAsync(db => db.Previews.CountAsync(p => p.ApplicationId == app.AppGuid)));

        // A developer cannot turn the setting on; an administrator can.
        var denied = await app.Tenant.Developer.PutAsync($"/api/v1/applications/{app.AppId}/previews/settings", Json.Body(new { enabled = true, allowForks = true }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var allowed = await app.Tenant.Admin.PutAsync($"/api/v1/applications/{app.AppId}/previews/settings", Json.Body(new { enabled = true, allowForks = true }));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True((await allowed.ReadAsync())["allowForks"]!.GetValue<bool>());
        Assert.NotEmpty(await app.Tenant.AuditAsync(app.AppId, "application.previews_configured"));

        var accepted = await app.SendOkAsync("opened", 8, headRepo: "octocat/site", branch: "fix/typo");
        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(accepted));
        var preview = await _h.PreviewAsync(app, 8);
        Assert.True(preview.FromFork);
        Assert.Equal("https://github.com/octocat/site.git", preview.SourceRepositoryUrl);
        Assert.Equal(DeploymentStatus.Running, deployment.Status);
        var build = Transport.Commands.Select(c => c.Command).OfType<BuildImageCommand>().Single();
        Assert.Equal("https://github.com/octocat/site.git", build.Spec.Git!.Url);
        Assert.Equal("fix/typo", build.Spec.Git.Ref);
        var log = await app.Tenant.Viewer.GetJsonAsync($"/api/v1/deployments/{deployment.Id}/logs?source=deploy");
        Assert.Contains(log["items"]!.AsArray(), l => l!["text"]!.GetValue<string>().Contains("from a fork"));
    }

    [RequiresDatabaseFact]
    public async Task A_fork_on_another_git_host_is_ignored_even_when_forks_are_allowed()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync(allowForks: true);
        // The webhook says the head repository is on another host: the application's credential must never be sent there.
        var body = """{"action":"opened","number":12,"pull_request":{"number":12,"title":"x","user":{"login":"o"},"head":{"ref":"x","sha":"6dcb09b5b57875f334f61aebed695e2e4193db5e","repo":{"full_name":"evil/site","clone_url":"https://evil.example.net/evil/site.git"}},"base":{"ref":"main","repo":{"full_name":"acme/site"}}}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, app.EndpointPath) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Event", "pull_request");
        request.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(app.Secret), System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant());

        var response = await fixture.Factory.CreateAnonymousClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ignored", Outcome(await response.ReadAsync()));
        Assert.Equal(0, await _h.WithDbAsync(db => db.Previews.CountAsync(p => p.ApplicationId == app.AppGuid)));
    }

    // ---- slots and sweep ------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Pull_requests_beyond_the_limit_wait_as_pending_and_deploy_when_a_slot_frees_up()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync(maxActive: 1);
        await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 1)));
        await _h.WaitForPreviewAsync(app, 1, p => p.Status == PreviewStatus.Running, "the first preview to run");

        var held = await app.SendOkAsync("opened", 2);
        Assert.Equal("accepted", Outcome(held));
        Assert.Null(held["deploymentId"]);
        Assert.Contains("free preview slot", held["detail"]!.GetValue<string>());
        var pending = await _h.PreviewAsync(app, 2);
        Assert.Equal(PreviewStatus.Pending, pending.Status);
        Assert.Equal(0, await _h.WithDbAsync(db => db.Deployments.CountAsync(d => d.PreviewId == pending.Id)));

        // A push to the running preview is not a new slot.
        Assert.Equal("accepted", Outcome(await app.SendOkAsync("synchronize", 1, sha: "cafebabecafebabecafebabecafebabecafebabe")));

        // The first pull request closes: its teardown frees the slot for the second.
        await app.SendOkAsync("closed", 1);
        await _h.WaitForRemovedAsync(app, 1);
        var second = await _h.WaitForPreviewAsync(app, 2, p => p.Status == PreviewStatus.Running, "the pending preview to run");
        Assert.NotNull(second.CurrentDeploymentId);
    }

    [RequiresDatabaseFact]
    public async Task The_sweep_removes_stale_previews_and_deploys_pending_ones()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync(maxActive: 2);
        await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 1)));
        var running = await _h.WaitForPreviewAsync(app, 1, p => p.Status == PreviewStatus.Running, "the first preview to run");
        await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 3)));
        var stale = await _h.WaitForPreviewAsync(app, 3, p => p.Status == PreviewStatus.Running, "the third preview to run");
        // The second pull request found both slots taken; the third one has been silent for 15 days.
        await app.SendOkAsync("opened", 2);
        Assert.Equal(PreviewStatus.Pending, (await _h.PreviewAsync(app, 2)).Status);
        await _h.WithDbAsync(db => db.Previews.Where(p => p.Id == stale.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.LastEventAt, DateTimeOffset.UtcNow.AddDays(-15))));

        var report = await SweepAsync(app);

        Assert.Equal(1, report.Stale);
        Assert.Equal(1, report.Deployed);
        await _h.WaitForRemovedAsync(app, 3);
        await _h.WaitForPreviewAsync(app, 2, p => p.Status == PreviewStatus.Running, "the pending preview to run");
        Assert.Equal(running.Id, (await _h.PreviewAsync(app, 1)).Id);
        Assert.Equal(PreviewStatus.Running, (await _h.PreviewAsync(app, 1)).Status);

        // A second pass has nothing left to do.
        Assert.Equal(new PreviewSweepReport(0, 0, 0), await SweepAsync(app));
    }

    private async Task<PreviewSweepReport> SweepAsync(PreviewApp app)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await PreviewSweepJobHandler.SweepAsync(
            scope.ServiceProvider.GetRequiredService<AetheraDbContext>(), scope.ServiceProvider.GetRequiredService<PreviewService>(), scope.ServiceProvider.GetRequiredService<IClock>(),
            app.Tenant.Identity.OrganizationId, CancellationToken.None);
    }

    [RequiresDatabaseFact]
    public async Task The_sweep_scheduler_queues_one_sweep_per_organization_and_day()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();
        await app.SendOkAsync("opened", 1);
        var scheduler = fixture.Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<PreviewSweepScheduler>().Single();

        await scheduler.QueueOnceAsync(CancellationToken.None);
        await scheduler.QueueOnceAsync(CancellationToken.None);

        var org = app.Tenant.Identity.OrganizationId;
        Assert.Equal(1, await _h.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == "preview.sweep" && j.OrganizationId == org)));
    }

    // ---- API ------------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_preview_deployment_is_never_a_rollback_point()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();
        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 1)));
        Assert.True(deployment.IsRollbackPoint);

        var response = await app.Tenant.Developer.PostAsync($"/api/v1/deployments/{deployment.Id}/rollback", new { });
        await response.AssertProblemAsync(409, "deployment.not_rollback_point");
        var dto = await app.Tenant.Viewer.GetJsonAsync($"/api/v1/deployments/{deployment.Id}");
        Assert.False(dto["canRollbackTo"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Redeploy_and_remove_work_from_the_api_and_respect_roles()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();
        await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 4)));
        await _h.WaitForPreviewAsync(app, 4, p => p.Status == PreviewStatus.Running, "the preview to run");

        Assert.Equal(HttpStatusCode.Forbidden, (await app.Tenant.Viewer.PostAsync($"/api/v1/applications/{app.AppId}/previews/4/redeploy", new { })).StatusCode);
        var redeploy = await app.Tenant.Developer.PostAsync($"/api/v1/applications/{app.AppId}/previews/4/redeploy", new { });
        Assert.Equal(HttpStatusCode.Accepted, redeploy.StatusCode);
        var second = await _h.WaitForDeploymentAsync(Guid.Parse((await redeploy.ReadAsync()).Id()));
        Assert.Equal(DeploymentStatus.Running, second.Status);
        Assert.Equal(DeploymentTrigger.PullRequest, second.Trigger);
        Assert.Equal(2, await _h.WithDbAsync(db => db.Deployments.CountAsync(d => d.WorkloadId == app.AppGuid && d.PreviewId != null)));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Tenant.Developer.PostAsync($"/api/v1/applications/{app.AppId}/previews/99/redeploy", new { })).StatusCode);
        var other = await fixture.NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Developer.GetAsync($"/api/v1/applications/{app.AppId}/previews")).StatusCode);

        var removal = await app.Tenant.Developer.DeleteAsync($"/api/v1/applications/{app.AppId}/previews/4");
        Assert.Equal(HttpStatusCode.Accepted, removal.StatusCode);
        Assert.Equal("closing", (await removal.ReadAsync())["status"]!.GetValue<string>());
        await _h.WaitForRemovedAsync(app, 4);

        // A removed preview cannot be redeployed; the pull request has to be reopened (or pushed to).
        await (await app.Tenant.Developer.PostAsync($"/api/v1/applications/{app.AppId}/previews/4/redeploy", new { })).AssertProblemAsync(409, "preview.removed");
        Assert.NotEmpty(await app.Tenant.AuditAsync(null, "preview.teardown_requested"));
        var again = await app.SendOkAsync("reopened", 4);
        var third = await _h.WaitForDeploymentAsync(DeploymentIdOf(again));
        Assert.Equal(DeploymentStatus.Running, third.Status);
        var reopened = await _h.PreviewAsync(app, 4);
        Assert.Equal(PreviewStatus.Running, reopened.Status);
        Assert.Null(reopened.RemovedAt);
    }

    [RequiresDatabaseFact]
    public async Task Settings_are_validated_and_describe_the_setup()
    {
        var app = await _h.NewAppAsync(previews: false);

        var before = await app.Tenant.Viewer.GetJsonAsync($"/api/v1/applications/{app.AppId}/previews/settings");
        Assert.False(before["enabled"]!.GetValue<bool>());
        Assert.False(before["allowForks"]!.GetValue<bool>());
        Assert.Equal(10, before["defaultMaxActive"]!.GetValue<int>());
        Assert.True(before["webhookConfigured"]!.GetValue<bool>());
        Assert.Equal(app.EndpointPath, before["webhookPath"]!.GetValue<string>());
        Assert.Matches(@"^pr-<number>-site-.*\.203-0-113-10\.sslip\.io$", before["hostnamePattern"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.Tenant.Admin.PutAsync($"/api/v1/applications/{app.AppId}/previews/settings", Json.Body(new { enabled = true, allowForks = false, maxActive = 0 }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Tenant.Admin.PutAsync($"/api/v1/applications/{app.AppId}/previews/settings", Json.Body(new { enabled = true, allowForks = false, serverId = Guid.NewGuid() }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Tenant.Viewer.PutAsync($"/api/v1/applications/{app.AppId}/previews/settings", Json.Body(new { enabled = true, allowForks = false }))).StatusCode);

        // An application that is not built from git cannot have previews.
        var image = await app.Tenant.CreateApplicationAsync(app.EnvironmentId, app.ServerId);
        await (await app.Tenant.Admin.PutAsync($"/api/v1/applications/{image.Id()}/previews/settings", Json.Body(new { enabled = true, allowForks = false }))).AssertProblemAsync(409, "previews.unsupported");

        var other = await app.Tenant.CreateServerAsync(publicIp: "203.0.113.20");
        var saved = await app.Tenant.Developer.PutAsync($"/api/v1/applications/{app.AppId}/previews/settings", Json.Body(new { enabled = true, allowForks = false, serverId = other.Id(), maxActive = 3 }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var after = await saved.ReadAsync();
        Assert.Equal(other.Id(), after["serverId"]!.GetValue<string>());
        Assert.Equal(3, after["maxActive"]!.GetValue<int>());
        Assert.Contains("203-0-113-20", after["hostnamePattern"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Previews_run_on_the_server_chosen_for_them()
    {
        _h.Scripted();
        var tenant = await fixture.NewTenantAsync();
        var previewServer = await tenant.CreateServerAsync(publicIp: "198.51.100.7");
        var app = await _h.NewAppAsync(existing: tenant);
        await tenant.Admin.PutAsync($"/api/v1/applications/{app.AppId}/previews/settings", Json.Body(new { enabled = true, allowForks = false, serverId = previewServer.Id() }));

        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 11)));

        Assert.Equal(Guid.Parse(previewServer.Id()), deployment.ServerId);
        var preview = await _h.PreviewAsync(app, 11);
        Assert.Equal(Guid.Parse(previewServer.Id()), preview.ServerId);
        Assert.EndsWith(".198-51-100-7.sslip.io", preview.Hostname);
        Assert.All(Transport.Commands.Where(c => c.Command is ContainerCreateCommand or BuildImageCommand), c => Assert.Equal(Guid.Parse(previewServer.Id()), c.ServerId));
    }

    // ---- variables ------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Preview_variables_override_regular_ones_in_previews_and_never_reach_production()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();
        var t = app.Tenant;
        await t.Developer.CreateAsync($"/api/v1/applications/{app.AppId}/env-vars", new { key = "DATABASE_URL", value = "postgres://prod/db" });
        await t.Developer.CreateAsync($"/api/v1/applications/{app.AppId}/env-vars", new { key = "DATABASE_URL", value = "postgres://preview/db", isPreview = true });
        var onlyPreview = await t.Developer.CreateAsync($"/api/v1/applications/{app.AppId}/env-vars", new { key = "FEATURE_FLAGS", value = "all", isPreview = true });
        Assert.True(onlyPreview["isPreview"]!.GetValue<bool>());
        await (await t.Developer.PostAsync($"/api/v1/applications/{app.AppId}/env-vars", new { key = "DATABASE_URL", value = "again", isPreview = true })).AssertProblemAsync(409, "env_var.already_exists");

        var list = await t.Viewer.GetJsonAsync($"/api/v1/applications/{app.AppId}/env-vars");
        Assert.Equal(3, list["items"]!.AsArray().Count);
        Assert.Contains("DATABASE_URL='postgres://prod/db'", await (await t.Viewer.GetAsync($"/api/v1/applications/{app.AppId}/env-vars/export")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("FEATURE_FLAGS", await (await t.Viewer.GetAsync($"/api/v1/applications/{app.AppId}/env-vars/export")).Content.ReadAsStringAsync());

        await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 10)));
        var previewEnv = Transport.Commands.Select(c => c.Command).OfType<ContainerCreateCommand>().Single().Spec.Env!.ToDictionary(e => e.Name, e => e.Plain);
        Assert.Equal("postgres://preview/db", previewEnv["DATABASE_URL"]);
        Assert.Equal("all", previewEnv["FEATURE_FLAGS"]);

        // A production deployment of the same application sees the regular variable only.
        Transport.Commands.Clear();
        var production = await (await t.Developer.PostAsync($"/api/v1/applications/{app.AppId}/deployments", new { })).ReadAsync();
        await _h.WaitForDeploymentAsync(Guid.Parse(production.Id()));
        var productionEnv = Transport.Commands.Select(c => c.Command).OfType<ContainerCreateCommand>().Single().Spec.Env!.ToDictionary(e => e.Name, e => e.Plain);
        Assert.Equal("postgres://prod/db", productionEnv["DATABASE_URL"]);
        Assert.DoesNotContain("FEATURE_FLAGS", productionEnv.Keys);

        // The production deployment promoted the application; the preview stays what it was.
        var json = await t.Developer.GetJsonAsync($"/api/v1/applications/{app.AppId}");
        Assert.Equal("running", json["state"]!["status"]!.GetValue<string>());
        Assert.Equal(PreviewStatus.Running, (await _h.PreviewAsync(app, 10)).Status);
        var productionContainer = Transport.Commands.Select(c => c.Command).OfType<ContainerCreateCommand>().Single().Spec;
        Assert.DoesNotContain("aethera.preview", productionContainer.Labels!.Keys);
        Assert.DoesNotContain("-pr-", productionContainer.Name);
    }

    [RequiresDatabaseFact]
    public async Task Only_applications_have_preview_variables_and_a_key_can_move_between_scopes()
    {
        var app = await _h.NewAppAsync(previews: false);
        var t = app.Tenant;
        var service = await t.Developer.CreateAsync("/api/v1/services", new { name = "db", environmentId = app.EnvironmentId, serverId = app.ServerId, templateKey = "redis" });
        var denied = await t.Developer.PostAsync($"/api/v1/services/{service.Id()}/env-vars", new { key = "X", value = "1", isPreview = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);

        var regular = await t.Developer.CreateAsync($"/api/v1/applications/{app.AppId}/env-vars", new { key = "MODE", value = "prod" });
        await t.Developer.CreateAsync($"/api/v1/applications/{app.AppId}/env-vars", new { key = "MODE", value = "preview", isPreview = true });
        var move = await t.Developer.PatchAsync($"/api/v1/applications/{app.AppId}/env-vars/{regular.Id()}", new { isPreview = true });
        await move.AssertProblemAsync(409, "env_var.already_exists");
    }

    // ---- GitLab ---------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task GitLab_merge_requests_make_previews_too()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync(provider: GitProvider.GitLab, withCredential: true);

        var accepted = await app.SendOkAsync("open", 21);
        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(accepted));
        var preview = await _h.WaitForPreviewAsync(app, 21, p => p.Status == PreviewStatus.Running, "the merge request preview to run");

        Assert.Equal(DeploymentStatus.Running, deployment.Status);
        Assert.Equal(GitProvider.GitLab, preview.Provider);
        Assert.StartsWith("pr-21-", preview.Hostname);
        await GatewayFixture.EventuallyAsync(() => Task.FromResult(_h.Commenter.For(app.Ref(21)).Any(c => c.Markdown.Contains("Running"))), "the merge request comment");

        // A wrong token is refused; an update without new commits (a new title) changes nothing; a close removes the preview.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync("open", 22, sigSecret: "nope")).StatusCode);
        Transport.Commands.Clear();
        Assert.Equal("accepted", Outcome(await app.SendOkAsync("update", 21)));
        await app.SendOkAsync("close", 21);
        await _h.WaitForRemovedAsync(app, 21);
    }

    [RequiresDatabaseFact]
    public async Task A_comment_that_cannot_be_posted_never_fails_the_deployment()
    {
        _h.Scripted();
        _h.Commenter.Error = "GitHub answered 403 Forbidden: Resource not accessible by personal access token";
        try
        {
            var app = await _h.NewAppAsync(withCredential: true);
            var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 31)));

            Assert.Equal(DeploymentStatus.Running, deployment.Status);
            var preview = await _h.WaitForPreviewAsync(app, 31, p => p.CommentError != null, "the comment error to be stored");
            Assert.Contains("403", preview.CommentError);
            Assert.DoesNotContain("ghp_PREVIEWTOKEN123", preview.CommentError);
            Assert.Equal(PreviewStatus.Running, preview.Status);
        }
        finally
        {
            _h.Commenter.Error = null;
        }
    }

    [RequiresDatabaseFact]
    public async Task Without_a_token_credential_nothing_is_commented()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();
        await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 32)));
        await Task.Delay(200);
        Assert.Empty(_h.Commenter.For(app.Ref(32)));
        Assert.Null((await _h.PreviewAsync(app, 32)).CommentError);
    }

    [RequiresDatabaseFact]
    public async Task Deleting_the_application_takes_the_previews_down_with_it()
    {
        _h.Scripted();
        var app = await _h.NewAppAsync();
        var deployment = await _h.WaitForDeploymentAsync(DeploymentIdOf(await app.SendOkAsync("opened", 41)));
        var preview = await _h.WaitForPreviewAsync(app, 41, p => p.Status == PreviewStatus.Running, "the preview to run");
        Transport.On<ContainerListCommand, IReadOnlyList<DockerContainer>>(c =>
            c.LabelFilters!.Any(f => f.StartsWith("aethera.workload=", StringComparison.Ordinal)) ? [PreviewHarness.Container(deployment.ContainerIds[0])] : []);
        Transport.Commands.Clear();

        var delete = await app.Tenant.Admin.DeleteAsync($"/api/v1/applications/{app.AppId}?confirm={Uri.EscapeDataString(await SlugOfAsync(app))}");
        Assert.True(delete.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted, $"delete answered {delete.StatusCode}");

        // The application's own teardown finds the preview's container through the workload label and removes it.
        await GatewayFixture.EventuallyAsync(() => Task.FromResult(Transport.Commands.Any(c => c.Command is ContainerRemoveCommand r && r.Container == deployment.ContainerIds[0])), "the preview container to be removed");

        // The sweep takes care of the rest (volumes, images, the row).
        var report = await SweepAsync(app);
        Assert.Equal(1, report.Stale);
        await _h.WaitForRemovedAsync(app, 41);
        Assert.Equal(preview.Id, (await _h.PreviewAsync(app, 41)).Id);
    }

    private async Task<List<Job>> TeardownJobsAsync(Guid previewId) =>
        (await _h.WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.Type == "preview.teardown").ToListAsync())).Where(j => j.PayloadJson.Contains(previewId.ToString())).ToList();

    private async Task<string> SlugOfAsync(PreviewApp app) =>
        (await app.Tenant.Viewer.GetJsonAsync($"/api/v1/applications/{app.AppId}"))["slug"]!.GetValue<string>();
}
