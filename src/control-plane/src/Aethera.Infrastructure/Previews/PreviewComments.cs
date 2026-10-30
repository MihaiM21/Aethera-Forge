using System.Text;
using Aethera.Domain;
using Aethera.Engine.Deployments;
using Aethera.Engine.Git;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aethera.Infrastructure.Previews;

/// <summary>What the comment on the pull request says about the preview.</summary>
public enum PreviewCommentState
{
    Deploying,
    Running,
    Failed,
    Removed,
}

/// <param name="Url">The preview's URL; only shown while it runs.</param>
/// <param name="FailedStep">The pipeline step a failed deployment stopped at, and its code. The failure text itself is never posted: it may quote build output.</param>
public sealed record PreviewCommentModel(
    PreviewCommentState State, int Number, string? Commit, int? DeploymentNumber, string? Url, string? FailedStep, string? FailureCode, bool FromFork);

/// <summary>The text of the preview comment. Pure; it holds a status, a URL and a commit, never a secret.</summary>
public static class PreviewCommentText
{
    /// <summary>Marks the comment as Aethera's, so a person looking at the markdown source can tell where it comes from.</summary>
    public const string Marker = "<!-- aethera-preview -->";

    public static string Build(PreviewCommentModel m)
    {
        var text = new StringBuilder();
        text.AppendLine(Marker);
        text.AppendLine("### Aethera preview");
        text.AppendLine();
        text.AppendLine("| | |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| **Status** | {StatusText(m)} |");
        if (m.State == PreviewCommentState.Running && m.Url is { Length: > 0 }) text.AppendLine($"| **Preview** | {m.Url} |");
        if (m.Commit is { Length: > 0 }) text.AppendLine($"| **Commit** | `{(m.Commit.Length > 7 ? m.Commit[..7] : m.Commit)}` |");
        if (m.DeploymentNumber is { } n && m.State != PreviewCommentState.Removed) text.AppendLine($"| **Deployment** | #{n} |");
        if (m.FromFork) text.AppendLine("| **Source** | fork |");
        text.AppendLine();
        text.AppendLine(m.State == PreviewCommentState.Removed
            ? "_The preview was removed._"
            : "_Updated on every push to this pull request and removed when it is closed._");
        return text.ToString();
    }

    private static string StatusText(PreviewCommentModel m) => m.State switch
    {
        PreviewCommentState.Deploying => "Building and deploying",
        PreviewCommentState.Running => "Running",
        PreviewCommentState.Failed => m.FailedStep is { Length: > 0 }
            ? $"Failed at the {m.FailedStep.ToLowerInvariant()} step{(m.FailureCode is { Length: > 0 } code ? $" (`{code}`)" : "")}"
            : "Failed",
        _ => "Removed",
    };
}

/// <summary>
/// Posts and updates the preview comment on the pull request (ADR 0012): once after a deployment started, once when it ended, once when the preview
/// is removed. Needs the git source's credential to be a token. It never throws and never fails a deployment: what went wrong is stored on the preview
/// as <see cref="PreviewEnvironment.CommentError"/>.
/// </summary>
public static class PreviewComments
{
    /// <summary>Posts the comment for the state the deployment is in (Deploying while it runs; Running or Failed once it ended).</summary>
    public static async Task PostForDeploymentAsync(IServiceProvider services, Guid deploymentId)
    {
        try
        {
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var d = await db.Deployments.AsNoTracking().Where(x => x.Id == deploymentId && x.PreviewId != null)
                .Select(x => new { x.PreviewId, x.Number, x.Status, x.CommitSha, x.FailedStep, x.FailureCode, x.ConfigSnapshotJson }).FirstOrDefaultAsync(CancellationToken.None);
            if (d is null) return;
            var state = d.Status switch
            {
                DeploymentStatus.Running => PreviewCommentState.Running,
                DeploymentStatus.Failed => PreviewCommentState.Failed,
                DeploymentStatus.Queued or DeploymentStatus.InProgress => PreviewCommentState.Deploying,
                _ => (PreviewCommentState?)null, // cancelled, superseded or stopped: nothing new to say
            };
            if (state is null) return;
            await PostAsync(scope.ServiceProvider, d.PreviewId!.Value, state.Value, d.Number, d.CommitSha, d.FailedStep?.ToString(), d.FailureCode, d.ConfigSnapshotJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("Aethera.Previews").LogWarning(ex, "The preview comment for deployment {Deployment} failed", deploymentId);
        }
    }

    /// <summary>Posts "removed" once the teardown is done.</summary>
    public static async Task PostRemovedAsync(IServiceProvider services, Guid previewId)
    {
        try
        {
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            await PostAsync(scope.ServiceProvider, previewId, PreviewCommentState.Removed, null, null, null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("Aethera.Previews").LogWarning(ex, "The preview comment for preview {Preview} failed", previewId);
        }
    }

    private static async Task PostAsync(
        IServiceProvider sp, Guid previewId, PreviewCommentState state, int? deploymentNumber, string? commit, string? failedStep, string? failureCode, string? snapshotJson)
    {
        var db = sp.GetRequiredService<AetheraDbContext>();
        var preview = await db.Previews.AsNoTracking().FirstOrDefaultAsync(p => p.Id == previewId, CancellationToken.None);
        if (preview is null) return;
        var git = await db.Applications.IgnoreQueryFilters().AsNoTracking().Where(a => a.Id == preview.ApplicationId).Select(a => a.GitSource).FirstOrDefaultAsync(CancellationToken.None);
        if (git?.GitCredentialId is not { } credentialId) return; // a public repository or a deploy key: there is no token to comment with

        var credential = await db.GitCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId, CancellationToken.None);
        if (credential is not { Kind: GitCredentialKind.Token }) return;
        var commenter = sp.GetServices<IPullRequestCommenter>().FirstOrDefault(c => c.Supports(preview.Provider));
        if (commenter is null) return;

        string token;
        try
        {
            token = await sp.GetRequiredService<BackupSecretReader>().ReadAsync(credential.SecretId, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is BackupFailure)
        {
            await StoreAsync(db, previewId, preview.ProviderCommentId, "The git credential could not be read.");
            return;
        }

        string? url = null;
        if (state == PreviewCommentState.Running && preview.Hostname is { Length: > 0 } host)
            url = $"{(HttpsOf(snapshotJson) ? "https" : "http")}://{host}";
        var markdown = PreviewCommentText.Build(new PreviewCommentModel(
            state, preview.Number, commit ?? preview.HeadSha, deploymentNumber, url, failedStep, failureCode, preview.FromFork));
        var result = await commenter.UpsertAsync(new PullRequestRef(preview.Provider, git.RepositoryUrl, preview.Number), preview.ProviderCommentId, markdown, token, CancellationToken.None);
        await StoreAsync(db, previewId, result.CommentId ?? preview.ProviderCommentId, result.Error);
    }

    private static bool HttpsOf(string? snapshotJson) =>
        snapshotJson is { Length: > 2 } && DeploymentSnapshot.TryFromJson(snapshotJson, out var snapshot) && snapshot!.Domains.FirstOrDefault()?.Https == true;

    /// <summary>Written with a direct update: the deployment that is running holds the tracked preview, and its writes must not clash with this one.</summary>
    private static Task StoreAsync(AetheraDbContext db, Guid previewId, string? commentId, string? error) =>
        db.Previews.Where(p => p.Id == previewId).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.ProviderCommentId, commentId)
            .SetProperty(p => p.CommentError, error == null ? null : error.Length > 1000 ? error.Substring(0, 1000) : error), CancellationToken.None);
}
