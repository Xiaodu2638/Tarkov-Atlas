using Avalonia;
using TarkovAtlas.Services;

namespace TarkovAtlas;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) AppPaths.LogCrash(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppPaths.LogCrash(e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // PerMonitorV2 (set in app.manifest) keeps the UI sharp on
                // 125%/150%/200% displays; forcing Unaware made everything blurry.
                DpiAwareness = Win32DpiAwareness.PerMonitorDpiAware,
            })
            .LogToTrace();
}
