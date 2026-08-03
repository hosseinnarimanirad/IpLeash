using System.IO;
using System.Text.Json;

namespace IpLeash.Services;

/// <summary>
/// <inheritdoc cref="IGeoCacheStore"/>
///
/// Its own file, deliberately not a section of settings.json: this is disposable data written
/// on a timer, and a half-written or corrupt cache must never reach the loader that owns the
/// user's monitored-app list.
/// </summary>
public sealed class GeoCacheStore : IGeoCacheStore
{
    private sealed record GeoCache(List<GeoCacheEntry> Entries);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    public GeoCacheStore()
        : this(Path.Combine(AppPaths.DataDirectory, "geo-cache.json"))
    {
    }

    public GeoCacheStore(string path) => _path = path;

    public IReadOnlyList<GeoCacheEntry> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return Array.Empty<GeoCacheEntry>();
                }

                var cache = JsonSerializer.Deserialize<GeoCache>(File.ReadAllText(_path));
                return cache?.Entries?
                    .Where(e => !string.IsNullOrWhiteSpace(e.Ip) && !string.IsNullOrWhiteSpace(e.Code))
                    .ToList() ?? (IReadOnlyList<GeoCacheEntry>)Array.Empty<GeoCacheEntry>();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Losing the cache costs one lookup per address. Never worth surfacing.
                return Array.Empty<GeoCacheEntry>();
            }
        }
    }

    public void Save(IEnumerable<GeoCacheEntry> entries)
    {
        var list = entries.ToList();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new GeoCache(list), SerializerOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }
}
