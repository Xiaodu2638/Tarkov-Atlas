using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TarkovAtlas.Models;

namespace TarkovAtlas.Controls;

/// <summary>Rendering options shared by the main view and the overlay.</summary>
public sealed class MapDisplayOptions
{
    public bool ShowLabels { get; set; } = true;
    public bool ShowExtracts { get; set; } = true;
    /// <summary>all | pmc | scav | shared</summary>
    public string ExtractFilter { get; set; } = "all";
    public bool ShowBosses { get; set; } = true;
    public bool ShowSpawns { get; set; }
    public bool ShowTransits { get; set; }
    public bool ShowQuestMarkers { get; set; } = true;
    /// <summary>Draw minefield / sniper danger polygons.</summary>
    public bool ShowHazards { get; set; } = true;

    public MapDisplayOptions Clone() => (MapDisplayOptions)MemberwiseClone();
}

public sealed class MapCanvas : Control
{
    private static readonly Typeface LabelTypeface = new("Microsoft YaHei UI");
    private static readonly Typeface MarkerTypeface = new("Microsoft YaHei UI");

    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#E4ECF3"));
    private static readonly IBrush LabelShadow = new SolidColorBrush(Color.Parse("#E605080B"));
    private static readonly IBrush EmptyBrush = new SolidColorBrush(Color.Parse("#7D8D9A"));
    private static readonly IBrush PinBrush = new SolidColorBrush(Color.Parse("#E0523D"));
    private static readonly IBrush PinStroke = new SolidColorBrush(Color.Parse("#FFF3DF"));
    private static readonly IBrush PlayerBrush = new SolidColorBrush(Color.Parse("#FFD166"));
    private static readonly IBrush PlayerDark = new SolidColorBrush(Color.Parse("#D98A22"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#D6A84B"));
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#D90B1017"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#8FA0AE"));
    private static readonly IBrush BorderBrush = new SolidColorBrush(Color.Parse("#3A4856"));

    private static readonly IBrush ExtractPmc = new SolidColorBrush(Color.Parse("#5FD68A"));
    private static readonly IBrush ExtractScav = new SolidColorBrush(Color.Parse("#E0C063"));
    private static readonly IBrush ExtractShared = new SolidColorBrush(Color.Parse("#67B2E8"));
    private static readonly IBrush BossBrush = new SolidColorBrush(Color.Parse("#E06052"));
    private static readonly IBrush BossHalo = new SolidColorBrush(Color.Parse("#33E06052"));
    private static readonly IBrush SpawnPmcBrush = new SolidColorBrush(Color.Parse("#7FA8E0"));
    private static readonly IBrush SpawnScavBrush = new SolidColorBrush(Color.Parse("#D08A5A"));
    private static readonly IBrush TransitBrush = new SolidColorBrush(Color.Parse("#B07FD8"));

    private readonly Dictionary<string, Bitmap?> _avatarCache = new(StringComparer.OrdinalIgnoreCase);

    private Bitmap? _bitmap;
    private MapDefinition? _map;
    private double _fit = 1;
    private double _zoom = 1;
    private Vector _pan;
    private Point _origin;
    private bool _panning;
    private Point _dragStart;
    private Vector _panStart;
    private Point _lastPointer;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        RenderOptions.SetEdgeMode(this, EdgeMode.Antialias);
    }

    public MapDefinition? Map => _map;
    public double Zoom => _zoom;
    public bool Compact { get; set; }
    public IReadOnlyList<FloorLayerView> Layers { get; set; } = Array.Empty<FloorLayerView>();
    public IReadOnlyList<HazardZone> Hazards { get; set; } = Array.Empty<HazardZone>();
    public MapDisplayOptions Options { get; set; } = new();
    public IReadOnlyList<ScreenshotInfo> Pins { get; set; } = Array.Empty<ScreenshotInfo>();
    public IReadOnlyList<QuestMarker> QuestMarkers { get; set; } = Array.Empty<QuestMarker>();
    public ScreenshotInfo? ActivePin { get; set; }

    public event EventHandler<ScreenshotInfo>? PinClicked;
    /// <summary>Raised when the user clicks a quest marker on the map.</summary>
    public event EventHandler<QuestMarker>? MarkerClicked;

    private readonly List<(QuestMarker Marker, Rect Box)> _markerHits = new();
    public event EventHandler<(double X, double Z)>? CursorGameMoved;

    public void SetMap(MapDefinition? map, Bitmap? bitmap)
    {
        var changed = !ReferenceEquals(map, _map);
        _map = map;
        _bitmap = bitmap;
        if (changed)
        {
            _zoom = 1;
            _pan = default;
            _avatarCache.Clear();
            Hazards = map?.Hazards ?? new List<HazardZone>();
            _hazardCache = null;
            _hazardCacheMap = null;
        }
        InvalidateVisual();
    }

    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    public void ZoomIn() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1.3);
    public void ZoomOut() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1 / 1.3);
    public void SetZoom(double zoom) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), zoom / Math.Max(0.0001, _zoom));

    /// <summary>Center the view on a game position at the given zoom.</summary>
    public void FocusOn(double gameX, double gameZ, double zoom = 3.2)
    {
        if (_map is null || !_map.HasValidAffine || Bounds.Width <= 0) return;
        EnsureLayout();
        _zoom = Math.Clamp(zoom, 0.35, 32);
        var p = _map.GameToImage(gameX, gameZ);
        var scale = _fit * _zoom;
        var drawW = _map.Width * scale;
        var drawH = _map.Height * scale;
        _pan = new Vector(drawW / 2 - p.X * scale, drawH / 2 - p.Y * scale);
        InvalidateVisual();
    }

    /// <summary>Center on a game position while keeping the current zoom.</summary>
        private void EnsureLayout()
    {
        if (_map is null || Bounds.Width <= 1 || Bounds.Height <= 1) return;
        var pad = Compact ? 0 : 22;
        _fit = Math.Min((Bounds.Width - pad * 2) / _map.Width, (Bounds.Height - pad * 2) / _map.Height);
        _fit = Math.Max(0.0001, _fit);
        var drawW = _map.Width * _fit * _zoom;
        var drawH = _map.Height * _fit * _zoom;
        _origin = new Point(
            (Bounds.Width - drawW) / 2 + _pan.X,
            (Bounds.Height - drawH) / 2 + _pan.Y);
    }

    private double Scale => _fit * _zoom;

    private Point ImageToScreen(Point p) => new(_origin.X + p.X * Scale, _origin.Y + p.Y * Scale);

    private Point ScreenToImage(Point s) => new((s.X - _origin.X) / Scale, (s.Y - _origin.Y) / Scale);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Compact ? CompactBackground : CanvasBackground, new Rect(Bounds.Size));

        if (_map is null || _bitmap is null)
        {
            if (Compact) return;
            var text = new FormattedText(
                _map is null ? "选择一张地图" : "无法加载地图图片",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, 18, EmptyBrush);
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
            return;
        }

        EnsureLayout();
        var dest = new Rect(_origin.X, _origin.Y, _map.Width * Scale, _map.Height * Scale);
        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), dest);

        // Floor artwork is baked into the base image's pixel grid, so it can be
        // stretched over exactly the same destination rectangle.
        foreach (var layer in Layers)
        {
            if (layer.Bitmap is null) continue;
            using (context.PushOpacity(Compact ? 0.85 : 0.75))
                context.DrawImage(layer.Bitmap,
                    new Rect(0, 0, layer.Bitmap.PixelSize.Width, layer.Bitmap.PixelSize.Height), dest);
        }

        if (!Compact)
            context.DrawRectangle(null, new Pen(BorderBrush, 1), dest);

        if (Options.ShowHazards) DrawHazards(context);
        if (Options.ShowSpawns) DrawSpawns(context);
        if (Options.ShowTransits) DrawTransits(context);
        if (Options.ShowExtracts) DrawExtracts(context);
        if (Options.ShowBosses) DrawBosses(context);
        if (Options.ShowQuestMarkers) DrawQuestMarkers(context);
        if (Options.ShowLabels && !Compact) DrawLabels(context);

        DrawPins(context);
        DrawActivePlayer(context, large: !Compact);

        if (!Compact)
        {
            DrawScaleBar(context);
            DrawCompass(context);
        }
    }

    // ------------------------------------------------------------------
    // markers

    private bool ExtractVisible(ExtractPoint e) => Options.ExtractFilter switch
    {
        "pmc" => e.Faction is "pmc" or "shared",
        "scav" => e.Faction is "scav" or "shared",
        "shared" => e.Faction is "shared",
        _ => true,
    };

    private void DrawExtracts(DrawingContext context)
    {
        if (_map is null || _zoom < 0.55) return;
        foreach (var e in _map.Extracts)
        {
            if (!ExtractVisible(e)) continue;
            var s = Project(e.X, e.Z);
            if (!OnScreen(s, 40)) continue;

            var brush = e.Faction == "scav" ? ExtractScav : e.Faction == "shared" ? ExtractShared : ExtractPmc;
            var size = Math.Clamp(6.5 * Math.Sqrt(_zoom), 5.5, 11);
            // Diamond marker so extracts never collide with round quest dots.
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(s.X, s.Y - size), true);
                g.LineTo(new Point(s.X + size, s.Y));
                g.LineTo(new Point(s.X, s.Y + size));
                g.LineTo(new Point(s.X - size, s.Y));
                g.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#CC0A0F15")), new Pen(brush, 2), geo);

            if (_zoom >= 1.15 && !Compact)
            {
                var label = new FormattedText(e.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    MarkerTypeface, 10.5, brush);
                var pos = new Point(s.X - label.Width / 2, s.Y + size + 3);
                context.DrawText(new FormattedText(e.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    MarkerTypeface, 10.5, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
                context.DrawText(label, pos);
            }
        }
    }

    private void DrawBosses(DrawingContext context)
    {
        if (_map is null || _zoom < 0.7) return;
        foreach (var boss in _map.Bosses)
        {
            foreach (var point in boss.Points)
            {
                var s = Project(point.X, point.Z);
                if (!OnScreen(s, 40)) continue;

                // A coloured dot gave no clue which boss was which; draw the portrait.
                var r = Math.Clamp(9.0 * Math.Sqrt(_zoom), 9, 20);
                var avatar = LoadCached("bosses", SlugForMob(boss.Mob));
                if (avatar is not null)
                {
                    context.DrawEllipse(BossHalo, null, s, r + 2, r + 2);
                    using (context.PushGeometryClip(new EllipseGeometry(new Rect(s.X - r, s.Y - r, r * 2, r * 2))))
                        context.DrawImage(avatar,
                            new Rect(0, 0, avatar.PixelSize.Width, avatar.PixelSize.Height),
                            new Rect(s.X - r, s.Y - r, r * 2, r * 2));
                    context.DrawEllipse(null, new Pen(BossBrush, 1.4), s, r, r);
                }
                else
                {
                    context.DrawEllipse(BossHalo, null, s, r + 7, r + 7);
                    context.DrawEllipse(BossBrush, new Pen(new SolidColorBrush(Color.Parse("#FFE8DF")), 1.2), s, r, r);
                    var initial = string.IsNullOrEmpty(boss.Name) ? "?" : boss.Name[..1].ToUpperInvariant();
                    var itxt = new FormattedText(initial, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        MarkerTypeface, r, new SolidColorBrush(Color.Parse("#171207")));
                    context.DrawText(itxt, new Point(s.X - itxt.Width / 2, s.Y - itxt.Height / 2));
                }

                if (!Compact)
                {
                    var size = _zoom >= 1.7 ? 10.5 : 9;
                    var txt = new FormattedText(boss.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        MarkerTypeface, size, BossBrush);
                    var pos = new Point(s.X + r + 4, s.Y - txt.Height / 2);
                    context.DrawText(new FormattedText(boss.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        MarkerTypeface, size, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
                    context.DrawText(txt, pos);
                }
            }
        }
    }

    /// <summary>
    /// Map an API mob id (bossKilla) to its portrait file name (killa). A few
    /// bosses do not use the in-game codename, so they are listed explicitly.
    /// </summary>
        private static readonly IBrush CanvasBackground = new SolidColorBrush(Color.Parse("#0A0F15"));
    private static readonly IBrush CompactBackground = new SolidColorBrush(Color.Parse("#080B10"));

    private static readonly IBrush MinefieldFill = new SolidColorBrush(Color.Parse("#33E0523D"));
    private static readonly IBrush MinefieldStroke = new SolidColorBrush(Color.Parse("#CCE0523D"));
    private static readonly IBrush SniperFill = new SolidColorBrush(Color.Parse("#33E0A23D"));
    private static readonly IBrush SniperStroke = new SolidColorBrush(Color.Parse("#CCE0A23D"));
    private static readonly IBrush HazardFill = new SolidColorBrush(Color.Parse("#33D06FE0"));
    private static readonly IBrush HazardStroke = new SolidColorBrush(Color.Parse("#CCD06FE0"));
    private static readonly IBrush HazardGlyph = new SolidColorBrush(Color.Parse("#0A0F15"));

    // Polygon outlines are cached in *image* coordinates (independent of pan
    // and zoom) and transformed at draw time. Rebuilding 330 StreamGeometry
    // objects on every frame was the main reason panning felt laggy.
    private List<(Geometry Geometry, IBrush Fill, IBrush Stroke)>? _hazardCache;
    private List<(double X, double Z)>? _hazardMarkers;
    private MapDefinition? _hazardCacheMap;

    private void DrawHazards(DrawingContext context)
    {
        if (_map is null || _zoom < 0.7) return;

        if (!ReferenceEquals(_hazardCacheMap, _map))
        {
            _hazardCache = new List<(Geometry, IBrush, IBrush)>();
            _hazardMarkers = new List<(double X, double Z)>();
            foreach (var zone in Hazards)
            {
                if (zone.Points.Count < 3) continue;

                if (HazardRules.IsAreaLevel(zone, _map))
                {
                    // Area-level trigger (e.g. the Labyrinth's 53 x 58 m box).
                    // A filled polygon here covers the whole map, so keep only
                    // a warning marker at its centre.
                    var cx = zone.Points.Average(p => p.X);
                    var cz = zone.Points.Average(p => p.Z);
                    _hazardMarkers.Add((cx, cz));
                    continue;
                }

                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    var first = _map.GameToImage(zone.Points[0].X, zone.Points[0].Z);
                    g.BeginFigure(new Point(first.X, first.Y), true);
                    for (var i = 1; i < zone.Points.Count; i++)
                    {
                        var p = _map.GameToImage(zone.Points[i].X, zone.Points[i].Z);
                        g.LineTo(new Point(p.X, p.Y));
                    }
                    g.EndFigure(true);
                }

                var (fill, stroke) = zone.Type switch
                {
                    "minefield" => (MinefieldFill, MinefieldStroke),
                    "sniper" => (SniperFill, SniperStroke),
                    _ => (HazardFill, HazardStroke),
                };
                _hazardCache.Add((geo, fill, stroke));
            }
            _hazardCacheMap = _map;
        }

        if (_hazardCache is null) return;

        using (context.PushTransform(Matrix.CreateScale(Scale, Scale) *
                                     Matrix.CreateTranslation(_origin.X, _origin.Y)))
        {
            foreach (var (geometry, fill, stroke) in _hazardCache)
                context.DrawGeometry(fill, new Pen(stroke, 1.4 / Math.Max(0.0001, Scale)), geometry);
        }

        if (_hazardMarkers is null) return;
        foreach (var (x, z) in _hazardMarkers)
        {
            var s = Project(x, z);
            if (OnScreen(s, 30)) DrawHazardWarning(context, s);
        }
    }

    /// <summary>Small "!" badge used for hazards too large to draw as an area.</summary>
    private void DrawHazardWarning(DrawingContext context, Point s)
    {
        var r = Math.Clamp(6.5 * Math.Sqrt(_zoom), 5.5, 9.5);
        context.DrawEllipse(HazardStroke, new Pen(HazardGlyph, 1.2), s, r, r);

        var bang = new FormattedText("!", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            MarkerTypeface, r * 1.7, HazardGlyph);
        context.DrawText(bang, new Point(s.X - bang.Width / 2, s.Y - bang.Height / 2));

        if (_zoom < 1.15 || Compact) return;
        var label = new FormattedText("危险区", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            MarkerTypeface, 10.5, HazardStroke);
        var pos = new Point(s.X - label.Width / 2, s.Y + r + 3);
        context.DrawText(new FormattedText("危险区", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            MarkerTypeface, 10.5, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
        context.DrawText(label, pos);
    }

    private void DrawSpawns(DrawingContext context)
    {
        if (_map is null || _zoom < 1.2) return;
        foreach (var sp in _map.Spawns)
        {
            var s = Project(sp.X, sp.Z);
            if (!OnScreen(s, 20)) continue;
            var brush = sp.Side == "scav" ? SpawnScavBrush : SpawnPmcBrush;
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#66222830")), new Pen(brush, 1), s, 4, 4);
        }
    }

    private void DrawTransits(DrawingContext context)
    {
        if (_map is null || _zoom < 0.9) return;
        foreach (var t in _map.Transits)
        {
            var s = Project(t.X, t.Z);
            if (!OnScreen(s, 30)) continue;
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#CC0A0F15")), new Pen(TransitBrush, 1.6),
                new Rect(s.X - 5, s.Y - 5, 10, 10));
        }
    }

    private void DrawQuestMarkers(DrawingContext context)
    {
        if (_map is null || QuestMarkers.Count == 0) return;
        var radius = Compact ? 11.0 : Math.Clamp(11 * Math.Sqrt(_zoom), 10, 16);
        _markerHits.Clear();
        foreach (var marker in QuestMarkers)
        {
            var s = Project(marker.X, marker.Z);
            if (!OnScreen(s, 70)) continue;
            _markerHits.Add((marker, new Rect(s.X - radius, s.Y - radius, radius * 2, radius * 2)));

            context.DrawEllipse(new SolidColorBrush(Color.Parse("#8805080B")), null,
                new Point(s.X + 1, s.Y + 2), radius + 1, radius + 1);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#F2D6A84B")), new Pen(new SolidColorBrush(Color.Parse("#FFF6E7C4")), 1.6),
                s, radius, radius);

            var avatar = LoadAvatar(marker.TraderSlug);
            if (avatar is not null)
            {
                var rect = new Rect(s.X - radius + 2, s.Y - radius + 2, radius * 2 - 4, radius * 2 - 4);
                context.DrawImage(avatar, new Rect(0, 0, avatar.PixelSize.Width, avatar.PixelSize.Height), rect);
            }
            else
            {
                var initial = string.IsNullOrEmpty(marker.TraderName) ? "?" : marker.TraderName[..1].ToUpperInvariant();
                var txt = new FormattedText(initial, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    MarkerTypeface, radius, new SolidColorBrush(Color.Parse("#171207")));
                context.DrawText(txt, new Point(s.X - txt.Width / 2, s.Y - txt.Height / 2));
            }

            if (_zoom >= 1.6 && !Compact)
            {
                var txt = new FormattedText(marker.QuestName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    MarkerTypeface, 10, new SolidColorBrush(Color.Parse("#F0E4C4")));
                var pos = new Point(s.X - txt.Width / 2, s.Y + radius + 2);
                context.DrawText(new FormattedText(marker.QuestName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    MarkerTypeface, 10, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
                context.DrawText(txt, pos);
            }
        }
    }

    private Bitmap? LoadAvatar(string slug) => LoadCached("traders", slug);

    /// <summary>Load a portrait from Assets/{folder}/{slug}.png, memoised.</summary>
    private Bitmap? LoadCached(string folder, string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var key = folder + "/" + slug;
        if (_avatarCache.TryGetValue(key, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", folder, slug + ".png");
            if (File.Exists(path))
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                bmp = new Bitmap(stream);
            }
        }
        catch
        {
            bmp = null;
        }
        _avatarCache[key] = bmp;
        return bmp;
    }

    /// <summary>
    /// Map an API mob id (bossKilla) to its portrait file name (killa). A few
    /// bosses do not use the in-game codename, so they are listed explicitly.
    /// </summary>
    private static string SlugForMob(string? mob)
    {
        if (string.IsNullOrWhiteSpace(mob)) return "";
        return mob.ToLowerInvariant() switch
        {
            "bossbully" or "bossbullyblackdiv" => "reshala",
            "bosskojaniy" => "shturman",
            "bossboar" => "kaban",
            _ when mob.StartsWith("boss", StringComparison.OrdinalIgnoreCase) =>
                mob[4..].Replace("_", "-").ToLowerInvariant(),
            _ => mob.Replace("_", "-").ToLowerInvariant(),
        };
    }

    private Point Project(double x, double z)
    {
        var p = _map!.GameToImage(x, z);
        return ImageToScreen(new Point(p.X, p.Y));
    }

    private bool OnScreen(Point s, double margin) =>
        s.X >= -margin && s.Y >= -margin && s.X <= Bounds.Width + margin && s.Y <= Bounds.Height + margin;

    // ------------------------------------------------------------------
    // labels / player / chrome

    private void DrawLabels(DrawingContext context)
    {
        if (_map is null || _zoom < 1.05) return;
        foreach (var label in _map.Labels)
        {
            if (label.Text.Length == 0) continue;
            if (_zoom < 1.9 && label.Size < 80) continue;

            var s = Project(label.X, label.Z);
            if (s.X < -140 || s.Y < -40 || s.X > Bounds.Width + 140 || s.Y > Bounds.Height + 40) continue;

            var fontSize = Math.Clamp(11 + label.Size / 100.0 * 2.4, 10.5, 16);
            var text = new FormattedText(label.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, fontSize, LabelBrush);
            var pos = new Point(s.X - text.Width / 2, s.Y - text.Height / 2);
            context.DrawText(new FormattedText(label.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, fontSize, LabelShadow), new Point(pos.X + 1.4, pos.Y + 1.4));
            context.DrawText(text, pos);
        }
    }

    private void DrawPins(DrawingContext context)
    {
        if (_map is null || Compact) return;
        var index = 0;
        foreach (var pin in Pins)
        {
            index++;
            var s = Project(pin.X, pin.Z);
            if (!OnScreen(s, 30)) continue;
            var active = ReferenceEquals(pin, ActivePin);
            var r = active ? 7.0 : 5.2;
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#33000000")), null,
                new Point(s.X + 1, s.Y + 2), r + 2, r + 2);
            context.DrawEllipse(PinBrush, new Pen(PinStroke, active ? 2 : 1.2), s, r, r);
            if (active || _zoom >= 1.8)
            {
                var txt = new FormattedText(index.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, LabelTypeface, 9, Brushes.White);
                context.DrawText(txt, new Point(s.X - txt.Width / 2, s.Y - txt.Height / 2));
            }
        }
    }

    private void DrawActivePlayer(DrawingContext context, bool large)
    {
        if (_map is null) return;
        var pin = ActivePin ?? (Pins.Count > 0 ? Pins[^1] : null);
        if (pin is null) return;
        var s = Project(pin.X, pin.Z);
        if (!OnScreen(s, 60)) return;

        var radius = large ? 9.0 : 6.0;
        var headingRad = pin.HeadingDegrees * Math.PI / 180.0;
        var dir = _map.GameVectorToImage(Math.Sin(headingRad), Math.Cos(headingRad));
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len > 1e-6)
        {
            var nx = dir.X / len;
            var ny = dir.Y / len;
            var reach = large ? 30 : 22;
            var tip = new Point(s.X + nx * reach, s.Y + ny * reach);
            var left = new Point(s.X - ny * 7, s.Y + nx * 7);
            var right = new Point(s.X + ny * 7, s.Y - nx * 7);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(left);
                g.LineTo(right);
                g.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#B3FFD166")), new Pen(PlayerBrush, 1.2), geo);
        }

        context.DrawEllipse(new SolidColorBrush(Color.Parse("#55FFD166")), null, s, radius + 8, radius + 8);
        context.DrawEllipse(PlayerDark, new Pen(PinStroke, 1.5), s, radius, radius);
        context.DrawEllipse(PlayerBrush, null, s, radius - 3, radius - 3);

        if (large)
        {
            var txt = new FormattedText($"你  {pin.X:0}, {pin.Z:0}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelTypeface, 11, PlayerBrush);
            var box = new Rect(s.X + 14, s.Y - 30, txt.Width + 14, txt.Height + 8);
            context.DrawRectangle(PanelBrush, new Pen(new SolidColorBrush(Color.Parse("#66D6A84B")), 1),
                new RoundedRect(box, 5));
            context.DrawText(txt, new Point(box.X + 7, box.Y + 4));
        }
    }

    private void DrawScaleBar(DrawingContext context)
    {
        if (_map is null || !_map.HasValidAffine) return;
        var a = _map.ImageToGame(0, 0);
        var b = _map.ImageToGame(1, 0);
        var metersPerImagePixel = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
        var metersPerScreenPixel = metersPerImagePixel / Math.Max(0.0001, Scale);
        if (!double.IsFinite(metersPerScreenPixel) || metersPerScreenPixel <= 0) return;

        var nice = NiceNumber(metersPerScreenPixel * 110);
        var barWidth = nice / metersPerScreenPixel;
        if (barWidth < 35 || barWidth > 220) return;

        var x = 22.0;
        var y = Bounds.Height - 28;
        var label = new FormattedText($"{nice:0} m", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, 11, MutedBrush);
        context.DrawRectangle(PanelBrush, null, new RoundedRect(new Rect(x - 8, y - 22, barWidth + 42, 40), 6));
        context.DrawLine(new Pen(AccentBrush, 2), new Point(x, y), new Point(x + barWidth, y));
        context.DrawLine(new Pen(AccentBrush, 2), new Point(x, y - 4), new Point(x, y + 4));
        context.DrawLine(new Pen(AccentBrush, 2), new Point(x + barWidth, y - 4), new Point(x + barWidth, y + 4));
        context.DrawText(label, new Point(x + barWidth + 8, y - label.Height / 2));
    }

    private static double NiceNumber(double value)
    {
        var exp = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, value))));
        var f = value / exp;
        var nice = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
        return nice * exp;
    }

    private void DrawCompass(DrawingContext context)
    {
        var center = new Point(Bounds.Width - 36, 36);
        context.DrawEllipse(PanelBrush, new Pen(BorderBrush, 1), center, 19, 19);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(center.X, center.Y - 12), true);
            g.LineTo(new Point(center.X - 5, center.Y + 3));
            g.LineTo(new Point(center.X, center.Y));
            g.LineTo(new Point(center.X + 5, center.Y + 3));
            g.EndFigure(true);
        }
        context.DrawGeometry(AccentBrush, null, geo);
        var n = new FormattedText("N", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, 9, MutedBrush);
        context.DrawText(n, new Point(center.X - n.Width / 2, center.Y + 4));
    }

    // ------------------------------------------------------------------
    // input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.18 : 1 / 1.18;
        ZoomAt(e.GetPosition(this), factor);
        e.Handled = true;
    }

    private void ZoomAt(Point screen, double factor)
    {
        if (_map is null) return;
        EnsureLayout();
        var before = ScreenToImage(screen);
        _zoom = Math.Clamp(_zoom * factor, 0.35, 32);
        EnsureLayout();
        var newScale = Scale;
        var drawW = _map.Width * newScale;
        var drawH = _map.Height * newScale;
        _pan = new Vector(
            screen.X - (Bounds.Width - drawW) / 2 - before.X * newScale,
            screen.Y - (Bounds.Height - drawH) / 2 - before.Y * newScale);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return;

        var pos = e.GetPosition(this);
        if (e.ClickCount == 2 && !Compact)
        {
            ZoomAt(pos, 1.6);
            e.Handled = true;
            return;
        }

        var marker = HitTestMarker(pos);
        if (marker is not null)
        {
            MarkerClicked?.Invoke(this, marker);
            e.Handled = true;
            return;
        }

        var hit = HitTestPin(pos);
        if (hit is not null)
        {
            ActivePin = hit;
            PinClicked?.Invoke(this, hit);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _panning = true;
        _dragStart = pos;
        _panStart = _pan;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    private QuestMarker? HitTestMarker(Point screen)
    {
        foreach (var (marker, box) in _markerHits)
            if (box.Contains(screen)) return marker;
        return null;
    }

    private ScreenshotInfo? HitTestPin(Point screen)
    {
        if (_map is null || Compact) return null;
        foreach (var pin in Pins.Reverse())
        {
            var s = Project(pin.X, pin.Z);
            var dx = s.X - screen.X;
            var dy = s.Y - screen.Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= 14) return pin;
        }
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _lastPointer = pos;

        if (_panning)
        {
            _pan = _panStart + (pos - _dragStart);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_map is not null)
        {
            EnsureLayout();
            var img = ScreenToImage(pos);
            var game = _map.ImageToGame(img.X, img.Y);
            if (double.IsFinite(game.X) && double.IsFinite(game.Z))
                CursorGameMoved?.Invoke(this, game);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning)
        {
            _panning = false;
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_panning) Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Add:
            case Key.OemPlus:
                ZoomIn();
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomOut();
                e.Handled = true;
                break;
            case Key.Home:
            case Key.D0:
                ResetView();
                e.Handled = true;
                break;
        }
    }
}