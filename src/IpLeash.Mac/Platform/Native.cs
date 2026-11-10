using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace IpLeash.Platform;

/// <summary>
/// The few libSystem calls the macOS build needs: the process table (libproc) and signals.
///
/// Called directly rather than through <see cref="System.Diagnostics.Process"/>, which on macOS
/// cannot report a parent PID, a run state, or an owner — all three of which decide whether a
/// process may be frozen.
/// </summary>
internal static partial class Native
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    /// <summary>PROC_PIDPATHINFO_MAXSIZE: 4 * MAXPATHLEN.</summary>
    private const int MaxPathSize = 4096;

    /// <summary>PROC_PIDTBSDINFO, which fills a struct proc_bsdinfo.</summary>
    private const int ProcPidTBsdInfo = 3;

    /// <summary>sizeof(struct proc_bsdinfo).</summary>
    private const int BsdInfoSize = 136;

    public const int SIGKILL = 9;
    public const int SIGSTOP = 17;
    public const int SIGCONT = 19;

    /// <summary>pbi_status value for a stopped process (SSTOP in sys/proc.h).</summary>
    public const uint StatusStopped = 4;

    /// <summary>pbi_status value for a zombie (SZOMB). A zombie holds no sockets and is skipped.</summary>
    public const uint StatusZombie = 5;

    [LibraryImport(LibSystem, SetLastError = true)]
    private static partial int proc_listallpids(int[]? buffer, int bufferSize);

    [LibraryImport(LibSystem, SetLastError = true)]
    private static unsafe partial int proc_pidpath(int pid, byte* buffer, uint bufferSize);

    [LibraryImport(LibSystem, SetLastError = true)]
    private static unsafe partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int bufferSize);

    [LibraryImport(LibSystem, EntryPoint = "kill", SetLastError = true)]
    private static partial int SysKill(int pid, int signal);

    [LibraryImport(LibSystem, EntryPoint = "getuid")]
    public static partial uint GetUid();

    [LibraryImport(LibSystem, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SysRealPath(string path, nint resolved);

    [LibraryImport(LibSystem, EntryPoint = "free")]
    private static partial void Free(nint pointer);

    /// <summary>Every PID currently in the process table.</summary>
    public static int[] ListAllPids()
    {
        // The table can grow between the sizing call and the fill, so leave generous headroom and
        // retry if the answer exactly fills the buffer.
        var capacity = Math.Max(proc_listallpids(null, 0), 256) + 256;

        while (true)
        {
            var buffer = new int[capacity];
            var count = proc_listallpids(buffer, buffer.Length * sizeof(int));
            if (count <= 0)
            {
                return [];
            }

            if (count < buffer.Length)
            {
                return buffer.AsSpan(0, count).ToArray();
            }

            capacity *= 2;
        }
    }

    /// <summary>The resolved image path of <paramref name="pid"/>, or null when it cannot be read.</summary>
    public static unsafe string? GetPath(int pid)
    {
        // Stack buffers: the enforcement sweep reads every process several times a second, and a
        // heap buffer per call would be pure garbage-collector churn.
        Span<byte> buffer = stackalloc byte[MaxPathSize];
        int length;
        fixed (byte* pointer = buffer)
        {
            length = proc_pidpath(pid, pointer, (uint)buffer.Length);
        }

        return length > 0 ? Encoding.UTF8.GetString(buffer[..length]) : null;
    }

    /// <summary>Parent, owner, run state and start time of <paramref name="pid"/>; null if it has exited.</summary>
    public static unsafe BsdInfo? GetBsdInfo(int pid)
    {
        Span<byte> span = stackalloc byte[BsdInfoSize];
        int filled;
        fixed (byte* pointer = span)
        {
            filled = proc_pidinfo(pid, ProcPidTBsdInfo, 0, pointer, BsdInfoSize);
        }

        if (filled != BsdInfoSize)
        {
            return null;
        }

        // Offsets follow struct proc_bsdinfo in sys/proc_info.h.
        var status = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var parent = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        var comm = ReadCString(span.Slice(48, 16));
        var startSeconds = BinaryPrimitives.ReadUInt64LittleEndian(span[120..]);
        var startMicros = BinaryPrimitives.ReadUInt64LittleEndian(span[128..]);

        return new BsdInfo(
            (int)parent,
            uid,
            status,
            comm,
            (long)(startSeconds * 1_000_000 + startMicros));
    }

    /// <summary>Sends <paramref name="signal"/>. Returns 0 on success, otherwise the errno.</summary>
    public static int Kill(int pid, int signal) =>
        SysKill(pid, signal) == 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>
    /// realpath(3): every symlink in <paramref name="path"/> resolved. Null when the path does not
    /// exist. This is the form proc_pidpath reports, so it is the form targets are compared in.
    /// </summary>
    public static string? RealPath(string path)
    {
        var resolved = SysRealPath(path, 0);
        if (resolved == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }
}

/// <param name="ParentPid">PID of the parent; 1 once the parent has gone and launchd adopted it.</param>
/// <param name="Uid">Effective owner. Only processes owned by this user can be signalled.</param>
/// <param name="Status">pbi_status: see <see cref="Native.StatusStopped"/>.</param>
/// <param name="Command">The kernel's short command name, for processes whose path is unreadable.</param>
/// <param name="StartMicros">
/// Start time in microseconds since the epoch. With the PID it identifies one process for its
/// whole life, which is what keeps a recycled PID from being frozen or resumed by mistake.
/// </param>
internal sealed record BsdInfo(int ParentPid, uint Uid, uint Status, string Command, long StartMicros);
