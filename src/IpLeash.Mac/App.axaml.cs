using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using IpLeash.Models;
using IpLeash.Platform;
using IpLeash.Services;
using IpLeash.ViewModels;
using IpLeash.Views;
using IpLeash.Views.Services;

namespace IpLeash;

/// <summary>
/// Composition root and lifecycle owner — the macOS counterpart of the WPF App, with the same
/// three interlocking rules:
///  - Closing the window hides it to the menu bar. Enforcement continues, because closing a
///    window should never be the thing that silently restores network access to a blocked app.
///  - Exiting (menu-bar Exit, or ⌘Q) resumes every frozen process. That is the only path that
///    ends enforcement, and it confirms first when there is something to lose.
///  - Starting up resumes processes recorded as frozen by a run that never got to exit, because
///    a crash or a forced kill cannot run cleanup code.
///
/// Only one instance may run, for the same reason as on Windows: a second instance's startup
/// recovery would resume what the first one has frozen.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private ServiceProvider? _provider;
    private MainViewModel? _viewModel;
    private IMonitorEngine? _engine;
    private ITrayIconService? _tray;
    private ISettingsStore? _settingsStore;
    private MainWindow? _window;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    private bool _exitRequested;
    private int _teardownStarted;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _desktop = desktop;

        // The ViewModel validates itself through INotifyDataErrorInfo. Avalonia's DataAnnotations
        // plugin would validate the same attributes a second time and report every error twice.
        foreach (var plugin in BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToList())
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }

        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire())
        {
            if (!_singleInstance.SignalRunningInstance())
            {
                Console.Error.WriteLine("IpLeash is already running. Look for its icon in the menu bar.");
            }

            _singleInstance.Dispose();
            _singleInstance = null;
            desktop.Shutdown();
            return;
        }

        _singleInstance.ShowRequested += (_, _) => Dispatcher.UIThread.Post(RestoreWindow);

        // The window no longer owns the app's lifetime: hiding it to the menu bar must not quit.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _provider = BuildServiceProvider();
        _engine = _provider.GetRequiredService<IMonitorEngine>();
        _settingsStore = _provider.GetRequiredService<ISettingsStore>();

        // Constructed on the UI thread so it captures the UI SynchronizationContext.
        _viewModel = _provider.GetRequiredService<MainViewModel>();

        _window = _provider.GetRequiredService<MainWindow>();
        _window.DataContext = _viewModel;
        _window.Closing += OnMainWindowClosing;
        desktop.MainWindow = _window;

        _tray = _provider.GetRequiredService<ITrayIconService>();
        _tray.Initialize();
        _tray.ShowWindowRequested += (_, _) => RestoreWindow();
        _tray.ExitRequested += (_, _) => RequestExit();

        _engine.SnapshotChanged += OnSnapshotChanged;
        UpdateTray(_engine.Snapshot);

        // A frozen process that a target spawns after the block went on has no other way into
        // the activity log: the engine only logs what it does itself.
        _provider.GetRequiredService<ProcessFreezeService>().EnforcementProblem += OnEnforcementProblem;

        // ⌘Q and Quit from the Dock arrive here; they get the same confirmation as menu-bar Exit.
        desktop.ShutdownRequested += OnShutdownRequested;
        desktop.Exit += (_, _) => Teardown();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Teardown();

        // Clicking the Dock icon while the window is hidden brings it back, as Mac apps do.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    RestoreWindow();
                }
            };
        }

        _window.Show();

        base.OnFrameworkInitializationCompleted();

        Dispatcher.UIThread.Post(async () => await StartupAsync(), DispatcherPriority.Background);
    }

    private async Task StartupAsync()
    {
        if (_engine is null || _provider is null)
        {
            return;
        }

        try
        {
            // After Show() so its log lines land in a visible window, but before the user can
            // realistically press Start.
            await _engine.CleanupOrphanedRulesAsync();
        }
        catch (Exception ex)
        {
            _provider.GetRequiredService<IDialogService>()
                .ShowError("Startup recovery failed", $"Could not resume processes left frozen by a previous run:\n\n{ex.Message}");
        }

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
        services.AddSingleton<ProcessFreezeService>();
        services.AddSingleton<IFirewallService>(sp => sp.GetRequiredService<ProcessFreezeService>());
        services.AddSingleton<IPublicIpService, PublicIpService>();
        services.AddSingleton<ILocalIpService, MacLocalIpService>();
        services.AddSingleton<IProcessWatcher, MacProcessWatcher>();
        services.AddSingleton<IAppDiscoveryService, MacAppDiscoveryService>();
        services.AddSingleton<IProxyService, MacProxyService>();
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

    // ---- Window and menu bar ---------------------------------------------------------------

    /// <summary>Closing hides rather than quits. A lifetime decision, so it lives here.</summary>
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
        ShowTrayHintOnce();
    }

    private void ShowTrayHintOnce()
    {
        if (_settingsStore is null || _tray is null)
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
            "It moved to the menu bar and is still enforcing. Use Exit from its menu-bar icon to stop and resume the frozen apps.");

        settings.HasShownTrayHint = true;
        _settingsStore.Save(settings);
    }

    private void RestoreWindow()
    {
        if (_window is not { } window)
        {
            return;
        }

        window.Show();

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void OnSnapshotChanged(object? sender, MonitorSnapshot snapshot) =>
        Dispatcher.UIThread.Post(() => UpdateTray(snapshot));

    private void UpdateTray(MonitorSnapshot snapshot)
    {
        if (_tray is null)
        {
            return;
        }

        var blockedApps = snapshot.Apps.Count(a => a.Executables.Any(x => x.IsBlocked));

        // The menu-bar text is as screenshot-visible as the window, so it honours the same setting.
        var publicIp = _viewModel?.HideIps == true
            ? IpMasker.MaskAddress(snapshot.PublicIp)
            : snapshot.PublicIp;

        var state = snapshot.Status switch
        {
            MonitorStatus.Allowed => $"Allowed — public IP {publicIp}",
            MonitorStatus.Blocked => $"BLOCKED — {blockedApps} app(s) frozen",
            MonitorStatus.Unknown => $"Public IP unknown — {blockedApps} app(s) frozen",
            _ => "Not monitoring",
        };

        _tray.UpdateStatus(snapshot.Status, $"IpLeash — {state}");
    }

    private void OnEnforcementProblem(object? sender, string message) =>
        Dispatcher.UIThread.Post(() =>
            _viewModel?.Log.Insert(0, new LogEntry(DateTimeOffset.Now, LogLevel.Error, message)));

    private void OnAboutClicked(object? sender, EventArgs e) =>
        _provider?.GetRequiredService<IDialogService>().ShowInfo(
            "About IpLeash",
            "IpLeash for macOS — freezes the listed applications whenever this Mac's public IP is not " +
            "the expected one, and resumes them when it is.\n\n" +
            "Data folder: ~/Library/Application Support/IpLeash");

    // ---- Exit ------------------------------------------------------------------------------

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        // Cancel the framework's shutdown and run ours, which may ask first.
        e.Cancel = true;
        Dispatcher.UIThread.Post(RequestExit);
    }

    /// <summary>
    /// The only path that ends enforcement, so it says plainly what will be un-blocked before
    /// doing it.
    /// </summary>
    private void RequestExit()
    {
        if (_exitRequested)
        {
            return;
        }

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
                    $"Exiting will resume the frozen processes of {blockedApps.Count} app(s) " +
                    $"({executables} target(s)) and restore their network access:\n\n{names}\n\nContinue?");

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
        Teardown();
        _desktop?.Shutdown();
    }

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

        // Removed first, so the icon disappears immediately rather than lingering.
        _tray?.Dispose();

        try
        {
            _engine?.StopAsync().Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception)
        {
            // Nothing useful can be shown at this point. The recovery records mean the next
            // launch will resume whatever was left frozen.
        }

        _viewModel?.Dispose();
        _provider?.Dispose();
        _singleInstance?.Dispose();
    }
}
