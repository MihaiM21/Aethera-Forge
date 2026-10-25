namespace Aethera.Infrastructure.DnsProviders;

/// <summary>Zone and record-name logic of the DNS check (pure, no I/O).</summary>
public static class CloudflareZones
{
    /// <summary>The zone covering <paramref name="hostname"/>: the longest zone name the hostname equals or is a subdomain of; null when none.</summary>
    public static string? FindZone(string hostname, IEnumerable<string> zones)
    {
        var name = Normalize(hostname);
        if (name.StartsWith("*.", StringComparison.Ordinal)) name = name[2..];
        string? best = null;
        foreach (var zone in zones.Select(Normalize))
        {
            if (name == zone || name.EndsWith("." + zone, StringComparison.Ordinal))
                if (best is null || zone.Length > best.Length) best = zone;
        }

        return best;
    }

    /// <summary>
    /// Record names to look for, in order: the hostname itself, then the wildcard records that would cover it
    /// (<c>*.parent</c> for each parent up to the zone). A wildcard hostname looks for its own wildcard record first.
    /// </summary>
    public static IReadOnlyList<string> CandidateNames(string hostname, string zone)
    {
        var name = Normalize(hostname);
        var candidates = new List<string> { name };
        var labels = name.Split('.');
        for (var skip = 1; skip < labels.Length; skip++)
        {
            var parent = string.Join('.', labels.Skip(skip));
            if (parent != zone && !parent.EndsWith("." + zone, StringComparison.Ordinal)) break;
            var wildcard = "*." + parent;
            if (!candidates.Contains(wildcard)) candidates.Add(wildcard);
        }

        return candidates;
    }

    private static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();
}
