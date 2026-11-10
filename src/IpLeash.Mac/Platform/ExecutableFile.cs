using System.IO;
using IpLeash.Platform;

namespace IpLeash.Services;

/// <summary>
/// Checks that a path is something IpLeash can actually enforce on — the macOS counterpart of
/// the Windows build's MZ check, with the same API so the shared engine and ViewModel use it
/// unchanged.
///
/// Usable means: an <c>.app</c> bundle, or a Mach-O executable. A shell script, a
/// <c>#!/usr/bin/env node</c> launcher or a missing file is rejected, because the process that
/// actually runs is the interpreter, so a target pointing at the script would match nothing —
/// a silent no-op, the worst outcome for a kill-switch.
///
/// System locations are rejected too. Enforcement freezes processes, and freezing something like
/// WindowServer or loginwindow would lock up the whole session, including IpLeash's own window.
/// </summary>
public static class ExecutableFile
{
    /// <summary>
    /// Sealed system volumes and Apple's own directories. /usr/local is carved out below: it is
    /// where Homebrew and most installers put user-facing tools.
    /// </summary>
    private static readonly string[] ProtectedPrefixes =
    [
        "/System/",
        "/bin/",
        "/sbin/",
        "/usr/",
        "/Library/Apple/",
        "/private/var/db/",
    ];

    private static readonly string[] AllowedUnderProtected =
    [
        "/usr/local/",
    ];

    /// <summary>Mach-O thin (32/64-bit, either byte order) and universal ("fat") magic numbers.</summary>
    private static readonly uint[] MachOMagics =
    [
        0xFEEDFACE, 0xCEFAEDFE,
        0xFEEDFACF, 0xCFFAEDFE,
        0xCAFEBABE, 0xBEBAFECA,
    ];

    /// <summary>True for an existing .app bundle or Mach-O executable outside the system locations.</summary>
    public static bool IsUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathRooted(path))
        {
            return false;
        }

        string? resolved;
        try
        {
            resolved = Native.RealPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        if (resolved is null || IsProtectedSystemPath(resolved) || IsIpLeashItself(resolved))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(resolved))
            {
                return IsAppBundlePath(resolved)
                       && Directory.Exists(Path.Combine(resolved, "Contents", "MacOS"));
            }

            return File.Exists(resolved) && HasMachOHeader(resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool IsAppBundlePath(string path) =>
        path.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The outermost .app bundle a path sits inside, or null. Helper apps nest bundles
    /// (Foo.app/Contents/Frameworks/…/Foo Helper.app), and it is the outer one the user knows.
    /// </summary>
    public static string? OutermostBundle(string path)
    {
        var index = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : path[..(index + ".app".Length)];
    }

    public static bool IsProtectedSystemPath(string resolvedPath)
    {
        if (AllowedUnderProtected.Any(p => resolvedPath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return ProtectedPrefixes.Any(p =>
            resolvedPath.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolvedPath + "/", p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the four-byte Mach-O magic. Catches a script or shim that is merely marked
    /// executable, without reading the rest of what may be a very large file.
    /// </summary>
    public static bool HasMachOHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            if (stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) != 4)
            {
                return false;
            }

            var magic = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            return MachOMagics.Contains(magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable (no rights). Treat as usable: this check is a safety net, not the
            // guarantee, and refusing a valid but unreadable binary would be worse.
            return true;
        }
    }

    /// <summary>Absolute form of <paramref name="path"/>, symlinks left in place so the stored path stays portable.</summary>
    public static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>A target that froze IpLeash would leave nothing able to unfreeze it.</summary>
    private static bool IsIpLeashItself(string resolvedPath)
    {
        var self = Environment.ProcessPath is { } processPath
            ? Native.RealPath(processPath) ?? processPath
            : null;

        if (self is null)
        {
            return false;
        }

        if (string.Equals(self, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsAppBundlePath(resolvedPath)
               && self.StartsWith(resolvedPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }
}
