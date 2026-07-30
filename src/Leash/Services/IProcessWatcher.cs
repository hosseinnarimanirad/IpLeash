using Leash.Models;

namespace Leash.Services;

/// <summary>Reports which processes are running, and which of them belong to monitored executables.</summary>
public interface IProcessWatcher
{
    /// <summary>
    /// Maps each requested executable path to the PIDs currently running from it. Paths with no
    /// live process map to an empty list, so the caller always gets an entry per requested path.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<int>> GetPidsByPath(IEnumerable<string> exePaths);

    /// <summary>
    /// Every distinct executable with at least one live process, for the "add from running
    /// process" picker. Processes whose path cannot be read are reported by image name only.
    /// </summary>
    IReadOnlyList<RunningExecutable> GetRunningExecutables();
}
