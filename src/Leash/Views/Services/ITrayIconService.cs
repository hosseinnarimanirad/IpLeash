using Leash.Models;

namespace Leash.Views.Services;

/// <summary>
/// The notification-area icon.
///
/// It is more than a convenience here: closing the window used to be the teardown path, so
/// closing it while monitoring silently removed every block rule and restored network access to
/// the apps being protected. With the window hiding to the tray instead, enforcement survives
/// the close and ending it becomes a deliberate act.
/// </summary>
public interface ITrayIconService : IDisposable
{
    /// <summary>Raised on double-click or the "Open" menu item.</summary>
    event EventHandler? ShowWindowRequested;

    /// <summary>Raised by the "Exit" menu item. The handler owns confirmation and teardown.</summary>
    event EventHandler? ExitRequested;

    /// <summary>Creates the icon. Call once, on the UI thread.</summary>
    void Initialize();

    /// <summary>Repoints the icon and hover text at the current state.</summary>
    void UpdateStatus(MonitorStatus status, string tooltip);

    /// <summary>Shows a balloon tip. Used once, to explain that closing did not quit.</summary>
    void ShowHint(string title, string message);
}
