using System.Text;
using System.Text.RegularExpressions;

namespace Aethera.Domain;

/// <summary>URL/DNS-safe identifier helper: lowercase letters, digits and single hyphens, 1-63 chars.</summary>
public static partial class Slug
{
    public const int MaxLength = 63;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ValidPattern();

    public static bool IsValid(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= MaxLength && ValidPattern().IsMatch(slug);

    /// <summary>Converts a display name to a slug; returns an empty string when nothing usable remains.</summary>
    public static string FromName(string name)
    {
        var sb = new StringBuilder(name.Length);
        var pendingHyphen = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && sb.Length > 0) sb.Append('-');
                pendingHyphen = false;
                sb.Append(ch);
            }
            else
            {
                pendingHyphen = true;
            }
        }
        var result = sb.ToString();
        return result.Length > MaxLength ? result[..MaxLength].TrimEnd('-') : result;
    }
}
