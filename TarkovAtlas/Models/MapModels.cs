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
    /// <summary>
    /// Optional thin-plate-spline correction over the affine: paired game and
    /// image control points. When present the canvas warps the affine result so
    /// locally-squeezed artwork (hand-calibrated maps) still lines up.
    /// </summary>
    public ThinPlateSpline? Spline { get; set; }
    /// <summary>minX, minZ, maxX, maxZ in game units.</summary>
    public double[] Bounds { get; set; } = Array.Empty<double>();
    /// <summary>
    /// left, top, right, bottom of the drawn artwork inside the baked canvas,
    /// in pixels. Some canvases are mostly empty (Icebreaker draws the hull on a
    /// fifth of its tile canvas), so the view fits this rect instead of the bitmap.
    /// Pixels rather than game coordinates because a rotated affine cannot map an
    /// axis-aligned artwork rect to an axis-aligned world rect.
    /// </summary>
    public double[] Content { get; set; } = Array.Empty<double>();
    public List<MapLabel> Labels { get; set; } = new();
    public List<ExtractPoint> Extracts { get; set; } = new();
    public List<BossSpawn> Bosses { get; set; } = new();
    public List<SpawnPoint> Spawns { get; set; } = new();
    public List<TransitPoint> Transits { get; set; } = new();
    /// <summary>
    /// Buried stash / cache spots (物资埋藏点). Sourced from the tarkov.dev
    /// lootContainer table, filtered to the buried-barrel-cache and ground-cache
    /// container types, which are the ones the game hides under the ground.
    /// </summary>
    public List<CachePoint> Caches { get; set; } = new();
    /// <summary>
    /// Named points of interest that are not extracts/bosses/loot tables —
    /// currently the Labyrinth chamber puzzle items (解谜道具).
    /// </summary>
    public List<PoiPoint> Pois { get; set; } = new();
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

    [JsonIgnore] public bool HasContent => Content.Length >= 4
        && Math.Abs(Content[2] - Content[0]) > 0.01 && Math.Abs(Content[3] - Content[1]) > 0.01;
    [JsonIgnore] public double ContentMinX => HasContent ? Math.Min(Content[0], Content[2]) : 0;
    [JsonIgnore] public double ContentMinZ => HasContent ? Math.Min(Content[1], Content[3]) : 0;
    [JsonIgnore] public double ContentMaxX => HasContent ? Math.Max(Content[0], Content[2]) : Width;
    [JsonIgnore] public double ContentMaxZ => HasContent ? Math.Max(Content[1], Content[3]) : Height;

    public bool HasValidAffine => Affine.Length == 6 && Math.Abs(Affine[0] * Affine[4] - Affine[1] * Affine[3]) > 1e-9;

    public (double X, double Y) GameToImage(double x, double z)
    {
        if (Affine.Length != 6) return (0, 0);
        var p = (Affine[0] * x + Affine[1] * z + Affine[2],
                 Affine[3] * x + Affine[4] * z + Affine[5]);
        if (Spline is null) return p;
        // The spline corrects the affine, so it needs the affine itself; the
        // repository injects it after load, but headless consumers (datacheck)
        // may construct maps directly.
        Spline.Affine ??= Affine;
        return Spline.Correct(p.Item1, p.Item2);
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

/// <summary>
/// A buried stash / cache (物资埋藏点): loot the player has to dig up, so it is
/// worth its own marker rather than being lumped in with loose loot.
/// </summary>
public sealed class CachePoint
{
    public double X { get; set; }
    public double Z { get; set; }
    /// <summary>buried-barrel-cache | ground-cache</summary>
    public string Kind { get; set; } = "ground-cache";
}

/// <summary>A labelled point of interest pinned on the map (e.g. 解谜道具).</summary>
public sealed class PoiPoint
{
    public double X { get; set; }
    public double Z { get; set; }
    /// <summary>Short category slug, e.g. "puzzle".</summary>
    public string Kind { get; set; } = "puzzle";
    /// <summary>Display label shown next to the dot.</summary>
    public string Text { get; set; } = "";
}

/// <summary>Per-map marker sets carried by markers.json (split from mapdata).</summary>
public sealed class MapPoints
{
    public List<ExtractPoint> Extracts { get; set; } = new();
    public List<BossSpawn> Bosses { get; set; } = new();
    public List<SpawnPoint> Spawns { get; set; } = new();
    public List<TransitPoint> Transits { get; set; } = new();
    public List<CachePoint> Caches { get; set; } = new();
    public List<PoiPoint> Pois { get; set; } = new();
}

/// <summary>Root of markers.json.</summary>
public sealed class MarkersManifest
{
    public string GeneratedAt { get; set; } = "";
    public Dictionary<string, MapPoints> Maps { get; set; } = new();
}

/// <summary>
/// Thin-plate-spline correction: paired game and image control points. The
/// canvas first applies the affine, then adds the TPS displacement so the
/// mapping follows locally-squeezed artwork. Exact interpolation at the
/// control points degrades gracefully to the affine between them.
/// </summary>
public sealed class ThinPlateSpline
{
    /// <summary>Control points in game coordinates.</summary>
    public List<List<double>> GamePoints { get; set; } = new();
    /// <summary>Matching control points in image pixels (the correction target).</summary>
    public List<List<double>> ImgPoints { get; set; } = new();

    [JsonIgnore] private double[,]? _w;
    [JsonIgnore] private double[]? _a;
    [JsonIgnore] private List<(double X, double Z)>? _pts;

    private static double U(double r2)
    {
        if (r2 < 1e-12) return 0;
        // TPS kernel U(r) = r^2 * log(r); work with r^2 directly.
        return 0.5 * r2 * Math.Log(r2);
    }

    /// <summary>Solve the TPS system once (Lamé coefficients + affine part).</summary>
    private void Solve()
    {
        if (_w is not null || GamePoints.Count == 0) return;
        var pts = GamePoints.Select(p => (p[0], p[1])).ToList();
        var tps = ImgPoints.Select(p => (p[0], p[1])).ToList();
        int n = pts.Count;
        // L = [[K, P], [P^T, 0]] where K_ij = U(|pi - pj|^2), P rows = [1, x, z].
        int m = n + 3;
        var L = new double[m, m];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double dx = pts[i].Item1 - pts[j].Item1;
                double dz = pts[i].Item2 - pts[j].Item2;
                L[i, j] = U(dx * dx + dz * dz);
            }
            L[i, n] = 1; L[i, n + 1] = pts[i].Item1; L[i, n + 2] = pts[i].Item2;
        }
        for (int j = 0; j < n; j++)
        {
            L[n, j] = 1; L[n + 1, j] = pts[j].Item1; L[n + 2, j] = pts[j].Item2;
        }
        // Targets: the DISPLACEMENT from the affine-projected point to the
        // control point, so the spline corrects the affine rather than replacing it.
        var rx = new double[m]; var ry = new double[m];
        for (int i = 0; i < n; i++)
        {
            var proj = ProjectAffine(pts[i].Item1, pts[i].Item2);
            rx[i] = tps[i].Item1 - proj.X;
            ry[i] = tps[i].Item2 - proj.Y;
        }
        var wx = SolveLinear(L, rx);
        var wy = SolveLinear(L, ry);
        _w = new double[n, 2];
        for (int i = 0; i < n; i++)
        {
            _w[i, 0] = wx[i];
            _w[i, 1] = wy[i];
        }
        _a = new[] { wx[n], wx[n + 1], wx[n + 2], ry[n], ry[n + 1], ry[n + 2] };
        _pts = pts;
    }

    private (double X, double Y) ProjectAffine(double x, double z)
        => (Affine![0] * x + Affine[1] * z + Affine[2],
            Affine[3] * x + Affine[4] * z + Affine[5]);

    /// <summary>Owning map's affine, injected by the model when deserializing.</summary>
    [JsonIgnore] internal double[]? Affine;

    private static double[] SolveLinear(double[,] a, double[] b)
    {
        int m = b.Length;
        var lu = (double[,])a.Clone();
        var r = (double[])b.Clone();
        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int row = col + 1; row < m; row++)
                if (Math.Abs(lu[row, col]) > Math.Abs(lu[piv, col])) piv = row;
            if (piv != col)
            {
                for (int k = 0; k < m; k++) (lu[col, k], lu[piv, k]) = (lu[piv, k], lu[col, k]);
                (r[col], r[piv]) = (r[piv], r[col]);
            }
            for (int row = col + 1; row < m; row++)
            {
                double f = lu[row, col] / lu[col, col];
                if (f == 0) continue;
                for (int k = col; k < m; k++) lu[row, k] -= f * lu[col, k];
                r[row] -= f * r[col];
            }
        }
        var x = new double[m];
        for (int row = m - 1; row >= 0; row--)
        {
            double s = r[row];
            for (int k = row + 1; k < m; k++) s -= lu[row, k] * x[k];
            x[row] = s / lu[row, row];
        }
        return x;
    }

    /// <summary>Correct an affine-projected image point with the spline displacement.</summary>
    public (double X, double Y) Correct(double px, double py)
    {
        Solve();
        if (_w is null || _a is null || _pts is null) return (px, py);
        double dx = _a[0], dy = _a[3];
        for (int i = 0; i < _pts.Count; i++)
        {
            double ddx = px - _pts[i].Item1;
            double ddz = py - _pts[i].Item2;
            double u = U(ddx * ddx + ddz * ddz);
            dx += _w[i, 0] * u;
            dy += _w[i, 1] * u;
        }
        return (px + dx, py + dy);
    }
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
