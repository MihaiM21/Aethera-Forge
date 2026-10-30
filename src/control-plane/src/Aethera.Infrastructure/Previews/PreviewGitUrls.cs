namespace Aethera.Infrastructure.Previews;

/// <summary>A repository address split into the host and the path (<c>owner/repo</c>, or <c>group/subgroup/project</c> on GitLab), without <c>.git</c>.</summary>
public sealed record RepositoryAddress(string Host, string Path)
{
    /// <summary>Parses an https, http, ssh or scp-like (<c>git@host:owner/repo.git</c>) address. Null when it is none of them.</summary>
    public static RepositoryAddress? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        string host, path;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            var at = url.IndexOf('@');
            var colon = url.IndexOf(':');
            if (colon <= 0 || colon <= at + 1) return null;
            host = url[(at + 1)..colon];
            path = url[(colon + 1)..];
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path.Length == 0 ? null : new RepositoryAddress(host.ToLowerInvariant(), path);
    }
}

/// <summary>Repository addresses of pull requests: where a fork is cloned from, and which hosts Aethera may call.</summary>
public static class PreviewGitUrls
{
    /// <summary>
    /// The address to clone a fork from. The fork's clone URL comes from the webhook (https); an application that is cloned over ssh
    /// (<c>git@host:owner/repo.git</c>, with a deploy key) clones its forks the same way, since the deploy key is what authenticates.
    /// Null when the fork is on another host than the application's repository: the application's credential must never be sent there.
    /// </summary>
    public static string? ForkCloneUrl(string applicationRepositoryUrl, string forkUrl)
    {
        var app = RepositoryAddress.Parse(applicationRepositoryUrl);
        var fork = RepositoryAddress.Parse(forkUrl);
        if (app is null || fork is null || !string.Equals(app.Host, fork.Host, StringComparison.OrdinalIgnoreCase)) return null;

        var scpLike = !applicationRepositoryUrl.Contains("://", StringComparison.Ordinal);
        if (scpLike)
        {
            var at = applicationRepositoryUrl.IndexOf('@');
            var user = at > 0 ? applicationRepositoryUrl[..(at + 1)] : "";
            return $"{user}{fork.Host}:{fork.Path}.git";
        }

        return forkUrl;
    }

    /// <summary>
    /// False for a host Aethera must not call with an access token: no host, an IP address, <c>localhost</c> or a name without a dot. A repository
    /// URL is entered by users, and the comment is an HTTP request from the control plane.
    /// </summary>
    public static bool IsPublicHost(string host) =>
        host.Contains('.', StringComparison.Ordinal) && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) && !System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _)
        && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}
