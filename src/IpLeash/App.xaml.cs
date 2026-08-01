using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using IpLeash.Models;
using IpLeash.Services;
using IpLeash.ViewModels;
using IpLeash.Views;
using IpLeash.Views.Services;

namespace IpLeash;

/// <summary>
/// Composition root and lifecycle owner.
///
/// Three rules define the lifecycle, and they interlock:
///  - Closing the window hides it to the tray. Enforcement continues, because closing a window
///    should never be the thing that silently restores network access to a blocked app.
///  - Exiting from the tray removes every block rule. That is the only path that ends
///    enforcement, and it confirms first when there is something to lose.
///  - Starting up removes rules recorded by a run that never got to exit, because a crash or a
///    forced kill cannot run cleanup code.
///
/// Only one instance may run: a second one's startup cleanup would delete the first's *active*
/// rules, silently unblocking apps while the first window still displayed BLOCKED. A second
/// launch therefore signals the running instance to surface and exits immediately.
/// </summary>
public partial class App : Application
{
    /// <summary>Global scope, because the app is elevated and must be unique machine-wide.</summary>
    private const string SingleInstanceMutexName = @"Global\IpLeash.SingleInstance";

    private const string ShowWindowEventName = @"Global\IpLeash.ShowWindow";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowRegistration;

    private ServiceProvider? _provider;
    private MainViewModel? _viewModel;
    private IMonitorEngine? _engine;
    private ITrayIconService? _tray;
    private ISettingsStore? _settingsStore;

