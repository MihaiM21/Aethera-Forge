using System.Security.Cryptography;
using System.Text;
using Aethera.Domain;
using Aethera.Engine.Deployments;
using Aethera.Engine.Git;
using Aethera.Engine.Proxy;

namespace Aethera.Engine.Tests;

/// <summary>Webhook payloads of pull and merge requests, shaped like the ones GitHub and GitLab send (R2.3, ADR 0012).</summary>
public class PullRequestWebhookTests
{
    private static Dictionary<string, string> GitHubHeaders(string delivery = "d-1") =>
        new() { ["X-GitHub-Event"] = "pull_request", ["X-GitHub-Delivery"] = delivery };

    private static Dictionary<string, string> GitLabHeaders() => new() { ["X-Gitlab-Event"] = "Merge Request Hook", ["X-Gitlab-Event-UUID"] = "uuid-1" };

    private static string GitHubBody(string action, bool merged = false, string headRepo = "acme/site", string baseRepo = "acme/site", string headRef = "feature/login", bool headRepoGone = false) =>
        $$"""
        {
          "action": "{{action}}",
          "number": 42,
          "pull_request": {
            "url": "https://api.github.com/repos/acme/site/pulls/42",
            "id": 1234567,
            "number": 42,
            "state": "{{(action == "closed" ? "closed" : "open")}}",
            "title": "Add the login page",
            "user": { "login": "octocat", "id": 1, "type": "User" },
            "merged": {{(merged ? "true" : "false")}},
            "head": {
              "label": "octocat:{{headRef}}",
              "ref": "{{headRef}}",
              "sha": "6dcb09b5b57875f334f61aebed695e2e4193db5e",
              "user": { "login": "octocat" },
              "repo": {{(headRepoGone ? "null" : $$"""{ "id": 7, "name": "site", "full_name": "{{headRepo}}", "clone_url": "https://github.com/{{headRepo}}.git", "fork": {{(headRepo != baseRepo ? "true" : "false")}} }""")}}
            },
            "base": {
              "label": "acme:main",
              "ref": "main",
              "sha": "9f2b0c1a1e4d7b8a6c5d4e3f2a1b0c9d8e7f6a5b",
              "repo": { "id": 3, "name": "site", "full_name": "{{baseRepo}}", "clone_url": "https://github.com/{{baseRepo}}.git" }
            }
          },
          "repository": { "id": 3, "full_name": "{{baseRepo}}" },
          "sender": { "login": "octocat" }
        }
        """;

    private static WebhookEvent ParseGitHub(string body) => new GitHubProvider().Parse(GitHubHeaders(), Encoding.UTF8.GetBytes(body))!;

    [Theory]
    [InlineData("opened", false, PullRequestAction.Opened)]
    [InlineData("reopened", false, PullRequestAction.Reopened)]
    [InlineData("synchronize", false, PullRequestAction.Updated)]
    [InlineData("closed", false, PullRequestAction.Closed)]
    [InlineData("closed", true, PullRequestAction.Merged)]
    [InlineData("labeled", false, PullRequestAction.Other)]
    [InlineData("edited", false, PullRequestAction.Other)]
    [InlineData("review_requested", false, PullRequestAction.Other)]
    public void GitHub_pull_request_actions_map_to_preview_actions(string action, bool merged, PullRequestAction expected)
    {
        var e = ParseGitHub(GitHubBody(action, merged));

        Assert.False(e.IsPush);
        Assert.Equal("pull_request", e.EventType);
        Assert.Equal("d-1", e.DeliveryId);
        var pr = e.PullRequest!;
        Assert.Equal(expected, pr.Action);
        Assert.Equal(42, pr.Number);
        Assert.Equal("Add the login page", pr.Title);
        Assert.Equal("octocat", pr.AuthorLogin);
        Assert.Equal("feature/login", pr.SourceBranch);
        Assert.Equal("main", pr.TargetBranch);
        Assert.Equal("6dcb09b5b57875f334f61aebed695e2e4193db5e", pr.HeadSha);
        Assert.Equal("https://github.com/acme/site.git", pr.SourceRepositoryUrl);
        Assert.False(pr.FromFork);
    }

    [Fact]
    public void GitHub_pull_request_from_another_repository_is_a_fork()
    {
        var pr = ParseGitHub(GitHubBody("opened", headRepo: "octocat/site")).PullRequest!;
        Assert.True(pr.FromFork);
        Assert.Equal("https://github.com/octocat/site.git", pr.SourceRepositoryUrl);

        // The comparison ignores case, as GitHub does.
        Assert.False(ParseGitHub(GitHubBody("opened", headRepo: "Acme/Site")).PullRequest!.FromFork);
    }

