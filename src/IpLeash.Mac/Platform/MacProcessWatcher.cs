using System.IO;
using IpLeash.Models;
using IpLeash.Services;

namespace IpLeash.Platform;

/// <summary>
/// Polls the process table through libproc.
///
/// Matching uses the same <see cref="TargetMatcher"/> as enforcement, so the PIDs shown beside a
/// target are exactly the processes a block would freeze (minus their children).
/// </summary>
public sealed class MacProcessWatcher : IProcessWatcher
{
    private readonly uint _selfUid = Native.GetUid();

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

        var table = ProcessTable.Capture();

        foreach (var path in requested)
        {
            var matcher = TargetMatcher.For(path);
            result[path] = table.Entries
                .Where(e => !e.IsZombie && matcher.Matches(e.Path))
                .Select(e => e.Pid)
                .Order()
                .ToList();
        }

        return result;
    }

    /// <summary>
    /// One row per app bundle or standalone executable. Grouping by outermost bundle is what turns
    /// "Claude", "Claude Helper", "Claude Helper (GPU)" and "Claude Helper (Renderer)" into the one
    /// line the user means.
    ///
    /// Processes that cannot be a target — unreadable, owned by another user, or part of macOS
    /// itself — are keyed by name alone. The picker shows those greyed out rather than hiding them,
    /// because a process that is simply missing from the list looks like a bug.
    /// </summary>
    public IReadOnlyList<RunningExecutable> GetRunningExecutables()
    {
        var byKey = new Dictionary<string, (string ImageName, List<int> Pids)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ProcessTable.Capture().Entries)
        {
            if (entry.IsZombie || entry.Pid == Environment.ProcessId)
            {
                continue;
            }

            string key;
            string imageName;

            if (entry.Path is { } path)
            {
                var bundle = ExecutableFile.OutermostBundle(path);
                imageName = bundle is not null
                    ? Path.GetFileNameWithoutExtension(bundle)
                    : Path.GetFileName(path);

                var blockable = entry.Uid == _selfUid && !ExecutableFile.IsProtectedSystemPath(path);
                key = blockable ? bundle ?? path : imageName;
            }
            else
            {
                imageName = entry.Command;
                key = entry.Command;
            }

            if (!byKey.TryGetValue(key, out var group))
            {
                group = (imageName, []);
                byKey[key] = group;
            }

            group.Pids.Add(entry.Pid);
        }

        return byKey
            .Select(kv => new RunningExecutable(kv.Key, kv.Value.ImageName, kv.Value.Pids.Order().ToList()))
            .OrderBy(e => e.ImageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
