using System.Net;

namespace Aethera.Infrastructure.DnsProviders;

/// <summary>
/// Cloudflare's published edge address ranges. A hostname whose every A/AAAA answer lies inside them is behind the Cloudflare proxy (orange
/// cloud): the answer is a Cloudflare edge address, not the origin, so it cannot be compared with the server's IP (ADR 0010).
/// </summary>
public static class CloudflareIpRanges
{
    /// <summary>Copied on 2026-10-24 from https://www.cloudflare.com/ips-v4 and https://www.cloudflare.com/ips-v6.</summary>
    public const string CopiedOn = "2026-10-24";

    // To refresh: curl -s https://www.cloudflare.com/ips-v4 && curl -s https://www.cloudflare.com/ips-v6, replace the lists below and update
    // CopiedOn. The ranges change rarely (a few additions a year); a stale list only makes a new range look like a plain "Mismatch".
    private static readonly string[] V4 =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20",
        "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13",
        "131.0.72.0/22",
    ];

    private static readonly string[] V6 =
    [
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
    ];

    private static readonly IPNetwork[] Networks = V4.Concat(V6).Select(IPNetwork.Parse).ToArray();

    /// <summary>The ranges in CIDR notation (IPv4 first).</summary>
    public static IReadOnlyList<string> Cidrs { get; } = V4.Concat(V6).ToArray();

    public static bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return Networks.Any(n => n.Contains(address));
    }

    /// <summary>True when there is at least one address and every one lies inside Cloudflare's ranges (a mixed answer is not proxied).</summary>
    public static bool AllInside(IEnumerable<IPAddress> addresses)
    {
        var any = false;
        foreach (var address in addresses)
        {
            if (!Contains(address)) return false;
            any = true;
        }

        return any;
    }
}
