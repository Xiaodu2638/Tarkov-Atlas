using System.Text.Json;
using TarkovAtlas.Models;

namespace TarkovAtlas.Services;

public static class AppPaths
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string AssetsDir => Path.Combine(BaseDir, "Assets");
    public static string MapsDir => Path.Combine(AssetsDir, "Maps");
    public static string ScriptsDir => Path.Combine(BaseDir, "Scripts");
    private static string? _dataDir;

    public static string DataDir
    {
        get
        {
            if (_dataDir is not null) return _dataDir;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TarkovAtlas");
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                _dataDir = dir;
            }
            catch
            {
                var fallback = Path.Combine(BaseDir, "data");
                Directory.CreateDirectory(fallback);
                _dataDir = fallback;
            }
            return _dataDir;
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string QuestProgressFile => Path.Combine(DataDir, "quest-progress.json");
    public static string CrashLog => Path.Combine(DataDir, "crash.log");

    public static string DefaultScreenshotsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Escape from Tarkov", "Screenshots");

    public static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(CrashLog,
                $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n{ex}\n");
        }
        catch
        {
            // Never let diagnostics crash the app.
        }
    }
}

public sealed class SettingsService
{
    private readonly object _gate = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
                File.WriteAllText(AppPaths.SettingsFile, json);
            }
            catch (Exception ex)
            {
                AppPaths.LogCrash(ex);
            }
        }
    }
}
