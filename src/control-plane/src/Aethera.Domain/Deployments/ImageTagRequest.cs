using System.Text.RegularExpressions;

namespace Aethera.Domain;

/// <summary>Why a requested image tag cannot be deployed (the field-level <c>code</c> of the 422).</summary>
public enum ImageTagProblem
{
    None,

    /// <summary><c>image_tag.invalid</c>: not a Docker tag, a <c>sha256:</c> digest or an image reference.</summary>
    Invalid,

    /// <summary><c>image_tag.repository_mismatch</c>: a full reference that names another repository than the application's image.</summary>
    RepositoryMismatch,
}

/// <summary>
/// Parses the <c>imageTag</c> of <c>POST /applications/{id}/deployments</c> (ADR 0007, "Deploying a pinned tag"). Accepted forms:
/// <list type="bullet">
/// <item>a tag: <c>3f2a9c1</c>, <c>v1.4.0</c> (Docker's grammar: <c>[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}</c>);</item>
/// <item>a digest: <c>sha256:&lt;64 hex&gt;</c> or <c>@sha256:&lt;64 hex&gt;</c>;</item>
/// <item>a full reference of the application's own repository: <c>ghcr.io/owner/app:3f2a9c1</c> or <c>ghcr.io/owner/app@sha256:...</c>.
/// Another repository is refused: deploying a different image is a configuration change (role and scope <c>write</c>), not a deploy.</item>
/// </list>
/// The result is what <c>ImageSource.Tag</c> holds: a plain tag, or <c>@sha256:...</c> for a digest.
/// </summary>
public static partial class ImageTagRequest
{
    public const int MaxLength = 200;

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\A@?sha256:[a-f0-9]{64}\z")]
    private static partial Regex DigestPattern();

    /// <summary>Normalises <paramref name="value"/> against the application's configured <paramref name="repository"/>.</summary>
    public static ImageTagProblem TryNormalize(string? value, string repository, out string tag)
    {
        tag = "";
        var text = value?.Trim() ?? "";
        if (text.Length is 0 or > MaxLength) return ImageTagProblem.Invalid;

        if (DigestPattern().IsMatch(text))
        {
            tag = text.StartsWith('@') ? text : "@" + text;
            return ImageTagProblem.None;
        }

        // A mistyped digest, not a repository called "sha256".
        if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || text.StartsWith('@')) return ImageTagProblem.Invalid;

        if (TagPattern().IsMatch(text))
        {
            tag = text;
            return ImageTagProblem.None;
        }

        // A full reference: <repository>@sha256:<hex> or <repository>:<tag> (the tag separator is a colon after the last slash).
        string reference, suffix;
        var at = text.IndexOf('@');
        if (at > 0)
        {
            reference = text[..at];
            suffix = text[at..];
            if (!DigestPattern().IsMatch(suffix)) return ImageTagProblem.Invalid;
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || colon < text.LastIndexOf('/')) return ImageTagProblem.Invalid;
            reference = text[..colon];
            suffix = text[(colon + 1)..];
            if (!TagPattern().IsMatch(suffix)) return ImageTagProblem.Invalid;
        }

        if (!IsRepository(reference)) return ImageTagProblem.Invalid;
        if (!string.Equals(Canonical(reference), Canonical(repository), StringComparison.Ordinal)) return ImageTagProblem.RepositoryMismatch;
        tag = suffix;
        return ImageTagProblem.None;
    }

    private static bool IsRepository(string reference) =>
        reference.Length > 0 && !reference.Contains("//", StringComparison.Ordinal) && !reference.StartsWith('/') && !reference.EndsWith('/')
        && reference.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':');

    /// <summary>
    /// <c>nginx</c>, <c>library/nginx</c> and <c>docker.io/library/nginx</c> are the same repository; registry hosts are compared case-insensitively.
    /// </summary>
    internal static string Canonical(string repository)
    {
        var text = repository.Trim();
        var slash = text.IndexOf('/');
        var first = slash < 0 ? "" : text[..slash];
        var hasRegistry = first.Length > 0 && (first.Contains('.') || first.Contains(':') || first == "localhost");
        if (!hasRegistry) text = "docker.io/" + text;
        slash = text.IndexOf('/');
        var host = text[..slash].ToLowerInvariant();
        if (host == "index.docker.io" || host == "registry-1.docker.io") host = "docker.io";
        var path = text[(slash + 1)..];
        if (host == "docker.io" && !path.Contains('/')) path = "library/" + path;
        return host + "/" + path;
    }
}
