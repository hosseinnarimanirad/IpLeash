using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IpLeash.Models;
using IpLeash.Services;
using IpLeash.Views.Services;

namespace IpLeash.ViewModels;

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
    private readonly IGeoIpService _geo;
    private readonly SynchronizationContext? _uiContext;

    /// <summary>Bounds the background chip-flag lookups to this ViewModel's lifetime.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private bool _suppressSave;
    private bool _disposed;

    public MainViewModel(
        IMonitorEngine engine,
        ISettingsStore settingsStore,
        IDialogService dialogService,
        IProcessWatcher processWatcher,
        IAppDiscoveryService discovery,
        IGeoIpService geo)
    {
        _engine = engine;
        _settingsStore = settingsStore;
        _dialogService = dialogService;
        _processWatcher = processWatcher;
        _discovery = discovery;
        _geo = geo;

        // Captured on the UI thread at construction; every engine callback is posted through it.
        _uiContext = SynchronizationContext.Current;

        IsElevated = engine.IsElevated;

        _suppressSave = true;
        var settings = _settingsStore.Load();

        // Mode first: both fields validate on assignment, and which of them is required depends
        // on the mode, so setting it afterwards would leave a stale error behind.
        MatchMode = settings.MatchMode;
        ExpectedIp = settings.ExpectedPublicIp;
        ExpectedCountry = CountryCatalog.Find(settings.ExpectedCountryCode);
        PollSeconds = settings.ClampedPollSeconds;
        HideIps = settings.HideIpAddresses;

        foreach (var app in settings.Apps)
        {
            Apps.Add(CreateAppViewModel(app));
        }

        foreach (var target in settings.SavedTargets)
        {
            SavedTargets.Add(CreateSavedTarget(target));
        }

        // The target in use is always offered back as a chip, even if its chip was deleted: the
        // list should never omit the one thing the app is currently enforcing.
        EnsureCurrentTargetSaved();

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
                "Not running as administrator — firewall rules cannot be created. Restart IpLeash elevated."));
        }

        _ = FillMissingChipFlagsAsync();
    }

    // ---- Configuration -------------------------------------------------------------------

    /// <summary>
    /// Which rule decides a match, for the whole list at once. Exact IP is the original
    /// behaviour and stays the default.
    /// </summary>
    [ObservableProperty]
    private MatchMode _matchMode = MatchMode.ExactIp;

    public bool IsExactIpMode => MatchMode == MatchMode.ExactIp;

    public bool IsCountryMode => MatchMode == MatchMode.Country;

    /// <summary>
    /// One expected IP for the whole list: when the machine is not on it, every enabled app is
    /// blocked together. Only enforced in <see cref="MatchMode.ExactIp"/>.
    ///
    /// [Required] is deliberately absent — it has no conditional form, and this field is
    /// irrelevant in country mode. The empty case is handled inside the validator instead, which
    /// can see which mode is active.
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(SaveExpectedTargetCommand))]
    [CustomValidation(typeof(MainViewModel), nameof(ValidateExpectedIp))]
    private string _expectedIp = string.Empty;

    /// <summary>The country to lock to. Only enforced in <see cref="MatchMode.Country"/>.</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(SaveExpectedTargetCommand))]
    [CustomValidation(typeof(MainViewModel), nameof(ValidateExpectedCountry))]
    private CountryOption? _expectedCountry;

    /// <summary>Every lockable country, for the picker.</summary>
    public IReadOnlyList<CountryOption> Countries => CountryCatalog.All;

    /// <summary>
    /// Masks every address on screen. Display only — it changes nothing about what is compared,
    /// saved, or blocked, so it is safe to leave on permanently.
    /// </summary>
    [ObservableProperty]
    private bool _hideIps;

    public string HideIpsToolTip => HideIps
        ? "Addresses are hidden. Click to show them."
        : "Hide every IP address on screen, for screenshots and screen sharing.";

    /// <summary>Previously used targets, addresses and countries alike, offered as one-click chips.</summary>
    public ObservableCollection<SavedTargetViewModel> SavedTargets { get; } = [];

    public bool HasSavedTargets => SavedTargets.Count > 0;

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

    /// <summary>ISO alpha-2 of the current public IP, or null. Drives the flag beside it.</summary>
    [ObservableProperty]
    private string? _currentCountryCode;

    [ObservableProperty]
    private string _currentCountryName = "unknown";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseDetectedCountryCommand))]
    private bool _hasCurrentCountry;

    /// <summary>
    /// Warns that the country on display belongs to the proxy's exit, not this machine. Not a
    /// failure — the user may want exactly that — but it changes what the flag means.
    /// </summary>
    public string CountryToolTip => ProxyAffectsPublicIp
        ? $"{CurrentCountryName} — via proxy, so this is the proxy's exit country"
        : CurrentCountryName;

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
    [NotifyCanExecuteChangedFor(nameof(SaveExpectedTargetCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseDetectedCountryCommand))]
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
        SaveExpectedTarget();
    }

    private bool CanUseDetectedIp() => HasDetectedIp && !IsMonitoring;

    /// <summary>Captures the country the machine is currently in as the expected one.</summary>
    [RelayCommand(CanExecute = nameof(CanUseDetectedCountry))]
    private void UseDetectedCountry()
    {
        if (CountryCatalog.Find(CurrentCountryCode) is not { } option)
        {
            // Providers can report codes that are not lockable (EU for anycast ranges, AP for the
            // APNIC region). Displayable, but there is nothing meaningful to pin to.
            _dialogService.ShowInfo(
                "Cannot lock to this country",
                $"The current address reports “{CurrentCountryName}”, which is not an ISO country " +
                "that can be locked to. Pick a country from the list instead.");
            return;
        }

        ExpectedCountry = option;
        SaveExpectedTarget();
    }

    private bool CanUseDetectedCountry() => HasCurrentCountry && !IsMonitoring;

    [RelayCommand(CanExecute = nameof(CanSaveExpectedTarget))]
    private void SaveExpectedTarget()
    {
        SavedTargetViewModel chip;

        if (MatchMode == MatchMode.Country)
        {
            if (ExpectedCountry is not { } country)
            {
                return;
            }

            chip = CreateSavedTarget(SavedTarget.ForCountry(country.Code));
        }
        else
        {
            var value = ExpectedIp.Trim();
            if (value.Length == 0)
            {
                return;
            }

            chip = CreateSavedTarget(SavedTarget.ForIp(value));
        }

        if (SavedTargets.Any(existing => existing.Matches(chip)))
        {
            return;
        }

        SavedTargets.Insert(0, chip);
        OnPropertyChanged(nameof(HasSavedTargets));
        SaveSettings();
        AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Info, $"Saved lock target {chip.DisplayText}."));

        _ = FillMissingChipFlagsAsync();
    }

    // Disabled while monitoring for the same reason the fields themselves are: the target is
    // locked, so offering to save it would be an action with nothing to act on.
    private bool CanSaveExpectedTarget() =>
        !IsMonitoring
        && (MatchMode == MatchMode.Country
            ? ExpectedCountry is not null
            : !string.IsNullOrWhiteSpace(ExpectedIp)
              && !GetErrors(nameof(ExpectedIp)).Cast<object>().Any());

    [RelayCommand]
    private void UseSavedTarget(SavedTargetViewModel? target)
    {
        if (target is null || IsMonitoring)
        {
            return;
        }

        // Mode first, so the field that is about to be set is the one being validated.
        MatchMode = target.Kind;

        if (target.Kind == MatchMode.Country)
        {
            ExpectedCountry = CountryCatalog.Find(target.CountryCode);
        }
        else
        {
            ExpectedIp = target.Ip;
        }
    }

    [RelayCommand]
    private void ForgetSavedTarget(SavedTargetViewModel? target)
    {
        if (target is null || !SavedTargets.Remove(target))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSavedTargets));
        SaveSettings();
    }

    private SavedTargetViewModel CreateSavedTarget(SavedTarget model)
    {
        string? cachedCode = null;

        // Seeded from the cache so a relaunch paints the right flags immediately, with no
        // network round trip and no flicker.
        if (model.Kind == MatchMode.ExactIp && IPAddress.TryParse(model.Ip.Trim(), out var address))
        {
            cachedCode = _geo.TryGetCached(address)?.Code;
        }

        return new SavedTargetViewModel(model, cachedCode);
    }

    private void EnsureCurrentTargetSaved()
    {
        SavedTargetViewModel? chip = null;

        if (MatchMode == MatchMode.Country && ExpectedCountry is { } country)
        {
            chip = CreateSavedTarget(SavedTarget.ForCountry(country.Code));
        }
        else if (MatchMode == MatchMode.ExactIp && !string.IsNullOrWhiteSpace(ExpectedIp))
        {
            chip = CreateSavedTarget(SavedTarget.ForIp(ExpectedIp));
        }

        if (chip is not null && !SavedTargets.Any(existing => existing.Matches(chip)))
        {
            SavedTargets.Add(chip);
        }
    }

    /// <summary>
    /// Resolves the country of any address chip that does not have one yet.
    ///
    /// Sequential rather than parallel, so the geolocation service's request spacing is honoured
    /// rather than fought. Gives up after a few consecutive failures: the results are cached, so
    /// the next launch simply tries again.
    /// </summary>
    private async Task FillMissingChipFlagsAsync()
    {
        const int MaxConsecutiveFailures = 3;

        var pending = SavedTargets.Where(t => t.NeedsFlagLookup).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var failures = 0;

        foreach (var chip in pending)
        {
            if (_lifetimeCts.IsCancellationRequested || failures >= MaxConsecutiveFailures)
            {
                return;
            }

            if (!IPAddress.TryParse(chip.Ip, out var address))
            {
                continue;
            }

            try
            {
                var country = await _geo.GetCountryAsync(address, _lifetimeCts.Token).ConfigureAwait(false);

                if (country is null)
                {
                    failures++;
                    continue;
                }

                failures = 0;
                Post(() => chip.FlagCode = country.Code);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                failures++;
            }
        }
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

    partial void OnExpectedCountryChanged(CountryOption? value) => SaveSettings();

    partial void OnPollSecondsChanged(int value) => SaveSettings();

    partial void OnHideIpsChanged(bool value)
    {
        OnPropertyChanged(nameof(HideIpsToolTip));
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleHideIps() => HideIps = !HideIps;

    partial void OnProxyAffectsPublicIpChanged(bool value) => OnPropertyChanged(nameof(CountryToolTip));

    partial void OnCurrentCountryNameChanged(string value) => OnPropertyChanged(nameof(CountryToolTip));

    /// <summary>
    /// Both fields have to be re-checked when the mode changes. Each is only required in its own
    /// mode, so an error left over from the other one would keep HasErrors true and Start
    /// disabled with nothing on screen to fix.
    /// </summary>
    partial void OnMatchModeChanged(MatchMode value)
    {
        OnPropertyChanged(nameof(IsExactIpMode));
        OnPropertyChanged(nameof(IsCountryMode));

        ValidateProperty(ExpectedIp, nameof(ExpectedIp));
        ValidateProperty(ExpectedCountry, nameof(ExpectedCountry));

        StartCommand.NotifyCanExecuteChanged();
        SaveExpectedTargetCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    // ---- Validation ----------------------------------------------------------------------

    // ObservableValidator builds its context with `new ValidationContext(this)`, so
    // context.ObjectInstance is this ViewModel — which is how these two see the current mode.

    public static ValidationResult? ValidateExpectedIp(string? value, ValidationContext context)
    {
        if (context.ObjectInstance is MainViewModel { MatchMode: not MatchMode.ExactIp })
        {
            // Irrelevant in country mode, so it must not gate Start.
            return ValidationResult.Success;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return new ValidationResult("Enter the expected public IP.");
        }

        return IPAddress.TryParse(value.Trim(), out _)
            ? ValidationResult.Success
            : new ValidationResult("Not a valid IP address.");
    }

    public static ValidationResult? ValidateExpectedCountry(CountryOption? value, ValidationContext context)
    {
        if (context.ObjectInstance is MainViewModel { MatchMode: not MatchMode.Country })
        {
            return ValidationResult.Success;
        }

        return value is null
            ? new ValidationResult("Pick the country to lock to.")
            : ValidationResult.Success;
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

        CurrentCountryCode = snapshot.CountryCode;
        CurrentCountryName = snapshot.CountryName ?? "unknown";
        HasCurrentCountry = snapshot.GeoState == GeoProbeState.Resolved && snapshot.CountryCode is not null;

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
        MatchMode = MatchMode,
        ExpectedPublicIp = ExpectedIp.Trim(),
        ExpectedCountryCode = ExpectedCountry?.Code ?? string.Empty,
        SavedTargets = SavedTargets.Select(t => t.ToModel()).ToList(),

        // Written as a mirror of the address entries so a build without country locking can still
        // read this file. Never read back: SavedTargets is authoritative.
        SavedExpectedIps = SavedTargets
            .Where(t => t.Kind == MatchMode.ExactIp)
            .Select(t => t.Ip)
            .ToList(),

        PollSeconds = PollSeconds,
        HideIpAddresses = HideIps,
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

        // Stops any chip-flag lookup still in flight: this ViewModel outlives nothing, and the
        // geolocation service it is calling into is about to be disposed with the container.
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}
