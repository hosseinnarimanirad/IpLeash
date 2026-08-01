using System.Net;
using System.Net.Sockets;

namespace IpLeash.Services;

/// <summary>
/// Tells apart addresses that can meaningfully be geolocated from ones that cannot.
///
/// Asking a geolocation provider about 192.168.1.10 wastes a request and, worse, invites a
/// provider to answer with the country of whoever asked. Screening these out locally keeps
/// "no country" honest: an unroutable reading means the machine is not on the expected exit,
/// which is exactly the case that should fail closed.
/// </summary>
public static class IpAddressClassifier
{
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6Multicast || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv4MappedToIPv6
                ? IsPubliclyRoutable(address.MapToIPv4())
                : !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            0 => false,                                        // 0.0.0.0/8, "this network"
            10 => false,                                       // RFC 1918
            127 => false,                                      // loopback
            100 => octets[1] is < 64 or > 127,                 // 100.64.0.0/10, carrier-grade NAT
            169 => octets[1] != 254,                           // 169.254.0.0/16, link-local
            172 => octets[1] is < 16 or > 31,                  // RFC 1918
            192 => !(octets[1] == 168),                        // RFC 1918
            >= 224 => false,                                   // multicast, reserved, broadcast
            _ => true,
        };
    }

    /// <summary>
    /// The cache key for an address: IPv4-mapped IPv6 is unmapped first, so ::ffff:1.2.3.4 and
    /// 1.2.3.4 do not occupy two entries and cost two lookups.
    /// </summary>
    public static string CacheKey(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
