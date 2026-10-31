using Aethera.Domain;

namespace Aethera.Infrastructure.Placement;

/// <summary>How a server's roles relate to what is being placed (ADR 0012, "Roles and placement").</summary>
public enum RoleFit
{
    /// <summary>The server has no roles: it accepts everything, so a one-server install never changes behaviour.</summary>
    Open,

    /// <summary>The server has the role the kind needs.</summary>
    Matches,

    /// <summary>
    /// The server is the control plane's own machine (<see cref="ServerRole.Master"/>) and has no role for the kind. It still accepts the workload,
    /// but a server that has the role is preferred: a single-server install keeps working with the roles the installer gave it.
    /// </summary>
    MasterFallback,

    /// <summary>The roles exclude the kind.</summary>
    Excluded,
}

/// <summary>The pure rules of placement: which role a kind needs, whether a server's roles accept it, and what to tell the user. No I/O.</summary>
public static class PlacementRules
{
    /// <summary>Applications and stateless services run on workers, databases and stateful services on storage servers, builds on build servers.</summary>
    public static ServerRole RequiredRole(PlacementKind kind) => kind switch
    {
        PlacementKind.StatefulService => ServerRole.Storage,
        PlacementKind.Build => ServerRole.Build,
        _ => ServerRole.Worker,
    };

    public static RoleFit Fit(IReadOnlyCollection<ServerRole> roles, PlacementKind kind)
    {
        if (roles.Count == 0) return RoleFit.Open;
        if (roles.Contains(RequiredRole(kind))) return RoleFit.Matches;
        return roles.Contains(ServerRole.Master) ? RoleFit.MasterFallback : RoleFit.Excluded;
    }

    /// <summary>The placement kind of a workload: an application, a service with a data volume (a database), or a service without data.</summary>
    public static PlacementKind KindOf(Workload workload) => workload switch
    {
        Service service => service.Volumes.Count > 0 ? PlacementKind.StatefulService : PlacementKind.StatelessService,
        _ => PlacementKind.Application,
    };

    /// <summary>The placement kind of a service template, from its volumes (a data volume makes it stateful).</summary>
    public static PlacementKind KindOfTemplate(int volumeCount) => volumeCount > 0 ? PlacementKind.StatefulService : PlacementKind.StatelessService;

    /// <summary>What a workload reserves on its server: the reservation, else the limit, else nothing (ADR 0012).</summary>
    public static double? CpuOf(RuntimeConfig runtime) => runtime.CpuReservation ?? runtime.CpuLimit;

    /// <inheritdoc cref="CpuOf"/>
    public static long? MemoryOf(RuntimeConfig runtime) => runtime.MemoryReservationBytes ?? runtime.MemoryLimitBytes;

    public static string RoleNames(IEnumerable<ServerRole> roles) => string.Join(", ", roles.OrderBy(r => r).Select(r => r.ToString().ToLowerInvariant()));

    public static string KindName(PlacementKind kind) => kind switch
    {
        PlacementKind.StatefulService => "a database or stateful service",
        PlacementKind.StatelessService => "a service",
        PlacementKind.Build => "a build",
        _ => "an application",
    };

    /// <summary>The warning for a server chosen by hand against its roles; null when the roles accept the kind.</summary>
    public static string? RoleWarning(string serverName, IReadOnlyCollection<ServerRole> roles, PlacementKind kind) =>
        Fit(roles, kind) == RoleFit.Excluded
            ? $"{serverName} has the roles {RoleNames(roles)}, which do not include {RequiredRole(kind).ToString().ToLowerInvariant()}, so Aethera would not place {KindName(kind)} there on its own. It was accepted because you chose the server; add the role in the server's settings to silence this."
            : null;

    /// <summary>Human text for a byte count (1.5 GiB), for reasons and warnings.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = unit == 0 ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return $"{(bytes < 0 ? "-" : "")}{text} {units[unit]}";
    }
}
