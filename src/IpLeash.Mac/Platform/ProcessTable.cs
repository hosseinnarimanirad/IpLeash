namespace IpLeash.Platform;

/// <summary>One live process, as read from the kernel at one instant.</summary>
/// <param name="Path">Resolved image path, or null when the kernel would not report it.</param>
internal sealed record ProcessEntry(
    int Pid,
    int ParentPid,
    uint Uid,
    uint Status,
    string? Path,
    string Command,
    long StartMicros)
{
    public bool IsStopped => Status == Native.StatusStopped;

    public bool IsZombie => Status == Native.StatusZombie;
}

/// <summary>A point-in-time read of the whole process table.</summary>
internal sealed class ProcessTable
{
    private readonly Dictionary<int, ProcessEntry> _byPid;
    private readonly Dictionary<int, List<ProcessEntry>> _childrenByParent;

    private ProcessTable(List<ProcessEntry> entries)
    {
        Entries = entries;
        _byPid = entries.ToDictionary(e => e.Pid);
        _childrenByParent = entries
            .GroupBy(e => e.ParentPid)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public IReadOnlyList<ProcessEntry> Entries { get; }

    public static ProcessTable Capture()
    {
        var entries = new List<ProcessEntry>();

        foreach (var pid in Native.ListAllPids())
        {
            if (pid <= 0)
            {
                continue;
            }

            // Exited between the listing and this read, which is routine.
            if (Native.GetBsdInfo(pid) is not { } info)
            {
                continue;
            }

            entries.Add(new ProcessEntry(
                pid,
                info.ParentPid,
                info.Uid,
                info.Status,
                Native.GetPath(pid),
                info.Command,
                info.StartMicros));
        }

        return new ProcessTable(entries);
    }

    public ProcessEntry? Find(int pid) => _byPid.GetValueOrDefault(pid);

    /// <summary>True when <paramref name="pid"/> is still the same process that started at <paramref name="startMicros"/>.</summary>
    public bool IsSameProcess(int pid, long startMicros) =>
        _byPid.TryGetValue(pid, out var entry) && entry.StartMicros == startMicros;

    /// <summary>Every descendant of <paramref name="pid"/>, parents before children.</summary>
    public IEnumerable<ProcessEntry> DescendantsOf(int pid)
    {
        var queue = new Queue<int>();
        var seen = new HashSet<int> { pid };
        queue.Enqueue(pid);

        while (queue.Count > 0)
        {
            if (!_childrenByParent.TryGetValue(queue.Dequeue(), out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (seen.Add(child.Pid))
                {
                    yield return child;
                    queue.Enqueue(child.Pid);
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="pid"/> and every process above it, up to launchd. Used to keep IpLeash from
    /// freezing the terminal it was started from, which would stall its own output.
    /// </summary>
    public HashSet<int> SelfAndAncestors(int pid)
    {
        var chain = new HashSet<int>();
        var current = pid;

        while (current > 1 && chain.Add(current))
        {
            current = _byPid.TryGetValue(current, out var entry) ? entry.ParentPid : 0;
        }

        return chain;
    }
}
