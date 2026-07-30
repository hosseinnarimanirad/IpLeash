using Leash.Models;

namespace Leash.Services;

/// <summary>
/// The state machine that ties one global expected public IP to firewall enforcement across
/// every enabled app in the list.
///
/// It is deliberately free of any UI awareness: it exposes events and is driven by a plain
/// <see cref="System.Timers.Timer"/> rather than a DispatcherTimer, so nothing here is pinned to
/// the UI thread. Marshalling to the UI is the ViewModel's job.
/// </summary>
public interface IMonitorEngine
{
    /// <summary>The latest published state. Safe to read from any thread.</summary>
    MonitorSnapshot Snapshot { get; }

    /// <summary>True when the process holds the administrator rights firewall changes require.</summary>
    bool IsElevated { get; }

    event EventHandler<MonitorSnapshot>? SnapshotChanged;

    event EventHandler<LogEntry>? LogEmitted;

    /// <summary>
    /// Sets the apps the idle process poll reports on, so PIDs are live in the list before
    /// monitoring starts. Has no effect on enforcement.
    /// </summary>
    void SetWatchTargets(IReadOnlyList<MonitoredApp> apps);

    /// <summary>
    /// Removes block rules left by a previous run that exited abnormally. Call once at startup,
    /// before the first evaluation, so enforcement begins from a known-clean baseline.
    /// </summary>
    Task<IReadOnlyList<string>> CleanupOrphanedRulesAsync(CancellationToken ct = default);

    /// <summary>
    /// Looks up the public IP and refreshes the proxy reading for display only — no firewall
    /// rule is created, removed, or even consulted. Safe to call before monitoring starts, and
    /// the reason the window can show a real IP at launch without implying enforcement.
    /// A no-op while monitoring, where the regular evaluation already keeps the reading current.
    /// </summary>
    Task ProbeAsync(CancellationToken ct = default);

    /// <summary>Begins periodic evaluation and enforcement. Evaluates once immediately.</summary>
    Task StartAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>Stops evaluation and removes every block rule this engine applied.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Forces an out-of-band evaluation.</summary>
    Task CheckNowAsync(CancellationToken ct = default);
}
