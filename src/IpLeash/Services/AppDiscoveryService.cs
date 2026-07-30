using System.IO;
using IpLeash.Models;

namespace IpLeash.Services;

/// <inheritdoc cref="IAppDiscoveryService"/>
public sealed class AppDiscoveryService : IAppDiscoveryService
{
    /// <summary>Path fragment that identifies an npm-installed Claude Code.</summary>
    private const string NpmPackageRelativePath = @"node_modules\@anthropic-ai\claude-code\bin\claude.exe";

    private readonly IProcessWatcher _processWatcher;

    public AppDiscoveryService(IProcessWatcher processWatcher) => _processWatcher = processWatcher;

    public IReadOnlyList<DiscoveredApp> DiscoverKnownApps()
    {
        var results = new List<DiscoveredApp>();

        var claudeCode = Collect(EnumerateClaudeCodeCandidates());
        if (claudeCode.Count > 0)
        {
            results.Add(new DiscoveredApp(
                "Claude Code",
                claudeCode.Keys.ToList(),
                claudeCode.Values.Distinct().ToList()));
        }

        var claudeDesktop = Collect(EnumerateClaudeDesktopCandidates());
        if (claudeDesktop.Count > 0)
        {
            results.Add(new DiscoveredApp(
                "Claude Desktop",
                claudeDesktop.Keys.ToList(),
                claudeDesktop.Values.Distinct().ToList()));
        }

        return results;
    }

    /// <summary>
    /// Screens candidates and deduplicates by normalized path, keeping the first source that
    /// found each one.
    /// </summary>
    private static Dictionary<string, string> Collect(IEnumerable<(string Path, string Source)> candidates)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, source) in candidates)
        {
            if (string.IsNullOrWhiteSpace(path) || !ExecutableFile.IsUsable(path))
            {
                continue;
            }

            var normalized = ExecutableFile.Normalize(path);
            if (!found.ContainsKey(normalized))
            {
                found[normalized] = source;
            }
        }

        return found;
    }

    private IEnumerable<(string Path, string Source)> EnumerateClaudeCodeCandidates()
    {
        // A live process is the most reliable evidence there is: it reports the real image path,
        // whatever install method put it there.
        foreach (var running in _processWatcher.GetRunningExecutables())
        {
            if (IsClaudeCodePath(running.Path))
            {
                yield return (running.Path, "running process");
            }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            yield return (Path.Combine(appData, "npm", NpmPackageRelativePath), "npm global prefix");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            yield return (Path.Combine(profile, ".local", "bin", "claude.exe"), "native install (~/.local/bin)");
            yield return (Path.Combine(profile, ".claude", "local", "claude.exe"), "local install (~/.claude/local)");
            yield return (Path.Combine(profile, ".claude", "local", NpmPackageRelativePath), "local install (~/.claude/local)");
        }

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return (Path.Combine(programFiles, "nodejs", NpmPackageRelativePath), "system node install");
            }
        }

        // Any directory on PATH may hold either the executable itself or the npm shim next to
        // the package directory that contains it.
        foreach (var directory in PathDirectories())
        {
            yield return (Path.Combine(directory, "claude.exe"), "PATH");
            yield return (Path.Combine(directory, NpmPackageRelativePath), "PATH (npm shim)");
        }
    }

    private IEnumerable<(string Path, string Source)> EnumerateClaudeDesktopCandidates()
    {
        foreach (var running in _processWatcher.GetRunningExecutables())
        {
            if (IsClaudeDesktopPath(running.Path))
            {
                yield return (running.Path, "running process");
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var anthropic = Path.Combine(localAppData, "AnthropicClaude");
            yield return (Path.Combine(anthropic, "claude.exe"), "per-user install");

            // Squirrel-style installers keep the live build in a versioned app-x.y.z folder.
            foreach (var versioned in SafeEnumerateDirectories(anthropic, "app-*"))
            {
                yield return (Path.Combine(versioned, "claude.exe"), "per-user install (versioned)");
            }

            yield return (Path.Combine(localAppData, "Programs", "Claude", "Claude.exe"), "per-user install");
        }

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return (Path.Combine(programFiles, "Claude", "Claude.exe"), "machine-wide install");
            }
        }
    }

    private static bool IsClaudeCodePath(string path) =>
        path.Contains("claude-code", StringComparison.OrdinalIgnoreCase)
        || (path.EndsWith(@"\claude.exe", StringComparison.OrdinalIgnoreCase) && !IsClaudeDesktopPath(path));

    private static bool IsClaudeDesktopPath(string path) =>
        path.Contains("AnthropicClaude", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Programs\Claude\", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> PathDirectories()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            yield break;
        }

        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim().Trim('"');
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, pattern)
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }
}
