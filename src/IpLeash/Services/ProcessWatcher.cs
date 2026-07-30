using System.Diagnostics;
using System.IO;
using IpLeash.Models;

namespace IpLeash.Services;

/// <summary>
/// Polls the process table.
///
/// Polling is used rather than a WMI <c>Win32_ProcessStartTrace</c> subscription: the UI only
/// needs second-granularity, and polling avoids managing an elevated WMI subscription and its
/// event-storm behaviour under heavy process churn.
///
/// Matching is by full image path where readable, because a list can now hold two different
/// installs of the same executable name and they must not be conflated. Image name is only a
/// fallback for processes whose path cannot be read.
/// </summary>
public sealed class ProcessWatcher : IProcessWatcher
{
    public IReadOnlyDictionary<string, IReadOnlyList<int>> GetPidsByPath(IEnumerable<string> exePaths)
    {
        var requested = exePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in requested)
        {
            result[path] = Array.Empty<int>();
        }

        if (requested.Count == 0)
        {
            return result;
        }

        // Group the requested paths by image name so only relevant processes are enumerated.
        var byImageName = requested
            .GroupBy(SafeImageName, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key));

        foreach (var group in byImageName)
        {
            foreach (var process in SafeGetProcessesByName(group.Key))
            {
                try
                {
                    var actualPath = TryGetPath(process);

                    foreach (var candidate in group)
                    {
                        var matches = actualPath is null
                            ? true   // Path unreadable: fall back to the image-name match we already have.
                            : PathsEqual(actualPath, candidate);

                        if (matches)
                        {
                            result[candidate] = result[candidate].Append(process.Id).ToList();
                        }
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return result;
    }

    public IReadOnlyList<RunningExecutable> GetRunningExecutables()
    {
        var byPath = new Dictionary<string, (string ImageName, List<int> Pids)>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<RunningExecutable>();
        }

        foreach (var process in processes)
        {
            try
            {
                var imageName = process.ProcessName;
                // Processes without a readable path (protected/system) are keyed by name so they
                // still appear, but they cannot be turned into a firewall rule.
                var key = TryGetPath(process) ?? imageName;

                if (!byPath.TryGetValue(key, out var entry))
                {
                    entry = (imageName, []);
                    byPath[key] = entry;
                }

                entry.Pids.Add(process.Id);
            }
            catch (InvalidOperationException)
            {
                // Exited between enumeration and read.
            }
            finally
            {
                process.Dispose();
            }
        }

        return byPath
            .Select(kv => new RunningExecutable(kv.Key, kv.Value.ImageName, kv.Value.Pids))
            .OrderBy(e => e.ImageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reading MainModule needs the same access level as the target process; it throws for
    /// protected and cross-bitness processes, which is expected rather than exceptional here.
    /// </summary>
    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string SafeImageName(string path)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(path);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static Process[] SafeGetProcessesByName(string imageName)
    {
        try
        {
            return Process.GetProcessesByName(imageName);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }
}
