using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Git;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Tests.Previews;

/// <summary>Records the comments the previews would post and answers like a provider that accepted them.</summary>
public sealed class FakePullRequestCommenter : IPullRequestCommenter
{
    private int _next = 1000;

    public List<CommentCall> Calls { get; } = [];

    /// <summary>When set, the next calls answer with this error instead of posting.</summary>
    public string? Error { get; set; }

    public bool Supports(GitProvider provider) => provider is GitProvider.GitHub or GitProvider.GitLab;

    public Task<PullRequestCommentResult> UpsertAsync(PullRequestRef pullRequest, string? existingCommentId, string markdown, string apiToken, CancellationToken ct)
    {
        lock (Calls) Calls.Add(new CommentCall(pullRequest, existingCommentId, markdown, apiToken));
        return Task.FromResult(Error is { } error
            ? new PullRequestCommentResult(existingCommentId, error)
            : new PullRequestCommentResult(existingCommentId ?? Interlocked.Increment(ref _next).ToString(), null));
    }

    public List<CommentCall> For(PullRequestRef pullRequest) { lock (Calls) return Calls.Where(c => c.Ref == pullRequest).ToList(); }
}

public sealed record CommentCall(PullRequestRef Ref, string? ExistingCommentId, string Markdown, string Token);

/// <summary>The API with fast job workers, a fake agent transport and a fake commenter.</summary>
public sealed class PreviewsFixture : IDisposable
{
    public PreviewsFixture()
    {
        Base = new AetheraApiFactory();
        Transport = new FakeServerTransport();
        Commenter = new FakePullRequestCommenter();
        Factory = Jobs.JobsApiFixture.Configure(Base).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IServerTransport>();
            services.AddSingleton<IServerTransport>(Transport);
            services.RemoveAll<IPullRequestCommenter>();
            services.AddSingleton<IPullRequestCommenter>(Commenter);
        }));
    }

    public AetheraApiFactory Base { get; }
    public WebApplicationFactory<Program> Factory { get; }
    public FakeServerTransport Transport { get; }
    public FakePullRequestCommenter Commenter { get; }

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class PreviewsApiCollection : ICollectionFixture<PreviewsFixture>
{
    public const string Name = "previews-api";
}

/// <summary>An application with a webhook and previews, plus helpers to send pull request events to it.</summary>
public sealed class PreviewApp(PreviewsFixture fixture, Tenant tenant, string appId, string serverId, string environmentId, string endpointPath, string secret, GitProvider provider)
{
    public const string HeadSha = "6dcb09b5b57875f334f61aebed695e2e4193db5e";
    public const string RepositoryUrl = "https://github.com/acme/site.git";

    private int _delivery;

    public Tenant Tenant { get; } = tenant;
    public string AppId { get; } = appId;
    public string ServerId { get; } = serverId;
    public string EnvironmentId { get; } = environmentId;
    public string EndpointPath { get; } = endpointPath;
    public string Secret { get; } = secret;
    public GitProvider Provider { get; } = provider;

    public Guid AppGuid => Guid.Parse(AppId);

    public PullRequestRef Ref(int number) => new(Provider, RepositoryUrl, number);

    /// <summary>Sends a signed pull request (or merge request) event and returns the response.</summary>
    public async Task<HttpResponseMessage> SendAsync(string action, int number, bool merged = false, string? headRepo = null, string branch = "feature/login", string target = "main",
        string? sha = null, string? delivery = null, string? sigSecret = null, string oldrev = "1111111")
    {
        var request = Provider == GitProvider.GitLab
            ? GitLabRequest(action, number, headRepo, branch, target, sha ?? HeadSha, delivery ?? $"mr-{number}-{Interlocked.Increment(ref _delivery)}-{Guid.NewGuid():N}", sigSecret ?? Secret, oldrev)
            : GitHubRequest(action, number, merged, headRepo, branch, target, sha ?? HeadSha, delivery ?? $"pr-{number}-{Interlocked.Increment(ref _delivery)}-{Guid.NewGuid():N}", sigSecret ?? Secret);
        return await fixture.Factory.CreateAnonymousClient().SendAsync(request);
    }

