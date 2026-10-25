using System.Reflection;
using System.Text.RegularExpressions;

namespace Aethera.Infrastructure.SelfOps;

/// <summary>A semantic version (semver.org 2.0) with precedence comparison. Build metadata (<c>+sha</c>) is ignored.</summary>
public sealed partial class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    [GeneratedRegex(@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();

    private readonly string[] _pre;

    private SemanticVersion(int major, int minor, int patch, string? pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = pre;
        _pre = pre is null ? [] : pre.Split('.');
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public bool IsPreRelease => PreRelease is not null;

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out var major) || !int.TryParse(m.Groups[2].Value, out var minor) || !int.TryParse(m.Groups[3].Value, out var patch)) return false;
        version = new SemanticVersion(major, minor, patch, m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a semantic version.");

    /// <summary>Precedence per semver 2.0 section 11: a release is newer than its own pre-releases; pre-release identifiers compare field by field.</summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (_pre.Length == 0 && other._pre.Length == 0) return 0;
        if (_pre.Length == 0) return 1;
        if (other._pre.Length == 0) return -1;
        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i];
            var b = other._pre[i];
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            if (aNumeric && bNumeric)
            {
                // Compared as numbers without overflowing: by length (no leading zeros are allowed), then text.
                c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            }
            else if (aNumeric) c = -1;
            else if (bNumeric) c = 1;
            else c = string.CompareOrdinal(a, b);
            if (c != 0) return Math.Sign(c);
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);
    public override string ToString() => $"{Major}.{Minor}.{Patch}{(PreRelease is null ? "" : "-" + PreRelease)}";

    public static bool operator ==(SemanticVersion? a, SemanticVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemanticVersion? a, SemanticVersion? b) => !(a == b);
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
}

/// <summary>The version of the running control plane: the informational version stamped by the release build (<c>0.0.0-dev</c> for a local build).</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var entry = Assembly.GetEntryAssembly() ?? assembly;
        var info = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // "1.2.3+<commit>" -> "1.2.3": the commit suffix is build metadata.
        var plus = info?.IndexOf('+') ?? -1;
        var clean = plus >= 0 ? info![..plus] : info;
        return SemanticVersion.TryParse(clean, out var v) ? v.ToString() : "0.0.0-dev";
    }
}
