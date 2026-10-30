using System.Globalization;
using System.Text.RegularExpressions;
using TarkovAtlas.Models;

namespace TarkovAtlas.Services;

/// <summary>
/// Import de-duplication keyed on a per-capture identity token
/// (path + creation time). The game screenshot name carries the coordinates
/// and has only minute precision, so identity must distinguish a later file
/// that reuses the same name. Creation time is fixed at file creation —
/// unlike FileTime it does not churn while the game streams the PNG, so one
/// capture emits exactly once and there is no write-stability game at all.
/// </summary>
public sealed class ScreenshotImportLedger
{
    private readonly Dictionary<string, string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Has this capture instance not been imported yet? A screenshot
    /// that is overwritten/recreated under the same name has a new token.</summary>
    public bool ShouldImport(ScreenshotInfo info)
    {
        lock (_gate)
        {
            return !_seen.TryGetValue(info.FilePath, out var seen) || seen != info.ImportToken;
        }
    }

    public void MarkImported(ScreenshotInfo info)
    {
        lock (_gate) _seen[info.FilePath] = info.ImportToken;
    }

    /// <summary>Entries never seen under their identity, oldest capture first.
    /// Paths that vanished (we deleted after import, or the user cleaned up)
    /// are forgotten, so a name reused by the game counts as fresh.</summary>
    public List<ScreenshotInfo> Diff(IEnumerable<ScreenshotInfo> current)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fresh = new List<ScreenshotInfo>();
            foreach (var c in current)
            {
                next[c.FilePath] = c.ImportToken;
                if (!_seen.TryGetValue(c.FilePath, out var seen) || seen != c.ImportToken)
                    fresh.Add(c);
            }
            _seen.Clear();
            foreach (var kv in next) _seen[kv.Key] = kv.Value;
            fresh.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
            return fresh;
        }
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

    private Timer? _poll;
    private Action<ScreenshotInfo>? _onNew;
    private string? _directory;
    private readonly ScreenshotImportLedger _emitted = new();

    /// <summary>How often the watched folder is re-listed. The screenshot NAME
    /// carries every coordinate and exists the moment the game creates the
    /// file, so there is nothing to wait for beyond noticing it: 600 ms is
    /// imperceptible and cheap (one directory listing, no file reads).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);

    /// <summary>Grace period before an imported screenshot is deleted (the
    /// game may still hold the file open right after creating it).</summary>
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
                CreatedUtc = File.GetCreationTimeUtc(path),
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

    /// <summary>Keep only the latest capture per map: the pin answers "where
    /// am I now", and an accumulating trail clutters the mini-map. Asserted
    /// by shotcheck so the replace-on-write semantics cannot regress.</summary>
    public static void RememberLatest(Dictionary<string, List<ScreenshotInfo>> shots, ScreenshotInfo info)
        => shots[info.MapKey] = new List<ScreenshotInfo> { info };

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
        _directory = directory;
        // Seed the ledger with what is already on disk: importing the whole
        // backlog on every (re)start would flood the map with old pins.
        lock (_emitted)
        {
            _emitted.Diff(Scan(directory));
        }
        _poll = new Timer(_ => PollOnce(), null, PollInterval, PollInterval);
    }

    private void PollOnce()
    {
        var dir = _directory;
        var cb = _onNew;
        if (dir is null || cb is null) return;
        try
        {
            List<ScreenshotInfo> fresh;
            lock (_emitted)
            {
                // Newest 64 files only: a live capture is always the newest,
                // and the ledger diff is per-path, so an older backlog entry
                // that never mattered cannot displace one.
                fresh = _emitted.Diff(Scan(dir, limit: 64));
            }
            foreach (var info in fresh) cb(info);
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    public void StopWatching()
    {
        var t = _poll;
        _poll = null;
        _directory = null;
        try { t?.Dispose(); } catch { }
    }

    public void Dispose() => StopWatching();
}
