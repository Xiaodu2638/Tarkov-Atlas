using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using TarkovAtlas.Services;

namespace TarkovAtlas;

internal static class Program
{
    private const string MutexName = "TarkovAtlas_SingleInstance";
    private const string WindowTitle = "Tarkov Atlas";
    private static Mutex? _mutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Single instance: a second launch just activates the existing window.
        bool createdNew;
        _mutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            return;
        }

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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// A second copy of the exe was started: bring the running window to the
    /// front instead of opening a second one. Best effort — if the window
    /// cannot be found (different desktop, elevated process) we exit silently.
    /// </summary>
    private static void ActivateExistingWindow()
    {
        try
        {
            var hWnd = FindWindow(null, WindowTitle);
            if (hWnd == IntPtr.Zero) return;
            if (IsIconic(hWnd)) ShowWindowAsync(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
        catch
        {
            // Never crash the second instance over focus politics.
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
