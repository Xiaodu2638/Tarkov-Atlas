using System.Text;
using Microsoft.Win32;

namespace TarkovAtlas.Services;

public sealed class MapDetectedEventArgs : EventArgs
{
    public string LocationId { get; init; } = "";
    public string MapKey { get; init; } = "";
    public string Line { get; init; } = "";
}

public sealed class GameLogService : IDisposable
{
    private CancellationTokenSource? _cts;
    private string? _logFile;
    private long _position;
    private string _tail = "";

    public string? LogsDirectory { get; private set; }
    public string? LastLocationId { get; private set; }
    public string? LastMapKey { get; private set; }
    public DateTime LastDetectedAt { get; private set; }
    public string Status { get; private set; } = "日志监听未启动";

    public event EventHandler<MapDetectedEventArgs>? MapDetected;

    public static string? FindGameInstall()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov");
            var uninstall = key?.GetValue("UninstallString") as string;
            if (!string.IsNullOrWhiteSpace(uninstall))
            {
                var dir = Path.GetDirectoryName(uninstall.Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
            }
        }
        catch { }

        foreach (var candidate in new[]
                 {
                     @"C:\Battlestate Games\EFT",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battlestate Games", "EFT"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battlestate Games", "EFT"),
                 })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static IEnumerable<string> CandidateLogDirectories()
    {
        var install = FindGameInstall();
        if (install is not null)
        {
            yield return Path.Combine(install, "Logs");
            yield return Path.Combine(install, "logs");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Temp", "Battlestate Games", "EscapeFromTarkov", "Logs");
        yield return Path.Combine(local, "Battlestate Games", "EscapeFromTarkov", "Logs");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Escape from Tarkov", "Logs");
    }

    public static string? ResolveLogDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        return CandidateLogDirectories().FirstOrDefault(Directory.Exists);
    }

    public void Start(string? directory)
    {
        Stop();
        LogsDirectory = ResolveLogDirectory(directory);
        if (LogsDirectory is null)
        {
            Status = "未找到日志目录，请在设置中指定";
            return;
        }

        _cts = new CancellationTokenSource();
        Status = "正在监听 " + LogsDirectory;
        _ = Task.Run(() => PollLoop(_cts.Token));
    }

    private async Task PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                ReadLatest();
            }
            catch (Exception ex)
            {
                AppPaths.LogCrash(ex);
            }

            try { await Task.Delay(1500, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ReadLatest()
    {
        var dir = LogsDirectory;
        if (dir is null || !Directory.Exists(dir)) return;

        // 1.3.0.0 writes one session directory per game launch
        // (Logs/log_<stamp>_<ver>/<stamp> application_000.log); pre-1.3
        // wrote flat application*.log. FindApplicationLog understands both.
        var latestPath = GameLogParsing.FindApplicationLog(dir);
        if (latestPath is null)
        {
            Status = "日志目录存在，但还没有 application*.log";
            return;
        }
        var latest = new FileInfo(latestPath);

        if (!string.Equals(_logFile, latest.FullName, StringComparison.OrdinalIgnoreCase))
        {
            _logFile = latest.FullName;
            _position = 0;
            _tail = "";
            Status = "已连接 " + latest.Name;
        }

        string text;
        try
        {
            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _position) _position = 0;
            if (_position == 0 && fs.Length > 262144)
                _position = Math.Max(0, fs.Length - 262144);

            fs.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            text = reader.ReadToEnd();
            _position = fs.Position;
        }
        catch (IOException)
        {
            // Session directories linger after crashes; a locked or half
            // deleted file just means we retry on the next poll.
            return;
        }

        if (text.Length == 0) return;
        _tail = (_tail + text);
        if (_tail.Length > 65536) _tail = _tail[^65536..];

        // Newest raid marker for a known map; same-as-last is filtered below.
        var id = GameLogParsing.NewestRaidLocation(_tail, MapRepository.LocationToMap.ContainsKey);
        if (id is null) return;        if (string.Equals(id, LastLocationId, StringComparison.OrdinalIgnoreCase)) return;

        if (!MapRepository.LocationToMap.TryGetValue(id, out var mapKey)) return;
        LastLocationId = id;
        LastMapKey = mapKey;
        LastDetectedAt = DateTime.Now;
        Status = $"战局地图: {id}";
        MapDetected?.Invoke(this, new MapDetectedEventArgs
        {
            LocationId = id,
            MapKey = mapKey,
            Line = id,
        });
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _logFile = null;
        _position = 0;
        _tail = "";
        Status = "日志监听已停止";
    }

    public void Dispose() => Stop();
}