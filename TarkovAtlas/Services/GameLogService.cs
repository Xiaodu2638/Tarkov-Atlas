using System.Text;
using System.Text.RegularExpressions;
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
    private static readonly Regex LocationRegex = new(
        @"(?:Load location|Loading location|location\s*[:=])\s*['""]?([A-Za-z0-9_\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
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

        var latest = new DirectoryInfo(dir)
            .EnumerateFiles("application*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is null)
        {
            Status = "日志目录存在，但还没有 application*.log";
            return;
        }

        if (!string.Equals(_logFile, latest.FullName, StringComparison.OrdinalIgnoreCase))
        {
            _logFile = latest.FullName;
            _position = 0;
            _tail = "";
            Status = "已连接 " + latest.Name;
        }

        string text;
        using (var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (fs.Length < _position) _position = 0;
            if (_position == 0 && fs.Length > 262144)
                _position = Math.Max(0, fs.Length - 262144);

            fs.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            text = reader.ReadToEnd();
            _position = fs.Position;
        }

        if (text.Length == 0) return;
        _tail = (_tail + text);
        if (_tail.Length > 65536) _tail = _tail[^65536..];

        var matches = LocationRegex.Matches(_tail);
        if (matches.Count == 0) return;

        // Process only the newest location line. The regex may also match
        // unrelated "location" JSON fields, so the map id must be known.
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var id = matches[i].Groups[1].Value.ToLowerInvariant();
            if (!MapRepository.LocationToMap.TryGetValue(id, out var key)) continue;
            if (string.Equals(id, LastLocationId, StringComparison.OrdinalIgnoreCase)) return;

            LastLocationId = id;
            LastMapKey = key;
            LastDetectedAt = DateTime.Now;
            Status = $"战局地图: {id}";
            MapDetected?.Invoke(this, new MapDetectedEventArgs
            {
                LocationId = id,
                MapKey = key,
                Line = matches[i].Value,
            });
            return;
        }
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