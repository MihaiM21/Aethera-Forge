using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Deployments;

/// <summary>
/// Which service of a compose file gets a route nobody configured (the automatic domain of ADR 0011, the host of a pull request preview): the
/// first service exposing a port a web server usually listens on, else the first one named like a web server. Nothing is guessed beyond that.
/// </summary>
public static class ComposeRoutePicker
{
    /// <summary>Ports a web server usually listens on, in order of preference.</summary>
    private static readonly int[] WebPorts = [80, 8080, 3000, 8000, 5000, 4000, 8888, 443];

    private static readonly string[] WebServiceNames = ["web", "app", "frontend", "nginx", "server", "site", "ui", "api"];

    /// <summary>The service and its port, or null when no service looks like a web server.</summary>
    public static (string Service, int? Port)? Pick(IReadOnlyList<ComposeServiceDefinition>? services)
    {
        var routable = (services ?? []).Where(s => s.Ports.Count > 0 && s.NetworkMode is null or "bridge" or "default").ToList();
        var pick = routable.Select(s => (Service: s, Port: WebPorts.Cast<int?>().FirstOrDefault(p => s.Ports.Contains(p!.Value))))
            .FirstOrDefault(x => x.Port is not null);
        if (pick.Service is null && routable.FirstOrDefault(s => WebServiceNames.Contains(s.Name)) is { } named)
            pick = (named, named.Ports[0]);
        return pick.Service is null ? null : (pick.Service.Name, pick.Port);
    }
}
