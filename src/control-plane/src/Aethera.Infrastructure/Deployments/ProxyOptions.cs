namespace Aethera.Infrastructure.Deployments;

/// <summary>Reverse proxy settings (<c>Aethera:Proxy</c>).</summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Aethera:Proxy";

    /// <summary>Let's Encrypt contact. Empty disables automatic HTTPS (routes still work over HTTP).</summary>
    public string? AcmeEmail { get; set; }

    /// <summary>Docker network shared by the proxy and routed applications.</summary>
    public string Network { get; set; } = "aethera-proxy";

    /// <summary>Traefik version tag; empty = the agent's default.</summary>
    public string? Version { get; set; }

    /// <summary>Use the Let's Encrypt staging CA (untrusted certificates, no rate limits) while testing.</summary>
    public bool Staging { get; set; }
}
