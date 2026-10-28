namespace TarkovAtlas.Models;

public sealed class AppSettings
{
    public string? LogsPath { get; set; }
    public string? ScreenshotsPath { get; set; }
    public string SelectedMap { get; set; } = "customs";

    public string OverlayHotkey { get; set; } = "Ctrl+Alt+M";
    public string OverlayLockHotkey { get; set; } = "Ctrl+Alt+L";
    public double OverlayOpacity { get; set; } = 0.9;
    /// <summary>Overlay size in DIPs; the window itself is borderless and follows the player.</summary>
    public double OverlaySize { get; set; } = 260;
    public double OverlayZoom { get; set; } = 2.4;
    /// <summary>Where the overlay sits vertically: top / middle / bottom of the work area.</summary>
    public string OverlayAnchor { get; set; } = "middle";
    public double OverlayOffsetX { get; set; } = 16;
    public double OverlayOffsetY { get; set; }
    public bool OverlayLocked { get; set; }

    public bool ShowLabels { get; set; } = true;
    public bool ShowExtracts { get; set; } = true;
    /// <summary>pmc | scav | shared | all</summary>
    public string ExtractFilter { get; set; } = "all";
    public bool ShowBosses { get; set; } = true;
    public bool ShowSpawns { get; set; }
    public bool ShowQuestMarkers { get; set; } = true;
    public bool ShowTransits { get; set; }
    /// <summary>Draw minefield / sniper danger polygons.</summary>
    public bool ShowHazards { get; set; } = true;
    /// <summary>Selected building level per map key ("" = automatic by player Y).</summary>
    public Dictionary<string, string> SelectedLayer { get; set; } = new();
    /// <summary>Switch the level automatically from the player's Y coordinate.</summary>
    public bool AutoLayer { get; set; } = true;

    public bool AutoDetect { get; set; } = true;
    public bool AutoSwitchMap { get; set; } = true;
    public bool ScanScreenshots { get; set; } = true;
    public bool ScanOnStartup { get; set; } = true;
    /// <summary>Delete the screenshot file once its coordinates were imported.</summary>
    public bool DeleteScreenshotsAfterScan { get; set; } = true;

    public bool OverlayVisibleOnStartup { get; set; }
    public bool OverlayShowsWholeMap { get; set; }

    public int QuestFilter { get; set; }
    /// <summary>Only show tasks that can be progressed on the selected map.</summary>
    public bool QuestFollowsMap { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Copy the overlay's live geometry back into the settings object.</summary>
    public void OverlayWidthFrom(object overlay)
    {
        if (overlay is not Avalonia.Controls.Window window) return;
        OverlaySize = Math.Clamp(window.Width, 120, 900);
        if (!double.IsNaN(window.Position.X) && window.Position.X >= 0)
            OverlayOffsetX = Math.Max(0, OverlayOffsetX);
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(SelectedMap)) SelectedMap = "customs";
        if (string.IsNullOrWhiteSpace(OverlayHotkey)) OverlayHotkey = "Ctrl+Alt+M";
        if (string.IsNullOrWhiteSpace(OverlayLockHotkey)) OverlayLockHotkey = "Ctrl+Alt+L";
        OverlayOpacity = Math.Clamp(OverlayOpacity, 0.25, 1.0);
        OverlaySize = Math.Clamp(OverlaySize, 120, 900);
        OverlayZoom = Math.Clamp(OverlayZoom, 0.5, 12);
        if (OverlayAnchor is not ("top" or "middle" or "bottom")) OverlayAnchor = "middle";
        if (ExtractFilter is not ("all" or "pmc" or "scav" or "shared")) ExtractFilter = "all";
    }
}

public sealed class QuestProgressFile
{
    /// <summary>Task id -> completed.</summary>
    public Dictionary<string, bool> Completed { get; set; } = new();
    /// <summary>Task ids currently tracked (markers drawn on the map).</summary>
    public List<string> Tracked { get; set; } = new();
    /// <summary>Story chapter ids that were ticked off in the main-story tab.</summary>
    public List<string> StoryDone { get; set; } = new();
}