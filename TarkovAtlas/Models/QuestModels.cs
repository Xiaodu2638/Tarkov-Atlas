using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TarkovAtlas.Models;

public sealed class QuestDatabase
{
    public string GeneratedAt { get; set; } = "";
    public string Source { get; set; } = "";
    public List<Quest> Quests { get; set; } = new();
}

/// <summary>Story chapters file (story.json); separate from the side tasks.</summary>
public sealed class StoryDatabase
{
    public string GeneratedAt { get; set; } = "";
    public string Source { get; set; } = "";
    public List<StoryChapter> Chapters { get; set; } = new();
}

/// <summary>
/// One main-story chapter. Story progress is tracked on its own, apart from the
/// side task list, but behaves the same way (ticked off, persisted).
/// </summary>
public sealed class StoryChapter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameZh { get; set; } = "";
    public int Order { get; set; }
    /// <summary>Map keys this chapter sends you to.</summary>
    public List<string> Maps { get; set; } = new();
    public List<string> MapNames { get; set; } = new();
    /// <summary>Localized (Chinese) objectives, each with an optional map location.</summary>
    public List<StoryObjective> Objectives { get; set; } = new();
    public int ObjectiveCount { get; set; }

    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(NameZh) ? Name : NameZh;
    [JsonIgnore] public string MapsText => MapNames.Count == 0 ? "任意地图" : string.Join(" / ", MapNames);
    [JsonIgnore] public string ObjectiveText =>
        Objectives.Count == 0 ? "无目标" : $"{Objectives.Count} 个目标";
}

/// <summary>
/// One step of a main-story chapter. <see cref="Map"/> is the map the step
/// happens on; <see cref="X"/>/<see cref="Z"/> are only filled when the
/// upstream marker position could be calibrated against this app's artwork.
/// </summary>
public sealed class StoryObjective : INotifyPropertyChanged
{
    private bool _isDone;
    private bool _isVisible = true;

    /// <summary>eftarkov objective id — the stable progress key (unique per objective).</summary>
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Map { get; set; } = "";
    public string MapName { get; set; } = "";
    public double X { get; set; }
    public double Z { get; set; }
    public bool HasCoordinates { get; set; }

    /// <summary>
    /// Tick state, mirrored from the progress file by QuestService. Lives on the
    /// model so the row's TwoWay checkbox has something to write to.
    /// </summary>
    [JsonIgnore]
    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value) return;
            _isDone = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Cleared when 「隐藏已完成」 hides this row (display only).</summary>
    [JsonIgnore]
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore] public bool HasLocation => !string.IsNullOrWhiteSpace(Map);
    [JsonIgnore] public string LocationText => HasLocation ? MapName : "任意地图";
    [JsonIgnore]
    public string Tooltip => HasCoordinates
        ? $"{Text}\n跳转到 {MapName} 的目标位置"
        : $"跳转到 {MapName}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Bindable wrapper for one story chapter in the 剧情任务 tab. The check state
/// lives on the row so the TwoWay binding has something to write to.
/// </summary>
public sealed class StoryRow : INotifyPropertyChanged
{
    private bool _isDone;
    private bool _isExpanded;

    public StoryRow(StoryChapter chapter, int index, bool isDone)
    {
        Chapter = chapter;
        Index = index;
        _isDone = isDone;
    }

