using Aethera.Domain;

namespace Aethera.Engine.Git;

/// <summary>The pull request a comment goes on: the repository's web URL (as configured on the application) and the number.</summary>
public sealed record PullRequestRef(GitProvider Provider, string RepositoryUrl, int Number);

/// <summary>The outcome of a comment: the provider's id of the comment (to edit it next time), or why it could not be posted.</summary>
public sealed record PullRequestCommentResult(string? CommentId, string? Error)
{
    public bool Posted => CommentId is not null;
}

/// <summary>
/// Posts the preview status on a pull request (ADR 0012, "Comments"): creates the comment once, then edits it. Needs an API token
/// (the git source's token credential). Implementations never throw for provider errors; they return them.
/// </summary>
public interface IPullRequestCommenter
{
    bool Supports(GitProvider provider);

    /// <param name="existingCommentId">The id from an earlier result; the comment is edited, or recreated when it was deleted.</param>
    /// <param name="markdown">The whole comment body; it must not contain secrets.</param>
    Task<PullRequestCommentResult> UpsertAsync(
        PullRequestRef pullRequest, string? existingCommentId, string markdown, string apiToken, CancellationToken ct);
}