    private HttpRequestMessage GitHubRequest(string action, int number, bool merged, string? headRepo, string branch, string target, string sha, string delivery, string secret)
    {
        headRepo ??= "acme/site";
        var body = new JsonObject
        {
            ["action"] = action, ["number"] = number,
            ["pull_request"] = new JsonObject
            {
                ["number"] = number, ["title"] = "Add the login page", ["merged"] = merged, ["user"] = new JsonObject { ["login"] = "octocat" },
                ["head"] = new JsonObject
                {
                    ["ref"] = branch, ["sha"] = sha,
                    ["repo"] = new JsonObject { ["full_name"] = headRepo, ["clone_url"] = $"https://github.com/{headRepo}.git" },
                },
                ["base"] = new JsonObject
                {
                    ["ref"] = target, ["sha"] = "9f2b0c1a1e4d7b8a6c5d4e3f2a1b0c9d8e7f6a5b",
                    ["repo"] = new JsonObject { ["full_name"] = "acme/site", ["clone_url"] = "https://github.com/acme/site.git" },
                },
            },
        }.ToJsonString();
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, EndpointPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Event", "pull_request");
        request.Headers.Add("X-GitHub-Delivery", delivery);
        request.Headers.Add("X-Hub-Signature-256", signature);
        return request;
    }

    private HttpRequestMessage GitLabRequest(string action, int number, string? headRepo, string branch, string target, string sha, string delivery, string secret, string oldrev)
    {
        var fork = headRepo is not null && headRepo != "acme/site";
        var body = new JsonObject
        {
            ["object_kind"] = "merge_request", ["user"] = new JsonObject { ["username"] = "ann" },
            ["project"] = new JsonObject { ["id"] = 1, ["git_http_url"] = "https://github.com/acme/site.git" },
            ["object_attributes"] = new JsonObject
            {
                ["iid"] = number, ["title"] = "Add the login page", ["action"] = action, ["oldrev"] = oldrev, ["source_branch"] = branch, ["target_branch"] = target,
                ["source_project_id"] = fork ? 2 : 1, ["target_project_id"] = 1, ["last_commit"] = new JsonObject { ["id"] = sha },
                ["source"] = new JsonObject { ["git_http_url"] = $"https://github.com/{headRepo ?? "acme/site"}.git" },
            },
        }.ToJsonString();
        var request = new HttpRequestMessage(HttpMethod.Post, EndpointPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Gitlab-Event", "Merge Request Hook");
        request.Headers.Add("X-Gitlab-Event-UUID", delivery);
        request.Headers.Add("X-Gitlab-Token", secret);
        return request;
    }

    public async Task<JsonNode> SendOkAsync(string action, int number, HttpStatusCode expected = HttpStatusCode.Accepted, bool merged = false, string? headRepo = null,
        string branch = "feature/login", string? sha = null)
    {
        var response = await SendAsync(action, number, merged, headRepo, branch, sha: sha);
        Assert.Equal(expected, response.StatusCode);
        return await response.ReadAsync();
    }
}

/// <summary>The scripted server and the database helpers shared by the preview tests.</summary>
public sealed class PreviewHarness(PreviewsFixture fixture)
{
    public FakeServerTransport Transport => fixture.Transport;
    public FakePullRequestCommenter Commenter => fixture.Commenter;

    public static DockerContainer Container(string id, ContainerRunState state = ContainerRunState.Running, IReadOnlyDictionary<string, string>? labels = null) =>
        new(id, id, "img", "sha", state, state.ToString(), ContainerHealthState.None, null, null, null, 0, false, 0, labels ?? new Dictionary<string, string>(), [], [], [], null);

    /// <summary>A healthy server that builds, starts containers and answers its probes.</summary>
    public FakeServerTransport Scripted()
    {
        var n = 0;
        Transport.Commands.Clear();
        return Transport
            .On<BuildImageCommand, BuildOutcome>(c => new BuildOutcome(c.Spec.BuildId, "sha256:img", "sha256:dig", c.Spec.ImageTags, [], 4242, TimeSpan.FromSeconds(1), "dockerfile",
                c.Spec.Git?.Commit ?? "deadbeefcafe", false, "linux/amd64", false))
            .On<ImageInspectCommand, DockerImage>(c => new DockerImage("sha256:img", [c.Image], [], 1, null, new Dictionary<string, string>(), "amd64", "linux", 0, ["3000/tcp"]))
            .On<NetworkCreateCommand, DockerNetwork>(c => new DockerNetwork("n", c.NetworkName, "bridge", "local", false, true, false, [], new Dictionary<string, string>(), null, []))
            .On<ContainerCreateCommand, ContainerCreated>(c => new ContainerCreated($"cid-{Interlocked.Increment(ref n)}-{c.Spec.Name}", c.Spec.Name, ContainerRunState.Running, []))
            .On<ContainerRemoveCommand, Unit>(_ => Unit.Value)
            .On<ContainerStopCommand, DockerContainer>(c => Container(c.Container, ContainerRunState.Exited))
            .On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => [])
            .On<VolumeRemoveCommand, Unit>(_ => Unit.Value)
            .On<VolumeListCommand, IReadOnlyList<DockerVolume>>(_ => [])
            .On<ImageRemoveCommand, Unit>(_ => Unit.Value)
            .On<HealthProbeCommand, HealthProbeOutcome>(_ => new HealthProbeOutcome(true, 3, 0, TimeSpan.Zero, "running", null))
            .On<ProxyEnsureCommand, ProxyEnsured>(_ => new ProxyEnsured("traefik", "proxy-1", "v3.1", true, true, "abc"));
    }

