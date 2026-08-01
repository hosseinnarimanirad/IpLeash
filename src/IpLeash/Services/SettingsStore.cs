using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using IpLeash.Models;

namespace IpLeash.Services;

/// <summary>
/// JSON-backed settings under %LOCALAPPDATA%\IpLeash. Deliberately swallows IO and parse
/// failures: a corrupt settings file must not stop the app from starting and enforcing.
///
/// A strict parse is tried first. When it throws, the file is re-read property by property so a
/// single unreadable value costs only that value — losing the whole file would silently discard
/// the user's monitored-app list, and the next save would write the emptiness back over it.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public SettingsStore()
        : this(Path.Combine(AppPaths.DataDirectory, "settings.json"))
    {
    }

    public SettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        string json;

        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, ReaderOptions);
            if (settings is not null)
            {
                settings.MigrateSavedTargets();
                return settings;
            }
        }
        catch (JsonException)
        {
            // Fall through to the salvage path below.
        }

        PreserveCorruptFile();

        var salvaged = LoadLenient(json);
        salvaged.MigrateSavedTargets();
        return salvaged;
    }

    /// <summary>
    /// Reads each known property independently, so one unparseable value does not take the rest
    /// of the file with it. Anything unreadable falls back to that property's default.
    /// </summary>
    private static AppSettings LoadLenient(string json)
    {
        JsonObject? root;

        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // Not even structurally valid JSON — nothing to salvage.
            return new AppSettings();
        }

        if (root is null)
        {
            return new AppSettings();
        }

        var settings = new AppSettings();

        settings.Apps = TryRead<List<MonitoredApp>>(root, nameof(AppSettings.Apps)) ?? [];
        settings.ExpectedPublicIp =
            TryRead<string>(root, nameof(AppSettings.ExpectedPublicIp)) ?? string.Empty;
        settings.SavedExpectedIps =
            TryRead<List<string>>(root, nameof(AppSettings.SavedExpectedIps)) ?? [];
        settings.SavedTargets =
            TryRead<List<SavedTarget>>(root, nameof(AppSettings.SavedTargets)) ?? [];
        settings.MatchMode =
            TryRead<MatchMode?>(root, nameof(AppSettings.MatchMode)) ?? MatchMode.ExactIp;
        settings.ExpectedCountryCode =
            TryRead<string>(root, nameof(AppSettings.ExpectedCountryCode)) ?? string.Empty;
        settings.HideIpAddresses =
            TryRead<bool?>(root, nameof(AppSettings.HideIpAddresses)) ?? false;
        settings.PollSeconds =
            TryRead<int?>(root, nameof(AppSettings.PollSeconds)) ?? AppSettings.DefaultPollSeconds;
        settings.HasShownTrayHint =
            TryRead<bool?>(root, nameof(AppSettings.HasShownTrayHint)) ?? false;

        return settings;
    }

    private static T? TryRead<T>(JsonObject root, string propertyName)
    {
        // The property name is matched case-insensitively, matching the strict reader, because a
        // hand-edited file is exactly the case this path exists to rescue.
        var node = root.FirstOrDefault(pair =>
            string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Value;

        if (node is null)
        {
            return default;
        }

        try
        {
            return node.Deserialize<T>(ReaderOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return default;
        }
    }

    /// <summary>
    /// Copies the unreadable file aside before anything overwrites it, so a salvage that drops a
    /// value leaves the original recoverable by hand.
    /// </summary>
    private void PreserveCorruptFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            var name = Path.GetFileNameWithoutExtension(_path);
            var backup = Path.Combine(
                directory ?? ".",
                $"{name}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}.json");

            File.Copy(_path, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best effort. Failing to keep a backup must not stop the app from starting.
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
