using IpLeash.Services;

namespace IpLeash.Platform;

/// <summary>
/// Decides whether a running process belongs to a monitored target.
///
/// A target is either one Mach-O executable or a whole <c>.app</c> bundle. The bundle form is
/// the normal one on macOS: an app like Claude Desktop or a browser runs as a main process plus
/// helper processes that live in their own executables inside the bundle, and every one of them
/// can open sockets. Matching by bundle prefix covers all of them with one list entry.
///
/// Comparison happens on fully resolved paths, because that is what the kernel reports. A target
/// stored as a symlink (~/.local/bin/claude, pointing into a versioned directory) is re-resolved
/// on every match, so it keeps matching after the symlink is moved to a new version.
/// </summary>
internal sealed class TargetMatcher
{
    private readonly string _resolved;
    private readonly string _bundlePrefix;

    private TargetMatcher(string target, string resolved, bool isBundle)
    {
        Target = target;
        _resolved = resolved;
        IsBundle = isBundle;
        _bundlePrefix = resolved.TrimEnd('/') + "/";
    }

    /// <summary>The path exactly as it appears in the settings file.</summary>
    public string Target { get; }

    public bool IsBundle { get; }

    public static TargetMatcher For(string target)
    {
        var resolved = Native.RealPath(target) ?? ExecutableFile.Normalize(target);
        return new TargetMatcher(target, resolved, ExecutableFile.IsAppBundlePath(resolved));
    }

    public bool Matches(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        // Case-insensitive because the default APFS volume is; the Windows build compares the
        // same way for the same reason.
        return IsBundle
            ? processPath.StartsWith(_bundlePrefix, StringComparison.OrdinalIgnoreCase)
            : string.Equals(processPath, _resolved, StringComparison.OrdinalIgnoreCase);
    }
}
