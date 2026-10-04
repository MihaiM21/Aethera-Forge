using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aethera.Domain;

namespace Aethera.Engine.Git;

/// <summary>What a verified webhook delivery says (provider-neutral).</summary>
public sealed record WebhookEvent(
    string EventType, string DeliveryId, bool IsPush, string? Ref, string? Branch, string? CommitSha, string? CommitMessage,
    string? CommitAuthor, bool BranchDeleted);

/// <summary>Provider-specific webhook handling (spec section 7). Credentials for cloning are resolved elsewhere.</summary>
public interface IGitProvider
{
    GitProvider Kind { get; }

    /// <summary>Constant-time verification of the delivery against the endpoint secret.</summary>
    bool VerifySignature(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body, string secret);

    /// <summary>Parses a verified delivery; null when the payload is not understood.</summary>
    WebhookEvent? Parse(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body);
}

internal static class GitProviderSupport
{
    public static string? Header(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var (k, v) in headers)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    public static bool ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string? BranchOf(string? gitRef) =>
        gitRef is not null && gitRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? gitRef["refs/heads/".Length..] : null;

    public static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    public static bool IsZeroSha(string? sha) => sha is not null && sha.Length > 0 && sha.All(c => c == '0');

    public static JsonDocument? TryParse(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>GitHub: HMAC-SHA256 over the body in <c>X-Hub-Signature-256</c>.</summary>
public sealed class GitHubProvider : IGitProvider
{
    public GitProvider Kind => GitProvider.GitHub;

    public bool VerifySignature(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body, string secret)
    {
        var header = GitProviderSupport.Header(headers, "X-Hub-Signature-256");
        if (header is null || !header.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        return GitProviderSupport.ConstantTimeEquals(expected, header.ToLowerInvariant());
    }

    public WebhookEvent? Parse(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body)
    {
        var type = GitProviderSupport.Header(headers, "X-GitHub-Event");
        var delivery = GitProviderSupport.Header(headers, "X-GitHub-Delivery");
        if (type is null || delivery is null) return null;
        if (type != "push") return new(type, delivery, false, null, null, null, null, null, false);

        using var doc = GitProviderSupport.TryParse(body);
        if (doc is null) return null;
        var root = doc.RootElement;
        var gitRef = GitProviderSupport.Str(root, "ref");
        var sha = GitProviderSupport.Str(root, "after");
        var deleted = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True;
        return new(type, delivery, true, gitRef, GitProviderSupport.BranchOf(gitRef), sha,
            GitProviderSupport.Str(root, "head_commit", "message"), GitProviderSupport.Str(root, "head_commit", "author", "name"),
            deleted || GitProviderSupport.IsZeroSha(sha));
    }
}

/// <summary>GitLab: shared token in <c>X-Gitlab-Token</c>.</summary>
public sealed class GitLabProvider : IGitProvider
{
    public GitProvider Kind => GitProvider.GitLab;

    public bool VerifySignature(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body, string secret) =>
        GitProviderSupport.Header(headers, "X-Gitlab-Token") is { } token && GitProviderSupport.ConstantTimeEquals(token, secret);

    public WebhookEvent? Parse(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body)
    {
        var type = GitProviderSupport.Header(headers, "X-Gitlab-Event");
        if (type is null) return null;
        // Older GitLab versions send no delivery id; the body hash makes redeliveries of the same payload idempotent.
        var delivery = GitProviderSupport.Header(headers, "X-Gitlab-Event-UUID")
                       ?? Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        if (type != "Push Hook") return new(type, delivery, false, null, null, null, null, null, false);

        using var doc = GitProviderSupport.TryParse(body);
        if (doc is null) return null;
        var root = doc.RootElement;
        var gitRef = GitProviderSupport.Str(root, "ref");
        var sha = GitProviderSupport.Str(root, "checkout_sha") ?? GitProviderSupport.Str(root, "after");
        string? message = null, author = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Array && commits.GetArrayLength() > 0)
        {
            var last = commits[commits.GetArrayLength() - 1];
            message = GitProviderSupport.Str(last, "message");
            author = GitProviderSupport.Str(last, "author", "name");
        }
        var after = GitProviderSupport.Str(root, "after");
        return new(type, delivery, true, gitRef, GitProviderSupport.BranchOf(gitRef), sha, message, author,
            GitProviderSupport.IsZeroSha(after) || GitProviderSupport.IsZeroSha(sha) && sha is not null);
    }
}

/// <summary>
/// Generic Git hosts: <c>X-Aethera-Token</c> shared token and a small JSON body <c>{"ref":"refs/heads/main","commit":"sha","message":"..."}</c>,
/// so any CI or self-hosted Git server can trigger a deployment.
/// </summary>
public sealed class GenericGitProvider : IGitProvider
{
    public GitProvider Kind => GitProvider.Generic;

    public bool VerifySignature(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body, string secret) =>
        GitProviderSupport.Header(headers, "X-Aethera-Token") is { } token && GitProviderSupport.ConstantTimeEquals(token, secret);

    public WebhookEvent? Parse(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body)
    {
        using var doc = GitProviderSupport.TryParse(body);
        if (doc is null) return null;
        var root = doc.RootElement;
        var gitRef = GitProviderSupport.Str(root, "ref");
        var delivery = GitProviderSupport.Header(headers, "X-Aethera-Delivery") ?? Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return new("push", delivery, gitRef is not null, gitRef, GitProviderSupport.BranchOf(gitRef), GitProviderSupport.Str(root, "commit"),
            GitProviderSupport.Str(root, "message"), GitProviderSupport.Str(root, "author"), false);
    }
}

public static class GitProviders
{
    public static IReadOnlyList<IGitProvider> Defaults { get; } = [new GitHubProvider(), new GitLabProvider(), new GenericGitProvider()];

    public static IGitProvider For(GitProvider kind) => Defaults.First(p => p.Kind == kind);
}

/// <summary>Branch filters of webhook endpoints: exact names, <c>*</c> within one path segment and <c>**</c> across segments.</summary>
public static class BranchMatcher
{
    public static bool Matches(string? filter, string configuredBranch, string branch)
    {
        if (string.IsNullOrWhiteSpace(filter)) return string.Equals(branch, configuredBranch, StringComparison.Ordinal);
        return filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(p => Glob(p, branch));
    }

    private static bool Glob(string pattern, string input)
    {
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace("\u0001", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, rx, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }
}
