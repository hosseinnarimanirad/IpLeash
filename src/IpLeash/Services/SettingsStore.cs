using System.IO;
using System.Text.Json;
using IpLeash.Models;

namespace IpLeash.Services;

/// <summary>
/// JSON-backed settings under %LOCALAPPDATA%\IpLeash. Deliberately swallows IO and parse
/// failures: a corrupt settings file must not stop the app from starting and enforcing.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public SettingsStore()
        : this(Path.Combine(AppPaths.DataDirectory, "settings.json"))
    {
    }

    public SettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a settings write is not worth crashing the app over.
        }
    }
}
