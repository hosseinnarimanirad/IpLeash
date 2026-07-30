using IpLeash.Models;

namespace IpLeash.Services;

/// <summary>Enumerates IPv4 addresses bound to up network interfaces. Display-only.</summary>
public interface ILocalIpService
{
    IReadOnlyList<AdapterInfo> GetAdapters();
}
