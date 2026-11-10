using System.IO;
using System.Text.Json;
using IpLeash.Services;
using Timer = System.Threading.Timer;

namespace IpLeash.Platform;

/// <summary>
/// macOS enforcement: a blocked target's processes are suspended with SIGSTOP and resumed with
/// SIGCONT.
///
/// Why not a firewall: macOS has no firewall that can block an application's outbound traffic.
/// pf matches addresses, ports and users, never processes, and the Application Firewall
/// (socketfilterfw) only governs inbound connections. Per-application filtering exists only as a
/// Network Extension content filter, which needs an Apple-issued entitlement and a provisioning
/// profile — it cannot be built or run from source. A stopped process, by contrast, cannot
/// read or write a socket, so this works with no special rights at all for the user's own apps.
///
/// What freezing guarantees that the Windows rules do not: connections that were already open
/// stop too, and the processes a target spawns (a CLI's git or node children) are frozen with it.
/// What it costs: the app is unresponsive while blocked, and macOS will call it "not responding".
///
/// A firewall rule stays in force on its own; a freeze has to be re-applied to every new process.
/// While anything is blocked, a sweep runs several times a second and freezes any matching
/// process that has appeared since the last one — an app relaunched mid-block, or a new helper.
///
/// Implements <see cref="IFirewallService"/> so the shared <see cref="MonitorEngine"/> drives it
/// exactly as it drives netsh on Windows: Apply means "this target must be frozen", Remove means
/// "it must not be", and the persisted state lets the next launch undo a crash.
/// </summary>
public sealed class ProcessFreezeService : IFirewallService, IDisposable
{
    /// <summary>
    /// How often a blocked target is re-checked for new processes. This bounds how long a freshly
    /// launched copy of a blocked app runs before it is frozen.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(200);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly IBlockStateStore _stateStore;
    private readonly string _frozenStatePath;
    private readonly object _gate = new();
    private readonly Timer _sweepTimer;
    private readonly int _selfPid = Environment.ProcessId;
    private readonly uint _selfUid = Native.GetUid();

    /// <summary>Targets that must be frozen right now. Guarded by <see cref="_gate"/>.</summary>
    private readonly HashSet<string> _armed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every process this service has stopped and not yet resumed. Guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<int, FrozenProcess> _frozen = [];

    /// <summary>
    /// Sweep failures already reported, by PID and start time. A process that cannot be frozen
    /// would otherwise be reported five times a second for as long as it lives.
    /// </summary>
    private readonly HashSet<(int Pid, long StartMicros)> _reported = [];

    private bool _disposed;

    public ProcessFreezeService(IBlockStateStore stateStore)
        : this(stateStore, Path.Combine(AppPaths.DataDirectory, "frozen-processes.json"))
    {
    }

    public ProcessFreezeService(IBlockStateStore stateStore, string frozenStatePath)
    {
        _stateStore = stateStore;
        _frozenStatePath = frozenStatePath;
        _sweepTimer = new Timer(_ => SweepFromTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Always true. Signalling a process needs no special rights when it belongs to the same
    /// user, and a target owned by someone else is reported as a failed block rather than
    /// gated up front — running a GUI app as root on macOS is not a reasonable requirement.
    /// </summary>
    public bool IsElevated => true;

    /// <summary>Raised from the sweep when a newly appeared process could not be frozen.</summary>
    public event EventHandler<string>? EnforcementProblem;

    public Task<FirewallResult> ApplyBlockAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Task.FromResult(new FirewallResult(false, "No executable specified."));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var wasArmed = !_armed.Add(exePath);
            var failures = SweepLocked();
            var mine = failures.Where(f => string.Equals(f.Target, exePath, StringComparison.OrdinalIgnoreCase)).ToList();

            if (mine.Count > 0 && !wasArmed)
            {
                // Partial enforcement is worse than none, because it reads as BLOCKED while some
                // of the app's traffic still flows. Roll back so the engine sees the failure and
                // retries on the next evaluation, exactly as with a failed netsh add.
                _armed.Remove(exePath);
                ReleaseUnclaimedLocked();
                PersistLocked();
                return Task.FromResult(new FirewallResult(false, string.Join(" ", mine.Select(f => f.Message))));
            }

            PersistLocked();
            UpdateTimerLocked();
        }

        return Task.FromResult(FirewallResult.Ok);
    }

