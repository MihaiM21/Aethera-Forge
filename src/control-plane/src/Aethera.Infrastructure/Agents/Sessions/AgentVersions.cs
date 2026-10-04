namespace Aethera.Infrastructure.Agents.Sessions;

/// <summary>Version comparison for <c>Welcome.min_agent_version</c>: <c>[v]major.minor[.patch][-pre][+build]</c>, pre-release and build ignored.</summary>
public static class AgentVersions
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0) core = core[..cut];
        if (!Version.TryParse(core, out var parsed) || parsed.Major < 0) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>True when <paramref name="agent"/> is at least <paramref name="minimum"/>. A minimum of 0.0.0 (or an unparseable one) accepts everything.</summary>
    public static bool IsAtLeast(string? agent, string? minimum)
    {
        if (!TryParse(minimum, out var min) || min == new Version(0, 0, 0)) return true;
        return TryParse(agent, out var actual) && actual >= min;
    }
}
