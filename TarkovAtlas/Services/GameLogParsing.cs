using System.Text.RegularExpressions;

namespace TarkovAtlas.Services;

/// <summary>
/// Pure log-format parsing, shared by GameLogService and the logic checks.
/// Deliberately free of registry/UI dependencies so it can be linked into
/// tools/logiccheck directly.
/// </summary>
public static class GameLogParsing
{
    // Pre-1.3: "Load location: bigmap" / "location: Woods". 1.3.0.0 removed
    // the per-frame location chatter; the stable raid markers are now
    // "scene preset path:maps/<x>_preset.bundle rcid:<id>.scenespreset.asset"
    // (fires on every raid load) and "[Transit] ... Locations:<id> ->".
    internal static readonly Regex LegacyLocation = new(
        @"(?:Load location|Loading location|location\s*[:=])\s*['""]?([A-Za-z0-9_\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static readonly Regex ScenePreset = new(
        @"scene preset path:\S+ rcid:([A-Za-z0-9_\-]+)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static readonly Regex TransitLocations = new(
        @"\[Transit\].*?Locations:([A-Za-z0-9_\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Location id from a raid marker line, or null if the line is
    /// not one. Callers must still validate against MapRepository.</summary>
    public static string? RaidLocationId(string line)
    {
        var m = ScenePreset.Match(line);
        if (m.Success) return m.Groups[1].Value;
        m = TransitLocations.Match(line);
        if (m.Success) return m.Groups[1].Value;
        m = LegacyLocation.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True for the application channel in either layout: pre-1.3
    /// "application[_NNN].log", 1.3 "&lt;timestamp&gt; application[_NNN].log".</summary>
    public static bool IsApplicationLogName(string fileName)
    {
        if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = fileName[..^4]; // drop .log
        var cut = stem.LastIndexOf(' ');
        if (cut >= 0) stem = stem[(cut + 1)..];   // 1.3 prefix: "<stamp> "
        var us = stem.LastIndexOf('_');
        if (us >= 0 && stem[(us + 1)..].Length == 3 && stem.AsSpan(us + 1).IndexOfAnyExcept("0123456789") < 0)
            stem = stem[..us];                     // rotation suffix _NNN
        return stem.Equals("application", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Newest application log: the 1.3 session-directory layout
    /// (Logs/log_&lt;stamp&gt;_&lt;ver&gt;/&lt;stamp&gt; application_000.log), then
    /// the pre-1.3 flat application*.log. Null when neither exists.</summary>
    public static string? FindApplicationLog(string? logsDirectory)
    {
        if (string.IsNullOrWhiteSpace(logsDirectory) || !Directory.Exists(logsDirectory)) return null;
        try
        {
            var sessions = new DirectoryInfo(logsDirectory)
                .EnumerateDirectories("log_*")
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(3);
            foreach (var dir in sessions)
            {
                var f = dir.EnumerateFiles("*application*.log")
                    .Where(x => IsApplicationLogName(x.Name))
                    .OrderByDescending(x => x.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (f is not null) return f.FullName;
            }

            return new DirectoryInfo(logsDirectory)
                .EnumerateFiles("application*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>Newest location id in a log window accepted by
    /// <paramref name="isKnownLocation"/>, or null. Filtering unknown ids
    /// (lobby or menu scenes) is what keeps false hits out.</summary>
    public static string? NewestRaidLocation(string window, Func<string, bool> isKnownLocation)
    {
        string? found = null;
        foreach (var raw in window.Split('\n'))
        {
            var id = RaidLocationId(raw);
            if (id is null) continue;
            if (!isKnownLocation(id)) continue;
            found = id;
        }
        return found;
    }
}
