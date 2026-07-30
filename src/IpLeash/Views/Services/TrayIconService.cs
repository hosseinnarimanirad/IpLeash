using System.Drawing;
using System.Windows;
using IpLeash.Models;
using Forms = System.Windows.Forms;

namespace IpLeash.Views.Services;

/// <summary>
/// Notification-area icon backed by <see cref="Forms.NotifyIcon"/>.
///
/// The context menu is a WinForms <see cref="Forms.ContextMenuStrip"/> rather than a WPF
/// ContextMenu. A WPF menu would match the app's styling, but a tray menu has no owning window
/// to take focus, so it needs foreground-activation workarounds to dismiss correctly. Native
/// behaviour matters more than brand consistency for a menu this small and this rarely seen.
/// </summary>
public sealed class TrayIconService : ITrayIconService
{
    private const string AssemblyResourcePrefix = "pack://application:,,,/IpLeash;component/Assets/";

    /// <summary>Shell limit for the hover text; a longer string throws.</summary>
    private const int MaxTooltipLength = 127;

    private readonly Dictionary<MonitorStatus, Icon> _icons = [];
    private Forms.NotifyIcon? _notifyIcon;
    private bool _disposed;

    public event EventHandler? ShowWindowRequested;

    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        LoadIcons();

        var menu = new Forms.ContextMenuStrip
        {
            Font = new Font("Segoe UI", 9f),
            ShowImageMargin = false,
        };

        var open = new Forms.ToolStripMenuItem("Open IpLeash");
        open.Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);

        var exit = new Forms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icons[MonitorStatus.Idle],
            Text = "IpLeash",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateStatus(MonitorStatus status, string tooltip)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        if (_icons.TryGetValue(status, out var icon))
        {
            _notifyIcon.Icon = icon;
        }

        _notifyIcon.Text = Truncate(tooltip);
    }

    public void ShowHint(string title, string message)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void LoadIcons()
    {
        _icons[MonitorStatus.Idle] = LoadIcon("ipleash-idle.ico");
        _icons[MonitorStatus.Allowed] = LoadIcon("ipleash-allowed.ico");
        _icons[MonitorStatus.Blocked] = LoadIcon("ipleash-blocked.ico");
        _icons[MonitorStatus.Unknown] = LoadIcon("ipleash-unknown.ico");
    }

    /// <summary>
    /// Loaded through an absolute pack URI rather than a relative one so the icons resolve even
    /// when the window is hosted by another assembly.
    /// </summary>
    private static Icon LoadIcon(string fileName)
    {
        var uri = new Uri(AssemblyResourcePrefix + fileName, UriKind.Absolute);
        var resource = Application.GetResourceStream(uri)
                       ?? throw new InvalidOperationException($"Tray icon resource missing: {fileName}");

        using var stream = resource.Stream;
        return new Icon(stream);
    }

    private static string Truncate(string value) =>
        value.Length <= MaxTooltipLength ? value : value[..(MaxTooltipLength - 1)] + "…";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notifyIcon is not null)
        {
            // Hide before disposing: an icon removed without being hidden can linger in the
            // notification area until the user hovers over it.
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }
}
