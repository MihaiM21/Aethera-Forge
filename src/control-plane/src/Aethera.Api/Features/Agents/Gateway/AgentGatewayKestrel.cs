using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Aethera.Domain;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Agents.Pki;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Agents.Gateway;

/// <summary>
/// The control plane's own server certificate for the gRPC listener, issued by the internal CA (ADR 0002: 90 days, "auto-renewed in
/// place"). It is created on the first handshake and replaced before it expires; Kestrel asks for it per handshake, so no restart is
/// needed. The SAN names the configured public endpoint plus loopback.
/// </summary>
public sealed class GatewayServerCertificate(IInternalCa ca, IOptions<AgentGatewayOptions> options, IClock clock) : IDisposable
{
    private readonly Lock _gate = new();
    private X509Certificate2? _current;
    private DateTimeOffset _renewAt;

    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                var now = clock.UtcNow;
                if (_current is null || now >= _renewAt)
                {
                    var settings = options.Value;
                    var fresh = ca.IssueServerCertificate([HostOf(settings.EffectivePublicEndpoint)], now);
                    var lifetime = fresh.NotAfter.ToUniversalTime() - now;
                    _renewAt = now + lifetime * 2 / 3; // 90 days -> renewed after 60
                    _current = fresh; // the replaced instance may still be used by running handshakes: left to its finalizer
                }

                return _current;
            }
        }
    }

    /// <summary>The host part of <c>host:port</c> (IPv6 literals in brackets are supported).</summary>
    public static string HostOf(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.StartsWith('[')) return trimmed[1..trimmed.IndexOf(']')];
        var colon = trimmed.LastIndexOf(':');
        return colon > 0 && trimmed.IndexOf(':') == colon ? trimmed[..colon] : trimmed;
    }

    public void Dispose() => _current?.Dispose();
}

/// <summary>
/// Configures Kestrel for the gateway (ADR 0002 "Listener and TLS"): the dedicated gRPC listener (default <c>:9443</c>) speaks HTTP/2 over
/// TLS 1.3 with <c>ClientCertificateMode.AllowCertificate</c>, validates presented certificates against the Aethera CA only, and keeps HTTP/2
/// keepalive pings (20 s / 10 s). Because Kestrel ignores <c>ASPNETCORE_URLS</c> as soon as an endpoint is configured in code, the API's
/// own addresses are re-bound here from the same settings.
/// </summary>
public sealed class AgentGatewayKestrelSetup(
    IConfiguration configuration, IOptions<AgentGatewayOptions> options, GatewayServerCertificate serverCertificate, IInternalCa ca) : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions kestrel)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;

        kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(20);
        kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);

        foreach (var address in ApiAddresses(configuration)) Bind(kestrel, address.Scheme, address.Host, address.Port);

        kestrel.Listen(IPAddress.TryParse(settings.GrpcBindAddress, out var bind) ? bind : IPAddress.Any, settings.GrpcPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(https =>
            {
                https.SslProtocols = SslProtocols.Tls13;
                https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate;
                https.CheckCertificateRevocation = false; // the control plane is the only verifier: revocation is a serial lookup
                https.ClientCertificateValidation = ca.IsTrustedClientCertificate;
                https.ServerCertificateSelector = (_, _) => serverCertificate.Current;
            });
        });
    }

    /// <summary>The addresses the API listens on: <c>urls</c>, else <c>HTTP_PORTS</c>/<c>HTTPS_PORTS</c>, else Kestrel's default <c>http://localhost:5000</c>.</summary>
    public static IReadOnlyList<(string Scheme, string Host, int Port)> ApiAddresses(IConfiguration configuration)
    {
        var result = new List<(string, string, int)>();
        var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (TryParseUrl(url, out var parsed)) result.Add(parsed);
        }
        else
        {
            foreach (var (key, scheme) in new[] { ("HTTP_PORTS", "http"), ("HTTPS_PORTS", "https") })
                foreach (var port in (configuration[key] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(port, out var number)) result.Add((scheme, "*", number));
        }

        if (result.Count == 0) result.Add(("http", "localhost", 5000));
        return result;
    }

    public static bool TryParseUrl(string url, out (string Scheme, string Host, int Port) parsed)
    {
        parsed = default;
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;
        var scheme = url[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https")) return false;
        var rest = url[(schemeEnd + 3)..].TrimEnd('/');
        var portAt = rest.LastIndexOf(':');
        var host = portAt > 0 && !rest[(portAt + 1)..].Contains(']') ? rest[..portAt] : rest;
        var port = portAt > 0 && int.TryParse(rest[(portAt + 1)..], out var explicitPort) ? explicitPort : scheme == "https" ? 443 : 80;
        parsed = (scheme, host.Trim('[', ']'), port);
        return true;
    }

    private static void Bind(KestrelServerOptions kestrel, string scheme, string host, int port)
    {
        void Configure(ListenOptions listen)
        {
            if (scheme == "https") listen.UseHttps();
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) kestrel.ListenLocalhost(port, Configure);
        else if (IPAddress.TryParse(host, out var ip)) kestrel.Listen(ip, port, Configure);
        else kestrel.ListenAnyIP(port, Configure); // "*", "+", "0.0.0.0" and host names
    }
}