    private bool _exitRequested;
    private int _teardownStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!TryAcquireSingleInstance())
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        // The window no longer owns the app's lifetime: hiding it to the tray must not quit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _provider = BuildServiceProvider();
        _engine = _provider.GetRequiredService<IMonitorEngine>();
        _settingsStore = _provider.GetRequiredService<ISettingsStore>();

        // Constructed on the UI thread so it captures the UI SynchronizationContext.
        _viewModel = _provider.GetRequiredService<MainViewModel>();

        var window = _provider.GetRequiredService<MainWindow>();
        window.DataContext = _viewModel;
        window.Closing += OnMainWindowClosing;
        MainWindow = window;
        window.Show();

        _tray = _provider.GetRequiredService<ITrayIconService>();
        _tray.Initialize();
        _tray.ShowWindowRequested += (_, _) => RestoreWindow();
        _tray.ExitRequested += (_, _) => RequestExit();

        _engine.SnapshotChanged += OnSnapshotChanged;
        UpdateTray(_engine.Snapshot);

        SessionEnding += OnSessionEnding;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        try
        {
            // Runs after Show() so its log lines land in a visible window, but before the user
            // can realistically press Start.
            await _engine.CleanupOrphanedRulesAsync();
        }
        catch (Exception ex)
        {
            _provider.GetRequiredService<IDialogService>()
                .ShowError("Startup cleanup failed", $"Could not clear leftover firewall rules:\n\n{ex.Message}");
        }

        // Fills in the public IP and proxy readings on launch. Awaited rather than blocked on:
        // the window is already shown and painted, and the UI thread returns to its message loop
        // for the whole duration of the lookup, so nothing freezes while it runs.
        try
        {
            await _engine.ProbeAsync();
        }
        catch (Exception)
        {
            // A failed lookup is already surfaced in the log and as "unavailable" in the UI.
        }
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IBlockStateStore, BlockStateStore>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IFirewallService, FirewallService>();
        services.AddSingleton<IPublicIpService, PublicIpService>();
        services.AddSingleton<ILocalIpService, LocalIpService>();
        services.AddSingleton<IProcessWatcher, ProcessWatcher>();
        services.AddSingleton<IAppDiscoveryService, AppDiscoveryService>();
        services.AddSingleton<IProxyService, ProxyService>();
        services.AddSingleton<IGeoCacheStore, GeoCacheStore>();

        // Registered concretely and then aliased, so the container disposes exactly one instance.
        services.AddSingleton<GeoIpService>();
        services.AddSingleton<IGeoIpService>(sp => sp.GetRequiredService<GeoIpService>());

        services.AddSingleton<MonitorEngine>();
        services.AddSingleton<IMonitorEngine>(sp => sp.GetRequiredService<MonitorEngine>());

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    // ---- Single instance -------------------------------------------------------------------

    /// <summary>
    /// Returns false when another instance already holds the mutex. An abandoned mutex (previous
    /// instance killed) still counts as acquired — that instance is gone, so taking over is correct.
    /// </summary>
    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
            if (!_singleInstanceMutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing. We now hold it.
        }
        catch (UnauthorizedAccessException)
        {
            // Held by a session we cannot open. Treat as "already running".
            return false;
        }

        // Owned: listen for a later launch asking us to surface.
        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showWindowEvent,
                (_, _) => Dispatcher.InvokeAsync(RestoreWindow),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Without this the app still works; a second launch just cannot surface it.
        }

        return true;
    }

    /// <summary>
    /// Asks the already-running instance to show itself. This process then exits, so exactly one
    /// instance is ever alive — the signal only decides whether the survivor becomes visible.
    /// </summary>
    private static void SignalRunningInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowWindowEventName);
            handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            // The running instance predates this mechanism, or is not reachable. Say something
            // rather than vanishing with no explanation.
            MessageBox.Show(
                "IpLeash is already running.\n\nLook for its icon in the notification area.",
                "IpLeash already running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    // ---- Window and tray -------------------------------------------------------------------

    /// <summary>
    /// Closing hides rather than quits. Subscribed here instead of in the window's code-behind,
    /// which stays empty: this is a lifetime decision, and lifetime belongs to App.
    /// </summary>
    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        MainWindow?.Hide();
        ShowTrayHintOnce();
    }

    private void ShowTrayHintOnce()
    {
        if (_settingsStore is null || _viewModel is null || _tray is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        if (settings.HasShownTrayHint)
        {
            return;
        }

        _tray.ShowHint(
            "IpLeash is still running",
            "It moved to the notification area and is still enforcing. Use Exit from its tray menu to stop and remove the firewall rules.");

        settings.HasShownTrayHint = true;
        _settingsStore.Save(settings);
    }

    private void RestoreWindow()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        window.Show();

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();

        // A hidden window is not in the foreground queue; this nudge is what actually raises it.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void OnSnapshotChanged(object? sender, MonitorSnapshot snapshot) =>
        Dispatcher.InvokeAsync(() => UpdateTray(snapshot));

    private void UpdateTray(MonitorSnapshot snapshot)
    {
        if (_tray is null)
        {
            return;
        }

        var blockedApps = snapshot.Apps.Count(a => a.Executables.Any(x => x.IsBlocked));

        // The tray tooltip is as screenshot-visible as the window, so it honours the same setting.
        var publicIp = _viewModel?.HideIps == true
            ? IpMasker.MaskAddress(snapshot.PublicIp)
            : snapshot.PublicIp;

        var state = snapshot.Status switch
        {
            MonitorStatus.Allowed => $"Allowed — public IP {publicIp}",
            MonitorStatus.Blocked => $"BLOCKED — {blockedApps} app(s) cut off",
            MonitorStatus.Unknown => $"Public IP unknown — {blockedApps} app(s) cut off",
            _ => "Not monitoring",
        };

        _tray.UpdateStatus(snapshot.Status, $"IpLeash — {state}");
    }

    // ---- Exit ------------------------------------------------------------------------------

    /// <summary>
    /// The only path that ends enforcement, so it says plainly what will be un-blocked before
    /// doing it.
    /// </summary>
    private void RequestExit()
    {
        if (_engine is { } engine && _provider is { } provider)
        {
            var snapshot = engine.Snapshot;
            var blockedApps = snapshot.Apps.Where(a => a.Executables.Any(x => x.IsBlocked)).ToList();

            if (snapshot.IsRunning && blockedApps.Count > 0)
            {
                var executables = blockedApps.Sum(a => a.Executables.Count(x => x.IsBlocked));
                var names = string.Join(", ", blockedApps.Select(a => a.Name));

                var confirmed = provider.GetRequiredService<IDialogService>().Confirm(
                    "Exit IpLeash",
                    $"Exiting will remove the block rules for {blockedApps.Count} app(s) " +
                    $"({executables} executable(s)) and restore their network access:\n\n{names}\n\nContinue?");

                if (!confirmed)
                {
                    return;
                }
            }
            else if (snapshot.IsRunning)
            {
                var confirmed = provider.GetRequiredService<IDialogService>().Confirm(
                    "Exit IpLeash",
                    "Monitoring is active. Exiting will stop it, so the listed apps will no longer be " +
                    "blocked if the public IP changes.\n\nContinue?");

                if (!confirmed)
                {
                    return;
                }
            }
        }

        _exitRequested = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Teardown();
        base.OnExit(e);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _exitRequested = true;
        Teardown();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Teardown();

    /// <summary>
    /// Idempotent: several shutdown paths funnel here and only the first does the work.
    /// Blocking is safe because the engine's awaits are all ConfigureAwait(false), so nothing
    /// is waiting on the UI thread we may be blocking.
    /// </summary>
    private void Teardown()
    {
        if (Interlocked.Exchange(ref _teardownStarted, 1) != 0)
        {
            return;
        }

        if (_engine is not null)
        {
            _engine.SnapshotChanged -= OnSnapshotChanged;
        }

        // Removed before the wait below, so the icon disappears immediately rather than
        // lingering while rules are torn down.
        _tray?.Dispose();

        try
        {
            // ProcessExit gives roughly two seconds; cap the wait so we never hang shutdown.
            _engine?.StopAsync().Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception)
        {
            // Nothing useful can be shown at this point. The block-state file means the next
            // launch will clean up whatever was left behind.
        }

        _viewModel?.Dispose();
        _provider?.Dispose();

        _showWindowRegistration?.Unregister(null);
        _showWindowEvent?.Dispose();

        if (_singleInstanceMutex is { } mutex)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned (shutdown before acquisition); nothing to release.
            }

            mutex.Dispose();
            _singleInstanceMutex = null;
        }
    }
}
