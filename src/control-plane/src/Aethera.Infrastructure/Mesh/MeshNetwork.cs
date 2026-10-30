using System.Net;
using System.Buffers.Binary;
using System.Net.Sockets;

namespace Aethera.Infrastructure.Mesh;

/// <summary>
/// An IPv4 range of the private mesh (ADR 0012): inside 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16, prefix /16 to /24, written as its network
/// address. The agent enforces the same rules, so a range the control plane accepts is never refused on the server.
/// </summary>
public readonly record struct MeshCidr
{
    public const int MinPrefix = 16;
    public const int MaxPrefix = 24;
    public const string Default = "10.210.0.0/16";

    private MeshCidr(uint network, int prefix)
    {
        Network = network;
        Prefix = prefix;
    }

    /// <summary>The network address as a number (big endian reading of the four octets).</summary>
    public uint Network { get; }

    public int Prefix { get; }

    public uint Broadcast => Network | (uint.MaxValue >> Prefix);

    /// <summary>Usable host addresses: everything except the network and the broadcast address.</summary>
    public int HostCapacity => (int)(Broadcast - Network - 1);

    public override string ToString() => $"{Format(Network)}/{Prefix}";

    public bool Contains(uint address) => address >= Network && address <= Broadcast;

    public bool IsHost(uint address) => address > Network && address < Broadcast;

    /// <summary>Parses "a.b.c.d/n". <paramref name="error"/> says what is wrong; the text is meant for the user.</summary>
    public static bool TryParse(string? text, out MeshCidr cidr, out string? error)
    {
        cidr = default;
        var parts = (text ?? "").Trim().Split('/');
        if (parts.Length != 2 || !TryParseAddress(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
        {
            error = "Must be an IPv4 range such as 10.210.0.0/16.";
            return false;
        }

        if (prefix is < MinPrefix or > MaxPrefix)
        {
            error = $"The prefix must be between /{MinPrefix} and /{MaxPrefix}.";
            return false;
        }

        if (!IsPrivate(address))
        {
            error = "Must be inside a private range: 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16.";
            return false;
        }

        var mask = uint.MaxValue << (32 - prefix);
        if ((address & mask) != address)
        {
            error = $"{parts[0]} is not the start of a /{prefix} range; did you mean {Format(address & mask)}/{prefix}?";
            return false;
        }

        cidr = new MeshCidr(address, prefix);
        error = null;
        return true;
    }

    public static MeshCidr Parse(string text) =>
        TryParse(text, out var cidr, out var error) ? cidr : throw new FormatException(error);

    /// <summary>Parses a dotted IPv4 address (no prefix, no leading zeros games).</summary>
    public static bool TryParseAddress(string? text, out uint address)
    {
        address = 0;
        if (text is null || !IPAddress.TryParse(text.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        if (text.Trim().Split('.').Length != 4) return false;
        address = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return true;
    }

    public static string Format(uint address) => $"{address >> 24}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}";

    private static bool IsPrivate(uint address) =>
        (address >> 24) == 10 || (address >> 20) == (172u << 4 | 1) || (address >> 16) == (192u << 8 | 168);

    /// <summary>
    /// The lowest free host address of the range, or null when it is full. <paramref name="used"/> are the addresses already taken (those outside
    /// the range are ignored).
    /// </summary>
    public uint? Allocate(IEnumerable<uint> used)
    {
        var taken = used.Where(IsHost).ToHashSet();
        if (taken.Count >= HostCapacity) return null;
        for (var candidate = Network + 1; candidate < Broadcast; candidate++)
            if (!taken.Contains(candidate)) return candidate;
        return null;
    }
}