    [Fact]
    public void GitHub_pull_request_whose_fork_was_deleted_is_a_fork_without_a_clone_url()
    {
        var pr = ParseGitHub(GitHubBody("closed", headRepoGone: true)).PullRequest!;
        Assert.True(pr.FromFork);
        Assert.Equal("", pr.SourceRepositoryUrl);
        Assert.Equal(PullRequestAction.Closed, pr.Action);
    }

    [Fact]
    public void GitHub_pull_request_payload_without_the_pull_request_is_not_understood() =>
        Assert.Null(new GitHubProvider().Parse(GitHubHeaders(), """{"action":"opened","number":3}"""u8));

    [Fact]
    public void GitHub_pull_request_signature_is_checked_like_a_push()
    {
        var body = GitHubBody("opened");
        var sig = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3"), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var provider = new GitHubProvider();
        Assert.True(provider.VerifySignature(new Dictionary<string, string> { ["X-Hub-Signature-256"] = sig }, Encoding.UTF8.GetBytes(body), "s3"));
        Assert.False(provider.VerifySignature(new Dictionary<string, string> { ["X-Hub-Signature-256"] = sig }, Encoding.UTF8.GetBytes(body), "other"));
    }

    private static string GitLabBody(string action, string state = "opened", string? oldrev = null, int sourceProject = 1, int targetProject = 1) =>
        $$"""
        {
          "object_kind": "merge_request",
          "event_type": "merge_request",
          "user": { "id": 1, "name": "Ann Example", "username": "ann", "avatar_url": "https://gitlab.example.com/uploads/avatar.png" },
          "project": { "id": {{targetProject}}, "name": "Site", "web_url": "https://gitlab.example.com/acme/site", "git_http_url": "https://gitlab.example.com/acme/site.git", "path_with_namespace": "acme/site" },
          "object_attributes": {
            "id": 99,
            "iid": 7,
            "target_branch": "main",
            "source_branch": "feature/login",
            "source_project_id": {{sourceProject}},
            "target_project_id": {{targetProject}},
            "title": "Add the login page",
            "state": "{{state}}",
            "action": "{{action}}",
            {{(oldrev is null ? "" : $"\"oldrev\": \"{oldrev}\",")}}
            "last_commit": { "id": "da1560886d4f094c3e6c9ef40349f7d38b5d27d7", "message": "Add login", "author": { "name": "Ann", "email": "ann@example.com" } },
            "source": { "name": "Site", "git_http_url": "https://gitlab.example.com/{{(sourceProject == targetProject ? "acme" : "ann")}}/site.git", "path_with_namespace": "acme/site" },
            "target": { "name": "Site", "git_http_url": "https://gitlab.example.com/acme/site.git", "path_with_namespace": "acme/site" }
          }
        }
        """;

    private static WebhookEvent ParseGitLab(string body) => new GitLabProvider().Parse(GitLabHeaders(), Encoding.UTF8.GetBytes(body))!;

    [Theory]
    [InlineData("open", null, PullRequestAction.Opened)]
    [InlineData("reopen", null, PullRequestAction.Reopened)]
    [InlineData("update", "5e1c0e5a", PullRequestAction.Updated)]
    [InlineData("update", null, PullRequestAction.Other)] // a new title or label, no new commits
    [InlineData("close", null, PullRequestAction.Closed)]
    [InlineData("merge", null, PullRequestAction.Merged)]
    [InlineData("approved", null, PullRequestAction.Other)]
    public void GitLab_merge_request_actions_map_to_preview_actions(string action, string? oldrev, PullRequestAction expected)
    {
        var e = ParseGitLab(GitLabBody(action, oldrev: oldrev));

        Assert.False(e.IsPush);
        Assert.Equal("Merge Request Hook", e.EventType);
        Assert.Equal("uuid-1", e.DeliveryId);
        var pr = e.PullRequest!;
        Assert.Equal(expected, pr.Action);
        Assert.Equal(7, pr.Number);
        Assert.Equal("Add the login page", pr.Title);
        Assert.Equal("ann", pr.AuthorLogin);
        Assert.Equal("feature/login", pr.SourceBranch);
        Assert.Equal("main", pr.TargetBranch);
        Assert.Equal("da1560886d4f094c3e6c9ef40349f7d38b5d27d7", pr.HeadSha);
        Assert.Equal("https://gitlab.example.com/acme/site.git", pr.SourceRepositoryUrl);
        Assert.False(pr.FromFork);
    }

    [Fact]
    public void GitLab_merge_request_from_another_project_is_a_fork()
    {
        var pr = ParseGitLab(GitLabBody("open", sourceProject: 2, targetProject: 1)).PullRequest!;
        Assert.True(pr.FromFork);
        Assert.Equal("https://gitlab.example.com/ann/site.git", pr.SourceRepositoryUrl);
    }

