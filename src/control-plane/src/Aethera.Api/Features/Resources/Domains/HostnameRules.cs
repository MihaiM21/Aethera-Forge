using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Aethera.Api.Features.Resources;

/// <summary>Hostname validation and normalization for domains and servers (RFC 1123 labels, IDN to punycode, optional leftmost wildcard).</summary>
public static partial class HostnameRules
{
    [GeneratedRegex(@"\A[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex LabelPattern();

    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    /// <summary>
    /// Normalizes <paramref name="input"/> to its lower-case ASCII (punycode) form. Rules: 1-253 characters; dot-separated labels of 1-63
    /// letters, digits and hyphens (not at either end); the last label is not purely numeric (so IP addresses are not hostnames); a single
    /// trailing dot is dropped; a wildcard is allowed only as the whole leftmost label (<c>*.example.com</c>) and only when
    /// <paramref name="allowWildcard"/> is set; the part after <c>*.</c> needs at least two labels.
    /// </summary>
    public static bool TryNormalize(string? input, bool allowWildcard, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        var text = input?.Trim() ?? "";
        if (text.EndsWith('.')) text = text[..^1];
        if (text.Length == 0) { error = "Required."; return false; }

        var wildcard = false;
        if (text.StartsWith("*.", StringComparison.Ordinal))
        {
            if (!allowWildcard) { error = "Wildcards are not allowed here."; return false; }
            wildcard = true;
            text = text[2..];
        }

        if (text.Contains('*')) { error = "A wildcard is only allowed as the whole leftmost label (*.example.com)."; return false; }
        if (IPAddress.TryParse(text, out _)) { error = "An IP address is not a hostname."; return false; }

        string ascii;
        try
        {
            ascii = text.All(char.IsAscii) ? text.ToLowerInvariant() : Idn.GetAscii(text).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            error = "Contains characters that cannot be used in a hostname.";
            return false;
        }

        if (ascii.Length + (wildcard ? 2 : 0) > 253) { error = "Must be at most 253 characters."; return false; }
        var labels = ascii.Split('.');
        if (labels.Any(l => !LabelPattern().IsMatch(l)))
        {
            error = "Each label must be 1-63 letters, digits or hyphens and must not start or end with a hyphen.";
            return false;
        }

        if (labels[^1].All(char.IsAsciiDigit)) { error = "The last label must not be purely numeric."; return false; }
        if (wildcard && labels.Length < 2) { error = "A wildcard needs a registrable name after it (*.example.com)."; return false; }

        normalized = (wildcard ? "*." : "") + ascii;
        return true;
    }
}
