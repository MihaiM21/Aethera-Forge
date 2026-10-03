using System.Net;
using System.Net.Sockets;

namespace Aethera.Api.Features.Resources.DomainNames;

/// <summary>Resolves a hostname to its A/AAAA addresses. An abstraction so tests (and, later, DoH) can replace the system resolver.</summary>
public interface IDnsResolver
{
    /// <summary>The addresses of <paramref name="hostname"/>; an empty list when the name does not exist or has no address records.</summary>
    /// <exception cref="Exception">The lookup itself failed (timeout, resolver unavailable).</exception>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken cancellationToken);
}

/// <summary><see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> (A and AAAA records via the system resolver).</summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(hostname, cancellationToken);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            return [];
        }
    }
}