    [Fact]
    public void GitLab_token_is_checked_for_merge_requests_too()
    {
        var provider = new GitLabProvider();
        Assert.True(provider.VerifySignature(new Dictionary<string, string> { ["X-Gitlab-Token"] = "tok" }, [], "tok"));
        Assert.False(provider.VerifySignature(new Dictionary<string, string> { ["X-Gitlab-Token"] = "nope" }, [], "tok"));
    }

    [Fact]
    public void GitLab_merge_request_without_attributes_is_not_understood() =>
        Assert.Null(new GitLabProvider().Parse(GitLabHeaders(), """{"object_kind":"merge_request"}"""u8));

    [Fact]
    public void Pushes_are_parsed_as_before_and_carry_no_pull_request()
    {
        var push = new GitHubProvider().Parse(
            new Dictionary<string, string> { ["X-GitHub-Event"] = "push", ["X-GitHub-Delivery"] = "d" },
            """{"ref":"refs/heads/main","after":"abc123","head_commit":{"message":"fix","author":{"name":"Ann"}}}"""u8)!;
        Assert.True(push.IsPush);
        Assert.Null(push.PullRequest);
        Assert.Equal("main", push.Branch);

    }
}

public class PreviewNamingTests
{
    [Theory]
    [InlineData(12, "web-3fa2c1.apps.example.com", "web", "pr-12-web-3fa2c1")]
    [InlineData(3, null, "my-app", "pr-3-my-app")]
    [InlineData(3, "", "My_App", "pr-3-my-app")]
    public void The_host_label_is_pr_number_and_the_label_of_the_application(int number, string? automatic, string slug, string expected) =>
        Assert.Equal(expected, PreviewNaming.HostLabel(number, automatic, slug));

    [Fact]
    public void The_host_label_stays_a_valid_dns_label_for_a_long_slug()
    {
        var label = PreviewNaming.HostLabel(1234, null, new string('a', 80) + "-");
        Assert.True(label.Length <= 63);
        Assert.StartsWith("pr-1234-", label);
        Assert.False(label.EndsWith('-'));
    }

    [Fact]
    public void The_hostname_sits_under_the_base_domain_of_the_server()
    {
        Assert.Equal("pr-12-web-3fa2c1.apps.example.com", PreviewNaming.Hostname(12, "web-3fa2c1.apps.example.com", "web", "apps.example.com"));
        Assert.Equal("pr-5-web.203-0-113-7.sslip.io", PreviewNaming.Hostname(5, null, "web", "203-0-113-7.sslip.io"));
        Assert.Equal("pr-5-web.apps.example.com", PreviewNaming.Hostname(5, null, "web", ".Apps.Example.com"));
    }

    private static readonly Guid AppId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void A_preview_never_mounts_a_volume_of_the_application()
    {
        // A plain name is the application's data in production and the preview's own volume in a preview.
        Assert.Equal("data", PreviewNaming.VolumeName("data", "web", AppId));
        var production = VolumeNames.DockerName("web", AppId, "data");
        var preview = VolumeNames.DockerName(PreviewEnvironment.ResourceSlug("web", 4), AppId, PreviewNaming.VolumeName("data", "web", AppId));
        Assert.NotEqual(production, preview);
        Assert.StartsWith("web-pr-4-", preview);

        // A name stored in the application's own namespace is brought back to the plain form first.
        Assert.Equal("data", PreviewNaming.VolumeName(production, "web", AppId));
        Assert.Equal(preview, VolumeNames.DockerName(PreviewEnvironment.ResourceSlug("web", 4), AppId, PreviewNaming.VolumeName(production, "web", AppId)));
    }

    [Fact]
    public void A_volume_of_another_workload_is_not_shared_with_a_preview()
    {
        var foreign = "other-1a2b3c4d-data";
        Assert.Equal(VolumeNameKind.Foreign, VolumeNames.Classify(foreign, "web", AppId));
        var name = PreviewNaming.VolumeName(foreign, "web", AppId);
        Assert.Equal("other_1a2b3c4d_data", name);
        var docker = VolumeNames.DockerName("web-pr-4", AppId, name);
        Assert.NotEqual(foreign, docker);
        Assert.StartsWith("web-pr-4-", docker);
    }
}

public class PreviewPlanTests
{
    private static readonly Guid Wid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid PreviewId = Guid.Parse("22222222-2222-3333-4444-555555555555");
    private static readonly Guid Did = Guid.Parse("0190f3c2-0000-7000-8000-000000000009");
    private static readonly Guid EnvId = Guid.Parse("abcd1234-0000-0000-0000-000000000000");
    private static readonly string EnvNetwork = DeploymentPlanBuilder.EnvironmentNetwork(EnvId);

