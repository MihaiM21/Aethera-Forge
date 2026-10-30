using System.Net;
using System.Text.Json;
using Aethera.Domain;
using Aethera.Engine.Deployments;
using Aethera.Engine.Git;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Previews;

namespace Aethera.Api.Tests.Previews;

/// <summary>An <see cref="IHttpClientFactory"/> whose client answers from a script and records what was sent.</summary>
public sealed class ScriptedHttp : IHttpClientFactory
{
    public List<(HttpMethod Method, string Url, string? Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = [];

    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new();

    public ScriptedHttp Answer(HttpStatusCode status, string json = "{}") => Answer(_ => new HttpResponseMessage(status) { Content = new StringContent(json) });

    public ScriptedHttp Answer(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _answers.Enqueue(answer);
        return this;
    }

    public HttpClient CreateClient(string name) => new(new Handler(this));

    private sealed class Handler(ScriptedHttp owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            owner.Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), headers));
            return owner._answers.Count > 0 ? owner._answers.Dequeue()(request) : throw new InvalidOperationException("No scripted answer left.");
        }
    }
}

public class PullRequestCommenterTests
{
    private const string Token = "ghp_SECRETTOKEN0123456789";

    private static PullRequestRef GitHubPr(string url = "https://github.com/acme/site.git", int number = 7) => new(GitProvider.GitHub, url, number);

    [Fact]
    public async Task GitHub_creates_the_comment_once_with_the_token_in_the_header_only()
    {
        var http = new ScriptedHttp().Answer(HttpStatusCode.Created, """{"id": 555, "body": "x"}""");

        var result = await new GitHubPullRequestCommenter(http).UpsertAsync(GitHubPr(), null, "### Aethera preview", Token, default);

        Assert.True(result.Posted);
        Assert.Equal("555", result.CommentId);
        Assert.Null(result.Error);
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/repos/acme/site/issues/7/comments", request.Url);
        Assert.Equal($"Bearer {Token}", request.Headers["Authorization"]);
        Assert.Equal("### Aethera preview", JsonDocument.Parse(request.Body!).RootElement.GetProperty("body").GetString());
        Assert.DoesNotContain(Token, request.Body);
    }

    [Fact]
    public async Task GitHub_edits_the_comment_it_made_before()
    {
        var http = new ScriptedHttp().Answer(HttpStatusCode.OK, """{"id": 555}""");

        var result = await new GitHubPullRequestCommenter(http).UpsertAsync(GitHubPr(), "555", "updated", Token, default);

        Assert.Equal("555", result.CommentId);
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("https://api.github.com/repos/acme/site/issues/comments/555", request.Url);
    }

