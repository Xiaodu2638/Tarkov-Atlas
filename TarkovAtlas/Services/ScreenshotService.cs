using System.Globalization;
using System.Text.RegularExpressions;
using TarkovAtlas.Models;

namespace TarkovAtlas.Services;

/// <summary>
/// Import de-duplication keyed by path + write time. The game screenshot name
/// only has minute precision, and with delete-after-import the SAME name
/// returns later as a brand-new capture, so a newer FileTime must import
/// again; the same FileTime (e.g. a second watcher event for one write) is
/// skipped.
/// </summary>
public sealed class ScreenshotImportLedger
{
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldImport(string path, DateTime fileTime) =>
        !_seen.TryGetValue(path, out var seen) || seen != fileTime;

    public void MarkImported(string path, DateTime fileTime) => _seen[path] = fileTime;
}

/// <summary>Test seam: expose the private stability wait to the check tools.</summary>
public static class ScreenshotService_TestHook
{
    public static Task<bool> WaitUntilStableForTest(string path)
    {
        // Invoke the private static via a tiny reflection shim so the service
        // keeps its surface clean.
        var m = typeof(ScreenshotService)
            .GetMethod("WaitUntilStableAsync",
                       System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (Task<bool>?)m?.Invoke(null, new object[] { path })
            ?? Task.FromResult(false);
    }
}

public sealed class ScreenshotService : IDisposable
{
    private static readonly Regex StrictPattern = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\[(?<time>\d{2}-\d{2})\]_" +
        @"(?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?),\s*(?<z>-?\d+(?:\.\d+)?)_" +
        @"(?<qx>-?\d+(?:\.\d+)?),\s*(?<qy>-?\d+(?:\.\d+)?),\s*" +
        @"(?<qz>-?\d+(?:\.\d+)?),\s*(?<qw>-?\d+(?:\.\d+)?)_" +
        @"(?<ingame>-?\d+(?:\.\d+)?)\s*(?:\(\d+\))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LoosePattern = new(
        @"(?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?),\s*(?<z>-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private FileSystemWatcher? _watcher;
    private Action<ScreenshotInfo>? _onNew;
    private readonly object _gate = new();

    // The game streams a multi-MB PNG in stretches that can pause over a
    // second, and fires watcher events all the way. Waiting for this much
    // quiet before parsing keeps half-written files out; the debounce loop
    // re-checks on every new event, so a longer write just re-runs the wait.
    internal static readonly TimeSpan StableWindow = TimeSpan.FromMilliseconds(1200);
    internal static readonly TimeSpan StableTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Grace period before an imported screenshot is deleted. Deleting
    /// right away raced the game's own write handle and let the next capture
    /// reuse the same name, which then looked like a lost screenshot.</summary>
    public static readonly TimeSpan DeleteGrace = TimeSpan.FromSeconds(3);

    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    public ScreenshotInfo? Parse(string path, bool strictOnly = false)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var match = StrictPattern.Match(name);
            double x, y, z, qx = 0, qy = 0, qz = 0, qw = 1;
            DateTime captured = default;

            if (match.Success)
            {
                x = Num(match.Groups["x"].Value);
                y = Num(match.Groups["y"].Value);
                z = Num(match.Groups["z"].Value);
                qx = Num(match.Groups["qx"].Value);
                qy = Num(match.Groups["qy"].Value);
                qz = Num(match.Groups["qz"].Value);
                qw = Num(match.Groups["qw"].Value);
                if (DateTime.TryParseExact(
                        match.Groups["date"].Value + " " + match.Groups["time"].Value.Replace('-', ':'),
                        "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                    captured = dt;
            }
            else
            {
                // A loose match on a hand-renamed file is fine for a one-off
                // import, but never for the folder scan: "IMG_1,2,3.png" is not
                // a screenshot and must not become a pin (or be deleted).
                if (strictOnly) return null;
                var loose = LoosePattern.Match(name);
                if (!loose.Success) return null;
                x = Num(loose.Groups["x"].Value);
                y = Num(loose.Groups["y"].Value);
                z = Num(loose.Groups["z"].Value);
            }

            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return null;

            var info = new ScreenshotInfo
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                X = x,
                Y = y,
                Z = z,
                Qx = qx,
                Qy = qy,
                Qz = qz,
                Qw = qw,
                CapturedAt = captured,
                FileTime = File.GetLastWriteTime(path),
            };
            info.HeadingDegrees = ComputeHeading(qx, qy, qz, qw);
            return info;
        }
        catch
        {
            return null;
        }
    }

