using System.Net;
using System.Net.Sockets;

namespace PlanShare;

/// <summary>
/// The one key every per-client limit (share, analytics, read, upload budget) is counted under.
/// An IPv4 address is its own key. An IPv4-mapped IPv6 address (how a dual-stack socket reports
/// an IPv4 caller) becomes that IPv4 address, so one caller has one key. Any other IPv6 address
/// becomes its /64 prefix: a single subscriber is normally given a whole /64, so counting per /64
/// makes one subscriber one client, however many addresses they use.
/// </summary>
internal static class ClientKey
{
    public const string Unknown = "unknown";

    public static string From(IPAddress? address)
    {
        if (address is null)
            return Unknown;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return $"{new IPAddress(bytes)}/64";
    }
}
