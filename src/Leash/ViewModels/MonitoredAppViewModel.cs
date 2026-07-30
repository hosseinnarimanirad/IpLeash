using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Leash.Models;

namespace Leash.ViewModels;

/// <summary>
/// One row in the monitored list: a named group of executables blocked and unblocked as a unit.
/// </summary>
public sealed partial class MonitoredAppViewModel : ObservableObject
{
    private readonly Action _onEdited;

    public MonitoredAppViewModel(MonitoredApp model, Action onEdited)
    {
        _onEdited = onEdited;
        Id = model.Id;
        _name = model.Name;
        _enabled = model.Enabled;

        foreach (var path in model.ExecutablePaths)
        {
            Executables.Add(new ExecutableViewModel(path));
        }

        UpdateSummaries();
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _enabled;

    public ObservableCollection<ExecutableViewModel> Executables { get; } = [];

    /// <summary>"BLOCKED" / "PARTIALLY BLOCKED" / "allowed" / "disabled".</summary>
    [ObservableProperty]
    private string _blockStatusText = "allowed";

    [ObservableProperty]
    private bool _isBlocked;

    [ObservableProperty]
    private bool _isPartiallyBlocked;

    /// <summary>e.g. "4 processes across 1 executable" — the answer to "Claude runs as several processes".</summary>
    [ObservableProperty]
    private string _processSummary = "not running";

    [ObservableProperty]
    private bool _hasMissingExecutable;

    partial void OnNameChanged(string value) => _onEdited();

    partial void OnEnabledChanged(bool value)
    {
        UpdateSummaries();
        _onEdited();
    }

    public MonitoredApp ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        ExecutablePaths = Executables.Select(e => e.Path).ToList(),
    };

    /// <summary>
    /// Merges engine state into the existing child view models rather than rebuilding them, so
    /// an in-progress rename or scroll position is not thrown away on every refresh.
    /// </summary>
    public void Apply(MonitoredAppState state)
    {
        Enabled = state.Enabled;

        foreach (var executableState in state.Executables)
        {
            var target = Executables.FirstOrDefault(e =>
                string.Equals(e.Path, executableState.Path, StringComparison.OrdinalIgnoreCase));
            target?.Apply(executableState);
        }

        UpdateSummaries();
    }

    public void AddExecutables(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Executables.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Executables.Add(new ExecutableViewModel(path));
        }

        UpdateSummaries();
        _onEdited();
    }

    public void RemoveExecutable(ExecutableViewModel executable)
    {
        Executables.Remove(executable);
        UpdateSummaries();
        _onEdited();
    }

    private void UpdateSummaries()
    {
        var blockedCount = Executables.Count(e => e.IsBlocked);
        var total = Executables.Count;

        IsBlocked = total > 0 && blockedCount == total;
        IsPartiallyBlocked = blockedCount > 0 && blockedCount < total;

        BlockStatusText = !Enabled
            ? "disabled"
            : IsBlocked ? "BLOCKED"
            : IsPartiallyBlocked ? $"PARTIALLY BLOCKED ({blockedCount}/{total})"
            : "allowed";

        var processCount = Executables.Sum(e => e.PidCount);
        var runningExecutables = Executables.Count(e => e.IsRunning);

        ProcessSummary = processCount == 0
            ? "not running"
            : $"{processCount} process{(processCount == 1 ? "" : "es")} across {runningExecutables} executable{(runningExecutables == 1 ? "" : "s")}";

        HasMissingExecutable = Executables.Any(e => !e.Exists);
    }
}
