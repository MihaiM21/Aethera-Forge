using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Aethera.Api.Http;

/// <summary>
/// Real client address and scheme behind a reverse proxy (Traefik). <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are honoured
/// <b>only</b> when the TCP peer is a trusted proxy; from anyone else they are ignored, so a client cannot choose its own address (login
/// rate limit, audit log) or scheme (cookie, origin checks). The trusted proxies are
/// <c>Aethera:Http:TrustedProxies</c> (environment <c>Aethera__Http__TrustedProxies</c>): comma-separated IP addresses or CIDR ranges,
/// for example <c>172.18.0.0/16, 10.0.0.5</c>. Not set means loopback only (<c>127.0.0.0/8</c>, <c>::1</c>); a configured list replaces that
/// default. There is no wildcard: trusting everyone would let anyone forge their address.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string TrustedProxiesKey = "Aethera:Http:TrustedProxies";

    /// <summary>What the middleware applies; also the default for <see cref="TrustedProxiesKey"/>.</summary>
    public const ForwardedHeaders Applied = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    public static IServiceCollection AddAetheraForwardedHeaders(this IServiceCollection services)
    {
        // Bound lazily so the final (host/test) configuration is used.
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = Applied;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var entry in Parse(configuration[TrustedProxiesKey]))
            {
                if (entry.Network is { } network) options.KnownIPNetworks.Add(network);
                else options.KnownProxies.Add(entry.Address!);
            }
        });
        return services;
    }

    public static IApplicationBuilder UseAetheraForwardedHeaders(this IApplicationBuilder app) => app.UseForwardedHeaders();

    /// <summary>A trusted proxy: a single address or a network.</summary>
    public readonly record struct TrustedProxy(IPAddress? Address, IPNetwork? Network);

    /// <summary>Parses the setting; empty or missing yields loopback only. Throws <see cref="InvalidOperationException"/> on an invalid entry.</summary>
    public static IReadOnlyList<TrustedProxy> Parse(string? value)
    {
        var entries = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            return [new TrustedProxy(null, new IPNetwork(IPAddress.Loopback, 8)), new TrustedProxy(IPAddress.IPv6Loopback, null)];

        var result = new List<TrustedProxy>();
        foreach (var entry in entries)
        {
            if (entry.Contains('/'))
            {
                if (!IPNetwork.TryParse(entry, out var network))
                    throw new InvalidOperationException($"{TrustedProxiesKey}: '{entry}' is not a valid CIDR range (for example 172.18.0.0/16).");
                result.Add(new TrustedProxy(null, network));
            }
            else
            {
                if (!IPAddress.TryParse(entry, out var address) || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    throw new InvalidOperationException($"{TrustedProxiesKey}: '{entry}' is not a valid IP address or CIDR range.");
                result.Add(new TrustedProxy(address, null));
            }
        }

        return result;
    }
}
