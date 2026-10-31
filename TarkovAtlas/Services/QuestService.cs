using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovAtlas.Models;

namespace TarkovAtlas.Services;

public sealed class QuestService
{
    // Keep CJK: most titles are Chinese ("枪弹专家 - 2"), and stripping every
    // non-ASCII letter left queries like "枪弹专家-2" as just "2" - matching
    // dozens of "- 2" tasks or none at all (the raw Contains fallback breaks on
    // the " - " spacing). Word-based Similarity/OCR scoring needs the words too.
    private static readonly Regex NonAlnum = new(@"[^0-9a-z\u4e00-\u9fff\u3400-\u4dbf]+", RegexOptions.Compiled);

    private readonly List<Quest> _quests = new();
    private readonly List<StoryChapter> _chapters = new();
    private readonly HashSet<string> _storyDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TraderInfo> _traders = new(StringComparer.OrdinalIgnoreCase);

    private string _search = "";
    private int _filter;
    private string? _mapKey;
    private bool _followMap = true;

    public ObservableCollection<QuestRow> VisibleQuests { get; } = new();
    public IReadOnlyList<Quest> Quests => _quests;
    public IReadOnlyDictionary<string, TraderInfo> Traders => _traders;
    public int TotalCount => _quests.Count;
    public int CompletedCount => _completed.Count(kv => kv.Value);
    public int TrackedCount => _tracked.Count;
    public string DatabaseDate { get; private set; } = "";
    public bool Searching => _search.Trim().Length > 0;

    public event EventHandler? ProgressChanged;

    public void Load()
    {
        _quests.Clear();
        _completed.Clear();
        _tracked.Clear();
        _storyDone.Clear();
        _traders.Clear();

        var questPath = Path.Combine(AppPaths.AssetsDir, AppPaths.QuestDataFile);
        if (File.Exists(questPath))
        {
            var db = JsonSerializer.Deserialize(File.ReadAllText(questPath), AppJsonContext.Default.QuestDatabase);
            if (db is not null)
            {
                _quests.AddRange(db.Quests);
                DatabaseDate = db.GeneratedAt;
            }
        }

        var mapDataPath = AppPaths.MapDataPath;
        if (File.Exists(mapDataPath))
        {
            var data = JsonSerializer.Deserialize(File.ReadAllText(mapDataPath), AppJsonContext.Default.MapManifest);
            if (data is not null)
            {
                foreach (var t in data.Traders) _traders[t.Id] = t;
                foreach (var m in data.Maps) _mapNames[m.Key] = m.DisplayName;
            }
        }

        LoadStory();
        ResolveTraderNames();
        LoadProgress();
        BuildStoryRows();
    }

