using System.IO;

namespace Leash.Services;

/// <summary>
/// Checks that a path is something Windows Firewall can actually bind a rule to.
///
/// This matters more than it looks: netsh happily accepts a program rule pointing at a .cmd
/// launcher or a missing file and reports success, while blocking nothing at all. A silent
/// no-op is the worst outcome for a kill-switch, so candidates are screened here.
/// </summary>
public static class ExecutableFile
{
    /// <summary>True when the path exists, ends in .exe, and starts with the MZ DOS signature.</summary>
    public static bool IsUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"'))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path) ||
                !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return HasMzHeader(path);
    }

    /// <summary>
    /// Reads the two-byte DOS header signature. Catches a script or shim that merely carries an
    /// .exe extension, without reading the rest of what may be a very large file.
    /// </summary>
    public static bool HasMzHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[2];
            return stream.ReadAtLeast(header, 2, throwOnEndOfStream: false) == 2
                   && header[0] == (byte)'M'
                   && header[1] == (byte)'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable (locked, or no rights). Treat as usable: this check is a safety net,
            // not the guarantee, and refusing a locked-but-valid exe would be worse.
            return true;
        }
    }

    /// <summary>Case-insensitive full-path comparison that tolerates unnormalizable input.</summary>
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
}