    public Task<FirewallResult> RemoveBlockAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Task.FromResult(new FirewallResult(false, "No executable specified."));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(FirewallResult.Ok);
            }

            _armed.Remove(exePath);
            var failures = ReleaseUnclaimedLocked();
            PersistLocked();
            UpdateTimerLocked();

            return Task.FromResult(failures.Count == 0
                ? FirewallResult.Ok
                : new FirewallResult(false, string.Join(" ", failures)));
        }
    }

    public Task<bool> IsBlockedAsync(string exePath, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_armed.Contains(exePath));
        }
    }

    /// <summary>
    /// Resumes what a run that never got to exit left frozen. Two records cover it: the exact
    /// processes that were stopped (PID plus start time, so a recycled PID is never touched), and
    /// the targets that were blocked, in case the process record was lost mid-write.
    /// </summary>
    public Task<IReadOnlyList<string>> RemoveOrphanedRulesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var recordedTargets = _stateStore.Load();
            var recordedProcesses = LoadFrozenState();

            if (recordedTargets.Count == 0 && recordedProcesses.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var table = ProcessTable.Capture();
            var resumed = new HashSet<int>();
            var released = new List<string>();

            foreach (var record in recordedProcesses)
            {
                if (table.Find(record.Pid) is { IsStopped: true } entry
                    && entry.StartMicros == record.StartMicros
                    && Native.Kill(record.Pid, Native.SIGCONT) == 0)
                {
                    resumed.Add(record.Pid);
                    if (!released.Contains(record.Target, StringComparer.OrdinalIgnoreCase))
                    {
                        released.Add(record.Target);
                    }
                }
            }

            foreach (var target in recordedTargets)
            {
                var claimed = Claim(table, [TargetMatcher.For(target)]);
                var any = false;

                foreach (var pid in claimed.Keys)
                {
                    if (resumed.Contains(pid) || table.Find(pid) is not { IsStopped: true } entry || entry.Uid != _selfUid)
                    {
                        continue;
                    }

                    if (Native.Kill(pid, Native.SIGCONT) == 0)
                    {
                        resumed.Add(pid);
                        any = true;
                    }
                }

                if (any && !released.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    released.Add(target);
                }
            }

            _armed.Clear();
            _frozen.Clear();
            _stateStore.Clear();
            DeleteFrozenState();
            UpdateTimerLocked();

            return Task.FromResult<IReadOnlyList<string>>(released);
        }
    }

    // ---- Sweep -----------------------------------------------------------------------------

    private void SweepFromTimer()
    {
        List<SweepFailure> failures;

        lock (_gate)
        {
            if (_disposed || _armed.Count == 0)
            {
                return;
            }

            failures = SweepLocked()
                .Where(f => _reported.Add((f.Pid, f.StartMicros)))
                .ToList();
            PersistLocked();
        }

        foreach (var failure in failures)
        {
            EnforcementProblem?.Invoke(this, $"FAILED TO FREEZE a new process of {failure.Target}. {failure.Message}");
        }
    }

    /// <summary>
    /// Freezes every process claimed by an armed target that is not already frozen. Returns the
    /// processes that could not be frozen, by target.
    /// </summary>
    private List<SweepFailure> SweepLocked()
    {
        var failures = new List<SweepFailure>();
        if (_armed.Count == 0)
        {
            return failures;
        }

        var table = ProcessTable.Capture();
        PruneExitedLocked(table);

        var claimed = Claim(table, _armed.Select(TargetMatcher.For).ToList());

        foreach (var (pid, claim) in claimed)
        {
            if (table.Find(pid) is not { } entry)
            {
                continue;
            }

            var target = claim.Target;

            // Already ours and still stopped. Anything else — ours but continued by someone else,
            // or never seen — is (re)stopped below.
            if (_frozen.TryGetValue(pid, out var known) && known.StartMicros == entry.StartMicros && entry.IsStopped)
            {
                continue;
            }

            if (entry.Uid != _selfUid)
            {
                // A process started from the target itself has to be frozen, or the block is not
                // real. A descendant running as someone else — the setuid `login` under a blocked
                // terminal, say — is skipped instead: failing the whole target over it would leave
                // everything unblocked, and the Windows rules never reach child processes at all.
                if (claim.IsDirect)
                {
                    failures.Add(new SweepFailure(target,
                        $"{Describe(entry)} belongs to another user (uid {entry.Uid}), and only your own processes can be frozen.",
                        pid, entry.StartMicros));
                }

                continue;
            }

            var error = Native.Kill(pid, Native.SIGSTOP);
            if (error == 0)
            {
                _frozen[pid] = new FrozenProcess(pid, entry.StartMicros, target, entry.Path ?? entry.Command);
            }
            else if (error != Errno.ESRCH)
            {
                // ESRCH just means it exited in between, which needs no freezing.
                failures.Add(new SweepFailure(target, $"Could not freeze {Describe(entry)}: errno {error}.", pid, entry.StartMicros));
            }
        }

        return failures;
    }

    /// <summary>
    /// Resumes every frozen process that no armed target claims any longer. Returns a message per
    /// process that could not be resumed.
    /// </summary>
    private List<string> ReleaseUnclaimedLocked()
    {
        var failures = new List<string>();
        if (_frozen.Count == 0)
        {
            return failures;
        }

        var table = ProcessTable.Capture();
        PruneExitedLocked(table);

        var stillClaimed = Claim(table, _armed.Select(TargetMatcher.For).ToList());

        // Children before parents, so a parent never wakes to find its children still stopped.
        foreach (var record in _frozen.Values.OrderByDescending(r => r.StartMicros).ToList())
        {
            if (stillClaimed.ContainsKey(record.Pid))
            {
                continue;
            }

            var error = Native.Kill(record.Pid, Native.SIGCONT);
            if (error is 0 or Errno.ESRCH)
            {
                _frozen.Remove(record.Pid);
            }
            else
            {
                failures.Add($"Could not resume PID {record.Pid} ({record.Path}): errno {error}.");
            }
        }

        return failures;
    }

    /// <summary>
    /// Maps every process that must be frozen to the target claiming it: processes whose image
    /// matches a target, and everything they have spawned. IpLeash itself, the processes above it
    /// and the processes it started are never claimed.
    /// </summary>
    private Dictionary<int, ProcessClaim> Claim(ProcessTable table, IReadOnlyList<TargetMatcher> matchers)
    {
        var claimed = new Dictionary<int, ProcessClaim>();
        if (matchers.Count == 0)
        {
            return claimed;
        }

        var exempt = table.SelfAndAncestors(_selfPid);
        exempt.Add(0);
        exempt.Add(1);

        // IpLeash's own children (scutil, networksetup, osascript) are never targets, and must
        // not become ones: the .NET runtime's child-reaping loop spins on a stopped child, which
        // stalls every later Process.Start in this process.
        foreach (var child in table.DescendantsOf(_selfPid))
        {
            exempt.Add(child.Pid);
        }

        foreach (var entry in table.Entries)
        {
            if (entry.IsZombie || exempt.Contains(entry.Pid) || claimed.TryGetValue(entry.Pid, out var prior) && prior.IsDirect)
            {
                continue;
            }

            var matcher = matchers.FirstOrDefault(m => m.Matches(entry.Path));
            if (matcher is null)
            {
                continue;
            }

            claimed[entry.Pid] = new ProcessClaim(matcher.Target, IsDirect: true);

            foreach (var descendant in table.DescendantsOf(entry.Pid))
            {
                if (!descendant.IsZombie && !exempt.Contains(descendant.Pid))
                {
                    claimed.TryAdd(descendant.Pid, new ProcessClaim(matcher.Target, IsDirect: false));
                }
            }
        }

        return claimed;
    }

    /// <summary>Forgets frozen records whose process has exited, or whose PID now belongs to another process.</summary>
    private void PruneExitedLocked(ProcessTable table)
    {
        foreach (var record in _frozen.Values.ToList())
        {
            if (!table.IsSameProcess(record.Pid, record.StartMicros))
            {
                _frozen.Remove(record.Pid);
            }
        }
    }

    private void UpdateTimerLocked()
    {
        if (_disposed)
        {
            return;
        }

        if (_armed.Count > 0)
        {
            _sweepTimer.Change(SweepInterval, SweepInterval);
        }
        else
        {
            _sweepTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private static string Describe(ProcessEntry entry) =>
        $"PID {entry.Pid} ({(entry.Path is { } path ? Path.GetFileName(path) : entry.Command)})";

    // ---- Persistence -----------------------------------------------------------------------

    /// <summary>Mirrors both records to disk, so a crash leaves enough behind to undo itself.</summary>
    private void PersistLocked()
    {
        _stateStore.Save(_armed);

        if (_frozen.Count == 0)
        {
            DeleteFrozenState();
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_frozenStatePath)!);
            File.WriteAllText(_frozenStatePath, JsonSerializer.Serialize(_frozen.Values.ToList(), SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. The target list in the block-state file still covers recovery.
        }
    }

    private List<FrozenProcess> LoadFrozenState()
    {
        try
        {
            return File.Exists(_frozenStatePath)
                ? JsonSerializer.Deserialize<List<FrozenProcess>>(File.ReadAllText(_frozenStatePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void DeleteFrozenState()
    {
        try
        {
            File.Delete(_frozenStatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sweepTimer.Dispose();
        }
    }

    private sealed record SweepFailure(string Target, string Message, int Pid, long StartMicros);

    /// <param name="IsDirect">True when the process image itself matches; false for a descendant.</param>
    private sealed record ProcessClaim(string Target, bool IsDirect);

    /// <summary>One process this service stopped. Serialized to frozen-processes.json.</summary>
    public sealed record FrozenProcess(int Pid, long StartMicros, string Target, string Path);

    private static class Errno
    {
        public const int ESRCH = 3;
    }
}
