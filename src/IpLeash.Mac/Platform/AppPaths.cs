using System.IO;

namespace IpLeash.Services;

/// <summary>
/// Where IpLeash keeps its files on macOS: ~/Library/Application Support/IpLeash. Same file
/// names as the Windows build, so settings and the crash-recovery records behave identically.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Application Support",
        "IpLeash");
}
