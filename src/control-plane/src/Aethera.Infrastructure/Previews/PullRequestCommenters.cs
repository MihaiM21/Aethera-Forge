using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aethera.Domain;
using Aethera.Engine.Git;

namespace Aethera.Infrastructure.Previews;

/// <summary>
/// Shared plumbing of the comment implementations: a client with a timeout, the JSON of a comment, and error text that names the provider's answer
/// without ever repeating the token.
/// </summary>
public abstract class PullRequestCommenterBase(IHttpClientFactory httpClients) : IPullRequestCommenter
{
    /// <summary>Name of the <see cref="HttpClient"/> the commenters use; tests and hosts may configure it.</summary>
    public const string HttpClientName = "aethera-pr-comments";

    /// <summary>The longest a call to the git host may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public abstract bool Supports(GitProvider provider);

    protected abstract Task<PullRequestCommentResult> UpsertCoreAsync(
        HttpClient client, RepositoryAddress repository, int number, string? existingCommentId, string markdown, string apiToken, CancellationToken ct);

    public async Task<PullRequestCommentResult> UpsertAsync(PullRequestRef pullRequest, string? existingCommentId, string markdown, string apiToken, CancellationToken ct)
    {
        try
        {
            var repository = RepositoryAddress.Parse(pullRequest.RepositoryUrl);
            if (repository is null) return new PullRequestCommentResult(null, "The repository address could not be read.");
            if (!PreviewGitUrls.IsPublicHost(repository.Host))
                return new PullRequestCommentResult(null, $"Comments are only posted to public git hosts, not to {repository.Host}.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var client = httpClients.CreateClient(HttpClientName);
            client.Timeout = Timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Aethera-Previews");
            var result = await UpsertCoreAsync(client, repository, pullRequest.Number, existingCommentId, markdown, apiToken, timeout.Token);
            return result.Error is null ? result : result with { Error = Scrub(result.Error, apiToken) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PullRequestCommentResult(existingCommentId, "The git host did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new PullRequestCommentResult(existingCommentId, Scrub($"The git host could not be reached ({ex.HttpRequestError}).", apiToken));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or UriFormatException)
        {
            return new PullRequestCommentResult(existingCommentId, $"The comment could not be posted ({ex.GetType().Name}).");
        }
    }

    protected static StringContent Body(string field, string markdown) =>
        new(JsonSerializer.Serialize(new Dictionary<string, string> { [field] = markdown }, Json), Encoding.UTF8, "application/json");

    /// <summary>The id of the created comment from a response body (<c>id</c> is a number on both GitHub and GitLab).</summary>
    protected static async Task<string?> ReadIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id)
            ? id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString()
            : null;
    }

    /// <summary>What the provider said was wrong: its status and, when it sends one, its message (never the token).</summary>
    protected static async Task<string> DescribeAsync(string provider, HttpResponseMessage response, CancellationToken ct)
    {
        string? message = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                message = m.GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON: the status says enough.
        }

        var text = $"{provider} answered {(int)response.StatusCode} {response.ReasonPhrase}";
        if (message is { Length: > 0 }) text += $": {(message.Length > 200 ? message[..200] : message)}";
        return text;
    }

    private static string Scrub(string text, string token) => token.Length >= 3 ? text.Replace(token, "********", StringComparison.Ordinal) : text;

    protected static HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content, string headerName, string headerValue)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (headerName == "Authorization") request.Headers.Authorization = AuthenticationHeaderValue.Parse(headerValue);
        else request.Headers.TryAddWithoutValidation(headerName, headerValue);
        return request;
    }
}

/// <summary>
/// GitHub: pull request comments are issue comments. <c>api.github.com</c> for github.com, <c>https://&lt;host&gt;/api/v3</c> for GitHub Enterprise.
/// Creates once, edits afterwards, and creates again when the comment was deleted (404).
/// </summary>
public sealed class GitHubPullRequestCommenter(IHttpClientFactory httpClients) : PullRequestCommenterBase(httpClients)
{
    public override bool Supports(GitProvider provider) => provider == GitProvider.GitHub;

    public static string ApiBase(string host) => host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com" : $"https://{host}/api/v3";

    protected override async Task<PullRequestCommentResult> UpsertCoreAsync(
        HttpClient client, RepositoryAddress repository, int number, string? existingCommentId, string markdown, string apiToken, CancellationToken ct)
    {
        var baseUrl = $"{ApiBase(repository.Host)}/repos/{repository.Path}";
        if (existingCommentId is { Length: > 0 } && long.TryParse(existingCommentId, out _))
        {
            using var edit = Request(HttpMethod.Patch, $"{baseUrl}/issues/comments/{existingCommentId}", Body("body", markdown), "Authorization", $"Bearer {apiToken}");
            using var edited = await client.SendAsync(edit, ct);
            if (edited.IsSuccessStatusCode) return new PullRequestCommentResult(existingCommentId, null);
            if (edited.StatusCode != HttpStatusCode.NotFound) return new PullRequestCommentResult(existingCommentId, await DescribeAsync("GitHub", edited, ct));
            // The comment was deleted on the pull request: write a new one.
        }

        using var create = Request(HttpMethod.Post, $"{baseUrl}/issues/{number}/comments", Body("body", markdown), "Authorization", $"Bearer {apiToken}");
        using var created = await client.SendAsync(create, ct);
        if (!created.IsSuccessStatusCode) return new PullRequestCommentResult(null, await DescribeAsync("GitHub", created, ct));
        return new PullRequestCommentResult(await ReadIdAsync(created, ct), null);
    }
}

/// <summary>GitLab: notes on a merge request, <c>https://&lt;host&gt;/api/v4</c>. The project is addressed by its URL-encoded path.</summary>
public sealed class GitLabPullRequestCommenter(IHttpClientFactory httpClients) : PullRequestCommenterBase(httpClients)
{
    public override bool Supports(GitProvider provider) => provider == GitProvider.GitLab;

    protected override async Task<PullRequestCommentResult> UpsertCoreAsync(
        HttpClient client, RepositoryAddress repository, int number, string? existingCommentId, string markdown, string apiToken, CancellationToken ct)
    {
        var notes = $"https://{repository.Host}/api/v4/projects/{Uri.EscapeDataString(repository.Path)}/merge_requests/{number}/notes";
        if (existingCommentId is { Length: > 0 } && long.TryParse(existingCommentId, out _))
        {
            using var edit = Request(HttpMethod.Put, $"{notes}/{existingCommentId}", Body("body", markdown), "PRIVATE-TOKEN", apiToken);
            using var edited = await client.SendAsync(edit, ct);
            if (edited.IsSuccessStatusCode) return new PullRequestCommentResult(existingCommentId, null);
            if (edited.StatusCode != HttpStatusCode.NotFound) return new PullRequestCommentResult(existingCommentId, await DescribeAsync("GitLab", edited, ct));
        }

        using var create = Request(HttpMethod.Post, notes, Body("body", markdown), "PRIVATE-TOKEN", apiToken);
        using var created = await client.SendAsync(create, ct);
        if (!created.IsSuccessStatusCode) return new PullRequestCommentResult(null, await DescribeAsync("GitLab", created, ct));
        return new PullRequestCommentResult(await ReadIdAsync(created, ct), null);
    }
}
