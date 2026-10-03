using Aethera.Api.Http.Errors;

namespace Aethera.Api.Features.Resources.Trust;

/// <summary>
/// Tunables of the trust model (ADR 0006), bound from <c>Aethera:Trust</c> (environment variables use <c>__</c>, e.g.
/// <c>Aethera__Trust__HostPathAllowlist__0=/srv/aethera/</c>). Both lists replace the defaults when set.
/// </summary>
public sealed class TrustOptions
{
    public const string SectionName = "Aethera:Trust";

    public static readonly string[] DefaultHostPathAllowlist = ["/var/lib/aethera/volumes/"];

    public static readonly int[] DefaultReservedPorts = [22, 80, 443, 2375, 2376, 5080, 9443];

    /// <summary>
    /// Host path prefixes that a bind mount may use even though they sit under a denied directory (the default lets Admins use
    /// <c>/var/lib/aethera/volumes/</c> although <c>/var/lib/aethera</c> is denied). A prefix must not be <c>/</c> or a parent of a denied
    /// directory; such a value stops the application from starting.
    /// </summary>
    public string[]? HostPathAllowlist { get; set; }

    /// <summary>Host ports used by Aethera itself (SSH, the proxy, the Docker API, the panel). Only an Admin that passes <c>allowReserved</c> may publish them.</summary>
    public int[]? ReservedPorts { get; set; }
}

/// <summary>Resolved trust-model settings plus the checks that use them. Singleton.</summary>
public sealed class TrustPolicy
{
    /// <summary>Directories (and everything below them) that never may be bind-mounted from the host, even by an Admin.</summary>
    public static readonly string[] DeniedHostPaths =
    [
        "/var/run", "/run", "/proc", "/sys", "/dev", "/etc", "/boot", "/root", "/var/lib/aethera",
        // Not in the product decision's list but equally root-equivalent: the Docker and containerd state directories.
        "/var/lib/docker", "/var/lib/containerd",
    ];

    /// <summary>Hosts ports below this number are "privileged" (Admin only).</summary>
    public const int PrivilegedPortLimit = 1024;

    public TrustPolicy(TrustOptions options)
    {
        var allowlist = options.HostPathAllowlist ?? TrustOptions.DefaultHostPathAllowlist;
        HostPathAllowlist = allowlist.Select(NormalizeAllowlistEntry).ToArray();
        ReservedPorts = (options.ReservedPorts ?? TrustOptions.DefaultReservedPorts).ToHashSet();
    }

    public IReadOnlyList<string> HostPathAllowlist { get; }

    public IReadOnlySet<int> ReservedPorts { get; }

    // ---- host paths -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Normalizes a host path: absolute, no whitespace, control characters, <c>:</c>, <c>,</c>, quotes or backslashes; repeated slashes are
    /// collapsed, <c>.</c> segments dropped, a trailing slash removed, and any <c>..</c> segment rejects the path. <c>/</c> itself is a valid
    /// (and always denied) path so that it is answered with <c>volume.host_path_forbidden</c> rather than a pattern error.
    /// </summary>
    public static bool TryNormalizeHostPath(string? path, out string normalized)
    {
        normalized = "";
        if (path is null) return false;
        var text = path; // no trimming: whitespace, a trailing newline included, makes the path invalid
        if (text.Length == 0 || text.Length > 500 || text[0] != '/') return false;
        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ':' or ',' or '"' or '\'' or '\\')) return false;
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        if (segments.Contains("..")) return false;
        normalized = "/" + string.Join('/', segments);
        return true;
    }

    /// <summary>The reason a normalized host path may not be bind-mounted, or null when it may.</summary>
    public string? HostPathDenial(string normalized)
    {
        if (normalized == "/") return "The root directory of the host cannot be mounted.";
        // An allowlisted prefix wins over a denied parent (its own sub-tree only).
        if (HostPathAllowlist.Any(prefix => IsSameOrBelow(normalized, prefix))) return null;
        var denied = DeniedHostPaths.FirstOrDefault(d => IsSameOrBelow(normalized, d));
        return denied is null ? null : $"'{denied}' and everything below it cannot be mounted from the host.";
    }

    private static bool IsSameOrBelow(string path, string directory) =>
        path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static string NormalizeAllowlistEntry(string entry)
    {
        if (!TryNormalizeHostPath(entry, out var normalized) || normalized == "/")
            throw new InvalidOperationException($"Aethera:Trust:HostPathAllowlist entry '{entry}' is not an absolute directory other than '/'.");
        // A parent of a denied directory (/var, /var/lib) would re-open it.
        if (DeniedHostPaths.Any(d => IsSameOrBelow(d, normalized)))
            throw new InvalidOperationException(
                $"Aethera:Trust:HostPathAllowlist entry '{entry}' contains a denied directory ('{DeniedHostPaths.First(d => IsSameOrBelow(d, normalized))}'); use a sub-directory.");
        return normalized;
    }

    // ---- ports ----------------------------------------------------------------------------------------------------------------------

    public bool IsPrivilegedPort(int port) => port < PrivilegedPortLimit;

    public bool IsReservedPort(int port) => ReservedPorts.Contains(port);
}

