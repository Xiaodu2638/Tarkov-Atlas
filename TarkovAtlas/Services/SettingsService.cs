using System.Text.Json;
using TarkovAtlas.Models;

namespace TarkovAtlas.Services;

public static class AppPaths
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string AssetsDir => Path.Combine(BaseDir, "Assets");
    public static string MapsDir => Path.Combine(AssetsDir, "maps");

    // Asset file names live here so the loader and the build/check tools agree
    // on one spelling instead of repeating literals in several files.
    public const string MapDataFile = "mapdata.json";
    public const string QuestDataFile = "quests.json";
    public const string StoryDataFile = "story.json";
    public const string HazardsFile = "hazards.json";
    public const string MarkersFile = "markers.json";
    public const string LayersIndexFile = "index.json";

    public static string MapDataPath => Path.Combine(MapsDir, MapDataFile);
    public static string HazardsPath => Path.Combine(MapsDir, HazardsFile);
    public static string MarkersPath => Path.Combine(MapsDir, MarkersFile);
    public static string LayersIndexPath => Path.Combine(MapsDir, "layers", LayersIndexFile);

    private static string? _dataDir;

    public static string DataDir
    {
        get
        {
            if (_dataDir is not null) return _dataDir;
            // Tests and portable runs redirect all mutable state into a local
            // folder so nothing is written to the host profile.
            var overrideDir = Environment.GetEnvironmentVariable("TARKOVATLAS_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                Directory.CreateDirectory(overrideDir);
                _dataDir = overrideDir;
                return _dataDir;
            }
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

    /// <summary>
    /// Write through a temp file and swap, so a kill mid-write cannot leave a
    /// truncated JSON behind. A truncated settings/progress file is silently
    /// discarded on the next start, which loses the player's whole checklist.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
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
                AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, json);
            }
            catch (Exception ex)
            {
                AppPaths.LogCrash(ex);
            }
        }
    }
}
