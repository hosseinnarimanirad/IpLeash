using System.IO;
using System.Text.Json;

namespace IpLeash.Services;

/// <inheritdoc cref="IBlockStateStore"/>
public sealed class BlockStateStore : IBlockStateStore
{
    private sealed record BlockState(List<string> ExePaths);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();

    public BlockStateStore()
        : this(Path.Combine(AppPaths.DataDirectory, "active-block.json"))
    {
    }

    public BlockStateStore(string path) => _path = path;

    public IReadOnlyList<string> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return Array.Empty<string>();
                }

                var state = JsonSerializer.Deserialize<BlockState>(File.ReadAllText(_path));
                return state?.ExePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
                       ?? (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }

    public void Save(IEnumerable<string> exePaths)
    {
        var paths = exePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        lock (_gate)
        {
            if (paths.Count == 0)
            {
                ClearCore();
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new BlockState(paths), SerializerOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort. Losing this only costs orphan detection after an abnormal exit.
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearCore();
        }
    }

    private void ClearCore()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
