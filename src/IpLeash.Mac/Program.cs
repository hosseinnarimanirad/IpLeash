using Avalonia;

namespace IpLeash;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

    // Also used by the Avalonia previewer; keep it free of side effects.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions
            {
                // The app supplies its own application menu (About IpLeash), not Avalonia's.
                DisableDefaultApplicationMenuItems = true,
            })
            .LogToTrace();
}
