using System.IO;
using System.Text.Json;
using Leash.Models;

namespace Leash.Services;

/// <summary>
/// JSON-backed settings under %LOCALAPPDATA%\Leash. Deliberately swallows IO and parse
/// failures: a corrupt settings file must not stop the app from starting and enforcing.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
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

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
            Migrate(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Folds the single-app layout written by earlier versions into the app list, so upgrading
    /// does not silently drop a configured target.
    /// </summary>
    private static void Migrate(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ExePath))
        {
            settings.ExePath = null;
            return;
        }

        var alreadyPresent = settings.Apps
            .SelectMany(a => a.ExecutablePaths)
            .Any(p => string.Equals(p, settings.ExePath, StringComparison.OrdinalIgnoreCase));

        if (!alreadyPresent)
        {
            settings.Apps.Add(new MonitoredApp
            {
                Name = Path.GetFileNameWithoutExtension(settings.ExePath),
                ExecutablePaths = [settings.ExePath],
                Enabled = true,
            });
        }

        settings.ExePath = null;
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
