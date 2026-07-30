using System.Net;
using System.Net.NetworkInformation;
using System.Timers;
using IpLeash.Models;
using Timer = System.Timers.Timer;

namespace IpLeash.Services;

/// <inheritdoc cref="IMonitorEngine"/>
public sealed class MonitorEngine : IMonitorEngine, IDisposable
{
    /// <summary>How often the process/adapter display is refreshed. Cheap, purely informational.</summary>
    private static readonly TimeSpan FastRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly IPublicIpService _publicIp;
    private readonly ILocalIpService _localIp;
    private readonly IProcessWatcher _processWatcher;
    private readonly IFirewallService _firewall;
    private readonly IProxyService _proxy;

    private readonly Timer _evalTimer = new() { AutoReset = true };
    private readonly Timer _fastTimer = new(FastRefreshInterval.TotalMilliseconds) { AutoReset = true };

    // Serializes evaluation. The poll timer and the NetworkAddressChanged event can fire at the
    // same moment, and two concurrent evaluations would issue conflicting netsh calls.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();

    private AppSettings? _settings;
    private CancellationTokenSource? _sessionCts;
    private bool _networkEventHooked;
    private bool _disposed;

    // Guarded by _stateLock.
    private MonitorStatus _status = MonitorStatus.Idle;
    private string? _publicIpText;
    private IpProbeState _probeState = IpProbeState.Unknown;
    private ProxyInfo _proxyInfo = ProxyInfo.None;
    private string _reason = "Not monitoring.";
    private DateTimeOffset? _lastCheckedAt;
    private IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();
    private bool _isRunning;
    private List<MonitoredApp> _watchedApps = [];

    /// <summary>Executable paths currently blocked by this engine. Guarded by <see cref="_stateLock"/>.</summary>
    private readonly HashSet<string> _blockedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Live PIDs per executable path. Guarded by <see cref="_stateLock"/>.</summary>
    private Dictionary<string, IReadOnlyList<int>> _pidsByPath = new(StringComparer.OrdinalIgnoreCase);

    public MonitorEngine(
        IPublicIpService publicIp,
        ILocalIpService localIp,
        IProcessWatcher processWatcher,
        IFirewallService firewall,
        IProxyService proxy)
    {
        _publicIp = publicIp;
        _localIp = localIp;
        _processWatcher = processWatcher;
        _firewall = firewall;
        _proxy = proxy;

        _evalTimer.Elapsed += OnEvalTimerElapsed;
        _fastTimer.Elapsed += OnFastTimerElapsed;

        // Hooked for the engine's whole lifetime, not just while monitoring, so the idle display
        // also reacts to a VPN going up or down.
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _networkEventHooked = true;

        // Runs from construction so adapters and PIDs are live before the user presses Start.
        _fastTimer.Start();
        RefreshFastState();
    }

