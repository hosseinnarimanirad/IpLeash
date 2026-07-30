using System.Net;

namespace Leash.Services;

/// <summary>Resolves the machine's public/WAN IP address by asking an external echo service.</summary>
public interface IPublicIpService
{
    /// <summary>
    /// Returns the public IP, or null if no provider answered. A null result is the caller's
    /// signal to fail closed — it must not be treated as "unchanged".
    /// </summary>
    Task<IPAddress?> GetPublicIpAsync(CancellationToken ct = default);
}
