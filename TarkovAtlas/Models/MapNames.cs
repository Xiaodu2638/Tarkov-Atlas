namespace TarkovAtlas.Models;

/// <summary>
/// Map key -> localized display name. The bundled quest/map JSON only stores
/// internal keys such as "woods"/"the-lab"; the UI must never print those raw.
/// </summary>
public static class MapNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ground-zero"] = "中心区",
        ["customs"] = "海关",
        ["factory"] = "工厂",
        ["woods"] = "森林",
        ["interchange"] = "立交桥",
        ["shoreline"] = "海岸线",
        ["reserve"] = "储备站",
        ["the-lab"] = "实验室",
        ["lighthouse"] = "灯塔",
        ["streets-of-tarkov"] = "塔科夫街区",
        ["the-labyrinth"] = "迷宫",
        ["terminal"] = "码头",
        ["icebreaker"] = "破冰船",
    };

    /// <summary>Localized name for a map key, falling back to the key itself.</summary>
    public static string Display(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "任意地图";
        return Names.TryGetValue(key, out var name) ? name : key;
    }
}
