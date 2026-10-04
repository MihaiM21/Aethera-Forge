using System.Net;
using System.Net.Sockets;
using Aethera.Domain;

namespace Aethera.Engine.Networking;

public interface IDnsResolver
{
    /// <summary>All A and AAAA addresses of <paramref name="host"/>; empty when it does not resolve.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}

public sealed record DnsCheckResult(string Host, DnsStatus Status, IReadOnlyList<string> Resolved, IReadOnlyList<string> Expected, string Message)
{
    public bool Ok => Status == DnsStatus.Ok;
}

/// <summary>Domain-to-server DNS check shown as a warning in the UI (spec section 12). Never blocks a deployment.</summary>
public sealed class DnsChecker(IDnsResolver resolver)
{
    public async Task<DnsCheckResult> CheckAsync(string host, IReadOnlyCollection<string> expectedAddresses, CancellationToken cancellationToken)
    {
        var expected = expectedAddresses.Select(Normalize).Where(a => a is not null).Select(a => a!).Distinct().ToList();
        var resolved = (await resolver.ResolveAsync(host, cancellationToken)).Select(a => Normalize(a.ToString())!).Distinct().ToList();

        if (resolved.Count == 0)
            return new(host, DnsStatus.Missing, resolved, expected, $"{host} does not resolve. Create an A record pointing to {string.Join(" or ", expected)}.");
        if (resolved.Intersect(expected).Any())
            return new(host, DnsStatus.Ok, resolved, expected, $"{host} points to this server.");
        return new(host, DnsStatus.Mismatch, resolved, expected,
            $"{host} resolves to {string.Join(", ", resolved)}, but this server is {string.Join(" or ", expected)}. Update the DNS record or HTTPS certificates cannot be issued.");
    }

    private static string? Normalize(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }
}