    private static DeploymentSnapshot Snapshot(bool preview, bool compose = false)
    {
        var slug = preview ? "web-pr-4" : "web";
        return new DeploymentSnapshot
        {
            WorkloadId = Wid, EnvironmentId = EnvId, Slug = slug, SourceKind = compose ? ApplicationSourceKind.Compose : ApplicationSourceKind.Git,
            Git = new GitSnapshot("https://github.com/acme/site.git", "feature/login", "6dcb09b5b57875f334f61aebed695e2e4193db5e", null),
            Build = compose ? null : new BuildSnapshot(BuildEngines.Dockerfile, ".", null, null, null, null, null, true, null),
            Compose = compose ? new ComposeSnapshot("", null, "docker-compose.yml") : null,
            Ports = [new(3000, PortProtocol.Tcp, null, true)],
            Domains = [new(preview ? "pr-4-web-3fa2c1.apps.example.com" : "web-3fa2c1.apps.example.com", "/", true, 3000, compose ? "web" : null)],
            Networks = [new(EnvNetwork, false, [slug])],
            Preview = preview ? new PreviewSnapshot(PreviewId, 4) : null,
        };
    }

    private static PlanContext Context() => new()
    {
        ServerId = Guid.NewGuid(), DeploymentId = Did, DeploymentNumber = 2, ResolveSecret = (_, _) => "x", Proxy = new TraefikProxyProvider(),
        ComposeServices = [new Aethera.Domain.Transport.ComposeServiceDefinition("web", "", true, [3000]), new Aethera.Domain.Transport.ComposeServiceDefinition("db", "postgres", false, [5432])],
    };

    [Fact]
    public void A_preview_container_has_its_own_name_labels_and_route()
    {
        var production = DeploymentPlanBuilder.Build(Snapshot(preview: false), Context()).Container;
        var preview = DeploymentPlanBuilder.Build(Snapshot(preview: true), Context()).Container;

        Assert.NotEqual(production.Name, preview.Name);
        Assert.StartsWith("web-pr-4-", preview.Name);
        Assert.False(production.Labels!.ContainsKey("aethera.preview"));
        Assert.Equal(PreviewId.ToString(), preview.Labels!["aethera.preview"]);
        Assert.Equal(Wid.ToString(), preview.Labels["aethera.workload"]);
        Assert.Contains(preview.Labels, kv => kv.Value.Contains("Host(`pr-4-web-3fa2c1.apps.example.com`)"));
        Assert.DoesNotContain(preview.Labels, kv => kv.Value.Contains("web-3fa2c1.apps.example.com") && !kv.Value.Contains("pr-4-"));
        // Router names are the proxy's global namespace: the preview's differ from production's.
        Assert.Empty(production.Labels.Keys.Intersect(preview.Labels.Keys.Where(k => k.StartsWith("traefik.http.routers.", StringComparison.Ordinal))));
    }

    [Fact]
    public void A_preview_build_does_not_share_the_cache_key_of_the_application()
    {
        var production = DeploymentPlanBuilder.Build(Snapshot(preview: false), Context()).Build!;
        var preview = DeploymentPlanBuilder.Build(Snapshot(preview: true), Context()).Build!;

        Assert.NotEqual(production.Cache!.CacheKey, preview.Cache!.CacheKey);
        Assert.Equal($"{Wid:N}-pr-4", preview.Cache.CacheKey);
        Assert.Equal("aethera/web-pr-4:" + Did, preview.ImageTags[0]);
    }

    [Fact]
    public void A_compose_preview_has_its_own_project_and_does_not_take_the_plain_service_alias()
    {
        var production = DeploymentPlanBuilder.Build(Snapshot(preview: false, compose: true), Context());
        var preview = DeploymentPlanBuilder.Build(Snapshot(preview: true, compose: true), Context());

        Assert.NotEqual(production.Compose!.ProjectName, preview.Compose!.ProjectName);
        Assert.StartsWith("web-pr-4-", preview.Compose.ProjectName);
        Assert.Contains("\"web\"", production.Compose.OverrideFile!); // the alias of the service
        var aliasesOfPreview = System.Text.Json.JsonDocument.Parse(preview.Compose.OverrideFile!).RootElement
            .GetProperty("services").GetProperty("web").GetProperty("networks").GetProperty(EnvNetwork).GetProperty("aliases")
            .EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.DoesNotContain("web", aliasesOfPreview);
        Assert.Contains("web-pr-4-web", aliasesOfPreview);
        Assert.Contains(PreviewId.ToString(), preview.Compose.OverrideFile!);
    }
}
