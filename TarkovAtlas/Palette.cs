using Avalonia.Media;

namespace TarkovAtlas;

/// <summary>
/// Single source of truth for the app's colours.
///
/// The XAML resources in <c>App.axaml</c> cover the controls declared in markup,
/// but the code-behind windows (map picker, image viewer, scan window) draw their
/// own trees and used to repeat raw hex literals. Keeping one palette here means
/// a colour change is a one-line edit instead of a hunt through six files.
/// </summary>
public static class Palette
{
    // Surfaces (dark → light)
    public static readonly Color AppBg = Color.Parse("#080C11");
    public static readonly Color PanelBg = Color.Parse("#0E141B");
    public static readonly Color PanelBg2 = Color.Parse("#111922");
    public static readonly Color CardBg = Color.Parse("#141C25");
    public static readonly Color CanvasBg = Color.Parse("#0A0F15");
    public static readonly Color CompactBg = Color.Parse("#080B10");
    public static readonly Color InputBg = Color.Parse("#0B1117");
    public static readonly Color DialogBg = Color.Parse("#090E14");
    public static readonly Color OverlayPanelBg = Color.Parse("#D90B1017");
    public static readonly Color TooltipBg = Color.Parse("#E60B1017");

    // Lines
    public static readonly Color BorderSoft = Color.Parse("#25313D");
    public static readonly Color BorderStrong = Color.Parse("#3A4856");
    public static readonly Color BorderDim = Color.Parse("#1C2732");
    public static readonly Color BorderPanel = Color.Parse("#26333F");

    // Text
    public static readonly Color TextPrimary = Color.Parse("#E8EDF2");
    public static readonly Color TextSecondary = Color.Parse("#9EAEBB");
    public static readonly Color TextMuted = Color.Parse("#6D7D8B");
    public static readonly Color TextDim = Color.Parse("#8FA0AE");

    // Semantic
    public static readonly Color Accent = Color.Parse("#D6A84B");
    public static readonly Color AccentHover = Color.Parse("#E6BB60");
    public static readonly Color AccentSoft = Color.Parse("#3B3220");

    // Chrome (headers, dividers, button states, list selection)
    public static readonly Color HeaderBg = Color.Parse("#0B1016");
    public static readonly Color DividerBg = Color.Parse("#1C2732");
    public static readonly Color ButtonBg = Color.Parse("#18212B");
    public static readonly Color ButtonHoverBg = Color.Parse("#22303D");
    public static readonly Color ButtonPressedBg = Color.Parse("#0C1117");
    public static readonly Color SurfaceBg = Color.Parse("#0A0F14");
    public static readonly Color SurfaceDeepBg = Color.Parse("#090D12");
    public static readonly Color SurfaceRaisedBg = Color.Parse("#0D131A");
    public static readonly Color PanelDeepBg = Color.Parse("#0C1219");
    public static readonly Color PanelHeaderBg = Color.Parse("#101822");
    public static readonly Color OverlayChromeBorder = Color.Parse("#26333F");
    public static readonly Color ListHoverBg = Color.Parse("#17212B");
    public static readonly Color ListSelectedBg = Color.Parse("#232D35");
    public static readonly Color ListSelectedHoverBg = Color.Parse("#28343E");
    public static readonly Color CardAltBg = Color.Parse("#131B24");
    public static readonly Color TextOnAccent = Color.Parse("#171207");
    public static readonly Color Danger = Color.Parse("#D85B4A");
    public static readonly Color DangerText = Color.Parse("#E08A7C");
    public static readonly Color Success = Color.Parse("#6FBF8B");
    public static readonly Color SuccessBg = Color.Parse("#17231D");
    public static readonly Color SuccessBorder = Color.Parse("#2E4A37");
    public static readonly Color DangerBg = Color.Parse("#2A1715");
    public static readonly Color DangerBorder = Color.Parse("#5A2B24");

    // Map markers
    public static readonly Color ExtractPmc = Color.Parse("#5FD68A");
    public static readonly Color ExtractScav = Color.Parse("#E0C063");
    public static readonly Color ExtractShared = Color.Parse("#67B2E8");
    public static readonly Color Boss = Color.Parse("#E06052");
    public static readonly Color BossHalo = Color.Parse("#33E06052");
    public static readonly Color SpawnPmc = Color.Parse("#7FA8E0");
    public static readonly Color SpawnScav = Color.Parse("#D08A5A");
    public static readonly Color Transit = Color.Parse("#B07FD8");
    public static readonly Color CacheFill = Color.Parse("#CC3A2A12");
    public static readonly Color CacheStroke = Color.Parse("#F2C98A");
    public static readonly Color Player = Color.Parse("#FFD166");
    public static readonly Color PlayerDark = Color.Parse("#D98A22");
    public static readonly Color PlayerHalo = Color.Parse("#55FFD166");
    public static readonly Color PlayerCone = Color.Parse("#B3FFD166");
    public static readonly Color Label = Color.Parse("#E4ECF3");
    public static readonly Color LabelShadow = Color.Parse("#E605080B");
    public static readonly Color QuestLabel = Color.Parse("#F0E4C4");
    public static readonly Color QuestMarkerFill = Color.Parse("#F2D6A84B");
    public static readonly Color QuestMarkerStroke = Color.Parse("#FFF6E7C4");
    public static readonly Color MarkerShadow = Color.Parse("#8805080B");
    public static readonly Color Empty = Color.Parse("#7D8D9A");
    public static readonly Color Muted = Color.Parse("#8FA0AE");
    public static readonly Color Dot = Color.Parse("#D6A84B");
    public static readonly Color Pin = Color.Parse("#E0523D");
    public static readonly Color PinStroke = Color.Parse("#FFF3DF");
    public static readonly Color PinShadow = Color.Parse("#33000000");
    public static readonly Color ExtractFill = Color.Parse("#CC0A0F15");
    public static readonly Color SpawnFill = Color.Parse("#66222830");
    public static readonly Color TransitFill = Color.Parse("#CC0A0F15");
    public static readonly Color BossEmptyStroke = Color.Parse("#FFE8DF");
    public static readonly Color BossInitial = Color.Parse("#171207");
    public static readonly Color PlayerPanelBorder = Color.Parse("#66D6A84B");
    public static readonly Color FocusHalo = Color.Parse("#44D6A84B");
    public static readonly Color HazardGlyph = Color.Parse("#0A0F15");

    public static readonly Color MinefieldFill = Color.Parse("#33E0523D");
    public static readonly Color MinefieldStroke = Color.Parse("#CCE0523D");
    public static readonly Color SniperFill = Color.Parse("#33E0A23D");
    public static readonly Color SniperStroke = Color.Parse("#CCE0A23D");
    public static readonly Color HazardFill = Color.Parse("#33D06FE0");
    public static readonly Color HazardStroke = Color.Parse("#CCD06FE0");

    // Raid status pill
    public static readonly Color StatusLiveBg = Color.Parse("#17231D");
    public static readonly Color StatusLiveBorder = Color.Parse("#2E4A37");
    public static readonly Color StatusLiveText = Color.Parse("#A9CEB8");
    public static readonly Color StatusIdleText = Color.Parse("#8FA0AE");
    public static readonly Color StatusErrorText = Color.Parse("#D85B4A");

    /// <summary>Opaque brush for a palette colour (cached per colour).</summary>
    public static IBrush Brush(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            _brushes[color] = brush;
        }
        return brush;
    }

    private static readonly Dictionary<Color, IBrush> _brushes = new();
}