    private static double ComputeHeading(double qx, double qy, double qz, double qw)
    {
        var len = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        if (len < 1e-8) return 0;
        qx /= len; qy /= len; qz /= len; qw /= len;

        // Unity forward (0,0,1) rotated by the quaternion; XZ components.
        var fx = 2 * (qx * qz + qw * qy);
        var fz = 1 - 2 * (qx * qx + qy * qy);
        var deg = Math.Atan2(fx, fz) * 180.0 / Math.PI;
        return (deg + 360) % 360;
    }

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public List<ScreenshotInfo> Scan(string directory, int limit = 3000)
    {
        var result = new List<ScreenshotInfo>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return result;

        try
        {
            var files = new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(limit);

            foreach (var file in files)
            {
                var info = Parse(file.FullName, strictOnly: true);
                if (info is not null) result.Add(info);
            }
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
        return result;
    }

    /// <summary>
    /// Permanently delete a screenshot once its coordinates were imported, so the
    /// folder does not fill with junk. The game re-screenshots in seconds, so a
    /// recycled copy only clutters the Recycle Bin; the coordinate pin already
    /// lives in our own state. Opt-in only (the settings default is off) and the
    /// file must carry the exact game-format name, because anything else in that
    /// folder belongs to the user.
    /// </summary>
    public bool TryDeleteAfterImport(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (!StrictPattern.IsMatch(Path.GetFileNameWithoutExtension(path))) return false;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
            return false;
        }
    }

    public void StartWatching(string directory, Action<ScreenshotInfo> onNew)
    {
        StopWatching();
        _onNew = onNew;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        try
        {
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnFile;
            _watcher.Changed += OnFile;
            _watcher.Renamed += OnRenamed;
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e) => OnFile(sender, e);

    private void OnFile(object sender, FileSystemEventArgs e)
    {
        if (!Extensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase)) return;

        // The game writes a multi-MB PNG in stretches that can pause for more
        // than a second, and it keeps ONE watcher thread busy with successive
        // events. Instead of racing it per-event, coalesce: remember the path
        // and let a single debouncer pick it up once the write has settled.
        var path = e.FullPath;
        lock (_gate)
        {
            _pending[path] = DateTime.UtcNow;
        }
        _debounce ??= Task.Run(DebounceLoop);
    }

    private readonly Dictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);

    private Task? _debounce;

    /// <summary>Single consumer: wait for every pending path's write to settle
    /// (no size/mtime change for <see cref="StableWindow"/>, 8 s cap), then
    /// hand it over once. Coalesces the event storm Unity produces.</summary>
    private async Task DebounceLoop()
    {
        while (true)
        {
            string[] batch;
            lock (_gate)
            {
                if (_pending.Count == 0) { _debounce = null; return; }
                batch = _pending.Keys.ToArray();
            }

            foreach (var path in batch)
            {
                if (!await WaitUntilStableAsync(path)) continue;
                ScreenshotInfo? info = null;
                try { info = Parse(path, strictOnly: true); }
                catch { /* gone mid-parse: drop, next event re-adds */ }
                if (info is not null) _onNew?.Invoke(info);
                lock (_gate) _pending.Remove(path);
            }

            await Task.Delay(200);
        }
    }

    /// <summary>Poll size + LastWriteTime until both stop changing for
    /// <see cref="StableWindow"/>. False on timeout or if the file vanished
    /// (the game can rename or clean it up). Every new watcher event for the
    /// path resets the wait, so an ongoing write is never reported stable.</summary>
    private static async Task<bool> WaitUntilStableAsync(string path)
    {
        var deadline = DateTime.UtcNow + StableTimeout;
        var lastSize = -1L;
        var lastWrite = DateTime.MinValue;
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
                fi.Refresh();
            }
            catch (IOException)
            {
                return false;
            }
            if (!fi.Exists) return false;

            var size = fi.Length;
            var write = fi.LastWriteTimeUtc;
            if (size != lastSize || write != lastWrite)
            {
                lastSize = size;
                lastWrite = write;
                stableSince = DateTime.UtcNow;
            }
            else if (size > 0 && DateTime.UtcNow - stableSince >= StableWindow)
            {
                return true;
            }

            await Task.Delay(200);
        }
        return false;
    }

    public void StopWatching()
    {
        if (_watcher is null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFile;
            _watcher.Changed -= OnFile;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
        }
        catch { }
        _watcher = null;
    }

    public void Dispose() => StopWatching();
}
