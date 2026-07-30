using System.IO;

namespace IpLeash.Services;

/// <summary>
/// Where IpLeash keeps its files. The app always runs elevated, so this resolves to the
/// administrator account's LocalAppData — consistent across runs, which is what matters for
/// finding the block-state file again after a crash.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IpLeash");
}
