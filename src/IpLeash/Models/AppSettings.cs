namespace IpLeash.Models;

/// <summary>
/// User settings, persisted as JSON under %LOCALAPPDATA%\IpLeash\settings.json.
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
    /// Previously used expected IPs. Superseded by <see cref="SavedTargets"/>, which also holds
    /// country entries, but kept as a <c>List&lt;string&gt;</c> and rewritten on every save as a
    /// mirror of the exact-IP entries. Retyping it would make files written before this feature
    /// fail to parse, and would stop an older build from reading files written by this one.
    /// </summary>
    public List<string> SavedExpectedIps { get; set; } = [];

    /// <summary>
    /// Which rule decides a match. Absent from files written before country locking existed, so
    /// those load as <see cref="MatchMode.ExactIp"/> and behave exactly as they did.
    /// </summary>
    public MatchMode MatchMode { get; set; } = MatchMode.ExactIp;

    /// <summary>ISO 3166-1 alpha-2 to lock to. Only consulted when <see cref="MatchMode"/> is Country.</summary>
    public string ExpectedCountryCode { get; set; } = string.Empty;

    /// <summary>
    /// Saved one-click targets, exact addresses and countries alike, in the user's own order.
    /// Authoritative; <see cref="SavedExpectedIps"/> is derived from it.
    /// </summary>
    public List<SavedTarget> SavedTargets { get; set; } = [];

    /// <summary>
    /// Masks every address on screen, for screenshots and screen sharing. Display only: what is
    /// compared and what reaches the firewall is unaffected.
    /// </summary>
    public bool HideIpAddresses { get; set; }

    /// <summary>Seconds between evaluations. Clamped to <see cref="MinPollSeconds"/>..<see cref="MaxPollSeconds"/>.</summary>
    public int PollSeconds { get; set; } = DefaultPollSeconds;

    /// <summary>
    /// Whether the "closing hides to the tray, it does not quit" balloon has been shown. Shown
    /// once, because silently vanishing on close is the kind of surprise that reads as a crash.
    /// </summary>
    public bool HasShownTrayHint { get; set; }

    public const int DefaultPollSeconds = 15;
    public const int MinPollSeconds = 5;
    public const int MaxPollSeconds = 3600;

    /// <summary>Derived, so it must not be written to the settings file.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ClampedPollSeconds =>
        Math.Clamp(PollSeconds <= 0 ? DefaultPollSeconds : PollSeconds, MinPollSeconds, MaxPollSeconds);

    /// <summary>
    /// Fills <see cref="SavedTargets"/> from the legacy <see cref="SavedExpectedIps"/> the first
    /// time a pre-country-locking file is loaded. One-way and idempotent: once SavedTargets has
    /// anything in it, it is authoritative and the legacy list is only ever written, never read.
    /// </summary>
    public void MigrateSavedTargets()
    {
        SavedTargets = SavedTargets.Where(t => t.IsUsable()).ToList();

        if (SavedTargets.Count == 0 && SavedExpectedIps.Count > 0)
        {
            SavedTargets = SavedExpectedIps
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Select(SavedTarget.ForIp)
                .ToList();
        }
    }

    /// <summary>Every executable across every enabled app, deduplicated.</summary>
    public IReadOnlyList<string> EnabledExecutablePaths() => Apps
        .Where(a => a.Enabled)
        .SelectMany(a => a.ExecutablePaths)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
