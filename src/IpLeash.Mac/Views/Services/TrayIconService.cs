using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using IpLeash.Models;

namespace IpLeash.Views.Services;

/// <summary>
/// The menu-bar icon, the macOS equivalent of the Windows notification-area icon.
///
/// It carries the same weight as on Windows: closing the window hides it rather than ending
/// enforcement, so this icon is how the app is reached again and Exit is the deliberate act that
/// ends enforcement.
/// </summary>
public sealed class TrayIconService : ITrayIconService
{
    private const string AssetPrefix = "avares://IpLeash/Assets/";

    private readonly Dictionary<MonitorStatus, WindowIcon> _icons = [];
    private TrayIcon? _trayIcon;
    private bool _disposed;

    public event EventHandler? ShowWindowRequested;

    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        if (_trayIcon is not null || Application.Current is not { } app)
        {
            return;
        }

        _icons[MonitorStatus.Idle] = LoadIcon("ipleash-idle.png");
        _icons[MonitorStatus.Allowed] = LoadIcon("ipleash-allowed.png");
        _icons[MonitorStatus.Blocked] = LoadIcon("ipleash-blocked.png");
        _icons[MonitorStatus.Unknown] = LoadIcon("ipleash-unknown.png");

        var status = new NativeMenuItem("Not monitoring") { IsEnabled = false };

        var open = new NativeMenuItem("Open IpLeash");
        open.Click += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);

        var exit = new NativeMenuItem("Exit IpLeash");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _trayIcon = new TrayIcon
        {
            Icon = _icons[MonitorStatus.Idle],
            ToolTipText = "IpLeash",
            Menu = new NativeMenu
            {
                Items = { status, new NativeMenuItemSeparator(), open, new NativeMenuItemSeparator(), exit },
            },
            IsVisible = true,
        };

        TrayIcon.SetIcons(app, [_trayIcon]);
    }

    public void UpdateStatus(MonitorStatus status, string tooltip)
    {
        if (_trayIcon is null)
        {
            return;
        }

        if (_icons.TryGetValue(status, out var icon))
        {
            _trayIcon.Icon = icon;
        }

        _trayIcon.ToolTipText = tooltip;

        // The first menu line repeats the state, because a menu-bar icon's tooltip is easy to miss.
        if (_trayIcon.Menu?.Items.FirstOrDefault() is NativeMenuItem header)
        {
            header.Header = tooltip.StartsWith("IpLeash — ", StringComparison.Ordinal)
                ? tooltip["IpLeash — ".Length..]
                : tooltip;
        }
    }

    /// <summary>
    /// Posts a Notification Center banner. Avalonia has no balloon-tip equivalent, and
    /// <c>osascript</c> is the one route that needs no notification entitlement.
    /// </summary>
    public void ShowHint(string title, string message)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Passed as arguments rather than spliced into the script, so no quoting can break it.
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);

            using var _ = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A missed hint costs nothing; the menu-bar icon is still there.
        }
    }

    private static WindowIcon LoadIcon(string fileName)
    {
        using var stream = AssetLoader.Open(new Uri(AssetPrefix + fileName));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _icons.Clear();
    }
}
