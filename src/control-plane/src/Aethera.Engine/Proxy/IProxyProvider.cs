using Aethera.Domain.Transport;

namespace Aethera.Engine.Proxy;

/// <summary>Server-wide proxy settings.</summary>
/// <param name="AcmeEmail">Contact for Let's Encrypt. Null disables automatic HTTPS (HTTPS routes then serve Traefik's default certificate).</param>
/// <param name="Network">Docker network the proxy and every routed application join.</param>
public sealed record ProxySettings(string? AcmeEmail, string Network = "aethera-proxy", string? Version = null, bool AcmeStaging = false)
{
    /// <summary>DNS-01 certificates (wildcards, certificates before the DNS switch). Null = only the HTTP-01 resolver.</summary>
    public DnsChallengeSettings? DnsChallenge { get; init; }

    /// <summary>The control plane's own domain, routed by this server's proxy (ADR 0011). Null on every other server.</summary>
    public ControlPlaneRoute? ControlPlane { get; init; }
}

/// <summary>The domain the Aethera UI and API are served on through the proxy (a file-provider route, not container labels).</summary>
/// <param name="Upstream">Where the control plane listens on the proxy network.</param>
public sealed record ControlPlaneRoute(string Host, bool Https, string Upstream = ControlPlaneRoute.DefaultUpstream)
{
    /// <summary>The network alias of the <c>aethera</c> service on <c>aethera-proxy</c> (<c>deploy/docker-compose.prod.yml</c>).</summary>
    public const string DefaultUpstream = "http://aethera-control-plane:8080";
}

/// <summary>A DNS provider the proxy can solve ACME DNS-01 challenges with (ADR 0010). Only Cloudflare today.</summary>
/// <param name="Provider">Traefik/lego provider name, <c>cloudflare</c>.</param>
/// <param name="ApiToken">SECRET: the provider's API token (Zone:DNS:Edit); passed to the proxy through its environment, never in the configuration file.</param>
public sealed record DnsChallengeSettings(string Provider, string ApiToken);

/// <summary>One public route to a container port.</summary>
/// <param name="Name">Unique per workload (<see cref="RouteNames"/>). Containers of consecutive deployments share it, so the proxy merges them.</param>
public sealed record RouteSpec(string Name, IReadOnlyList<string> Hosts, int ContainerPort, string Network)
{
    public bool Https { get; init; } = true;
    public bool RedirectToHttps { get; init; } = true;
    public string? PathPrefix { get; init; }

    /// <summary>Certificate resolver of the HTTPS router; null = Traefik's default certificate (no ACME email, or no resolver wanted).</summary>
    public string? CertResolver { get; init; } = TraefikProxyProvider.CertResolver;

    /// <summary>Answer with a permanent redirect to this hostname (path and query kept) instead of serving a container. <see cref="ContainerPort"/> is ignored.</summary>
    public string? RedirectTo { get; init; }

    /// <summary>Remove <see cref="PathPrefix"/> before the request reaches the container.</summary>
    public bool StripPathPrefix { get; init; }

    /// <summary>SECRET: htpasswd lines (<c>user:$2y$...</c>, bcrypt) for HTTP basic auth. Null = open.</summary>
    public IReadOnlyList<string>? BasicAuthUsers { get; init; }
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
