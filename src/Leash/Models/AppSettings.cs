namespace Leash.Models;

/// <summary>
/// User settings, persisted as JSON under %LOCALAPPDATA%\Leash\settings.json.
///
/// The expected IP is global: one address the machine must have, and every enabled app is
/// blocked together when it doesn't match.
/// </summary>
public sealed class AppSettings
{
    public List<MonitoredApp> Apps { get; set; } = [];

    /// <summary>The public/WAN IP the machine is expected to have, e.g. a VPN exit node.</summary>
    public string ExpectedPublicIp { get; set; } = string.Empty;

    /// <summary>
    /// Previously used expected IPs, offered as one-click chips so switching between known exit
    /// nodes does not mean retyping an address from memory.
    /// </summary>
    public List<string> SavedExpectedIps { get; set; } = [];

    /// <summary>Seconds between evaluations. Clamped to <see cref="MinPollSeconds"/>..<see cref="MaxPollSeconds"/>.</summary>
    public int PollSeconds { get; set; } = DefaultPollSeconds;

    /// <summary>
    /// Whether the "closing hides to the tray, it does not quit" balloon has been shown. Shown
    /// once, because silently vanishing on close is the kind of surprise that reads as a crash.
    /// </summary>
    public bool HasShownTrayHint { get; set; }

    /// <summary>
    /// Single-app path written by earlier versions. Read on load and folded into
    /// <see cref="Apps"/>, then never written again.
    /// </summary>
    public string? ExePath { get; set; }

    public const int DefaultPollSeconds = 15;
    public const int MinPollSeconds = 5;
    public const int MaxPollSeconds = 3600;

    /// <summary>Derived, so it must not be written to the settings file.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ClampedPollSeconds =>
        Math.Clamp(PollSeconds <= 0 ? DefaultPollSeconds : PollSeconds, MinPollSeconds, MaxPollSeconds);

    /// <summary>Every executable across every enabled app, deduplicated.</summary>
    public IReadOnlyList<string> EnabledExecutablePaths() => Apps
        .Where(a => a.Enabled)
        .SelectMany(a => a.ExecutablePaths)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
