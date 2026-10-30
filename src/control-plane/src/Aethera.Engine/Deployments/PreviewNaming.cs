using System.Text.RegularExpressions;

namespace Aethera.Engine.Deployments;

/// <summary>
/// Names of a pull request preview (ADR 0012): its host (<c>pr-&lt;n&gt;-&lt;label&gt;.&lt;base&gt;</c>) and the named volumes that make it share nothing
/// with the application it previews. Pure.
/// </summary>
public static partial class PreviewNaming
{
    /// <summary>A DNS label: lower-case letters, digits and dashes, not starting or ending with a dash, at most 63 characters.</summary>
    public const int MaxLabelLength = 63;

    [GeneratedRegex(@"\A[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex LabelPattern();

    /// <summary>
    /// <c>pr-&lt;number&gt;-&lt;label&gt;</c>, where the label is the application's: the first label of its automatic host
    /// (<paramref name="applicationHostname"/>, e.g. <c>web-3fa2c1</c> of <c>web-3fa2c1.apps.example.com</c>), else its slug. The label is shortened
    /// so the whole host label stays a valid DNS label.
    /// </summary>
    public static string HostLabel(int number, string? applicationHostname, string applicationSlug)
    {
        var first = applicationHostname is { Length: > 0 } host ? host.Split('.')[0] : null;
        var label = Clean(first is { Length: > 0 } ? first : applicationSlug);
        if (label.Length == 0) label = "app";
        var prefix = $"pr-{number}-";
        var max = MaxLabelLength - prefix.Length;
        if (label.Length > max) label = label[..max].TrimEnd('-');
        var result = prefix + label;
        return LabelPattern().IsMatch(result) ? result : $"pr-{number}";
    }

    /// <summary>The host of a preview: <see cref="HostLabel"/> under <paramref name="baseDomain"/> (the server's wildcard domain or its sslip.io name). Null when it would not be a valid host name.</summary>
    public static string? Hostname(int number, string? applicationHostname, string applicationSlug, string baseDomain)
    {
        var host = $"{HostLabel(number, applicationHostname, applicationSlug)}.{baseDomain.Trim().TrimStart('.').ToLowerInvariant()}";
        return host.Length <= 253 ? host : null;
    }

    /// <summary>
    /// The name of a stored volume as the preview sees it. A preview never mounts a volume of the application (or of any other workload): the name
    /// is reduced to a plain one, which <see cref="VolumeNames.DockerName"/> then puts in the preview's own namespace. A name in the application's
    /// namespace loses its prefix; one that names another workload's volume gets its dashes replaced, so it no longer reads as a qualified name.
    /// </summary>
    public static string VolumeName(string storedName, string applicationSlug, Guid applicationId)
    {
        if (VolumeNames.IsOwn(storedName, applicationSlug, applicationId))
        {
            var prefix = VolumeNames.Prefix(applicationSlug, applicationId);
            if (storedName.StartsWith(prefix, StringComparison.Ordinal)) return storedName[prefix.Length..];
            var short8 = $"{applicationSlug}-{applicationId.ToString("N")[..8]}-";
            return storedName[short8.Length..];
        }

        return VolumeNames.Classify(storedName, applicationSlug, applicationId) == VolumeNameKind.Foreign ? storedName.Replace('-', '_') : storedName;
    }

    private static string Clean(string value) =>
        new string(value.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray()).Trim('-');
}
