using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Deployments;

[Collection(DeploymentsApiCollection.Name)]
public sealed class BuildAndCredentialApiTests(DeploymentsFixture fixture)
{
    private FakeServerTransport Transport => fixture.Transport;

    private async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    private static BuildOutcome Built(BuildImageCommand c) =>
        new(c.Spec.BuildId, "sha256:img", "sha256:dig", c.Spec.ImageTags, [], 4242, TimeSpan.FromSeconds(3), "dockerfile", "deadbeefcafe", false, "linux/amd64", true);

    private FakeServerTransport Scripted()
    {
        Transport.Commands.Clear();
        return Transport
            .On<BuildImageCommand, BuildOutcome>(Built)
            .On<ContainerCreateCommand, ContainerCreated>(c => new ContainerCreated("cid-build", c.Spec.Name, ContainerRunState.Running, []))
            .On<ContainerRemoveCommand, Unit>(_ => Unit.Value)
            .On<ImageRemoveCommand, Unit>(_ => Unit.Value)
            .On<NetworkCreateCommand, DockerNetwork>(c => new DockerNetwork("n", c.NetworkName, "bridge", "local", c.Internal, true, false, [], new Dictionary<string, string>(), null, []));
    }

    private async Task<(Tenant Tenant, string AppId)> GitAppAsync(object? build = null)
    {
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var (_, env) = await tenant.CreateProjectWithEnvironmentAsync();
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("api"), environmentId = env, serverId = server.Id(), sourceKind = "git",
            gitSource = new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" },
            build = build ?? new { engine = "dockerfile", context = ".", dockerfilePath = "Dockerfile" },
        });
        return (tenant, app.Id());
    }

    private async Task<Deployment> DeployAndWaitAsync(Tenant t, string appId)
    {
        var response = await t.Developer.PostAsync($"/api/v1/applications/{appId}/deployments", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = Guid.Parse((await response.ReadAsync()).Id());
        await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(db =>
            db.Deployments.AsNoTracking().AnyAsync(d => d.Id == id && (d.Status == DeploymentStatus.Running || d.Status == DeploymentStatus.Failed))), "the deployment to finish", 15000);
        await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(db =>
            db.Jobs.AsNoTracking().AnyAsync(j => j.Id == db.Deployments.First(d => d.Id == id).JobId && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed))), "the job to finish", 15000);
        return await WithDbAsync(db => db.Deployments.AsNoTracking().Include(d => d.Steps).FirstAsync(d => d.Id == id));
    }

    [RequiresDatabaseFact]
    public async Task A_git_deployment_builds_tags_the_image_per_deployment_and_records_the_build()
    {
        Scripted();
        var (t, appId) = await GitAppAsync();

        var d = await DeployAndWaitAsync(t, appId);

        Assert.Equal(DeploymentStatus.Running, d.Status);
        Assert.Equal("deadbeefcafe", d.CommitSha);
        Assert.StartsWith("aethera/api-", d.ImageRef!);
        Assert.EndsWith(":" + d.Id, d.ImageRef!);
        Assert.Equal("sha256:dig", d.ImageDigest);

        var build = await WithDbAsync(db => db.Builds.AsNoTracking().SingleAsync(b => b.DeploymentId == d.Id));
        Assert.Equal(BuildStatus.Succeeded, build.Status);
        Assert.Equal(d.BuildId, build.Id);
        Assert.Equal("dockerfile", build.Engine);
        Assert.True(build.CacheHit);
        Assert.Equal(4242, build.ResultImageSizeBytes);
        Assert.Equal("linux/amd64", build.Platform);

        var command = Transport.Commands.Select(c => c.Command).OfType<BuildImageCommand>().Single();
        Assert.Equal(build.Id.ToString(), command.Spec.BuildId); // the agent streams logs as build:<build id>
        Assert.Equal("https://github.com/acme/api.git", command.Spec.Git!.Url);
        Assert.Equal("main", command.Spec.Git.Ref);
        Assert.Equal(1, command.Spec.Git.Depth);
    }

    [RequiresDatabaseFact]
    public async Task A_failed_build_marks_the_build_and_the_deployment_failed_at_the_build_step()
    {
        Scripted().Fail<BuildImageCommand, BuildOutcome>(CommandErrorCode.BuildFailed, "npm ERR! missing script: build");
        var (t, appId) = await GitAppAsync();

        var d = await DeployAndWaitAsync(t, appId);

        Assert.Equal(DeploymentStatus.Failed, d.Status);
        Assert.Equal(DeploymentStep.Build, d.FailedStep);
        Assert.Equal("build.failed", d.FailureCode);
        Assert.Contains("missing script", d.FailureReason);
        var build = await WithDbAsync(db => db.Builds.AsNoTracking().SingleAsync(b => b.DeploymentId == d.Id));
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.DoesNotContain(Transport.Commands, c => c.Command is ContainerCreateCommand);
    }

    [RequiresDatabaseFact]
    public async Task Internal_networks_are_created_without_outside_connectivity()
    {
        Scripted();
        var tenant = await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync();
        var (project, env) = await tenant.CreateProjectWithEnvironmentAsync();
        var app = await tenant.CreateApplicationAsync(env, server.Id());
        await WithDbAsync(async db =>
        {
            var network = new Network { ProjectId = Guid.Parse(project.Id()), ServerId = Guid.Parse(server.Id()), Name = "db", DockerName = "proj-db", IsInternal = true };
            db.Networks.Add(network);
            db.WorkloadNetworks.Add(new WorkloadNetwork { WorkloadId = Guid.Parse(app.Id()), NetworkId = network.Id, Aliases = ["api"] });
            await db.SaveChangesAsync();
            return 0;
        });
        Transport.On<ImagePullCommand, ImagePulled>(_ => new ImagePulled("id", "dg", 1, [], false));

        var d = await DeployAndWaitAsync(tenant, app.Id());

        Assert.Equal(DeploymentStatus.Running, d.Status);
        var create = Transport.Commands.Select(c => c.Command).OfType<NetworkCreateCommand>().Single(n => n.NetworkName == "proj-db");
        Assert.True(create.Internal);
        var container = Transport.Commands.Select(c => c.Command).OfType<ContainerCreateCommand>().Single();
        Assert.Contains(container.Spec.Networks!, n => n.Network == "proj-db" && n.Aliases!.Contains("api"));
    }

    // ---- git credentials ------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Git_credentials_are_write_only_and_deletion_needs_the_name()
    {
        var t = await fixture.NewTenantAsync();
        var created = await t.Admin.CreateAsync("/api/v1/git-credentials", new { name = "gh", kind = "token", provider = "gitHub", username = "bot", value = "ghp_supersecret" });
        Assert.DoesNotContain("ghp_supersecret", created.ToJsonString());
        Assert.Equal("token", created["kind"]!.GetValue<string>());

        var got = await t.Viewer.GetJsonAsync($"/api/v1/git-credentials/{created.Id()}");
        Assert.DoesNotContain("ghp_supersecret", got.ToJsonString());
        Assert.Single((await t.Viewer.GetJsonAsync("/api/v1/git-credentials"))["items"]!.AsArray());

        await (await t.Admin.DeleteAsync($"/api/v1/git-credentials/{created.Id()}")).AssertProblemAsync(428, "confirmation.required");
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/git-credentials/{created.Id()}?confirm=gh")).StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Developers_cannot_create_git_credentials_and_an_in_use_credential_cannot_be_deleted()
    {
        var t = await fixture.NewTenantAsync();
        var body = new { name = "k", kind = "deployKey", value = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----" };
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.PostAsync("/api/v1/git-credentials", body)).StatusCode);

        var credential = await t.Admin.CreateAsync("/api/v1/git-credentials", body);
        var server = await t.CreateServerAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("priv"), environmentId = env, serverId = server.Id(), sourceKind = "git",
            gitSource = new { repositoryUrl = "git@github.com:acme/private.git", branch = "main", gitCredentialId = credential.Id() },
        });

        await (await t.Admin.DeleteAsync($"/api/v1/git-credentials/{credential.Id()}?confirm=k")).AssertProblemAsync(409, "git_credential.in_use");
    }

    [RequiresDatabaseFact]
    public async Task A_deployment_passes_the_git_credential_to_the_agent_but_never_logs_it()
    {
        Scripted();
        var t = await fixture.NewTenantAsync();
        var credential = await t.Admin.CreateAsync("/api/v1/git-credentials", new { name = "tok", kind = "token", provider = "gitHub", username = "bot", value = "ghp_topsecretvalue" });
        var server = await t.CreateServerAsync();
        var (_, env) = await t.CreateProjectWithEnvironmentAsync();
        var app = await t.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("priv"), environmentId = env, serverId = server.Id(), sourceKind = "git",
            gitSource = new { repositoryUrl = "https://github.com/acme/private.git", branch = "main", gitCredentialId = credential.Id() },
        });

        var d = await DeployAndWaitAsync(t, app.Id());

        Assert.Equal(DeploymentStatus.Running, d.Status);
        var spec = Transport.Commands.Select(c => c.Command).OfType<BuildImageCommand>().Single().Spec;
        Assert.Equal("ghp_topsecretvalue", spec.Git!.Credentials!.HttpsToken!.Value);
        Assert.Equal("bot", spec.Git.Credentials.HttpsUsername);
        var logged = await WithDbAsync(db => db.LogChunks.AsNoTracking().Select(c => c.Data).ToListAsync());
        Assert.DoesNotContain(logged, text => text.Contains("ghp_topsecretvalue"));
        Assert.DoesNotContain("ghp_topsecretvalue", d.ConfigSnapshotJson);
    }

    // ---- detection and proxy --------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Build_detection_returns_candidates_from_the_agent()
    {
        Transport.On<BuildDetectCommand, BuildDetection>(c => new BuildDetection(
        [
            new BuildCandidateInfo(BuildEngineKind.Dockerfile, "", 0.95, "Dockerfile found at ./Dockerfile", "Dockerfile", "", "", "", "", "", [8080]),
            new BuildCandidateInfo(BuildEngineKind.Nixpacks, "", 0.5, "package.json found", "", "npm ci", "npm run build", "", "", "node", [3000]),
        ], "c0ffee"));
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync();

        var result = await t.Developer.PostAsync($"/api/v1/servers/{server.Id()}/build-detect", new { repositoryUrl = "https://github.com/acme/api.git", branch = "main" });

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var json = await result.ReadAsync();
        Assert.Equal("c0ffee", json["commitSha"]!.GetValue<string>());
        Assert.Equal("dockerfile", json["candidates"]![0]!["engine"]!.GetValue<string>());
        Assert.Equal(8080, json["candidates"]![0]!["suggestedPorts"]![0]!.GetValue<int>());
        Assert.Null(json["candidates"]![0]!["installCommand"]);
    }

    [RequiresDatabaseFact]
    public async Task Build_detection_rejects_option_like_repository_urls()
    {
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync();
        var response = await t.Developer.PostAsync($"/api/v1/servers/{server.Id()}/build-detect", new { repositoryUrl = "--upload-pack=touch /tmp/x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [RequiresDatabaseFact]
    public async Task Ensuring_the_proxy_runs_the_provider_command_and_is_admin_only()
    {
        Transport.On<ProxyEnsureCommand, ProxyEnsured>(_ => new ProxyEnsured("traefik", "p1", "v3.1", true, true, "hash"));
        var t = await fixture.NewTenantAsync();
        var server = await t.CreateServerAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Developer.PostAsync($"/api/v1/servers/{server.Id()}/proxy/ensure", new { })).StatusCode);
        var response = await t.Admin.PostAsync($"/api/v1/servers/{server.Id()}/proxy/ensure", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.ReadAsync())["running"]!.GetValue<bool>());
    }
}