/// <summary>Problem responses of the trust model (ADR 0006). Every one carries <c>errors[]</c> with the offending JSON Pointers.</summary>
public static class TrustProblems
{
    public const string HostPathRequiresAdmin = "volume.host_path_requires_admin";
    public const string HostPathForbidden = "volume.host_path_forbidden";
    public const string PrivilegedPortRequiresAdmin = "port.privileged_requires_admin";
    public const string PortReserved = "port.reserved";
    public const string ComposeOptionRequiresAdmin = "compose.option_requires_admin";
    public const string ComposeInvalid = "compose.invalid";

    public static ApiProblem HostPathNeedsAdmin(string pointer) =>
        With(StatusCodes.Status403Forbidden, HostPathRequiresAdmin,
            "Mounting a directory of the host into a container is root-equivalent on that server and needs the Administrator role. "
            + "Use a named volume instead.", [FieldError.AtPointer(pointer, HostPathRequiresAdmin, "Requires the Administrator role.")]);

    public static ApiProblem HostPathDenied(string pointer, string reason) =>
        With(StatusCodes.Status422UnprocessableEntity, HostPathForbidden, reason, [FieldError.AtPointer(pointer, HostPathForbidden, reason)]);

    public static ApiProblem PrivilegedPort(IEnumerable<(string Pointer, int Port)> ports)
    {
        var list = ports.ToList();
        return With(StatusCodes.Status403Forbidden, PrivilegedPortRequiresAdmin,
            $"Publishing a host port below {TrustPolicy.PrivilegedPortLimit} needs the Administrator role ({string.Join(", ", list.Select(p => p.Port))}).",
            list.Select(p => FieldError.AtPointer(p.Pointer, PrivilegedPortRequiresAdmin, $"Port {p.Port} is privileged: Administrator only.")));
    }

    public static ApiProblem ReservedPort(IEnumerable<(string Pointer, int Port)> ports, bool callerIsAdmin)
    {
        var list = ports.ToList();
        var hint = callerIsAdmin ? " Pass allowReserved: true on the port to publish it anyway." : " Only an Administrator can publish it.";
        return With(StatusCodes.Status422UnprocessableEntity, PortReserved,
            $"Host port(s) {string.Join(", ", list.Select(p => p.Port))} are reserved by Aethera.{hint}",
            list.Select(p => FieldError.AtPointer(p.Pointer, PortReserved, $"Port {p.Port} is reserved by Aethera.")));
    }

    public static ApiProblem ComposeNeedsAdmin(string field, IReadOnlyList<ComposeFinding> findings) =>
        With(StatusCodes.Status403Forbidden, ComposeOptionRequiresAdmin,
            "The compose file uses options that are root-equivalent on the server and need the Administrator role: "
            + string.Join("; ", findings.Take(10).Select(f => $"{f.Pointer} ({f.Reason})")) + (findings.Count > 10 ? "; ..." : "") + ".",
            [FieldError.AtPointer(field, ComposeOptionRequiresAdmin, "Uses options that need the Administrator role.")])
            .WithExtension("pointers", findings.Select(f => f.Pointer).ToList());

    public static ApiProblem ComposeHostPathDenied(string field, IReadOnlyList<ComposeFinding> findings) =>
        With(StatusCodes.Status422UnprocessableEntity, HostPathForbidden,
            "The compose file mounts host paths that are never allowed: "
            + string.Join("; ", findings.Take(10).Select(f => $"{f.Pointer} ({f.Reason})")) + (findings.Count > 10 ? "; ..." : "") + ".",
            [FieldError.AtPointer(field, HostPathForbidden, "Mounts a forbidden host path.")])
            .WithExtension("pointers", findings.Select(f => f.Pointer).ToList());

    public static ApiProblem ComposeBroken(string field, string reason) =>
        With(StatusCodes.Status422UnprocessableEntity, ComposeInvalid, reason, [FieldError.AtPointer(field, ComposeInvalid, reason)]);

    private static ApiProblem With(int status, string code, string detail, IEnumerable<FieldError> errors) =>
        new ApiProblem(status, code, detail).WithExtension("errors", errors.ToList());
}
