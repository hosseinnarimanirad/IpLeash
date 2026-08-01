using System.Net;
using IpLeash.Models;

namespace IpLeash.Services;

/// <summary>
/// Resolves which country an address belongs to, in front of a cache.
///
/// Kept separate from <see cref="IPublicIpService"/> on purpose. The public-IP path is three
/// plain-text echo endpoints with nothing to parse and no rate limit; folding JSON geolocation
/// into it would put schema drift and quota errors into the fail-closed decision the whole app
/// rests on.
/// </summary>
public interface IGeoIpService
{
    /// <summary>
    /// Cache only. Never touches the network and never blocks, so it is safe to call from the UI
    /// thread or from inside a lock. Null means "not cached", not "no country".
    /// </summary>
    CountryInfo? TryGetCached(IPAddress address);

    /// <summary>
    /// Cache, then the provider chain. Null means the country could not be determined — the
    /// caller's signal to fail closed when the country is part of the decision.
    ///
    /// Throws <see cref="OperationCanceledException"/> only when <paramref name="ct"/> itself is
    /// cancelled. The service's own time budget expiring is reported as null, not as a throw.
    /// </summary>
    Task<CountryInfo?> GetCountryAsync(IPAddress address, CancellationToken ct = default);
}