    [Fact]
    public async Task GitHub_creates_the_comment_again_when_it_was_deleted()
    {
        var http = new ScriptedHttp().Answer(HttpStatusCode.NotFound, """{"message":"Not Found"}""").Answer(HttpStatusCode.Created, """{"id": 777}""");

        var result = await new GitHubPullRequestCommenter(http).UpsertAsync(GitHubPr(), "555", "again", Token, default);

        Assert.Equal("777", result.CommentId);
        Assert.Null(result.Error);
        Assert.Equal([HttpMethod.Patch, HttpMethod.Post], http.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task GitHub_enterprise_uses_the_api_v3_path_of_its_host()
    {
        var http = new ScriptedHttp().Answer(HttpStatusCode.Created, """{"id": 1}""");

        await new GitHubPullRequestCommenter(http).UpsertAsync(GitHubPr("git@ghe.example.com:acme/site.git"), null, "x", Token, default);

        Assert.Equal("https://ghe.example.com/api/v3/repos/acme/site/issues/7/comments", http.Requests[0].Url);
    }

    [Fact]
    public async Task A_provider_error_is_returned_not_thrown_and_never_repeats_the_token()
    {
        var http = new ScriptedHttp().Answer(HttpStatusCode.Forbidden, $$"""{"message":"Bad credentials for {{Token}}"}""");

        var result = await new GitHubPullRequestCommenter(http).UpsertAsync(GitHubPr(), null, "x", Token, default);

        Assert.False(result.Posted);
        Assert.Contains("403", result.Error);
        Assert.Contains("Bad credentials", result.Error);
        Assert.DoesNotContain(Token, result.Error);
    }

    [Fact]
    public async Task A_failed_edit_keeps_the_comment_id_for_the_next_try()
    {
        var http = new ScriptedHttp().Answer(HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var result = await new GitHubPullRequestCommenter(http).UpsertAsync(GitHubPr(), "555", "x", Token, default);

        Assert.Equal("555", result.CommentId);
        Assert.Contains("502", result.Error);
    }

    [Fact]
    public async Task Timeouts_and_network_errors_are_results()
    {
        var timeout = new ScriptedHttp().Answer(_ => throw new TaskCanceledException("timed out"));
        var network = new ScriptedHttp().Answer(_ => throw new HttpRequestException("connection refused"));

        var slow = await new GitHubPullRequestCommenter(timeout).UpsertAsync(GitHubPr(), null, "x", Token, default);
        var down = await new GitHubPullRequestCommenter(network).UpsertAsync(GitHubPr(), null, "x", Token, default);

        Assert.Contains("did not answer in time", slow.Error);
        Assert.Contains("could not be reached", down.Error);
    }

    [Theory]
    [InlineData("https://localhost/acme/site.git")]
    [InlineData("https://127.0.0.1/acme/site.git")]
    [InlineData("https://10.0.0.5/acme/site.git")]
    [InlineData("https://gitserver/acme/site.git")]
    [InlineData("not a url")]
    public async Task Hosts_that_are_not_public_git_hosts_get_no_call_and_no_token(string url)
    {
        var http = new ScriptedHttp();

        var result = await new GitHubPullRequestCommenter(http).UpsertAsync(GitHubPr(url), null, "x", Token, default);

        Assert.False(result.Posted);
        Assert.NotNull(result.Error);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task GitLab_posts_a_note_on_the_merge_request_of_the_url_encoded_project()
    {
        var http = new ScriptedHttp().Answer(HttpStatusCode.Created, """{"id": 12}""");
        var reference = new PullRequestRef(GitProvider.GitLab, "https://gitlab.example.com/group/sub/site.git", 3);

        var result = await new GitLabPullRequestCommenter(http).UpsertAsync(reference, null, "hello", "glpat-secret", default);

        Assert.Equal("12", result.CommentId);
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/group%2Fsub%2Fsite/merge_requests/3/notes", request.Url);
        Assert.Equal("glpat-secret", request.Headers["PRIVATE-TOKEN"]);
        Assert.DoesNotContain("glpat-secret", request.Body);
    }

    [Fact]
    public async Task GitLab_edits_the_note_and_recreates_it_when_it_is_gone()
    {
        var http = new ScriptedHttp().Answer(HttpStatusCode.OK, """{"id": 12}""");
        var reference = new PullRequestRef(GitProvider.GitLab, "https://gitlab.com/acme/site", 3);

        var edited = await new GitLabPullRequestCommenter(http).UpsertAsync(reference, "12", "v2", "t0ken-value", default);

        Assert.Equal("12", edited.CommentId);
        Assert.Equal(HttpMethod.Put, http.Requests[0].Method);
        Assert.Equal("https://gitlab.com/api/v4/projects/acme%2Fsite/merge_requests/3/notes/12", http.Requests[0].Url);

        var gone = new ScriptedHttp().Answer(HttpStatusCode.NotFound).Answer(HttpStatusCode.Created, """{"id": 13}""");
        var recreated = await new GitLabPullRequestCommenter(gone).UpsertAsync(reference, "12", "v3", "t0ken-value", default);
        Assert.Equal("13", recreated.CommentId);
        Assert.Equal([HttpMethod.Put, HttpMethod.Post], gone.Requests.Select(r => r.Method));
    }

    [Fact]
    public void Each_commenter_serves_its_own_provider()
    {
        var http = new ScriptedHttp();
        Assert.True(new GitHubPullRequestCommenter(http).Supports(GitProvider.GitHub));
        Assert.False(new GitHubPullRequestCommenter(http).Supports(GitProvider.GitLab));
        Assert.True(new GitLabPullRequestCommenter(http).Supports(GitProvider.GitLab));
        Assert.False(new GitLabPullRequestCommenter(http).Supports(GitProvider.Generic));
    }
}

public class PreviewCommentTextTests
{
    [Fact]
    public void A_running_preview_shows_its_url_commit_and_deployment()
    {
        var text = PreviewCommentText.Build(new PreviewCommentModel(PreviewCommentState.Running, 7, "6dcb09b5b57875f334f61aebed695e2e4193db5e", 4, "https://pr-7-web.example.com", null, null, false));

        Assert.StartsWith(PreviewCommentText.Marker, text);
        Assert.Contains("| **Status** | Running |", text);
        Assert.Contains("https://pr-7-web.example.com", text);
        Assert.Contains("`6dcb09b`", text);
        Assert.Contains("#4", text);
        Assert.DoesNotContain("fork", text);
    }

    [Fact]
    public void A_failed_preview_names_the_step_and_the_code_but_no_log_text()
    {
        var text = PreviewCommentText.Build(new PreviewCommentModel(PreviewCommentState.Failed, 7, "abc1234", 2, "https://x.example.com", "Build", "build.failed", true));

        Assert.Contains("Failed at the build step (`build.failed`)", text);
        Assert.DoesNotContain("https://x.example.com", text); // only a running preview has a URL
        Assert.Contains("fork", text);
    }

    [Fact]
    public void A_removed_preview_says_so()
    {
        var text = PreviewCommentText.Build(new PreviewCommentModel(PreviewCommentState.Removed, 7, null, 3, "https://x.example.com", null, null, false));
        Assert.Contains("Removed", text);
        Assert.DoesNotContain("Deployment", text);
        Assert.DoesNotContain("https://x.example.com", text);
    }
}

public class PreviewGitUrlTests
{
    [Theory]
    [InlineData("https://github.com/acme/site.git", "github.com", "acme/site")]
    [InlineData("https://github.com/acme/site", "github.com", "acme/site")]
    [InlineData("git@github.com:acme/site.git", "github.com", "acme/site")]
    [InlineData("ssh://git@gitlab.example.com/group/sub/site.git", "gitlab.example.com", "group/sub/site")]
    [InlineData("https://GitLab.com/Acme/Site/", "gitlab.com", "Acme/Site")]
    public void Repository_addresses_are_split_into_host_and_path(string url, string host, string path) =>
        Assert.Equal(new RepositoryAddress(host, path), RepositoryAddress.Parse(url));

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("https://github.com/")]
    public void Anything_else_is_not_an_address(string url) => Assert.Null(RepositoryAddress.Parse(url));

    [Fact]
    public void A_fork_is_cloned_from_its_own_address_but_only_on_the_same_host()
    {
        Assert.Equal("https://github.com/octocat/site.git", PreviewGitUrls.ForkCloneUrl("https://github.com/acme/site.git", "https://github.com/octocat/site.git"));
        Assert.Equal("git@github.com:octocat/site.git", PreviewGitUrls.ForkCloneUrl("git@github.com:acme/site.git", "https://github.com/octocat/site.git"));
        Assert.Null(PreviewGitUrls.ForkCloneUrl("https://github.com/acme/site.git", "https://evil.example.net/octocat/site.git"));
        Assert.Null(PreviewGitUrls.ForkCloneUrl("https://github.com/acme/site.git", ""));
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("git.example.org", true)]
    [InlineData("localhost", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("[::1]", false)]
    [InlineData("intranet", false)]
    [InlineData("box.local", false)]
    public void Only_public_hosts_are_called(string host, bool expected) => Assert.Equal(expected, PreviewGitUrls.IsPublicHost(host));
}

public class PreviewSnapshotTests
{
    private static Application App(Action<Application>? edit = null)
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var app = new Application
        {
            Name = "Web", Slug = "web", EnvironmentId = Guid.Parse("abcd1234-0000-0000-0000-000000000000"), ServerId = Guid.NewGuid(), SourceKind = ApplicationSourceKind.Git,
            GitSource = new GitSource { RepositoryUrl = "https://github.com/acme/site.git", Branch = "main", GitCredentialId = Guid.NewGuid() },
            BuildConfig = new BuildConfig(),
        };
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(app, id);
        app.Ports.Add(new WorkloadPort { ContainerPort = 3000, PublishedPort = 8080, IsHttp = true });
        app.Volumes.Add(new Volume { Name = "data", MountPath = "/data" });
        app.Volumes.Add(new Volume { Name = "web-11111111-uploads", MountPath = "/uploads" });
        app.Volumes.Add(new Volume { Name = "other-1a2b3c4d-shared", MountPath = "/shared" });
        app.Volumes.Add(new Volume { Name = "prod-config", MountPath = "/etc/prod", HostPath = "/srv/prod" });
        app.Volumes.Add(new Volume { Name = "ro-config", MountPath = "/etc/ro", HostPath = "/srv/ro", ReadOnly = true });
        app.CustomLabels["traefik.http.routers.web.rule"] = "Host(`prod.example.com`)";
        var domain = new WorkloadDomain { WorkloadId = id, ServerId = Guid.NewGuid(), IsAutomatic = true, IsPrimary = true, TargetPort = 3000 };
        domain.SetHostname("web-3fa2c1.apps.example.com");
        app.Domains.Add(domain);
        var other = new WorkloadDomain { WorkloadId = id, ServerId = Guid.NewGuid() };
        other.SetHostname("www.example.com");
        app.Domains.Add(other);
        app.EnvironmentVariables.Add(new EnvironmentVariable { WorkloadId = id, Key = "A", Value = "regular" });
        app.EnvironmentVariables.Add(new EnvironmentVariable { WorkloadId = id, Key = "B", Value = "regular-b" });
        app.EnvironmentVariables.Add(new EnvironmentVariable { WorkloadId = id, Key = "A", Value = "preview", IsPreview = true });
        app.EnvironmentVariables.Add(new EnvironmentVariable { WorkloadId = id, Key = "C", Value = "only-preview", IsPreview = true });
        edit?.Invoke(app);
        return app;
    }

    private static PreviewEnvironment Preview(Application app, bool fork = false) => new()
    {
        ApplicationId = app.Id, ServerId = app.ServerId, Number = 12, SourceBranch = "feature/login",
        SourceRepositoryUrl = fork ? "https://github.com/octocat/site.git" : "https://github.com/acme/site.git", FromFork = fork,
    };

    private static readonly string Sha = "6dcb09b5b57875f334f61aebed695e2e4193db5e";

    [Fact]
    public void A_preview_has_its_own_names_source_route_and_variables()
    {
        var app = App();
        var preview = Preview(app);

        var result = PreviewSnapshotFactory.Create(app, preview, Sha, "pr-12-web-3fa2c1.apps.example.com", https: true);
        var s = result.Snapshot;

        Assert.Equal("web-pr-12", s.Slug);
        Assert.Equal(app.Id, s.WorkloadId);
        Assert.Equal(new PreviewSnapshot(preview.Id, 12), s.Preview);
        Assert.Equal(new GitSnapshot("https://github.com/acme/site.git", "feature/login", Sha, app.GitSource!.GitCredentialId), s.Git);
        // The route: the preview's host only, with the port of the application's own domain.
        var route = Assert.Single(s.Domains);
        Assert.Equal(new DomainEntry("pr-12-web-3fa2c1.apps.example.com", "/", true, 3000), route);
        // Variables: preview-only values win, and a preview-only key joins.
        Assert.Equal(["A", "B", "C"], s.Env.Select(e => e.Key));
        Assert.Equal("preview", s.Env.Single(e => e.Key == "A").Value);
        Assert.Equal("regular-b", s.Env.Single(e => e.Key == "B").Value);
        // Nothing of the application's own wiring.
        Assert.All(s.Ports, p => Assert.Null(p.PublishedPort));
        Assert.Null(s.CustomLabels);
        Assert.Equal([s.Slug], s.Networks.Single(n => n.DockerName == DeploymentPlanBuilder.EnvironmentNetwork(app.EnvironmentId)).Aliases);
    }

    [Fact]
    public void The_regular_snapshot_never_contains_preview_variables()
    {
        var snapshot = DeploymentSnapshotFactory.Create(App());

        Assert.Equal(["A", "B"], snapshot.Env.Select(e => e.Key));
        Assert.Equal("regular", snapshot.Env.Single(e => e.Key == "A").Value);
    }

    [Fact]
    public void Volumes_are_the_previews_own_and_host_paths_that_could_be_written_stay_out()
    {
        var app = App();
        var result = PreviewSnapshotFactory.Create(app, Preview(app), Sha, "pr-12-web.example.com", https: false);

        var names = result.Snapshot.Volumes.Where(v => v.HostPath is null).Select(v => VolumeNames.DockerName(result.Snapshot.Slug, app.Id, v.Name)).ToList();
        var production = app.Volumes.Where(v => v.HostPath is null).Select(v => VolumeNames.DockerName(app.Slug, app.Id, v.Name)).ToList();
        Assert.Equal(3, names.Count);
        Assert.Empty(names.Intersect(production));
        Assert.All(names, n => Assert.StartsWith("web-pr-12-55555555-", n));
        // The read-only host path stays; the writable one is named in the notes.
        Assert.Equal(["/srv/ro"], result.Snapshot.Volumes.Where(v => v.HostPath is not null).Select(v => v.HostPath));
        Assert.Contains(result.Notes, n => n.Contains("/srv/prod"));
    }

    [Fact]
    public void A_fork_is_cloned_from_its_own_repository_and_flagged_in_the_log()
    {
        var app = App();
        var result = PreviewSnapshotFactory.Create(app, Preview(app, fork: true), Sha, "pr-12-web.example.com", https: false);

        Assert.Equal("https://github.com/octocat/site.git", result.Snapshot.Git!.Url);
        Assert.Contains(result.Notes, n => n.Contains("fork"));
        Assert.False(result.Snapshot.Domains.Single().Https);
    }

    [Fact]
    public void Unusable_sources_and_branches_are_refused_with_a_code()
    {
        var image = App(a => { a.SourceKind = ApplicationSourceKind.DockerImage; a.GitSource = null; a.ImageSource = new ImageSource { Image = "nginx" }; });
        Assert.Equal("preview.no_git_source", Assert.Throws<UnsupportedSourceException>(() => PreviewSnapshotFactory.Create(image, Preview(image), Sha, "h.example.com", false)).Code);

        var app = App();
        var bad = Preview(app);
        bad.SourceBranch = "--upload-pack=evil";
        Assert.Equal("source.invalid_ref", Assert.Throws<UnsupportedSourceException>(() => PreviewSnapshotFactory.Create(app, bad, Sha, "h.example.com", false)).Code);
    }

    [Fact]
    public void A_compose_preview_routes_to_the_service_found_in_the_file()
    {
        var app = App(a => { a.SourceKind = ApplicationSourceKind.Compose; a.ComposeSource = new ComposeSource { FilePath = "docker-compose.yml" }; a.Domains.Clear(); });
        var snapshot = PreviewSnapshotFactory.Create(app, Preview(app), Sha, "pr-12-web.example.com", false).Snapshot;
        Assert.Null(snapshot.Domains.Single().ComposeService);

        var services = new[]
        {
            new Aethera.Domain.Transport.ComposeServiceDefinition("db", "postgres", false, [5432]),
            new Aethera.Domain.Transport.ComposeServiceDefinition("web", "", true, [8080]),
        };
        var completed = PreviewSnapshotFactory.CompleteComposeRoute(snapshot, services);
        Assert.Equal("web", completed.Domains.Single().ComposeService);
        Assert.Equal(8080, completed.Domains.Single().TargetPort);

        var none = new[] { new Aethera.Domain.Transport.ComposeServiceDefinition("db", "postgres", false, [5432]) };
        Assert.Equal("preview.no_route", Assert.Throws<ComposeDiscoveryException>(() => PreviewSnapshotFactory.CompleteComposeRoute(snapshot, none)).Code);
    }
}
