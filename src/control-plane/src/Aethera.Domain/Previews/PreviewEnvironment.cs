namespace Aethera.Domain;

public enum PreviewStatus
{
    /// <summary>Recorded, not deployed yet (queued, or held back by <see cref="PreviewConfig.MaxActive"/>).</summary>
    Pending = 0,
    Deploying = 1,
    Running = 2,
    Failed = 3,

    /// <summary>The pull request was closed or merged; teardown is queued or running.</summary>
    Closing = 4,

    /// <summary>Containers, route and volumes are gone. The row stays for history.</summary>
    Removed = 5,
}

/// <summary>
/// The preview of one pull request (GitHub) or merge request (GitLab) of an application (ADR 0012). Its deployments are ordinary
/// <see cref="Deployment"/>s with <see cref="Deployment.PreviewId"/> set. Unique per (application, number).
/// </summary>
public class PreviewEnvironment : MutableEntity
{
    public Guid ApplicationId { get; set; }
    public Application Application { get; set; } = null!;

    /// <summary>Server the preview runs on, fixed when it is created (<see cref="PreviewConfig.ServerId"/> or the application's).</summary>
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;

    public GitProvider Provider { get; set; } = GitProvider.GitHub;

    /// <summary>Pull request number (GitHub) or merge request iid (GitLab).</summary>
    public int Number { get; set; }

    public string? Title { get; set; }
    public string? AuthorLogin { get; set; }

    /// <summary>The branch the pull request comes from.</summary>
    public required string SourceBranch { get; set; }

    /// <summary>Clone URL of the head repository (differs from the application's for a fork).</summary>
    public required string SourceRepositoryUrl { get; set; }

    public bool FromFork { get; set; }

    /// <summary>Head commit of the last event.</summary>
    public string? HeadSha { get; set; }

    /// <summary>Hostname routed to the preview, e.g. <c>pr-12-web-3fa2c1.apps.example.com</c>. Null until the first deployment assigns it.</summary>
    public string? Hostname { get; set; }

    public PreviewStatus Status { get; set; } = PreviewStatus.Pending;
    public string? StatusReason { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }

    /// <summary>The preview's live deployment (the workload's own <see cref="Workload.CurrentDeploymentId"/> is never touched).</summary>
    public Guid? CurrentDeploymentId { get; set; }

    /// <summary>Last webhook event for the pull request; <c>preview.sweep</c> removes previews silent for 14 days.</summary>
    public DateTimeOffset? LastEventAt { get; set; }

    /// <summary>Id of the comment Aethera posted on the pull request, so later deployments edit it instead of adding new ones.</summary>
    public string? ProviderCommentId { get; set; }

    /// <summary>Why the last comment could not be posted, if it could not.</summary>
    public string? CommentError { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>Names of the preview's containers, volumes and compose project: <c>&lt;slug&gt;-pr-&lt;number&gt;</c>.</summary>
    public static string ResourceSlug(string applicationSlug, int number) => $"{applicationSlug}-pr-{number}";

    public bool IsActive => Status is PreviewStatus.Pending or PreviewStatus.Deploying or PreviewStatus.Running or PreviewStatus.Failed;
}
