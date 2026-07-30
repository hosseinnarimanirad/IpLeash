using System.IO;

namespace Leash.Services;

/// <summary>
/// Where Leash keeps its files. The app always runs elevated, so this resolves to the
/// administrator account's LocalAppData — consistent across runs, which is what matters for
/// finding the block-state file again after a crash.
/// </summary>
public static class AppPaths
{
    /// <summary>Name this app used before it was called Leash.</summary>
    private const string LegacyFolderName = "NetMonitor";

    static AppPaths()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Leash");

        MigrateLegacyData();
    }

    public static string DataDirectory { get; }

    /// <summary>
    /// Carries settings and, more importantly, the active-block record across the rename.
    ///
    /// Losing settings would be an annoyance. Losing active-block.json would be a correctness
    /// failure: it is the only record of which executables a previous run left blocked, so
    /// without it a rule written under the old name could never be found and removed, and an
    /// application would stay cut off with nothing in the UI admitting it.
    /// </summary>
    private static void MigrateLegacyData()
    {
        try
        {
            if (Directory.Exists(DataDirectory))
            {
                return;
            }

            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LegacyFolderName);

            if (!Directory.Exists(legacy))
            {
                return;
            }

            Directory.CreateDirectory(DataDirectory);

            foreach (var file in Directory.EnumerateFiles(legacy, "*.json"))
            {
                // Copied rather than moved: if anything goes wrong the old folder is still intact.
                File.Copy(file, Path.Combine(DataDirectory, Path.GetFileName(file)), overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting with default settings is survivable; failing to start is not.
        }
    }
}
