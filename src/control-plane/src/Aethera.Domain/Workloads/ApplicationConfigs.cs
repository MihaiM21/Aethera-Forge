namespace Aethera.Domain;

public enum GitProvider
{
    Generic = 0,
    GitHub = 1,
    GitLab = 2,
}

public enum ImagePullPolicy
{
    IfNotPresent = 0,
    Always = 1,
    Never = 2,
}

/// <summary>Well-known build engine names; the column is a free string so engines can be added without a migration.</summary>
public static class BuildEngines
{
    public const string Dockerfile = "dockerfile";
    public const string Nixpacks = "nixpacks";
    public const string Static = "static";
    public const string Image = "image";
    public const string Compose = "compose";
}

// The four configs below are optional 1:1 dependents of Application; their primary key is the application id.

public class GitSource
{
    public Guid ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public GitProvider Provider { get; set; } = GitProvider.Generic;
    public required string RepositoryUrl { get; set; }
    public string Branch { get; set; } = "main";

    /// <summary>Pin to an exact commit; null follows the branch head.</summary>
    public string? CommitPin { get; set; }

    /// <summary>Deploy key / token for private repositories (a first-class resource, ADR 0003 /git-credentials).</summary>
    public Guid? GitCredentialId { get; set; }
    public GitCredential? GitCredential { get; set; }

    /// <summary>Deploy automatically when a verified webhook for <see cref="Branch"/> arrives (see <see cref="WebhookEndpoint"/>).</summary>
    public bool AutoDeploy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class BuildConfig
{
    public Guid ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string Engine { get; set; } = BuildEngines.Dockerfile;
    public string Context { get; set; } = ".";
    public string? DockerfilePath { get; set; }
    public string? DockerfileInline { get; set; }
    public string? InstallCommand { get; set; }
    public string? BuildCommand { get; set; }
    public string? StartCommand { get; set; }
    public string? OutputDirectory { get; set; }
    public Dictionary<string, string> BuildArgs { get; set; } = [];
    public bool CacheEnabled { get; set; } = true;

    /// <summary>e.g. <c>linux/amd64</c>; null builds for the target server's architecture.</summary>
    public string? TargetPlatform { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ImageSource
{
    public Guid ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public Guid? RegistryId { get; set; }
    public Registry? Registry { get; set; }
    public required string Image { get; set; }
    public string Tag { get; set; } = "latest";
    public ImagePullPolicy PullPolicy { get; set; } = ImagePullPolicy.IfNotPresent;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ComposeSource
{
    public Guid ApplicationId { get; set; }
    public Application Application { get; set; } = null!;

    /// <summary>Path of the compose file inside the repository; null when <see cref="InlineContent"/> is used.</summary>
    public string? FilePath { get; set; }
    public string? InlineContent { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
