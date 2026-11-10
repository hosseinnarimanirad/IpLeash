using System.IO;
using IpLeash.Models;
using IpLeash.Services;

namespace IpLeash.Platform;

/// <summary>
/// Locates Claude Code and Claude Desktop wherever they are installed on this Mac.
///
/// Stable paths are preferred over the ones a running process reports. The native Claude Code
/// install runs from a versioned directory (~/.local/share/claude/versions/2.1.x) behind a
/// symlink (~/.local/bin/claude); the versioned path goes stale at the next update, the symlink
/// does not, and both resolve to the same file. Candidates are therefore deduplicated by resolved
/// path, with the symlink locations listed first so they are the ones kept.
/// </summary>
public sealed class MacAppDiscoveryService : IAppDiscoveryService
{
    private const string NpmPackageRelativePath = "node_modules/@anthropic-ai/claude-code/bin/claude";

    private readonly IProcessWatcher _processWatcher;

    public MacAppDiscoveryService(IProcessWatcher processWatcher) => _processWatcher = processWatcher;

    public IReadOnlyList<DiscoveredApp> DiscoverKnownApps()
    {
        var results = new List<DiscoveredApp>();
        var running = _processWatcher.GetRunningExecutables();

        var claudeCode = Collect(EnumerateClaudeCodeCandidates(running));
        if (claudeCode.Count > 0)
        {
            results.Add(new DiscoveredApp(
                "Claude Code",
                claudeCode.Select(c => c.Path).ToList(),
                claudeCode.Select(c => c.Source).Distinct().ToList()));
        }

        var claudeDesktop = Collect(EnumerateClaudeDesktopCandidates(running));
        if (claudeDesktop.Count > 0)
        {
            results.Add(new DiscoveredApp(
                "Claude Desktop",
                claudeDesktop.Select(c => c.Path).ToList(),
                claudeDesktop.Select(c => c.Source).Distinct().ToList()));
        }

        return results;
    }

    /// <summary>Screens candidates and keeps the first path found for each distinct file.</summary>
    private static List<(string Path, string Source)> Collect(IEnumerable<(string Path, string Source)> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(string Path, string Source)>();

        foreach (var (path, source) in candidates)
        {
            if (string.IsNullOrWhiteSpace(path) || !ExecutableFile.IsUsable(path))
            {
                continue;
            }

            var identity = Native.RealPath(path) ?? ExecutableFile.Normalize(path);
            if (seen.Add(identity))
            {
                found.Add((ExecutableFile.Normalize(path), source));
            }
        }

        return found;
    }

    private static IEnumerable<(string Path, string Source)> EnumerateClaudeCodeCandidates(
        IReadOnlyList<RunningExecutable> running)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return (Path.Combine(home, ".local", "bin", "claude"), "native install (~/.local/bin)");
            yield return (Path.Combine(home, ".claude", "local", "claude"), "local install (~/.claude/local)");
            yield return (Path.Combine(home, ".claude", "local", NpmPackageRelativePath), "local install (~/.claude/local)");
        }

        // Homebrew (Apple silicon, then Intel), then the npm global prefixes beside them.
        foreach (var prefix in new[] { "/opt/homebrew", "/usr/local" })
        {
            yield return (Path.Combine(prefix, "bin", "claude"), "Homebrew / npm global");
            yield return (Path.Combine(prefix, "lib", NpmPackageRelativePath), "npm global prefix");
        }

        foreach (var directory in PathDirectories())
        {
            yield return (Path.Combine(directory, "claude"), "PATH");
        }

        // Last, so a stable path to the same file wins; still useful for an install in a place
        // nothing above knows about.
        foreach (var process in running)
        {
            if (IsClaudeCodePath(process.Path))
            {
                yield return (process.Path, "running process");
            }
        }
    }

    private static IEnumerable<(string Path, string Source)> EnumerateClaudeDesktopCandidates(
        IReadOnlyList<RunningExecutable> running)
    {
        yield return ("/Applications/Claude.app", "Applications folder");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return (Path.Combine(home, "Applications", "Claude.app"), "per-user Applications folder");
        }

        foreach (var process in running)
        {
            if (IsClaudeDesktopPath(process.Path))
            {
                yield return (process.Path, "running process");
            }
        }
    }

    private static bool IsClaudeCodePath(string path) =>
        !IsClaudeDesktopPath(path)
        && (path.Contains("/claude/versions/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("claude-code", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/claude", StringComparison.OrdinalIgnoreCase));

    private static bool IsClaudeDesktopPath(string path) =>
        path.EndsWith("/Claude.app", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Claude.app/", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> PathDirectories()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            yield break;
        }

        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length > 0 && Path.IsPathRooted(trimmed))
            {
                yield return trimmed;
            }
        }
    }
}
