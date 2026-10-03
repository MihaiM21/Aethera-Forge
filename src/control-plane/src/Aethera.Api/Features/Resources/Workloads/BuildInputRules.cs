using System.Text.RegularExpressions;

namespace Aethera.Api.Features.Resources.Workloads;

/// <summary>
/// Validation of inputs that end up on a command line or in a path on a build host (ADR 0006): relative paths that cannot leave the
/// repository, git refs and repository URLs that cannot inject options. Everything here is a syntactic check; the engine re-checks and
/// passes such values after <c>--</c> where the tool supports it (Phase 3).
/// </summary>
public static partial class BuildInputRules
{
    [GeneratedRegex(@"\A(?:https?|ssh|git)://[^\s\x00-\x1f\x7f]+\z")]
    private static partial Regex SchemeUrlPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9._][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9.-]*:[^\s\x00-\x1f\x7f]+\z")]
    private static partial Regex ScpAddressPattern();

    /// <summary>
    /// A path inside the repository: relative, <c>/</c>-separated, no <c>..</c> segment, no absolute root (<c>/</c>, <c>~</c>, a drive letter),
    /// no backslash, no control character and not starting with <c>-</c>. Empty and null mean "not set" and are accepted.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (path.Any(char.IsControl) || path.Contains('\\')) return false;
        var text = path.Trim();
        if (text[0] is '/' or '~' or '-') return false;
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') return false;
        return !text.Split('/').Contains("..");
    }

    /// <summary>
    /// A branch, tag or ref name that cannot be mistaken for an option and is a valid git ref: not empty, not starting with <c>-</c>, no
    /// whitespace or control characters, none of <c>~ ^ : ? * [ \</c>, no <c>..</c> and no <c>@{</c>.
    /// </summary>
    public static bool IsSafeGitRef(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference[0] == '-') return false;
        if (reference.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')) return false;
        return !reference.Contains("..", StringComparison.Ordinal) && !reference.Contains("@{", StringComparison.Ordinal);
    }

    /// <summary>
    /// An http(s), ssh or git URL, or an scp-style address (<c>git@host:org/repo.git</c>), where no part of the user or host can start
    /// with <c>-</c> (ssh option injection such as <c>ssh://-oProxyCommand=...</c>) and no transport helper is named (<c>ext::</c>,
    /// <c>file://</c>). Whether the host is one the clone host should reach is an engine policy (SSRF), see ADR 0006.
    /// </summary>
    public static bool IsSafeRepositoryUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] == '-') return false;
        if (url.Contains("::", StringComparison.Ordinal)) return false; // ext::, fd::, any "transport::address" helper
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;

        if (SchemeUrlPattern().IsMatch(url))
        {
            var rest = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var end = rest.IndexOfAny(['/', '?', '#']);
            var authority = end < 0 ? rest : rest[..end];
            if (authority.Length == 0 || authority[0] == '-') return false;
            var host = authority[(authority.LastIndexOf('@') + 1)..];
            return host.Length > 0 && host[0] != '-';
        }

        return ScpAddressPattern().IsMatch(url);
    }
}
