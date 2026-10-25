using System.Text.RegularExpressions;

namespace Aethera.Engine.Deployments;

/// <summary>Where a volume name of a workload points (<see cref="VolumeNames.Classify"/>).</summary>
public enum VolumeNameKind
{
    /// <summary>A plain name of this workload: the Docker volume is <c>&lt;slug&gt;-&lt;id8&gt;-&lt;name&gt;</c>.</summary>
    Local,

    /// <summary>Already in the workload's own namespace (what the service templates store).</summary>
    Own,

    /// <summary>In the namespace of another workload: shares its data. Administrators only.</summary>
    Foreign,

    /// <summary>A volume of the Aethera installation itself (database, backups, certificates): never.</summary>
    Reserved,
}

/// <summary>
/// Names of the Docker volumes of a workload. Every named volume lives in the workload's own namespace, <c>&lt;slug&gt;-&lt;last 8 hex digits of
/// the workload id&gt;-&lt;name&gt;</c> (the service templates always did this), so the same name in production and development never shares data
/// and a user-chosen name can never address a volume of the host or of Aethera itself.
/// </summary>
public static partial class VolumeNames
{
    /// <summary>A name already in <c>&lt;slug&gt;-&lt;8 hex&gt;-&lt;rest&gt;</c> form, i.e. one that names a Docker volume of some workload.</summary>
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]*-[0-9a-f]{8}-[A-Za-z0-9_.-]+\z")]
    private static partial Regex QualifiedPattern();

    public static string Prefix(string slug, Guid workloadId) => $"{slug}-{workloadId.ToString("N")[^8..]}-";

    /// <summary>The name of the Docker volume behind a stored volume name.</summary>
    public static string DockerName(string slug, Guid workloadId, string name) =>
        QualifiedPattern().IsMatch(name) ? name : Prefix(slug, workloadId) + name;

    /// <summary>Own namespace; the first 8 digits (the form of container names) are accepted too.</summary>
    public static bool IsOwn(string name, string slug, Guid workloadId)
    {
        var id = workloadId.ToString("N");
        return name.Length > slug.Length + 10
               && (name.StartsWith(Prefix(slug, workloadId), StringComparison.Ordinal) || name.StartsWith($"{slug}-{id[..8]}-", StringComparison.Ordinal));
    }

    /// <summary>Volumes of the installation: <c>aethera_pgdata</c>, <c>aethera_aethera_data</c>, <c>aethera-traefik-acme</c>, anything with that prefix.</summary>
    public static bool IsReserved(string name) =>
        name.StartsWith("aethera_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("aethera-", StringComparison.OrdinalIgnoreCase);

    public static VolumeNameKind Classify(string name, string slug, Guid workloadId)
    {
        if (IsOwn(name, slug, workloadId)) return VolumeNameKind.Own;
        if (IsReserved(name)) return VolumeNameKind.Reserved;
        return QualifiedPattern().IsMatch(name) ? VolumeNameKind.Foreign : VolumeNameKind.Local;
    }
}
