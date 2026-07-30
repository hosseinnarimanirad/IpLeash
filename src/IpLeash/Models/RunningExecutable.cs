namespace IpLeash.Models;

/// <summary>
/// One executable that currently has at least one live process, with all of its PIDs.
///
/// Grouping by path rather than listing processes flat is what makes "Claude is running as four
/// processes" read as one line instead of four.
/// </summary>
/// <param name="Path">Full path to the image, or the image name alone if the path was unreadable.</param>
/// <param name="ImageName">Process name without extension, e.g. "claude".</param>
/// <param name="Pids">Every live process started from this image.</param>
public sealed record RunningExecutable(string Path, string ImageName, IReadOnlyList<int> Pids)
{
    public string PidText => Pids.Count == 0 ? "not running" : $"PID {string.Join(", ", Pids)}";

    public string DisplayText => $"{ImageName} — {Pids.Count} process{(Pids.Count == 1 ? "" : "es")}";
}