    /// <summary>
    /// Map each task's trader id onto a readable name. The API only exposes
    /// object ids; without this the sidebar printed raw hex ids.
    /// </summary>
    private void ResolveTraderNames()
    {
        foreach (var quest in _quests)
        {
            // The task list keeps the trader's original (English) nickname; the
            // Chinese translation is only used for display elsewhere.
            var display = "";
            var slug = "";
            if (_traders.TryGetValue(quest.Trader, out var trader))
            {
                display = trader.Name;
                slug = trader.Slug;
            }
            if (string.IsNullOrWhiteSpace(slug))
                slug = display.Replace(".", "").Replace(" ", "-").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(display) && !TraderInfo.IsRawId(quest.Trader))
                display = quest.Trader;

            // Never let a raw hex object id reach the UI.
            quest.TraderDisplay = string.IsNullOrWhiteSpace(display) || TraderInfo.IsRawId(display)
                ? "未知商人"
                : display;
            quest.TraderSlug = string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
            // Built once here; the search box used to re-normalise every name on
            // every keystroke.
            quest.SearchKey = Normalize(quest.Name) + "\n" + Normalize(quest.NameEn);
        }
    }

    /// <summary>Set when the progress file could not be read, so the UI can warn.</summary>
    public string? LoadWarning { get; private set; }

    private void LoadProgress()
    {
        try
        {
            if (!File.Exists(AppPaths.QuestProgressFile)) return;
            var file = JsonSerializer.Deserialize(File.ReadAllText(AppPaths.QuestProgressFile),
                AppJsonContext.Default.QuestProgressFile);
            if (file is null) return;
            foreach (var (id, done) in file.Completed) _completed[id] = done;
            foreach (var id in file.Tracked) _tracked.Add(id);
            foreach (var id in file.StoryDone) _storyDone.Add(id);
        }
        catch (Exception ex)
        {
            // Keep the damaged file instead of overwriting it on the next save.
            try { File.Copy(AppPaths.QuestProgressFile, AppPaths.QuestProgressFile + ".bad", overwrite: true); }
            catch { }
            LoadWarning = "任务进度文件损坏，已备份为 quest-progress.json.bad，进度从零开始。";
            AppPaths.LogCrash(ex);
        }
    }

    private readonly object _saveGate = new();
    private bool _savePending;

    /// <summary>
    /// Schedule a debounced write. Toggling a checkbox used to serialise the
    /// whole progress file synchronously on the UI thread, which stuttered when
    /// several tasks were updated in a row.
    /// </summary>
    public void SaveProgress()
    {
        lock (_saveGate)
        {
            if (_savePending) return;
            _savePending = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(600).ConfigureAwait(false);
            lock (_saveGate) _savePending = false;
            WriteProgressNow();
        });
    }

    /// <summary>Flush pending progress to disk (called on shutdown).</summary>
    public void FlushProgress() => WriteProgressNow();

    private void WriteProgressNow()
    {
        try
        {
            var data = new QuestProgressFile
            {
                Completed = new Dictionary<string, bool>(_completed),
                Tracked = _tracked.ToList(),
                StoryDone = _storyDone.ToList(),
            };
            AppPaths.WriteAllTextAtomic(AppPaths.QuestProgressFile,
                JsonSerializer.Serialize(data, AppJsonContext.Default.QuestProgressFile));
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    public bool IsCompleted(string id) => _completed.TryGetValue(id, out var done) && done;
    public bool IsTracked(string id) => _tracked.Contains(id);

    public void SetCompleted(string id, bool done)
    {
        _completed[id] = done;
        if (done) _tracked.Remove(id);
        SaveProgress();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetTracked(string id, bool tracked)
    {
        if (tracked) _tracked.Add(id);
        else _tracked.Remove(id);
        SaveProgress();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Current map constraint (null = every map) plus whether it applies.</summary>
    public void SetMap(string? mapKey, bool followMap)
    {
        _mapKey = mapKey;
        _followMap = followMap;
    }

    public void SetQuery(string query) => _search = query ?? "";

    public void SetFilter(int filter) => _filter = filter;

    public void Apply()
    {
        var query = _search.Trim();
        var normalized = Normalize(query);
        var rows = new List<QuestRow>();

        foreach (var quest in _quests)
        {
            var done = IsCompleted(quest.Id);
            if (_filter == 1 && done) continue;
            if (_filter == 2 && !done) continue;

            // Search spans every task; the map constraint only applies when the
            // user is not actively searching (so global search still works).
            var browsing = normalized.Length == 0;
            if (browsing && _followMap && _mapKey is not null && !quest.AppliesTo(_mapKey))
            {
                // Tasks without map coordinates ("anywhere" tasks) are still
                // actionable on every map, so keep them - only pushed last so
                // the map-specific ones stay on top.
                if (!quest.IsAnyLocation) continue;
            }

            if (!browsing && !Matches(quest, query, normalized))
                continue;

            rows.Add(new QuestRow(quest, done, IsTracked(quest.Id), OnCompletedChanged, OnTrackedChanged));
        }

        rows.Sort((a, b) =>
        {
            var c = a.IsCompleted.CompareTo(b.IsCompleted);
            if (c != 0) return c;
            // "Anywhere" tasks sort after the tasks tied to the current map.
            c = a.Quest.IsAnyLocation.CompareTo(b.Quest.IsAnyLocation);
            if (c != 0) return c;
            c = b.IsTracked.CompareTo(a.IsTracked);
            if (c != 0) return c;
            c = a.Quest.Level.CompareTo(b.Quest.Level);
            if (c != 0) return c;
            return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        VisibleQuests.Clear();
        foreach (var row in rows) VisibleQuests.Add(row);
    }

    private static bool Matches(Quest quest, string raw, string normalized)
    {
        if (quest.Name.Contains(raw, StringComparison.OrdinalIgnoreCase)) return true;
        if (quest.NameEn.Contains(raw, StringComparison.OrdinalIgnoreCase)) return true;
        if (TraderDisplay(quest).Contains(raw, StringComparison.OrdinalIgnoreCase)) return true;
        if (normalized.Length == 0) return false;
        return quest.SearchKey.Contains(normalized, StringComparison.Ordinal);
    }

    private static string TraderDisplay(Quest quest) => quest.TraderDisplay;

    private void OnCompletedChanged(QuestRow row, bool done)
    {
        _completed[row.Id] = done;
        if (done) _tracked.Remove(row.Id);
        SaveProgress();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTrackedChanged(QuestRow row, bool tracked)
    {
        if (tracked) _tracked.Add(row.Id);
        else _tracked.Remove(row.Id);
        SaveProgress();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>Localized names supplied by the bundled manifest, keyed by map key.</summary>
    private readonly Dictionary<string, string> _mapNames = new(StringComparer.OrdinalIgnoreCase);

    public TraderInfo? FindTrader(string id) => _traders.TryGetValue(id, out var t) ? t : null;

        public IReadOnlyList<QuestCandidate> MatchText(string text, int maxResults = 40)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(l => l.Length >= 3)
            .ToList();
        if (lines.Count == 0) return Array.Empty<QuestCandidate>();

        var joined = string.Join(" ", lines);
        var candidates = new List<QuestCandidate>();

        foreach (var quest in _quests)
        {
            double best = 0;
            string evidence = "";
            foreach (var title in new[] { quest.Name, quest.NameEn })
            {
                var probe = Normalize(title);
                if (probe.Length < 3) continue;

                if (joined.Contains(probe, StringComparison.Ordinal))
                {
                    if (best < 1.0) { best = 1.0; evidence = "整段命中"; }
                    continue;
                }

                foreach (var line in lines)
                {
                    if (line.Contains(probe, StringComparison.Ordinal) ||
                        (probe.Contains(line, StringComparison.Ordinal) && line.Length >= Math.Max(5, probe.Length * 0.6)))
                    {
                        if (best < 0.97) { best = 0.97; evidence = "行内命中"; }
                        break;
                    }
                    var score = Similarity(probe, line);
                    if (score > best) { best = score; evidence = "模糊匹配"; }
                }
            }

            if (best >= 0.6)
                candidates.Add(new QuestCandidate { Quest = quest, Score = Math.Round(best, 3), Evidence = evidence });
        }

        return candidates
            .GroupBy(c => c.Quest.Id)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Quest.Name)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Markers for the tasks that can be progressed on the given map. Only
    /// tracked tasks contribute, and only objectives that carry coordinates.
    /// </summary>
    public IReadOnlyList<QuestMarker> BuildMarkers(string? mapKey)
    {
        var markers = new List<QuestMarker>();
        if (string.IsNullOrWhiteSpace(mapKey)) return markers;

        foreach (var quest in _quests)
        {
            if (!IsTracked(quest.Id)) continue;

            foreach (var objective in quest.Objectives)
            {
                foreach (var zone in objective.Zones)
                {
                    if (!string.Equals(zone.Map, mapKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    markers.Add(new QuestMarker
                    {
                        QuestId = quest.Id,
                        QuestName = quest.Title,
                        TraderSlug = quest.TraderSlug,
                        TraderName = TraderDisplay(quest),
                        X = zone.X,
                        Z = zone.Z,
                        ObjectiveText = objective.Text,
                    });
                }
            }
        }

        return markers;
    }

    /// <summary>Story chapters for the main-quest panel (story.json).</summary>
    public IReadOnlyList<StoryChapter> Chapters => _chapters;

    public bool IsStoryDone(string id) => _storyDone.Contains(id);

    /// <summary>Chapters flattened for the story tab, with their tick state.</summary>
    public ObservableCollection<StoryRow> StoryRows { get; } = new();

    /// <summary>
    /// What the story ListBox is bound to: <see cref="StoryRows"/> minus the
    /// ticked chapters when 「隐藏已完成」 is on. Kept separate so the full list
    /// (and the progress count) never depends on a display toggle.
    /// </summary>
    public ObservableCollection<StoryRow> StoryView { get; } = new();

    /// <summary>Hide ticked chapters from the story list. Display only.</summary>
    public bool HideDoneStories { get; set; }

    /// <summary>Rebuild <see cref="StoryView"/> from the current tick state.</summary>
    public void RefreshStoryView()
    {
        StoryView.Clear();
        foreach (var row in StoryRows)
        {
            if (HideDoneStories && row.IsDone) continue;
            StoryView.Add(row);
        }
    }

    public void SetStoryDone(string id, bool done)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (done) _storyDone.Add(id);
        else _storyDone.Remove(id);

        // Keep the bound row in sync before the list is rebuilt, so the tick
        // does not visibly bounce back when the save is debounced.
        var row = StoryRows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        if (row is not null) row.IsDone = done;

        SaveProgress();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildStoryRows()
    {
        StoryRows.Clear();
        for (var i = 0; i < _chapters.Count; i++)
        {
            var chapter = _chapters[i];
            StoryRows.Add(new StoryRow(chapter, i, IsStoryDone(chapter.Id)));
        }
        RefreshStoryView();
    }

    public void SetStoryDoneByIndex(int index, bool done)
    {
        if (index < 0 || index >= _chapters.Count) return;
        SetStoryDone(_chapters[index].Id, done);
    }

    public int StoryDoneCount => _chapters.Count(c => IsStoryDone(c.Id));

    private void LoadStory()
    {
        _chapters.Clear();
        try
        {
            var path = Path.Combine(AppPaths.AssetsDir, AppPaths.StoryDataFile);
            if (!File.Exists(path)) return;
            var db = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.StoryDatabase);
            if (db?.Chapters is null) return;
            _chapters.AddRange(db.Chapters.OrderBy(c => c.Order));
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    public Quest? FindById(string id) => _quests.FirstOrDefault(q => q.Id == id);

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lower = s.ToLowerInvariant()
            .Replace('’', '\'')
            .Replace('–', '-')
            .Replace('—', '-');
        return NonAlnum.Replace(lower, " ").Trim();
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;

        var aw = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bw = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var common = aw.Intersect(bw, StringComparer.Ordinal).Count();
        var jaccard = common / (double)Math.Max(1, aw.Union(bw, StringComparer.Ordinal).Count());

        var distance = Levenshtein(a, b, 12);
        var lev = 1.0 - distance / (double)Math.Max(a.Length, b.Length);
        return Math.Max(jaccard, lev);
    }

    private static int Levenshtein(string a, string b, int cap)
    {
        if (Math.Abs(a.Length - b.Length) > cap) return cap + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > cap) return cap + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}