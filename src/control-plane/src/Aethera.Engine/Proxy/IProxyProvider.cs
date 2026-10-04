using Aethera.Domain.Transport;

namespace Aethera.Engine.Proxy;

/// <summary>Server-wide proxy settings.</summary>
/// <param name="AcmeEmail">Contact for Let's Encrypt. Null disables automatic HTTPS.</param>
/// <param name="Network">Docker network the proxy and every routed application join.</param>
public sealed record ProxySettings(string? AcmeEmail, string Network = "aethera-proxy", string? Version = null, bool AcmeStaging = false);

/// <summary>One public route to a container port.</summary>
/// <param name="Name">Stable per application (slug). Containers of consecutive deployments share it, so the proxy merges them.</param>
public sealed record RouteSpec(string Name, IReadOnlyList<string> Hosts, int ContainerPort, string Network)
{
    public bool Https { get; init; } = true;
    public bool RedirectToHttps { get; init; } = true;
    public string? PathPrefix { get; init; }
}

/// <summary>Turns routes into whatever a reverse proxy needs (ADR 0002 proxy abstraction, spec section 12).</summary>
public interface IProxyProvider
{
    string Name { get; }

    /// <summary>The command that makes the proxy present and configured on a server.</summary>
    ProxyEnsureCommand BuildEnsureCommand(ProxySettings settings);

    /// <summary>Container labels that publish <paramref name="route"/>. No labels (an empty map) when there are no hosts.</summary>
    IReadOnlyDictionary<string, string> RouteLabels(RouteSpec route);
}