    public MonitorSnapshot Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return BuildSnapshotLocked();
            }
        }
    }

    public bool IsElevated => _firewall.IsElevated;

    public event EventHandler<MonitorSnapshot>? SnapshotChanged;

    public event EventHandler<LogEntry>? LogEmitted;

    public void SetWatchTargets(IReadOnlyList<MonitoredApp> apps)
    {
        lock (_stateLock)
        {
            // Copied, because the ViewModel keeps mutating the instances it handed over.
            _watchedApps = apps.Select(a => new MonitoredApp
            {
                Id = a.Id,
                Name = a.Name,
                Enabled = a.Enabled,
                ExecutablePaths = [.. a.ExecutablePaths],
            }).ToList();
        }

        RefreshFastState();
    }

    public async Task<IReadOnlyList<string>> CleanupOrphanedRulesAsync(CancellationToken ct = default)
    {
        if (!_firewall.IsElevated)
        {
            return Array.Empty<string>();
        }

        var removed = await _firewall.RemoveOrphanedRulesAsync(ct).ConfigureAwait(false);
        foreach (var path in removed)
        {
            Log(LogLevel.Warning, $"Removed block rules left over from a previous run: {path}");
        }

        return removed;
    }

    public async Task StartAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_firewall.IsElevated)
        {
            Log(LogLevel.Error, "Administrator rights are required to change firewall rules. Restart IpLeash elevated.");
            return;
        }

        if (Volatile.Read(ref _isRunning))
        {
            return;
        }

        var targets = settings.EnabledExecutablePaths();
        if (targets.Count == 0)
        {
            Log(LogLevel.Error, "No enabled applications to monitor. Add at least one and enable it.");
            return;
        }

        _settings = settings;
        _sessionCts = new CancellationTokenSource();
        SetWatchTargets(settings.Apps);

        // Trust the firewall, not memory: rules may already exist from an earlier session.
        var preexisting = new List<string>();
        foreach (var path in targets)
        {
            if (await _firewall.IsBlockedAsync(path, ct).ConfigureAwait(false))
            {
                preexisting.Add(path);
            }
        }

        RefreshProxy();

        lock (_stateLock)
        {
            _isRunning = true;
            _probeState = IpProbeState.Checking;
            _blockedPaths.Clear();
            foreach (var path in preexisting)
            {
                _blockedPaths.Add(path);
            }

            _status = MonitorStatus.Unknown;
            _reason = "Checking public IP…";
        }

        Publish();

        _evalTimer.Interval = TimeSpan.FromSeconds(settings.ClampedPollSeconds).TotalMilliseconds;
        _evalTimer.Start();

        var appCount = settings.Apps.Count(a => a.Enabled);
        Log(LogLevel.Info,
            $"Monitoring started — {appCount} app{(appCount == 1 ? "" : "s")} ({targets.Count} executable{(targets.Count == 1 ? "" : "s")}), " +
            $"expected public IP {settings.ExpectedPublicIp}, polling every {settings.ClampedPollSeconds}s.");

        await EvaluateAsync(_sessionCts.Token).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!Volatile.Read(ref _isRunning))
        {
            return;
        }

        _evalTimer.Stop();

        // The network-change hook stays attached: the idle display keeps reacting to VPN changes.

        // Cancel in-flight work (an IP fetch can be mid-timeout) before taking the gate.
        if (_sessionCts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<string> toRemove;
            lock (_stateLock)
            {
                toRemove = _blockedPaths.ToList();
            }

            // Rules must not outlive the app: stopping means we no longer enforce.
            foreach (var path in toRemove)
            {
                var result = await _firewall.RemoveBlockAsync(path, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    lock (_stateLock)
                    {
                        _blockedPaths.Remove(path);
                    }

                    Log(LogLevel.Info, $"Block rules removed for {path}.");
                }
                else
                {
                    Log(LogLevel.Error, $"Failed to remove block rules for {path}: {result.Detail}");
                }
            }

            lock (_stateLock)
            {
                _isRunning = false;
                _status = MonitorStatus.Idle;
                _reason = "Not monitoring.";
            }
        }
        finally
        {
            _gate.Release();
        }

        _sessionCts?.Dispose();
        _sessionCts = null;

        Log(LogLevel.Info, "Monitoring stopped.");
        Publish();
    }

    public Task CheckNowAsync(CancellationToken ct = default)
    {
        // While stopped this still refreshes the reading — it just cannot change any rules.
        return Volatile.Read(ref _isRunning)
            ? EvaluateAsync(_sessionCts?.Token ?? ct)
            : ProbeAsync(ct);
    }

    /// <inheritdoc />
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _isRunning))
        {
            // Enforcement evaluation already keeps the reading current; a second lookup would
            // only add load and could race the displayed value backwards.
            return;
        }

        // Proxy first: it is part of interpreting whatever address comes back.
        RefreshProxy();

        if (!await _gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            lock (_stateLock)
            {
                _probeState = IpProbeState.Checking;
            }

            Publish();

            var current = await _publicIp.GetPublicIpAsync(ct).ConfigureAwait(false);

            lock (_stateLock)
            {
                // Enforcement state is deliberately untouched: nothing is being monitored, so a
                // failed lookup here must not read as a fail-closed block.
                _publicIpText = current?.ToString();
                _probeState = current is null ? IpProbeState.Failed : IpProbeState.Resolved;
                _lastCheckedAt = DateTimeOffset.Now;
            }

            Log(current is null ? LogLevel.Warning : LogLevel.Info, current is null
                ? "Public IP could not be determined."
                : $"Public IP is {current}.");
        }
        catch (OperationCanceledException)
        {
            lock (_stateLock)
            {
                _probeState = IpProbeState.Unknown;
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _probeState = IpProbeState.Failed;
            }

            Log(LogLevel.Error, $"Public IP lookup failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            Publish();
        }
    }

    private void RefreshProxy()
    {
        try
        {
            var info = _proxy.GetProxyInfo();

            bool changed;
            lock (_stateLock)
            {
                changed = _proxyInfo != info;
                _proxyInfo = info;
            }

            if (changed && info.AffectsPublicIpCheck)
            {
                Log(LogLevel.Warning,
                    $"Requests to the IP-check service go through proxy {info.Summary}, so the reported " +
                    "public IP is the proxy's exit address, not this machine's.");
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Proxy lookup failed: {ex.Message}");
        }
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            // An evaluation is already running; its result will be current enough.
            return;
        }

        try
        {
            var settings = _settings;
            if (settings is null || !Volatile.Read(ref _isRunning))
            {
                return;
            }

            IPAddress? current;
            try
            {
                current = await _publicIp.GetPublicIpAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var (desired, reason) = Decide(current, settings.ExpectedPublicIp);
            var shouldBlock = desired != MonitorStatus.Allowed;

            MonitorStatus previousStatus;
            string? previousIp;

            lock (_stateLock)
            {
                previousStatus = _status;
                previousIp = _publicIpText;

                _status = desired;
                _publicIpText = current?.ToString();
                _probeState = current is null ? IpProbeState.Failed : IpProbeState.Resolved;
                _reason = reason;
                _lastCheckedAt = DateTimeOffset.Now;
            }

            if (previousIp != current?.ToString())
            {
                Log(LogLevel.Info, current is null
                    ? "Public IP could not be determined."
                    : $"Public IP is {current}.");
            }

            await ReconcileAsync(settings, shouldBlock, ct).ConfigureAwait(false);

            if (previousStatus != desired)
            {
                Log(desired == MonitorStatus.Allowed ? LogLevel.Info : LogLevel.Warning, reason);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Evaluation failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            Publish();
        }
    }

    /// <summary>
    /// Drives every enabled executable to the desired state. Only paths whose state actually
    /// differs are touched, so netsh is not invoked on every tick.
    /// </summary>
    private async Task ReconcileAsync(AppSettings settings, bool shouldBlock, CancellationToken ct)
    {
        var targets = settings.EnabledExecutablePaths();

        HashSet<string> currentlyBlocked;
        lock (_stateLock)
        {
            currentlyBlocked = new HashSet<string>(_blockedPaths, StringComparer.OrdinalIgnoreCase);
        }

        // Anything blocked that is no longer an enabled target must be released, or disabling an
        // app while monitoring would strand it blocked.
        var stale = currentlyBlocked.Except(targets, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in stale)
        {
            await ApplyOneAsync(path, block: false, ct).ConfigureAwait(false);
        }

        foreach (var path in targets)
        {
            if (currentlyBlocked.Contains(path) != shouldBlock)
            {
                await ApplyOneAsync(path, shouldBlock, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyOneAsync(string path, bool block, CancellationToken ct)
    {
        var result = block
            ? await _firewall.ApplyBlockAsync(path, ct).ConfigureAwait(false)
            : await _firewall.RemoveBlockAsync(path, ct).ConfigureAwait(false);

        if (result.Success)
        {
            lock (_stateLock)
            {
                if (block)
                {
                    _blockedPaths.Add(path);
                }
                else
                {
                    _blockedPaths.Remove(path);
                }
            }

            Log(LogLevel.Warning, block
                ? $"BLOCKED — inbound and outbound firewall rules applied to {path}."
                : $"UNBLOCKED — firewall rules removed from {path}.");
        }
        else
        {
            // Surface loudly: a failed block means traffic is still flowing.
            Log(LogLevel.Error, block
                ? $"FAILED TO BLOCK {path}. Traffic is NOT being stopped. {result.Detail}"
                : $"Failed to remove block rules for {path}. {result.Detail}");
        }
    }

    /// <summary>
    /// Maps the observed public IP to an enforcement decision. A null observation is a mismatch,
    /// not an unchanged state — that is the fail-closed rule.
    /// </summary>
    private static (MonitorStatus Status, string Reason) Decide(IPAddress? current, string expected)
    {
        if (current is null)
        {
            return (MonitorStatus.Unknown, "Public IP could not be determined — blocking (fail-closed).");
        }

        if (!IPAddress.TryParse(expected, out var expectedAddress))
        {
            return (MonitorStatus.Blocked, $"Expected IP '{expected}' is not a valid address — blocking.");
        }

        return current.Equals(expectedAddress)
            ? (MonitorStatus.Allowed, $"Public IP {current} matches the expected address.")
            : (MonitorStatus.Blocked, $"Public IP {current} does not match expected {expectedAddress}.");
    }

    private void OnEvalTimerElapsed(object? sender, ElapsedEventArgs e) =>
        _ = EvaluateAsync(_sessionCts?.Token ?? CancellationToken.None);

    private void OnFastTimerElapsed(object? sender, ElapsedEventArgs e) => RefreshFastState();

    /// <summary>
    /// A VPN going up or down changes the adapter table, which fires this well before the next
    /// poll tick — this is what makes the app react in about a second rather than up to 15.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        RefreshFastState();
        Log(LogLevel.Info, "Network address change detected — re-checking.");

        // While monitoring this reconciles rules; while idle it only refreshes the display.
        _ = Volatile.Read(ref _isRunning)
            ? EvaluateAsync(_sessionCts?.Token ?? CancellationToken.None)
            : ProbeAsync(CancellationToken.None);
    }

    private void RefreshFastState()
    {
        try
        {
            List<string> paths;
            lock (_stateLock)
            {
                paths = _watchedApps.SelectMany(a => a.ExecutablePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            var pids = _processWatcher.GetPidsByPath(paths);
            var adapters = _localIp.GetAdapters();

            bool changed;
            lock (_stateLock)
            {
                changed = !_adapters.SequenceEqual(adapters) || !SamePids(_pidsByPath, pids);
                _pidsByPath = pids.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                _adapters = adapters;
            }

            if (changed)
            {
                Publish();
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Status refresh failed: {ex.Message}");
        }
    }

    private static bool SamePids(
        IReadOnlyDictionary<string, IReadOnlyList<int>> a,
        IReadOnlyDictionary<string, IReadOnlyList<int>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !value.SequenceEqual(other))
            {
                return false;
            }
        }

        return true;
    }

    private MonitorSnapshot BuildSnapshotLocked()
    {
        var apps = _watchedApps.Select(app => new MonitoredAppState(
            app.Id,
            app.Name,
            app.Enabled,
            app.ExecutablePaths.Select(path => new ExecutableState(
                path,
                _blockedPaths.Contains(path),
                ExecutableFile.IsUsable(path),
                _pidsByPath.TryGetValue(path, out var pids) ? pids : Array.Empty<int>())).ToList()))
            .ToList();

        return new MonitorSnapshot(
            _status,
            _publicIpText,
            _probeState,
            _reason,
            _lastCheckedAt,
            _proxyInfo,
            apps,
            _adapters,
            _isRunning);
    }

    private void Publish()
    {
        MonitorSnapshot snapshot;
        lock (_stateLock)
        {
            snapshot = BuildSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, snapshot);
    }

    private void Log(LogLevel level, string message) =>
        LogEmitted?.Invoke(this, new LogEntry(DateTimeOffset.Now, level, message));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_networkEventHooked)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _networkEventHooked = false;
        }

        _evalTimer.Elapsed -= OnEvalTimerElapsed;
        _fastTimer.Elapsed -= OnFastTimerElapsed;
        _evalTimer.Dispose();
        _fastTimer.Dispose();
        _sessionCts?.Dispose();
        _gate.Dispose();
    }
}
