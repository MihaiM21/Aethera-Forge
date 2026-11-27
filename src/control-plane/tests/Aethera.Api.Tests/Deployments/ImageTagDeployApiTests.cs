using System.Net;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Tests.Deployments;

/// <summary>Deploying a pinned image tag (<c>POST /applications/{id}/deployments</c> with <c>imageTag</c>), as CI does after pushing an image.</summary>
public sealed partial class DeploymentApiTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static async Task<HttpResponseMessage> DeployTagAsync(Tenant t, string appId, string imageTag, string kind = "applications") =>
        await t.Developer.PostAsync($"/api/v1/{kind}/{appId}/deployments", new { imageTag, reason = "ci run 42" });

    private static async Task<string> AcceptedIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.ReadAsync()).Id();
    }

    [RequiresDatabaseFact]
    public async Task An_image_tag_deploys_exactly_that_tag_and_leaves_the_configured_tag_alone()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync(new { image = new { image = "nginx", tag = "1.27", pullPolicy = "always" } });

        var response = await DeployTagAsync(t, appId, "3f2a9c1");
        var queued = await response.ReadAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("3f2a9c1", queued["imageTag"]!.GetValue<string>());
        var d = await WaitForAsync(queued.Id(), Finished, "the pinned deployment");

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Equal("3f2a9c1", d.ImageTag);
        Assert.Equal("nginx:3f2a9c1", d.ImageRef);
        Assert.Contains(Transport.Commands, c => c.Command is ImagePullCommand p && p.Reference == "nginx:3f2a9c1");
        Assert.DoesNotContain(Transport.Commands, c => c.Command is ImagePullCommand p && p.Reference == "nginx:1.27");

        var app = await t.Developer.GetJsonAsync($"/api/v1/applications/{appId}");
        Assert.Equal("1.27", app["image"]!["tag"]!.GetValue<string>());
        var detail = await t.Viewer.GetJsonAsync($"/api/v1/deployments/{queued.Id()}");
        Assert.Equal("3f2a9c1", detail["imageTag"]!.GetValue<string>());
    }

    [RequiresDatabaseTheory]
    [InlineData("docker.io/library/nginx:1.28", "1.28", "nginx:1.28")]
    [InlineData("nginx:1.28", "1.28", "nginx:1.28")]
    [InlineData(Digest, "@" + Digest, "nginx@" + Digest)]
    [InlineData("nginx@" + Digest, "@" + Digest, "nginx@" + Digest)]
    public async Task Full_references_of_the_same_repository_and_digests_are_accepted(string requested, string tag, string reference)
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();

        var d = await WaitForAsync(await AcceptedIdAsync(await DeployTagAsync(t, appId, requested)), Finished, "the deployment");

        Assert.Equal(tag, d.ImageTag);
        Assert.Equal(reference, d.ImageRef);
    }

    [RequiresDatabaseFact]
    public async Task A_rollback_to_a_pinned_deployment_runs_its_tag_again()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        var pinned = await WaitForAsync(await AcceptedIdAsync(await DeployTagAsync(t, appId, "aaa111")), Finished, "first");
        await WaitForAsync(await AcceptedIdAsync(await DeployTagAsync(t, appId, "bbb222")), d => d.Status == DeploymentStatus.Running, "second");

        var response = await t.Developer.PostAsync($"/api/v1/deployments/{pinned.Id}/rollback", new { });
        var rollback = await WaitForAsync(await AcceptedIdAsync(response), d => d.Status == DeploymentStatus.Running, "the rollback");

        Assert.Equal(DeploymentTrigger.Rollback, rollback.Trigger);
        Assert.Equal("aaa111", rollback.ImageTag);
        Assert.Equal("nginx:aaa111", rollback.ImageRef);
    }

    [RequiresDatabaseFact]
    public async Task A_deploy_without_a_tag_still_runs_the_configured_one()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        await WaitForAsync(await AcceptedIdAsync(await DeployTagAsync(t, appId, "pinned")), Finished, "pinned");

        var plain = await WaitForAsync((await DeployAsync(t, appId)).Id(), d => d.Status == DeploymentStatus.Running, "plain");

        Assert.Null(plain.ImageTag);
        Assert.Equal("nginx:1.27", plain.ImageRef);
    }

    [RequiresDatabaseTheory]
    [InlineData("", "image_tag.invalid")]
    [InlineData("-leading-dash", "image_tag.invalid")]
    [InlineData("has space", "image_tag.invalid")]
    [InlineData("sha256:short", "image_tag.invalid")]
    [InlineData("nginx:", "image_tag.invalid")]
    [InlineData("ghcr.io/someone/else:1.0", "image_tag.repository_mismatch")]
    [InlineData("redis:7", "image_tag.repository_mismatch")]
    public async Task Bad_tags_are_refused_with_422_and_queue_nothing(string requested, string code)
    {
        var (t, appId) = await ImageAppAsync();

        var response = await DeployTagAsync(t, appId, requested);

        var body = await response.ReadJsonAsync();
        response.AssertProblem(body, 422, "validation.failed");
        Assert.Equal("/imageTag", body["errors"]![0].Str("pointer"));
        Assert.Equal(code, body["errors"]![0].Str("code"));
        var list = await t.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/deployments");
        Assert.Empty(list["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task A_tag_on_a_git_application_or_a_service_is_refused()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var git = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("git"), environmentId = env, serverId = server.Id(), sourceKind = "git",
            gitSource = new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" },
            build = new { engine = "dockerfile", context = ".", dockerfilePath = "Dockerfile" },
        });
        var service = await t.Developer.CreateAsync("/api/v1/services", new { name = Tenant.Unique("cache"), environmentId = env, serverId = server.Id(), templateKey = "redis" });

        foreach (var (kind, id) in new[] { ("applications", git.Id()), ("services", service.Id()) })
        {
            var response = await DeployTagAsync(t, id, "1.0", kind);
            var body = await response.ReadJsonAsync();
            response.AssertProblem(body, 422, "validation.failed");
            Assert.Equal("image_tag.not_image_source", body["errors"]![0].Str("code"));
        }
    }

    [RequiresDatabaseFact]
    public async Task The_tag_and_the_reason_are_audited()
    {
        Healthy();
        var (t, appId) = await ImageAppAsync();
        await WaitForAsync(await AcceptedIdAsync(await DeployTagAsync(t, appId, "3f2a9c1")), Finished, "the deployment");

        var id = Guid.Parse(appId);
        var metadata = await WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "application.deploy_requested" && e.ResourceId == id).Select(e => e.MetadataJson).FirstAsync());
        Assert.Contains("3f2a9c1", metadata);
        Assert.Contains("ci run 42", metadata);
    }
}
