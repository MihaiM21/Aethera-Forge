using System.Text.RegularExpressions;

namespace Aethera.Engine.Proxy;

/// <summary>
/// A domain cannot be routed (a compose service that is not in the compose file, a wildcard host without a DNS provider, a port that cannot
/// be derived). The deploy job reports it as <c>proxy.failed</c> with this message.
/// </summary>
public sealed class ProxyRoutingException(string message) : Exception(message);

/// <summary>
/// Limits and refusals of a workload's custom container labels (ADR 0010): administrators only, merged last over the generated labels,
/// never the control plane's own <c>aethera.*</c> labels and never <c>traefik.docker.network</c> (it decides which network the proxy talks
/// to a container on).
/// </summary>
public static partial class CustomLabelRules
{
    public const int MaxCount = 50;
    public const int MaxKeyLength = 256;
    public const int MaxValueLength = 2048;
    public const int MaxTotalBytes = 16 * 1024;

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._/-]*\z")]
    private static partial Regex KeyPattern();

    public static bool IsProtected(string key) =>
        key.StartsWith("aethera.", StringComparison.OrdinalIgnoreCase) || key.StartsWith("traefik.docker.network", StringComparison.OrdinalIgnoreCase);

    /// <summary>What is wrong with <paramref name="labels"/>, as (key or empty, message) pairs; empty = valid.</summary>
    public static IReadOnlyList<(string Key, string Message)> Validate(IReadOnlyDictionary<string, string> labels)
    {
        var errors = new List<(string, string)>();
        if (labels.Count > MaxCount) errors.Add(("", $"At most {MaxCount} labels."));
        var total = 0;
        foreach (var (key, value) in labels)
        {
            total += key.Length + value.Length;
            if (key.Length > MaxKeyLength || !KeyPattern().IsMatch(key))
                errors.Add((key, $"A label name starts with a letter or digit and has at most {MaxKeyLength} letters, digits and . _ / -."));
            else if (IsProtected(key))
                errors.Add((key, "Labels starting with 'aethera.' and 'traefik.docker.network' are managed by Aethera and cannot be set."));
            if (value.Length > MaxValueLength) errors.Add((key, $"A label value has at most {MaxValueLength} characters."));
            else if (value.Any(char.IsControl)) errors.Add((key, "A label value cannot contain control characters."));
        }

        if (total > MaxTotalBytes) errors.Add(("", $"The labels are limited to {MaxTotalBytes / 1024} KiB in total."));
        return errors;
    }
}
