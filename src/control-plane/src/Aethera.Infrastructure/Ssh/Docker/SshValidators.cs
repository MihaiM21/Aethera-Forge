using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Ssh.Docker;

/// <summary>
/// Strict validators for every variable part of a Docker CLI command line (ADR 0002 "SshTransport"). A value that does not match is refused
/// with <c>transport.command_rejected</c> before anything is sent. Validation is defence in depth: <see cref="ShellQuote"/> still quotes
/// everything.
/// </summary>
public static partial class SshValidators
{
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z")]
    private static partial Regex NamePattern();

    // OCI distribution grammar: [domain/]path[:tag][@digest]
    [GeneratedRegex(@"\A(?:(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9])(?:\.(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9]))*(?::[0-9]{1,5})?/)?[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*(?::[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127})?(?:@[A-Za-z][A-Za-z0-9]*(?:[-_+.][A-Za-z][A-Za-z0-9]*)*:[0-9a-fA-F]{32,})?\z")]
    private static partial Regex ImageRefPattern();

    [GeneratedRegex(@"\A(?:sha256:)?[0-9a-f]{12,64}\z")]
    private static partial Regex ImageIdPattern();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,254}\z")]
    private static partial Regex EnvNamePattern();

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.\-/]{0,254}\z")]
    private static partial Regex LabelKeyPattern();

    [GeneratedRegex(@"\A/(?:[A-Za-z0-9_.@+-]+(?:/[A-Za-z0-9_.@+-]+)*)?\z")]
    private static partial Regex AbsolutePathPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_.@+-]+(?:/[A-Za-z0-9_.@+-]+)*\z")]
    private static partial Regex RelativePathPattern();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,62}\z")]
    private static partial Regex ComposeProjectPattern();

    [GeneratedRegex(@"\A[a-z0-9]+(?:/[a-z0-9._-]+){1,2}\z")]
    private static partial Regex PlatformPattern();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9]{0,31}\z")]
    private static partial Regex SignalPattern();

    [GeneratedRegex(@"\A(?:ALL|[A-Z][A-Z0-9_]{1,31})\z")]
    private static partial Regex CapabilityPattern();

    [GeneratedRegex(@"\A[0-9]+(?:[,-][0-9]+)*\z")]
    private static partial Regex CpusetPattern();

    [GeneratedRegex(@"\A(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?))*(?::[0-9]{1,5})?\z")]
    private static partial Regex RegistryHostPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,199}\z")]
    private static partial Regex GitRefPattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]*\z")]
    private static partial Regex DriverPattern();

    [GeneratedRegex(@"\A[a-zA-Z0-9_.][a-zA-Z0-9_.-]*(?::[a-zA-Z0-9_.][a-zA-Z0-9_.-]*)?\z")]
    private static partial Regex UserPattern();

    private static ServerTransportException Reject(string what, string why) => new(TransportErrors.CommandRejected, $"{what} {why}");

    public static bool IsName(string? value) => value is not null && NamePattern().IsMatch(value);

    /// <summary>A container, volume or network name or a (short or full) container id.</summary>
    public static string Name(string? value, string what)
    {
        if (!IsName(value)) throw Reject(what, "must match ^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$.");
        return value!;
    }

    public static bool IsImageRef(string? value) =>
        value is { Length: > 0 and <= 255 } && (ImageRefPattern().IsMatch(value) || ImageIdPattern().IsMatch(value));

    /// <summary>An image reference per the OCI grammar, or an image id (<c>sha256:...</c> / hex).</summary>
    public static string ImageRef(string? value, string what = "The image reference")
    {
        if (!IsImageRef(value)) throw Reject(what, "is not a valid image reference.");
        return value!;
    }

    /// <summary>An image operand: valid by the OCI grammar, which also rules out a leading '-' (option injection).</summary>
    public static string NotOptionImage(string? value) => ImageRef(value);

    /// <summary>A tag to build: an OCI name with an optional tag, never a digest.</summary>
    public static string ImageTag(string? value)
    {
        ImageRef(value, "The image tag");
        if (value!.Contains('@')) throw Reject("The image tag", "must not carry a digest.");
        return value;
    }

    public static int Port(int value, string what, bool allowZero = false)
    {
        if (value < (allowZero ? 0 : 1) || value > 65535) throw Reject(what, "must be a port between 1 and 65535.");
        return value;
    }

    public static string HostIp(string? value)
    {
        if (value is null || !IPAddress.TryParse(value, out var ip)) throw Reject("The host IP", "must be an IP address.");
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
    }

    public static string EnvName(string? value)
    {
        if (value is null || !EnvNamePattern().IsMatch(value)) throw Reject("An environment variable name", "must match ^[A-Za-z_][A-Za-z0-9_]*$.");
        return value;
    }

    /// <summary>Environment values reach Docker through an env-file: one line each, so no line breaks or NUL.</summary>
    public static string EnvValue(string name, string? value)
    {
        value ??= "";
        if (value.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0) throw Reject($"The value of {name}", "must not contain line breaks or NUL.");
        return value;
    }

    public static string LabelKey(string? value)
    {
        if (value is null || !LabelKeyPattern().IsMatch(value)) throw Reject("A label key", "is not valid.");
        return value;
    }

    public static string LabelValue(string key, string? value)
    {
        value ??= "";
        if (value.Length > 4096 || value.Any(char.IsControl)) throw Reject($"The label {key}", "has a value that is too long or contains control characters.");
        return value;
    }

    /// <summary>A <c>label=...</c> filter value: <c>key</c> or <c>key=value</c>.</summary>
    public static string LabelFilter(string? value)
    {
        if (string.IsNullOrEmpty(value)) throw Reject("A label filter", "must not be empty.");
        LabelKey(value.Split('=', 2)[0]);
        if (value.Length > 4096 || value.Any(char.IsControl)) throw Reject("A label filter", "contains control characters.");
        return value;
    }

    /// <summary>An absolute path without <c>..</c>, safe for a <c>--mount</c> CSV value (no comma, quote or equals sign).</summary>
    public static string AbsolutePath(string? value, string what)
    {
        if (value is not { Length: > 0 and <= 1024 } || !AbsolutePathPattern().IsMatch(value) || HasDotSegment(value)) throw Reject(what, "must be a plain absolute path.");
        return value;
    }

    /// <summary>A relative path inside a repository (build context, Dockerfile).</summary>
    public static string RelativePath(string? value, string what)
    {
        if (value is not { Length: > 0 and <= 512 } || !RelativePathPattern().IsMatch(value) || HasDotSegment(value)) throw Reject(what, "must be a plain relative path.");
        return value;
    }

    private static bool HasDotSegment(string path) => path.Split('/').Any(segment => segment is ".." or ".");

    public static string ComposeProject(string? value)
    {
        if (value is null || !ComposeProjectPattern().IsMatch(value)) throw Reject("The Compose project name", "must match ^[a-z0-9][a-z0-9_-]{0,62}$.");
        return value;
    }

    public static string ServiceName(string? value) => Name(value, "A service name");

    public static string Platform(string? value)
    {
        if (value is null || !PlatformPattern().IsMatch(value)) throw Reject("The platform", "must look like linux/amd64.");
        return value;
    }

    public static string Signal(string? value)
    {
        var text = value?.StartsWith("SIG", StringComparison.Ordinal) == true ? value[3..] : value;
        if (text is null || !SignalPattern().IsMatch(text)) throw Reject("The stop signal", "is not valid.");
        return text;
    }

    public static string Capability(string? value)
    {
        if (value is null || !CapabilityPattern().IsMatch(value)) throw Reject("A capability name", "is not valid.");
        return value;
    }

    public static string Cpuset(string? value)
    {
        if (value is null || !CpusetPattern().IsMatch(value)) throw Reject("The cpuset", "must look like 0-3 or 0,2.");
        return value;
    }

    public static string LogDriver(string? value)
    {
        if (value is not ("json-file" or "local" or "none")) throw Reject("The log driver", "must be json-file, local or none.");
        return value;
    }

    public static string NetworkDriver(string? value)
    {
        if (value is null || !DriverPattern().IsMatch(value)) throw Reject("The network driver", "is not valid.");
        return value;
    }

    public static string VolumeDriver(string? value)
    {
        if (value is null || !DriverPattern().IsMatch(value)) throw Reject("The volume driver", "is not valid.");
        return value;
    }

    /// <summary>A <c>key=value</c> driver option; key restricted, value free of control characters.</summary>
    public static string KeyValueOption(string key, string? value, string what)
    {
        if (!LabelKeyPattern().IsMatch(key)) throw Reject(what, "has an invalid key.");
        value ??= "";
        if (value.Length > 1024 || value.Any(char.IsControl)) throw Reject(what, "has an invalid value.");
        return $"{key}={value}";
    }

    public static string RegistryServer(string? value)
    {
        if (value is null) throw Reject("The registry", "must be set.");
        if (value is "https://index.docker.io/v1/") return value;
        if (value.Length > 255 || !RegistryHostPattern().IsMatch(value)) throw Reject("The registry", "must be a host name with an optional port.");
        return value;
    }

    /// <summary>User names and passwords are quoted data (or stdin lines); they only have to fit on one line.</summary>
    public static string OneLine(string? value, string what)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0) throw Reject(what, "must be a single line.");
        return value;
    }

    public static string Hostname(string? value)
    {
        if (!IsName(value)) throw Reject("The hostname", "is not valid.");
        return value!;
    }

    public static string User(string? value)
    {
        if (value is null || value.Length > 128 || !UserPattern().IsMatch(value)) throw Reject("The container user", "must be a name, uid or name:group.");
        return value;
    }

    public static string ExtraHost(string? value)
    {
        if (value is null || value.Length > 300 || value.Any(c => char.IsControl(c) || c == ' ')) throw Reject("An extra host", "must look like host:ip.");
        var parts = value.Split(':', 2);
        if (parts.Length != 2 || !IsName(parts[0])) throw Reject("An extra host", "must look like host:ip.");
        return value;
    }

    public static string GitRef(string? value)
    {
        if (value is null || !GitRefPattern().IsMatch(value) || value.Contains("..", StringComparison.Ordinal) || value.Contains("//", StringComparison.Ordinal)
            || value.EndsWith('/') || value.EndsWith(".lock", StringComparison.Ordinal))
            throw Reject("The git ref", "is not a plain branch, tag or commit.");
        return value;
    }

    /// <summary>
    /// A git URL <c>docker build</c> may fetch: <c>https://host/path</c> without credentials, query or fragment, so it is public by
    /// construction (ADR 0002: only a public git URL).
    /// </summary>
    public static string PublicGitUrl(string? value)
    {
        const string forbidden = " '\"\\#`$;|&<>(){}*!";
        if (value is not { Length: > 0 and <= 500 } || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || value.Any(c => char.IsControl(c) || forbidden.Contains(c)))
            throw Reject("The repository URL", "must be a public https URL without credentials.");
        if (uri.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic) throw Reject("The repository URL", "has an invalid host.");
        return value;
    }

    /// <summary>A whole number of seconds (rounded up) for options such as <c>--time</c>.</summary>
    public static string Seconds(TimeSpan value, string what)
    {
        var seconds = (long)Math.Ceiling(value.TotalSeconds);
        if (seconds is < 0 or > 86400 * 7) throw Reject(what, "is out of range.");
        return seconds.ToString(CultureInfo.InvariantCulture);
    }

    public static string Hex(string? value, string what, int min = 12, int max = 64)
    {
        if (value is null || value.Length < min || value.Length > max || !value.All(Uri.IsHexDigit)) throw Reject(what, "must be a hexadecimal id.");
        return value;
    }
}