    public async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    public async Task<PreviewApp> NewAppAsync(
        GitProvider provider = GitProvider.GitHub, bool previews = true, bool allowForks = false, int? maxActive = null, bool withCredential = false, bool withDomainBase = true,
        object? extra = null, Tenant? existing = null)
    {
        var tenant = existing ?? await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync(publicIp: withDomainBase ? "203.0.113.10" : null);
        var (_, env) = await tenant.CreateProjectWithEnvironmentAsync();

        string? credentialId = null;
        if (withCredential)
        {
            var credential = await tenant.Admin.CreateAsync("/api/v1/git-credentials", new { name = Tenant.Unique("gh"), kind = "token", provider = provider == GitProvider.GitLab ? "gitLab" : "gitHub", username = "bot", value = "ghp_PREVIEWTOKEN123" });
            credentialId = credential.Id();
        }

        var body = new JsonObject
        {
            ["name"] = Tenant.Unique("site"), ["environmentId"] = env, ["serverId"] = server.Id(), ["sourceKind"] = "git",
            ["gitSource"] = new JsonObject { ["repositoryUrl"] = PreviewApp.RepositoryUrl, ["branch"] = "main", ["gitCredentialId"] = credentialId },
            ["build"] = new JsonObject { ["engine"] = "dockerfile", ["context"] = ".", ["dockerfilePath"] = "Dockerfile" },
        };
        if (extra is not null)
            foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(extra, Json.Options)!.AsObject()) body[key] = value?.DeepClone();
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", body);

        var hook = await (await tenant.Developer.PutAsync($"/api/v1/applications/{app.Id()}/webhook", Json.Body(new { provider = provider == GitProvider.GitLab ? "gitLab" : "gitHub" }))).ReadAsync();
        if (previews)
        {
            var response = await tenant.Admin.PutAsync($"/api/v1/applications/{app.Id()}/previews/settings", Json.Body(new { enabled = true, allowForks, maxActive }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        return new PreviewApp(fixture, tenant, app.Id(), server.Id(), env, hook["path"]!.GetValue<string>(), hook["secret"]!.GetValue<string>(), provider);
    }

    public Task<PreviewEnvironment> PreviewAsync(PreviewApp app, int number) =>
        WithDbAsync(db => db.Previews.AsNoTracking().FirstAsync(p => p.ApplicationId == app.AppGuid && p.Number == number));

    public Task<Deployment> DeploymentAsync(Guid id) =>
        WithDbAsync(db => db.Deployments.AsNoTracking().Include(d => d.Steps).FirstAsync(d => d.Id == id));

    public async Task<PreviewEnvironment> WaitForPreviewAsync(PreviewApp app, int number, Func<PreviewEnvironment, bool> done, string what, int timeoutMs = 20000)
    {
        PreviewEnvironment? last = null;
        try
        {
            await GatewayFixture.EventuallyAsync(async () => done(last = await PreviewAsync(app, number)), what, timeoutMs);
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}. Preview: {last?.Status} {last?.StatusReason}");
        }

        return last!;
    }

    /// <summary>Waits for the deployment to end and for its job to finish (the comment and notifications are written after the status).</summary>
    public async Task<Deployment> WaitForDeploymentAsync(Guid id, string what = "the deployment to finish", int timeoutMs = 20000)
    {
        Deployment? last = null;
        try
        {
            await GatewayFixture.EventuallyAsync(async () =>
                (last = await DeploymentAsync(id)).Status is DeploymentStatus.Running or DeploymentStatus.Failed or DeploymentStatus.Cancelled or DeploymentStatus.Superseded, what, timeoutMs);
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}. Deployment: {last?.Status}/{last?.FailureCode} {last?.FailureReason}");
        }

        if (last?.JobId is { } jobId)
            await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(db => db.Jobs.AsNoTracking()
                .AnyAsync(j => j.Id == jobId && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled))), $"the job of {what}", timeoutMs);
        return await DeploymentAsync(id);
    }

    /// <summary>Waits for the teardown job of the preview to finish.</summary>
    public async Task WaitForRemovedAsync(PreviewApp app, int number) =>
        await WaitForPreviewAsync(app, number, p => p.Status == PreviewStatus.Removed, "the preview to be removed");
}
