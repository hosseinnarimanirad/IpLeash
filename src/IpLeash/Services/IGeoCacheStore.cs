namespace IpLeash.Services;

/// <summary>One remembered address-to-country answer.</summary>
/// <param name="Ip">Normalised address string, the cache key.</param>
/// <param name="ResolvedAt">When the answer was obtained, used for expiry and eviction.</param>
public sealed record GeoCacheEntry(
    string Ip,
    string Code,
    string Name,
    string? City,
    DateTimeOffset ResolvedAt);

/// <summary>
/// Persists resolved countries so a restart does not mean re-querying every saved address, and
/// so a provider outage cannot blank out a flag the app already knew.
/// </summary>
public interface IGeoCacheStore
{
    IReadOnlyList<GeoCacheEntry> Load();

    void Save(IEnumerable<GeoCacheEntry> entries);
}
