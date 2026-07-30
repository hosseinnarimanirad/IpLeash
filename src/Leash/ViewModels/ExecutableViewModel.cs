using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Leash.Models;

namespace Leash.ViewModels;

/// <summary>One executable row inside a monitored app, showing its live PIDs and block state.</summary>
public sealed partial class ExecutableViewModel : ObservableObject
{
    public ExecutableViewModel(string path)
    {
        Path = path;
        FileName = SafeFileName(path);
        Directory = SafeDirectory(path);
    }

    public string Path { get; }

    public string FileName { get; }

    /// <summary>Shown under the file name so two installs of the same exe are told apart at a glance.</summary>
    public string Directory { get; }

    [ObservableProperty]
    private string _pidText = "not running";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isBlocked;

    /// <summary>Number of live processes, kept as a number so callers never parse PidText back.</summary>
    [ObservableProperty]
    private int _pidCount;

    /// <summary>False when the file is missing — a rule on it would silently block nothing.</summary>
    [ObservableProperty]
    private bool _exists = true;

    public void Apply(ExecutableState state)
    {
        PidText = state.PidText;
        PidCount = state.Pids.Count;
        IsRunning = state.IsRunning;
        IsBlocked = state.IsBlocked;
        Exists = state.Exists;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return System.IO.Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            return System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
