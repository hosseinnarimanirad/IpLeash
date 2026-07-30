using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Leash.Models;
using Leash.Services;
using Leash.Views.Services;

namespace Leash.ViewModels;

/// <summary>
/// Projects <see cref="IMonitorEngine"/> state onto bindable properties and turns user intent
/// into engine calls. Holds no reference to any WPF type: modal UI goes through
/// <see cref="IDialogService"/>, and thread marshalling goes through the captured
/// <see cref="SynchronizationContext"/> rather than a Dispatcher.
/// </summary>
public sealed partial class MainViewModel : ObservableValidator, IDisposable
{
    private const int MaxLogEntries = 500;

    private readonly IMonitorEngine _engine;
    private readonly ISettingsStore _settingsStore;
    private readonly IDialogService _dialogService;
    private readonly IProcessWatcher _processWatcher;
    private readonly IAppDiscoveryService _discovery;
    private readonly SynchronizationContext? _uiContext;

    private bool _suppressSave;
    private bool _disposed;

    public MainViewModel(
        IMonitorEngine engine,
        ISettingsStore settingsStore,
        IDialogService dialogService,
        IProcessWatcher processWatcher,
        IAppDiscoveryService discovery)
    {
        _engine = engine;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _processWatcher = processWatcher;
        _discovery = discovery;

        // Captured on the UI thread at construction; every engine callback is posted through it.
        _uiContext = SynchronizationContext.Current;

        IsElevated = engine.IsElevated;

        _suppressSave = true;
        var settings = _settingsStore.Load();
        ExpectedIp = settings.ExpectedPublicIp;
        PollSeconds = settings.ClampedPollSeconds;

        foreach (var app in settings.Apps)
        {
            Apps.Add(CreateAppViewModel(app));
        }

        foreach (var ip in settings.SavedExpectedIps)
        {
            SavedIps.Add(ip);
        }

        // The address in use is always offered back, so an upgrade from before saved addresses
        // existed does not present an empty list.
        if (!string.IsNullOrWhiteSpace(ExpectedIp) && !SavedIps.Contains(ExpectedIp))
        {
            SavedIps.Add(ExpectedIp);
        }

        _suppressSave = false;

        ValidateAllProperties();

        // HasErrors and the app list both gate Start, so changes must re-evaluate CanExecute.
        ErrorsChanged += (_, _) => StartCommand.NotifyCanExecuteChanged();
        Apps.CollectionChanged += (_, _) =>
        {
            StartCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasApps));
        };

        _engine.SetWatchTargets(Apps.Select(a => a.ToModel()).ToList());
        _engine.SnapshotChanged += OnSnapshotChanged;
        _engine.LogEmitted += OnLogEmitted;

        ApplySnapshot(_engine.Snapshot);

        if (!IsElevated)
        {
            AppendLog(new LogEntry(
                DateTimeOffset.Now,
                LogLevel.Error,
                "Not running as administrator — firewall rules cannot be created. Restart Leash elevated."));
        }
    }

    // ---- Configuration -------------------------------------------------------------------

    /// <summary>
    /// One expected IP for the whole list: when the machine is not on it, every enabled app is
    /// blocked together.
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(SaveExpectedIpCommand))]
    [Required(ErrorMessage = "Enter the expected public IP.")]
    [CustomValidation(typeof(MainViewModel), nameof(ValidateExpectedIp))]
    private string _expectedIp = string.Empty;

    /// <summary>Previously used expected addresses, offered as one-click chips.</summary>
    public ObservableCollection<string> SavedIps { get; } = [];

    public bool HasSavedIps => SavedIps.Count > 0;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(AppSettings.MinPollSeconds, AppSettings.MaxPollSeconds,
        ErrorMessage = "Poll interval must be between 5 and 3600 seconds.")]
    private int _pollSeconds = AppSettings.DefaultPollSeconds;

    public ObservableCollection<MonitoredAppViewModel> Apps { get; } = [];

    public bool HasApps => Apps.Count > 0;

    // ---- Engine-derived state ------------------------------------------------------------

    [ObservableProperty]
    private MonitorStatus _status = MonitorStatus.Idle;

    [ObservableProperty]
    private string _statusReason = "Not monitoring.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseDetectedIpCommand))]
    private string _publicIpText = "—";

    [ObservableProperty]
    private string _lastCheckedText = "Never checked";

    /// <summary>Drives the "checking…" affordance without blocking anything.</summary>
    [ObservableProperty]
    private bool _isCheckingPublicIp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseDetectedIpCommand))]
    private bool _hasDetectedIp;

    [ObservableProperty]
    private string _proxyText = "not configured";

    [ObservableProperty]
    private string _proxyDetail = string.Empty;

    [ObservableProperty]
    private bool _hasProxy;

    /// <summary>True when the proxy sits in front of the IP probe, making the reading the proxy's.</summary>
    [ObservableProperty]
    private bool _proxyAffectsPublicIp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddByBrowseCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddFromRunningProcessCommand))]
    [NotifyCanExecuteChangedFor(nameof(DetectKnownAppsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAppCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveExpectedIpCommand))]
    private bool _isMonitoring;

    /// <summary>False when the process lacks the rights netsh needs; disables Start and shows a banner.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isElevated;

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];

    public ObservableCollection<LogEntry> Log { get; } = [];

    // ---- List management -----------------------------------------------------------------

    /// <summary>
    /// The list is editable only while stopped. Letting targets change under a running engine
    /// would mean reconciling rules mid-flight for a gain nobody asked for.
    /// </summary>
    private bool CanEditList() => !IsMonitoring;

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private void AddByBrowse()
    {
        var picked = _dialogService.BrowseForExecutable(null);
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        if (!ExecutableFile.IsUsable(picked))
        {
            _dialogService.ShowError(
                "Not a usable executable",
                $"{picked}\n\nA firewall rule binds to a process image. A .cmd, .bat or .ps1 launcher " +
                "cannot be blocked — point at the .exe it starts.");
            return;
        }

        AddApp(Path.GetFileNameWithoutExtension(picked), [picked], "browse");
    }

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private void AddFromRunningProcess()
    {
        var candidates = _processWatcher.GetRunningExecutables();
        var selected = _dialogService.PickRunningExecutables(candidates);

        var usable = selected.Where(ExecutableFile.IsUsable).ToList();
        if (usable.Count == 0)
        {
            return;
        }

        AddApp(Path.GetFileNameWithoutExtension(usable[0]), usable, "running process");
    }

    /// <summary>
    /// Re-resolves known apps against this machine. The point is portability: Claude's install
    /// path differs between systems, so a hand-typed path does not survive being moved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditList))]
    private void DetectKnownApps()
    {
        var discovered = _discovery.DiscoverKnownApps();

        if (discovered.Count == 0)
        {
            _dialogService.ShowInfo(
                "Nothing detected",
                "No known applications were found on this system. Use “From running process…” or " +
                "“Browse…” to add one by hand.");
            return;
        }

        var added = 0;
        var merged = 0;

        foreach (var app in discovered)
        {
            // Merge into an existing entry of the same name so re-running detection on a machine
            // with a second install extends that app instead of creating a duplicate row.
            var existing = Apps.FirstOrDefault(a =>
                string.Equals(a.Name, app.Name, StringComparison.OrdinalIgnoreCase));

            var fresh = app.ExecutablePaths
                .Where(p => !IsAlreadyMonitored(p, exceptApp: existing))
                .ToList();

            if (fresh.Count == 0)
            {
                continue;
            }

            if (existing is null)
            {
                AddApp(app.Name, fresh, string.Join(", ", app.Sources));
                added++;
            }
            else
            {
                existing.AddExecutables(fresh);
                merged++;
                AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Info,
                    $"Added {fresh.Count} executable(s) to “{app.Name}” (found via {string.Join(", ", app.Sources)})."));
            }
        }

        if (added == 0 && merged == 0)
        {
            _dialogService.ShowInfo(
                "Already up to date",
                $"Found {discovered.Count} known app(s), but every executable is already in the list.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private void RemoveApp(MonitoredAppViewModel? app)
    {
        if (app is null)
        {
            return;
        }

        if (_dialogService.Confirm("Remove application", $"Remove “{app.Name}” from the monitored list?"))
        {
            Apps.Remove(app);
            OnListEdited();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private void RemoveExecutable(ExecutableViewModel? executable)
    {
        if (executable is null)
        {
            return;
        }

        var owner = Apps.FirstOrDefault(a => a.Executables.Contains(executable));
        owner?.RemoveExecutable(executable);

        // An app with no executables cannot be blocked, so don't leave an inert row behind.
        if (owner is not null && owner.Executables.Count == 0)
        {
            Apps.Remove(owner);
        }

        OnListEdited();
    }

    private void AddApp(string name, IReadOnlyList<string> paths, string source)
    {
        var fresh = paths.Where(p => !IsAlreadyMonitored(p, exceptApp: null)).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var model = new MonitoredApp
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New app" : name,
            ExecutablePaths = [.. fresh],
            Enabled = true,
        };

        Apps.Add(CreateAppViewModel(model));
        OnListEdited();

        AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Info,
            $"Added “{model.Name}” with {fresh.Count} executable(s) (found via {source})."));
    }

    private bool IsAlreadyMonitored(string path, MonitoredAppViewModel? exceptApp) =>
        Apps.Where(a => !ReferenceEquals(a, exceptApp))
            .SelectMany(a => a.Executables)
            .Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))
        || (exceptApp?.Executables.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) ?? false);

    private MonitoredAppViewModel CreateAppViewModel(MonitoredApp model) => new(model, OnListEdited);

    private void OnListEdited()
    {
        SaveSettings();
        _engine.SetWatchTargets(Apps.Select(a => a.ToModel()).ToList());
        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasApps));
    }

    // ---- Expected IP -----------------------------------------------------------------------

    /// <summary>Captures the address the machine is on right now as the expected one.</summary>
    [RelayCommand(CanExecute = nameof(CanUseDetectedIp))]
    private void UseDetectedIp()
    {
        ExpectedIp = PublicIpText;
        SaveExpectedIp();
    }

    private bool CanUseDetectedIp() => HasDetectedIp && !IsMonitoring;

    [RelayCommand(CanExecute = nameof(CanSaveExpectedIp))]
    private void SaveExpectedIp()
    {
        var value = ExpectedIp.Trim();
        if (value.Length == 0 || SavedIps.Contains(value))
        {
            return;
        }

        SavedIps.Insert(0, value);
        OnPropertyChanged(nameof(HasSavedIps));
        SaveSettings();
        AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Info, $"Saved expected IP {value}."));
    }

    // Disabled while monitoring for the same reason the field itself is: the address is locked,
    // so offering to save it would be an action with nothing to act on.
    private bool CanSaveExpectedIp() =>
        !IsMonitoring
        && !string.IsNullOrWhiteSpace(ExpectedIp)
        && !GetErrors(nameof(ExpectedIp)).Cast<object>().Any();

    [RelayCommand]
    private void UseSavedIp(string? ip)
    {
        if (!string.IsNullOrWhiteSpace(ip) && !IsMonitoring)
        {
            ExpectedIp = ip;
        }
    }

    [RelayCommand]
    private void ForgetSavedIp(string? ip)
    {
        if (ip is null || !SavedIps.Remove(ip))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSavedIps));
        SaveSettings();
    }

    // ---- Monitoring commands -------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        SaveSettings();
        await _engine.StartAsync(BuildSettings()).ConfigureAwait(true);
    }

    private bool CanStart() =>
        !IsMonitoring && IsElevated && !HasErrors && Apps.Any(a => a.Enabled && a.Executables.Count > 0);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => _engine.StopAsync();

    private bool CanStop() => IsMonitoring;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task CheckNowAsync() => _engine.CheckNowAsync();

    // ---- Property change hooks -----------------------------------------------------------

    partial void OnExpectedIpChanged(string value) => SaveSettings();

    partial void OnPollSecondsChanged(int value) => SaveSettings();

    // ---- Validation ----------------------------------------------------------------------

    public static ValidationResult? ValidateExpectedIp(string? value, ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ValidationResult.Success;   // [Required] already reports the empty case.
        }

        return IPAddress.TryParse(value.Trim(), out _)
            ? ValidationResult.Success
            : new ValidationResult("Not a valid IP address.");
    }

    // ---- Engine plumbing -----------------------------------------------------------------

    private void OnSnapshotChanged(object? sender, MonitorSnapshot snapshot) =>
        Post(() => ApplySnapshot(snapshot));

    private void OnLogEmitted(object? sender, LogEntry entry) =>
        Post(() => AppendLog(entry));

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        Status = snapshot.Status;
        StatusReason = snapshot.Reason;
        IsMonitoring = snapshot.IsRunning;

        IsCheckingPublicIp = snapshot.ProbeState == IpProbeState.Checking;
        HasDetectedIp = snapshot.ProbeState == IpProbeState.Resolved && snapshot.PublicIp is not null;

        // A failed lookup reads as "unavailable" rather than as a block: while stopped, nothing
        // is being enforced, so showing the fail-closed wording here would be a lie.
        PublicIpText = snapshot.ProbeState switch
        {
            IpProbeState.Checking => "checking…",
            IpProbeState.Resolved => snapshot.PublicIp ?? "—",
            IpProbeState.Failed => "unavailable",
            _ => "—",
        };

        ProxyText = snapshot.Proxy.Summary;
        ProxyDetail = snapshot.Proxy.Detail;
        HasProxy = snapshot.Proxy.IsConfigured;
        ProxyAffectsPublicIp = snapshot.Proxy.AffectsPublicIpCheck;

        LastCheckedText = snapshot.LastCheckedAt is { } checkedAt
            ? $"Last checked {checkedAt.ToLocalTime():HH:mm:ss}"
            : "Never checked";

        foreach (var appState in snapshot.Apps)
        {
            var target = Apps.FirstOrDefault(a => a.Id == appState.Id);
            target?.Apply(appState);
        }

        SyncAdapters(snapshot.Adapters);
    }

    /// <summary>
    /// Rebuilds the adapter list only when it actually differs, so the ItemsControl does not
    /// flicker on every snapshot.
    /// </summary>
    private void SyncAdapters(IReadOnlyList<AdapterInfo> adapters)
    {
        if (Adapters.SequenceEqual(adapters))
        {
            return;
        }

        Adapters.Clear();
        foreach (var adapter in adapters)
        {
            Adapters.Add(adapter);
        }
    }

    private void AppendLog(LogEntry entry)
    {
        Log.Insert(0, entry);
        while (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    private AppSettings BuildSettings() => new()
    {
        Apps = Apps.Select(a => a.ToModel()).ToList(),
        ExpectedPublicIp = ExpectedIp.Trim(),
        SavedExpectedIps = SavedIps.ToList(),
        PollSeconds = PollSeconds,
    };

    private void SaveSettings()
    {
        if (_suppressSave)
        {
            return;
        }

        _settingsStore.Save(BuildSettings());
    }

    /// <summary>
    /// Engine events arrive on timer threads. Posting through the captured context keeps every
    /// ObservableCollection mutation on the UI thread without the ViewModel knowing about WPF.
    /// </summary>
    private void Post(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.SnapshotChanged -= OnSnapshotChanged;
        _engine.LogEmitted -= OnLogEmitted;
    }
}
