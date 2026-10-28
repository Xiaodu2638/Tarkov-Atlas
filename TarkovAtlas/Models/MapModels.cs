using System.Text.Json.Serialization;

namespace TarkovAtlas.Models;

public sealed class MapManifest
{
    public string GeneratedAt { get; set; } = "";
    public List<MapDefinition> Maps { get; set; } = new();
    public List<TraderInfo> Traders { get; set; } = new();
}

public sealed class TraderInfo
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameZh { get; set; } = "";

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(NameZh) && !IsRawId(NameZh)) return NameZh;
            if (!string.IsNullOrWhiteSpace(Name) && !IsRawId(Name)) return Name;
            return string.IsNullOrWhiteSpace(Slug) ? "未知商人" : Slug;
        }
    }

    /// <summary>True for a 24 char hex object id such as "54cb50c76803fa8b248b4571".</summary>
    public static bool IsRawId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 24
        && value.All(Uri.IsHexDigit);

}

public sealed class MapDefinition
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Description { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public string Players { get; set; } = "";
    public int RaidMinutes { get; set; }
    public string Image { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>a,b,c,d,e,f: px = a*x + b*z + c; py = d*x + e*z + f.</summary>
    public double[] Affine { get; set; } = Array.Empty<double>();
    /// <summary>minX, minZ, maxX, maxZ in game units.</summary>
    public double[] Bounds { get; set; } = Array.Empty<double>();
    public List<MapLabel> Labels { get; set; } = new();
    public List<ExtractPoint> Extracts { get; set; } = new();
    public List<BossSpawn> Bosses { get; set; } = new();
    public List<SpawnPoint> Spawns { get; set; } = new();
    public List<TransitPoint> Transits { get; set; } = new();
    public List<MapLayer> Layers { get; set; } = new();
    /// <summary>Minefield / sniper zones, loaded from hazards.json.</summary>
    [JsonIgnore] public List<HazardZone> Hazards { get; set; } = new();
    public string Source { get; set; } = "";
    public int Order { get; set; }

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Key : Name;
    [JsonIgnore] public string SecondaryName => NameEn;
    [JsonIgnore] public string MetaLine => $"{Players} 人  ·  {RaidMinutes} 分钟  ·  {Difficulty}";

    [JsonIgnore] public double MinX => Bounds.Length >= 4 ? Math.Min(Bounds[0], Bounds[2]) : 0;
    [JsonIgnore] public double MinZ => Bounds.Length >= 4 ? Math.Min(Bounds[1], Bounds[3]) : 0;
    [JsonIgnore] public double MaxX => Bounds.Length >= 4 ? Math.Max(Bounds[0], Bounds[2]) : Width;
    [JsonIgnore] public double MaxZ => Bounds.Length >= 4 ? Math.Max(Bounds[1], Bounds[3]) : Height;

    public bool HasValidAffine => Affine.Length == 6 && Math.Abs(Affine[0] * Affine[4] - Affine[1] * Affine[3]) > 1e-9;

    public (double X, double Y) GameToImage(double x, double z)
    {
        if (Affine.Length != 6) return (0, 0);
        return (Affine[0] * x + Affine[1] * z + Affine[2],
                Affine[3] * x + Affine[4] * z + Affine[5]);
    }

    public (double X, double Z) ImageToGame(double px, double py)
    {
        if (Affine.Length != 6) return (0, 0);
        var a = Affine[0]; var b = Affine[1]; var c = Affine[2];
        var d = Affine[3]; var e = Affine[4]; var f = Affine[5];
        var det = a * e - b * d;
        if (Math.Abs(det) < 1e-12) return (0, 0);
        var u = px - c;
        var v = py - f;
        return ((e * u - b * v) / det, (-d * u + a * v) / det);
    }

    /// <summary>Direction on the image for a world-space XZ vector.</summary>
    public (double X, double Y) GameVectorToImage(double dx, double dz)
    {
        if (Affine.Length != 6) return (0, 0);
        return (Affine[0] * dx + Affine[1] * dz, Affine[3] * dx + Affine[4] * dz);
    }

    public bool Contains(double x, double z, double margin = 0)
    {
        return x >= MinX - margin && x <= MaxX + margin &&
               z >= MinZ - margin && z <= MaxZ + margin;
    }
}

/// <summary>
/// A building-level overlay. The PNG is baked into the same pixel grid as the
/// base map, so it is drawn over the exact same destination rectangle.
/// </summary>
public sealed class MapLayer
{
    public string Name { get; set; } = "";
    /// <summary>Path relative to Assets/Maps.</summary>
    public string Image { get; set; } = "";
    public int Order { get; set; }
    /// <summary>Player Y range (game units) where this level is the right one.</summary>
    public double? MinHeight { get; set; }
    public double? MaxHeight { get; set; }

    public bool MatchesHeight(double y) =>
        (MinHeight is null || y >= MinHeight.Value) &&
        (MaxHeight is null || y <= MaxHeight.Value);
}

public sealed class MapLabel
{
    public double X { get; set; }
    public double Z { get; set; }
    public string Text { get; set; } = "";
    public string En { get; set; } = "";
    public double Size { get; set; } = 100;
}

public sealed class ExtractPoint
{
    public string Name { get; set; } = "";
    public string En { get; set; } = "";
    /// <summary>pmc | scav | shared</summary>
    public string Faction { get; set; } = "pmc";
    public double X { get; set; }
    public double Z { get; set; }
}

public sealed class BossSpawn
{
    public string Name { get; set; } = "";
    /// <summary>API mob identifier (e.g. bossKilla); also the portrait file name.</summary>
    public string Mob { get; set; } = "";
    public List<GameMarker> Points { get; set; } = new();

    /// <summary>
    /// Portrait file stem under Assets/bosses. Group mobs are mapped explicitly:
    /// ex-USEC rogues share rogue.png, raiders share raider.png, black division
    /// variants share their own artwork.
    /// </summary>
    [JsonIgnore]
    public string AvatarSlug
    {
        get
        {
            var mob = Mob ?? "";
            return mob.ToLowerInvariant() switch
            {
                "bossbully" => "reshala",
                "bosskojaniy" => "shturman",
                "bossboar" => "kaban",
                "bossgluhar" => "glukhar",
                "sectantpriest" => "sectant-priest",
                "pmcbot" => "raider",
                "exusecfree" or "exusec" => "rogue",
                "vsrf" or "vsrfsniper" => "ruaf",
                "blackdivision" or "pmcbotblackdiv" => "black-division",
                "bossbullyblackdiv" => "black-division-icebreaker",
                _ when mob.StartsWith("boss", StringComparison.OrdinalIgnoreCase) =>
                    mob[4..].Replace("_", "-").ToLowerInvariant(),
                _ => mob.Replace("_", "-").ToLowerInvariant(),
            };
        }
    }
}

public sealed class SpawnPoint
{
    public double X { get; set; }
    public double Z { get; set; }
    public string Side { get; set; } = "pmc";
}

public sealed class TransitPoint
{
    public double X { get; set; }
    public double Z { get; set; }
}

public sealed class GameMarker
{
    public double X { get; set; }
    public double Z { get; set; }
}

/// <summary>A floor overlay ready for drawing: the bitmap plus its ordering.</summary>
public sealed class FloorLayerView
{
    public string Name { get; init; } = "";
    /// <summary>Decoded artwork; filled in lazily the first time the level is shown.</summary>
    public Avalonia.Media.Imaging.Bitmap? Bitmap { get; set; }
    public int Order { get; init; }
    public double? MinHeight { get; init; }
    public double? MaxHeight { get; init; }
    /// <summary>Backing layer entry, used to fetch Bitmap on demand.</summary>
    public MapLayer? Source { get; init; }

    public bool MatchesHeight(double y) =>
        (MinHeight is null || y >= MinHeight.Value) &&
        (MaxHeight is null || y <= MaxHeight.Value);
}

/// <summary>Minefield / sniper area, drawn as a translucent danger polygon.</summary>
public sealed class HazardZone
{
    /// <summary>minefield | sniper | hazard</summary>
    public string Type { get; set; } = "hazard";
    public string Name { get; set; } = "";
    public List<GameMarker> Points { get; set; } = new();
}

/// <summary>
/// Presentation rules for hazard polygons, kept out of the control so the
/// headless checks can assert the same thresholds.
/// </summary>
public static class HazardRules
{
    /// <summary>
    /// Generic "hazard" zones bigger than this share of the map bounds are
    /// area-level triggers, not walkable trap patches. The Labyrinth has one
    /// 53 x 58 m rectangle over its whole centre (25.7% of the bounds); filling
    /// or even outlining it buries the map, so MapCanvas draws one warning
    /// marker instead. tarkov.dev does the same thing by hiding hazard outlines
    /// until you hover their marker.
    /// </summary>
    public const double AreaLevelFraction = 0.02;

    /// <summary>Shoelace area of a game-space polygon. Returns 0 for &lt; 3 points.</summary>
    public static double Area(IReadOnlyList<GameMarker> points)
    {
        if (points.Count < 3) return 0;
        var twice = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            twice += a.X * b.Z - b.X * a.Z;
        }
        return Math.Abs(twice) / 2;
    }

    public static double Area(MapDefinition map) =>
        map.Bounds.Length < 4
            ? 0
            : Math.Abs((map.MaxX - map.MinX) * (map.MaxZ - map.MinZ));

    /// <summary>True when the zone is too big to paint as a normal hazard patch.</summary>
    public static bool IsAreaLevel(HazardZone zone, MapDefinition map)
    {
        // Minefields and snipers are always meaningful as areas, however large.
        if (zone.Type is "minefield" or "sniper") return false;
        var mapArea = Area(map);
        return mapArea > 0 && Area(zone.Points) > mapArea * AreaLevelFraction;
    }
}
