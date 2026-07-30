namespace Leash.Models;

/// <summary>Live state of one executable inside a monitored app.</summary>
/// <param name="Path">Full path the firewall rule is bound to.</param>
/// <param name="IsBlocked">Whether this app's block rules are currently in place for this path.</param>
/// <param name="Exists">False when the file is missing — the rule would be a silent no-op.</param>
/// <param name="Pids">Live processes started from this path.</param>
public sealed record ExecutableState(string Path, bool IsBlocked, bool Exists, IReadOnlyList<int> Pids)
{
    public bool IsRunning => Pids.Count > 0;

    public string PidText => Pids.Count == 0 ? "not running" : $"PID {string.Join(", ", Pids)}";
}

/// <summary>Live state of one monitored app.</summary>
public sealed record MonitoredAppState(
    string Id,
    string Name,
    bool Enabled,
    IReadOnlyList<ExecutableState> Executables)
{
    /// <summary>True only when every executable is blocked — a partial block is not a block.</summary>
    public bool IsFullyBlocked => Executables.Count > 0 && Executables.All(e => e.IsBlocked);

    public bool IsPartiallyBlocked => Executables.Any(e => e.IsBlocked) && !IsFullyBlocked;

    public int RunningProcessCount => Executables.Sum(e => e.Pids.Count);
}