    public StoryChapter Chapter { get; }
    public int Index { get; }
    public string Id => Chapter.Id;
    public string Title => Chapter.Title;
    public string MapsText => Chapter.MapsText;
    public string ObjectiveText => Chapter.ObjectiveText;
    public IReadOnlyList<StoryObjective> Objectives => Chapter.Objectives;
    public bool HasObjectives => Chapter.Objectives.Count > 0;

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value) return;
            _isDone = value;
            // Ticking a chapter off folds its objective list away: the row is
            // done, so the per-objective 前往 buttons stop being interesting.
            if (value) IsExpanded = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Whether the chapter's 目标列表 expander is open.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public string StatusText => _isDone ? "已完成" : "未完成";

    /// <summary>Ticked objectives of this chapter, e.g. "12/26 目标".</summary>
    public string ObjectiveProgressText => Chapter.Objectives.Count == 0
        ? "无目标"
        : $"{Chapter.Objectives.Count(o => o.IsDone)}/{Chapter.Objectives.Count} 目标";

    /// <summary>Re-read the chapter's objective ticks after one of them changed.</summary>
    public void NotifyObjectivesChanged() => OnPropertyChanged(nameof(ObjectiveProgressText));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Quest
{
    /// <summary>Task id from tarkov.dev (hex object id); used for progress storage.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameEn { get; set; } = "";
    /// <summary>Trader object id (hex) as delivered by the API.</summary>
    public string Trader { get; set; } = "";
    /// <summary>Human readable trader name resolved at load time (never an id).</summary>
    [JsonIgnore] public string TraderDisplay { get; set; } = "";
    /// <summary>Avatar file stem for the giver (never an id).</summary>
    [JsonIgnore] public string TraderSlug { get; set; } = "";
    /// <summary>Lowercase alphanumeric search key for Name + NameEn, built once at load.</summary>
    [JsonIgnore] public string SearchKey { get; set; } = "";
    public int Level { get; set; }
    public string Wiki { get; set; } = "";
    public List<string> Prerequisites { get; set; } = new();
    /// <summary>Map keys this task can be progressed on (objectives' maps first).</summary>
    public List<string> Maps { get; set; } = new();
    public List<QuestObjective> Objectives { get; set; } = new();

    [JsonIgnore] public string Title => Name;
    [JsonIgnore] public string LevelText => Level > 0 ? $"Lv.{Level}" : "无等级要求";
    [JsonIgnore]
    public string MapsText => Maps.Count == 0
        ? "任意地图"
        : string.Join(" / ", Maps.Select(MapNames.Display));
    /// <summary>True when the task has objectives on more than one raid map.</summary>
    [JsonIgnore] public bool IsMultiMap => Maps.Count > 1;

    /// <summary>
    /// True for tasks that carry no map at all ("kill 20 PMCs anywhere", pure
    /// hand-ins). These still show up on every map, just sorted to the bottom.
    /// </summary>
    [JsonIgnore] public bool IsAnyLocation => Maps.Count == 0;

    public IEnumerable<QuestObjective> ObjectivesFor(string mapKey) =>
        Objectives.Where(o => o.Zones.Any(z => string.Equals(z.Map, mapKey, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// True when this task can be progressed on the given map. Tasks with no map
    /// data at all (e.g. "kill 20 PMCs anywhere" or pure hand-in tasks) are NOT
    /// shown in a single map's list, mirroring how the game groups tasks; they
    /// stay reachable through search and the "all maps" toggle.
    /// </summary>
    public bool AppliesTo(string mapKey) =>
        Maps.Any(m => string.Equals(m, mapKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the objective list carries at least one coordinate.</summary>
    [JsonIgnore] public bool HasAnyZones => Objectives.Any(o => o.Zones.Count > 0);

    /// <summary>The jump button is useful whenever the task names a map.</summary>
    [JsonIgnore] public bool CanJump => Maps.Count > 0 || HasAnyZones;

    [JsonIgnore] public IEnumerable<string> ZoneMaps =>
        Objectives.SelectMany(o => o.Zones).Select(z => z.Map)
            .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct();
}

public sealed class QuestObjective
{
    public string Text { get; set; } = "";
    /// <summary>kill | visit | find | pickup | mark | ...</summary>
    public string Type { get; set; } = "";
    public List<ObjectiveZone> Zones { get; set; } = new();
}

public sealed class ObjectiveZone
{
    public string Map { get; set; } = "";
    public double X { get; set; }
    public double Z { get; set; }
}

public sealed class QuestRow : INotifyPropertyChanged
{
    private bool _isCompleted;
    private bool _isTracked;
    private readonly Action<QuestRow, bool>? _onCompletedChanged;
    private readonly Action<QuestRow, bool>? _onTrackedChanged;

    public QuestRow(Quest quest, bool isCompleted, bool isTracked,
        Action<QuestRow, bool>? onCompletedChanged = null,
        Action<QuestRow, bool>? onTrackedChanged = null)
    {
        Quest = quest;
        _isCompleted = isCompleted;
        _isTracked = isTracked;
        _onCompletedChanged = onCompletedChanged;
        _onTrackedChanged = onTrackedChanged;
    }

    /// <summary>
    /// Re-sync the check states from the service without raising the change
    /// callbacks. Used when the list must not re-sort (jump keeps scroll).
    /// </summary>
    public void RefreshState(bool isCompleted, bool isTracked)
    {
        if (_isCompleted != isCompleted)
        {
            _isCompleted = isCompleted;
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(StatusText));
        }
        if (_isTracked != isTracked)
        {
            _isTracked = isTracked;
            OnPropertyChanged(nameof(IsTracked));
        }
    }

    public Quest Quest { get; }
    public string Id => Quest.Id;
    public string Title => Quest.Title;
    public string Giver => string.IsNullOrWhiteSpace(Quest.TraderDisplay) ? Quest.Trader : Quest.TraderDisplay;
    public string LevelText => Quest.LevelText;
    public string MapsText => Quest.MapsText;
    public bool IsMultiMap => Quest.IsMultiMap;

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted == value) return;
            _isCompleted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            _onCompletedChanged?.Invoke(this, value);
        }
    }

    /// <summary>Tracked tasks draw their objective markers on the map.</summary>
    public bool IsTracked
    {
        get => _isTracked;
        set
        {
            if (_isTracked == value) return;
            _isTracked = value;
            OnPropertyChanged();
            _onTrackedChanged?.Invoke(this, value);
        }
    }

    public string StatusText => _isCompleted ? "已完成" : "未完成";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class QuestCandidate
{
    public Quest Quest { get; init; } = new();
    public double Score { get; init; }
    public string Evidence { get; init; } = "";
    public bool Selected { get; set; } = true;
    public string ScoreText => $"{Score * 100:0}%";
}

/// <summary>A quest objective marker to draw on the map, with the giver avatar.</summary>
public sealed class QuestMarker
{
    public string QuestId { get; init; } = "";
    public string QuestName { get; init; } = "";
    public string TraderSlug { get; init; } = "";
    public string TraderName { get; init; } = "";
    public double X { get; init; }
    public double Z { get; init; }
    public string ObjectiveText { get; init; } = "";
    public string Tooltip => $"{QuestName}\n{TraderName}\n{ObjectiveText}";
}