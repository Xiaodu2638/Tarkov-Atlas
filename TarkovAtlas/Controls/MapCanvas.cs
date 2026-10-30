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
    /// <summary>Draw buried stash / cache spots (物资埋藏点).</summary>
    public bool ShowCaches { get; set; } = true;
    /// <summary>Loot container kinds currently toggled off in the legend.</summary>
    public IReadOnlyCollection<string> LootKindsHidden { get; set; } = Array.Empty<string>();
    /// <summary>Master switch for loot container markers (物资点).</summary>
    public bool ShowLoot { get; set; } = true;
    public bool ShowQuestMarkers { get; set; } = true;
    /// <summary>Draw minefield / sniper danger polygons.</summary>
    public bool ShowHazards { get; set; } = true;
    /// <summary>Draw BTR taxi stops (BTR 站点).</summary>
    public bool ShowBtrs { get; set; } = true;

    public MapDisplayOptions Clone() => (MapDisplayOptions)MemberwiseClone();
}

public sealed class MapCanvas : Control
{
    /// <summary>Legend swatch kinds, resolved to the same colours the canvas draws.</summary>
    public enum SwatchKind
    {
        ExtractPmc, Boss, SpawnPmc, Transit, CacheStroke, MinefieldStroke, Label, QuestMarkerStroke,
    }

    /// <summary>Resolve a legend swatch colour (frozen, shared with the canvas).</summary>
    public static IBrush PaletteBrush(SwatchKind kind) => kind switch
    {
        SwatchKind.ExtractPmc => Palette.Brush(Palette.ExtractPmc),
        SwatchKind.Boss => Palette.Brush(Palette.Boss),
        SwatchKind.SpawnPmc => Palette.Brush(Palette.SpawnPmc),
        SwatchKind.Transit => Palette.Brush(Palette.Transit),
        SwatchKind.CacheStroke => Palette.Brush(Palette.CacheStroke),
        SwatchKind.MinefieldStroke => Palette.Brush(Palette.MinefieldStroke),
        SwatchKind.Label => Palette.Brush(Palette.Label),
        SwatchKind.QuestMarkerStroke => Palette.Brush(Palette.QuestMarkerStroke),
        _ => Palette.Brush(Palette.Muted),
    };

    private static readonly Typeface LabelTypeface = new("Microsoft YaHei UI");

    private static readonly IBrush LabelBrush = Palette.Brush(Palette.Label);
    private static readonly IBrush LabelShadow = Palette.Brush(Palette.LabelShadow);
    private static readonly IBrush EmptyBrush = Palette.Brush(Palette.Empty);
    private static readonly IBrush PinBrush = Palette.Brush(Palette.Pin);
    private static readonly IBrush PinStroke = Palette.Brush(Palette.PinStroke);
    private static readonly IBrush PlayerBrush = Palette.Brush(Palette.Player);
    private static readonly IBrush PlayerDark = Palette.Brush(Palette.PlayerDark);
    private static readonly IBrush AccentBrush = Palette.Brush(Palette.Accent);
    private static readonly IBrush PanelBrush = Palette.Brush(Palette.OverlayPanelBg);
    private static readonly IBrush MutedBrush = Palette.Brush(Palette.Muted);
    private static readonly IBrush BorderBrush = Palette.Brush(Palette.BorderStrong);

    private static readonly IBrush ExtractPmc = Palette.Brush(Palette.ExtractPmc);
    private static readonly IBrush ExtractScav = Palette.Brush(Palette.ExtractScav);
    private static readonly IBrush ExtractShared = Palette.Brush(Palette.ExtractShared);
    private static readonly IBrush BossBrush = Palette.Brush(Palette.Boss);
    private static readonly IBrush BossHalo = Palette.Brush(Palette.BossHalo);
    private static readonly IBrush SpawnPmcBrush = Palette.Brush(Palette.SpawnPmc);
    private static readonly IBrush SpawnScavBrush = Palette.Brush(Palette.SpawnScav);
    private static readonly IBrush TransitBrush = Palette.Brush(Palette.Transit);
    /// <summary>Buried stash / cache markers (物资埋藏点).</summary>
    private static readonly IBrush CacheFill = Palette.Brush(Palette.CacheFill);
    private static readonly IBrush CacheStroke = Palette.Brush(Palette.CacheStroke);
    private static readonly Pen CachePen = new(CacheStroke, 1.5);

    // Per-frame brushes/pens used to be allocated for every marker; on Streets
    // that was hundreds of allocations per mouse-move. All of these are frozen
    // by convention and safe to share.
    private static readonly IBrush MarkerShadow = Palette.Brush(Palette.MarkerShadow);
    private static readonly IBrush QuestMarkerFill = Palette.Brush(Palette.QuestMarkerFill);
    private static readonly IBrush QuestMarkerStroke = Palette.Brush(Palette.QuestMarkerStroke);
    private static readonly IBrush ExtractFill = Palette.Brush(Palette.ExtractFill);
    private static readonly IBrush SpawnFill = Palette.Brush(Palette.SpawnFill);
    private static readonly IBrush TransitFill = Palette.Brush(Palette.TransitFill);
    private static readonly IBrush BossEmptyStroke = Palette.Brush(Palette.BossEmptyStroke);
    private static readonly IBrush BossInitial = Palette.Brush(Palette.BossInitial);
    private static readonly IBrush PinShadow = Palette.Brush(Palette.PinShadow);
    private static readonly IBrush PlayerHalo = Palette.Brush(Palette.PlayerHalo);
    private static readonly IBrush PlayerCone = Palette.Brush(Palette.PlayerCone);
    private static readonly IBrush PlayerPanelBorder = Palette.Brush(Palette.PlayerPanelBorder);
    private static readonly IBrush QuestLabelBrush = Palette.Brush(Palette.QuestLabel);
    private static readonly IBrush FocusHalo = Palette.Brush(Palette.FocusHalo);
    private static readonly IBrush DotBrush = Palette.Brush(Palette.Dot);

    private static readonly Pen ExtractPmcPen = new(ExtractPmc, 2);
    private static readonly Pen ExtractScavPen = new(ExtractScav, 2);
    private static readonly Pen ExtractSharedPen = new(ExtractShared, 2);
    private static readonly Pen BossRingPen = new(BossBrush, 1.4);
    private static readonly Pen BossEmptyPen = new(BossEmptyStroke, 1.2);
    private static readonly Pen SpawnPmcPen = new(SpawnPmcBrush, 1);
    private static readonly Pen SpawnScavPen = new(SpawnScavBrush, 1);
    private static readonly Pen TransitPen = new(TransitBrush, 1.6);
    private static readonly Pen QuestMarkerPen = new(QuestMarkerStroke, 1.6);
    private static readonly Pen PlayerPen = new(PinStroke, 1.5);
    private static readonly Pen ActivePinPen = new(PinStroke, 2);
    private static readonly Pen PinPen = new(PinStroke, 1.2);
    private static readonly Pen PlayerConePen = new(PlayerBrush, 1.2);
    private static readonly Pen PlayerPanelPen = new(PlayerPanelBorder, 1);
    private static readonly Pen BorderPen = new(BorderBrush, 1);
    private static readonly Pen AccentPen = new(AccentBrush, 2);

    private readonly Dictionary<string, Bitmap?> _avatarCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Folder, string Slug, int Size), Bitmap?> _circleCache = new();
    private readonly Dictionary<(string Text, double Size, IBrush Brush), FormattedText> _textCache = new();
    private readonly Dictionary<(IBrush Brush, double Width), Pen> _penCache = new();
    private readonly Dictionary<int, Geometry> _diamondCache = new();

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

    // Downscaled copy of the base artwork. Zoomed out, the viewport covers a
    // small fraction of a 5600 px image and the rasteriser re-samples the whole
    // texture every drag frame; drawing a pre-shrunk mip is the difference
    // between smooth panning and visible stutter.
    private Bitmap? _mipBitmap;
    private Bitmap? _mipSource;
    private double _mipFactor = 1;
    /// <summary>Mip of the floor overlay; swapped whenever the base mip swaps
    /// so a deck change rebuilds both together.</summary>
    private Bitmap? _floorMipBitmap;
    private Bitmap? _floorMipSource;
    private double _floorMipFactor = 1;

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
    /// <summary>POI kinds currently toggled off in the legend.</summary>
    public IReadOnlyCollection<string> PoiKindsHidden { get; set; } = Array.Empty<string>();
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
    private (double X, double Z, string Label)? _focusPin;
    /// <summary>
    /// Extra objective pins for a task jump: a single quest can have several
    /// zones on one map, so the view marks all of them and centres on the
    /// first. Each entry is labelled 1/2/3… in draw order.
    /// </summary>
    private List<(double X, double Z, string Label)>? _focusPins;
    public event EventHandler<(double X, double Z)>? CursorGameMoved;
    /// <summary>Raised after the zoom level changed, so the UI can update its label.</summary>
    public event EventHandler? ViewChanged;

    public void SetMap(MapDefinition? map, Bitmap? bitmap)
    {
        var changed = !ReferenceEquals(map, _map);
        _map = map;
        _bitmap = bitmap;
        if (!ReferenceEquals(_mipSource, bitmap))
        {
            _mipBitmap = null;
            _mipSource = null;
            _mipFactor = 1;
        }
        // Floor decks change with the layer combo, not just with the map; the
        // floor mip slot has to drop its stale source whenever the map changes.
        _floorMipBitmap = null;
        _floorMipSource = null;
        _floorMipFactor = 1;
        _floorMipFailed = null;
        _mipFailed = null;
        if (changed)
        {
            _zoom = 1;
            _pan = default;
            _avatarCache.Clear();
            _circleCache.Clear();
            _markerHits.Clear();
            _focusPin = null;
            _focusPins = null;
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
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1.3);
    public void ZoomOut() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1 / 1.3);
    public void SetZoom(double zoom) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), zoom / Math.Max(0.0001, _zoom));

    /// <summary>
    /// While dragging, bilinear sampling is indistinguishable from HighQuality
    /// but much cheaper for a 6-30 MP base image; restore the sharp filter when
    /// the gesture ends.
    /// </summary>
    private void SetInteractiveQuality(bool interactive) =>
        RenderOptions.SetBitmapInterpolationMode(this,
            interactive ? BitmapInterpolationMode.LowQuality : BitmapInterpolationMode.HighQuality);

    /// <summary>
    /// Screen point (control-relative) that <see cref="FocusOn"/> lands the game
    /// position on. The mini-map sets this to the mouse cursor after a wheel zoom
    /// so a fresh screenshot re-pins the player to the spot the user was looking
    /// at instead of snapping back to the middle. Null = control centre.
    /// </summary>
    public Point? FocusAnchor { get; set; }

    /// <summary>Center the view on a game position at the given zoom.</summary>
    public void FocusOn(double gameX, double gameZ, double zoom = 3.2)
    {
        if (_map is null || !_map.HasValidAffine || Bounds.Width <= 0) return;
        EnsureLayout();
        _zoom = Math.Clamp(zoom, 0.35, 32);
        var p = _map.GameToImage(gameX, gameZ);
        var scale = _fit * _zoom;
        var anchor = FocusAnchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        // Screen(p) = W/2 + (p - boxCentre)*scale + pan  =>  solve for pan.
        var box = FitBox();
        _pan = new Vector(
            anchor.X - Bounds.Width / 2 - (p.X - (box.X + box.Width / 2)) * scale,
            anchor.Y - Bounds.Height / 2 - (p.Y - (box.Y + box.Height / 2)) * scale);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drop a labelled pin at a game position (task/story jump target). When
    /// <paramref name="focus"/> is set, the view is also centred on that spot at
    /// the standard jump zoom, so a jump is always one call.
    /// </summary>
    public void SetFocusPin(double gameX, double gameZ, string label, bool focus = false, double zoom = 3.2)
    {
        _focusPin = (gameX, gameZ, label ?? "");
        _focusPins = null;
        if (focus) FocusOn(gameX, gameZ, zoom);
        else InvalidateVisual();
    }

    /// <summary>
    /// Mark every objective zone of one jump at once. The view centres on the
    /// centroid of all pins and picks a zoom that fits the whole group, so a
    /// task with several far-apart points is readable in one jump.
    /// </summary>
    public void SetFocusPins(IReadOnlyList<(double X, double Z, string Label)> pins, double zoom = 0)
    {
        if (pins.Count == 0)
        {
            ClearFocusPin();
            return;
        }
        _focusPins = pins.ToList();
        _focusPin = (pins[0].X, pins[0].Z, pins[0].Label);

        // Centroid of the group (not the first point) so the whole group frames.
        var cx = pins.Average(p => p.X);
        var cz = pins.Average(p => p.Z);

        if (zoom <= 0 && _map is not null && _fit > 0 && Bounds.Width > 1 && Bounds.Height > 1)
        {
            // Largest half-extent from the centroid, in game metres.
            var spread = pins.Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz)));
            spread = Math.Max(spread, 25.0);
            // pixels per metre at zoom 1 = _fit * |affine a|
            var pxPerMetre1 = Math.Abs(_map.Affine[0]) * _fit;
            if (pxPerMetre1 > 1e-9)
            {
                var targetPxPerMetre = (Math.Min(Bounds.Width, Bounds.Height) * 0.45) / spread;
                zoom = Math.Clamp(targetPxPerMetre / pxPerMetre1, 0.5, 12);
            }
            else
            {
                zoom = 3.2;
            }
        }
        if (zoom <= 0) zoom = 3.2;

        FocusOn(cx, cz, zoom);
    }

    public void ClearFocusPin()
    {
        if (_focusPin is null && _focusPins is null) return;
        _focusPin = null;
        _focusPins = null;
        InvalidateVisual();
    }

    /// <summary>Center on a game position while keeping the current zoom.</summary>
        /// <summary>The artwork rect the view fits, in image pixels.</summary>
    private Rect FitBox() => _map is null || !_map.HasContent
        ? new Rect(0, 0, _map?.Width ?? 1, _map?.Height ?? 1)
        : new Rect(_map.ContentMinX, _map.ContentMinZ,
            _map.ContentMaxX - _map.ContentMinX, _map.ContentMaxZ - _map.ContentMinZ);

    private void EnsureLayout()
    {
        if (_map is null || Bounds.Width <= 1 || Bounds.Height <= 1) return;
        var pad = Compact ? 0 : 22;
        // Fit the drawn artwork, not the baked canvas: Icebreaker's hull fills a
        // fifth of its tile canvas, so fitting the canvas showed a tiny ship in
        // a black field.
        var box = FitBox();
        _fit = Math.Min((Bounds.Width - pad * 2) / Math.Max(1, box.Width),
                        (Bounds.Height - pad * 2) / Math.Max(1, box.Height));
        _fit = Math.Max(0.0001, _fit);
        _origin = new Point(
            Bounds.Width / 2 - (box.X + box.Width / 2) * Scale + _pan.X,
            Bounds.Height / 2 - (box.Y + box.Height / 2) * Scale + _pan.Y);
    }

    private double Scale => _fit * _zoom;

    private Point ImageToScreen(Point p) => new(_origin.X + p.X * Scale, _origin.Y + p.Y * Scale);

    private Point ScreenToImage(Point s) => new((s.X - _origin.X) / Scale, (s.Y - _origin.Y) / Scale);

    /// <summary>
    /// Draw only the part of <paramref name="bitmap"/> that lands inside the
    /// control. Avalonia still uploads the full texture, but the rasteriser no
    /// longer resamples 15-30 megapixels on every pan frame.
    /// </summary>
    private void DrawBitmapCropped(DrawingContext context, Bitmap bitmap, Rect dest, double opacity)
    {
        var pixel = bitmap.PixelSize;
        if (pixel.Width <= 0 || pixel.Height <= 0 || dest.Width <= 0 || dest.Height <= 0) return;

        var visible = dest.Intersect(new Rect(Bounds.Size));
        if (visible.Width <= 0 || visible.Height <= 0) return;

        var sx = (visible.X - dest.X) / dest.Width * pixel.Width;
        var sy = (visible.Y - dest.Y) / dest.Height * pixel.Height;
        var sw = visible.Width / dest.Width * pixel.Width;
        var sh = visible.Height / dest.Height * pixel.Height;

        // Guard against sub-pixel rounding pushing the source rect past the edge.
        if (sx < 0) { sw += sx; sx = 0; }
        if (sy < 0) { sh += sy; sy = 0; }
        if (sx + sw > pixel.Width) sw = pixel.Width - sx;
        if (sy + sh > pixel.Height) sh = pixel.Height - sy;
        if (sw <= 0 || sh <= 0) return;

        var source = new Rect(sx, sy, sw, sh);
        if (opacity >= 0.999)
        {
            context.DrawImage(bitmap, source, visible);
            return;
        }
        using (context.PushOpacity(opacity))
            context.DrawImage(bitmap, source, visible);
    }

    /// <summary>Memoised FormattedText: labels and marker captions were rebuilt
    /// (including glyph shaping) on every single frame.</summary>
    private FormattedText Text(string text, double size, IBrush brush)
    {
        var key = (text, Math.Round(size, 1), brush);
        if (_textCache.TryGetValue(key, out var cached)) return cached;
        var created = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, key.Item2, brush);
        // Keep the cache bounded; maps rarely have more than a few hundred
        // distinct labels, but zooming changes sizes constantly.
        if (_textCache.Count > 2048) _textCache.Clear();
        _textCache[key] = created;
        return created;
    }

    private Pen CachedPen(IBrush brush, double width)
    {
        var key = (brush, Math.Round(width, 2));
        if (_penCache.TryGetValue(key, out var pen)) return pen;
        if (_penCache.Count > 64) _penCache.Clear();
        pen = new Pen(brush, key.Item2);
        _penCache[key] = pen;
        return pen;
    }

    /// <summary>Diamond geometry of a given half-size, reused for every extract.</summary>
    private Geometry Diamond(double size)
    {
        var bucket = (int)Math.Round(size * 4);
        if (_diamondCache.TryGetValue(bucket, out var cached)) return cached;
        var s = bucket / 4.0;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(0, -s), true);
            g.LineTo(new Point(s, 0));
            g.LineTo(new Point(0, s));
            g.LineTo(new Point(-s, 0));
            g.EndFigure(true);
        }
        _diamondCache[bucket] = geometry;
        return geometry;
    }

    /// <summary>
    /// Pick the bitmap to sample for the current zoom. Above ~0.5 screen px per
    /// source px the native image is fine; below that the view is a downscale
    /// anyway, so hand the rasteriser a pre-shrunk copy built once and cached
    /// until the map changes. <paramref name="isFloor"/> selects the mip slot:
    /// base and floor overlay are cached separately so drawing a deck does not
    /// invalidate the base mip every frame.
    /// Hysteresis: the switch-back threshold sits below the build threshold so
    /// a zoom right at the boundary cannot flip slots every frame (that flip
    /// showed as flicker on multi-deck maps).
    /// </summary>
    private const double MipBuildScale = 0.5;
    private const double MipDropScale = 0.42;

    private Bitmap PickDrawBitmap(Bitmap source, bool isFloor = false)
    {
        if (Scale >= MipBuildScale) return source;
        // Below the drop threshold the downscale is cheap enough at full res.
        if (isFloor && Scale < MipDropScale) return source;

        var mipSource = isFloor ? _floorMipSource : _mipSource;
        var mipBitmap = isFloor ? _floorMipBitmap : _mipBitmap;
        var mipFactor = isFloor ? _floorMipFactor : _mipFactor;

        // Quantise to halves so the mip is rebuilt a couple of times per zoom
        // gesture instead of on every frame.
        var bucket = Scale < 0.25 ? 0.25 : 0.5;
        if (ReferenceEquals(mipSource, source) && mipBitmap is not null
            && Math.Abs(mipFactor - bucket) < 0.001)
            return mipBitmap;

        var w = Math.Max(1, (int)Math.Round(source.PixelSize.Width * bucket));
        var h = Math.Max(1, (int)Math.Round(source.PixelSize.Height * bucket));
        try
        {
            var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawImage(source,
                    new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height),
                    new Rect(0, 0, w, h));
            }
            if (isFloor)
            {
                _floorMipBitmap?.Dispose();
                _floorMipBitmap = rtb;
                _floorMipSource = source;
                _floorMipFactor = bucket;
            }
            else
            {
                _mipBitmap?.Dispose();
                _mipBitmap = rtb;
                _mipSource = source;
                _mipFactor = bucket;
            }
        }
        catch
        {
            // Mip build failed: fall back to the full image for THIS slot only,
            // and remember the failure so we do not retry every frame.
            if (isFloor) _floorMipFailed = source;
            else _mipFailed = source;
            return source;
        }
        return isFloor ? _floorMipBitmap! : _mipBitmap!;
    }

    /// <summary>Sources whose mip build failed — never retried.</summary>
    private Bitmap? _mipFailed;
    private Bitmap? _floorMipFailed;

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
        _markerHits.Clear();
        var dest = new Rect(_origin.X, _origin.Y, _map.Width * Scale, _map.Height * Scale);

        // Zoomed out, the viewport covers a small slice of a 5600 px bitmap and
        // the rasteriser re-samples the whole texture every drag frame. Drawing
        // a pre-shrunk copy keeps pixels identical but makes panning smooth.
        var floor = Layers.FirstOrDefault(l => l.Bitmap is not null);
        var baseBitmap = PickDrawBitmap(_bitmap);
        // The selected deck is drawn opaque over a dimmed base, so the base
        // pixels below it are invisible — resampling BOTH full-size textures
        // per drag frame was the main cost on multi-deck maps (Factory,
        // Customs, Streets vs single-bitmap Woods). Floor overlays get the
        // same pre-shrunk mip treatment as the base.
        var floorBitmap = floor is null || _floorMipFailed == floor.Bitmap
            ? floor?.Bitmap
            : PickDrawBitmap(floor.Bitmap!, isFloor: true);

        // A floor overlay is a different deck, not a highlight: blending it
        // over the ground floor at 75% left both wall sets visible at once,
        // which read as a doubled/ghosted map. Dim the base, then draw the
        // selected deck opaque on top.
        if (floor is null)
        {
            DrawBitmapCropped(context, baseBitmap, dest, 1.0);
        }
        else
        {
            DrawBitmapCropped(context, baseBitmap, dest, Compact ? 0.30 : 0.35);
            DrawBitmapCropped(context, floorBitmap!, dest, 1.0);
        }

        if (!Compact)
            context.DrawRectangle(null, BorderPen, dest);

        if (Options.ShowHazards) DrawHazards(context);
        if (Options.ShowSpawns) DrawSpawns(context);
        if (Options.ShowTransits) DrawTransits(context);
        if (Options.ShowBtrs) DrawBtrs(context);
        if (Options.ShowLoot) DrawLoot(context);
        if (Options.ShowCaches) DrawCaches(context);
        DrawPois(context);
        if (Options.ShowExtracts) DrawExtracts(context);
        if (Options.ShowBosses) DrawBosses(context);
        if (Options.ShowQuestMarkers) DrawQuestMarkers(context);
        if (Options.ShowLabels && !Compact) DrawLabels(context);

        DrawFocusPin(context);
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

    /// <summary>Shared zoom gate for room-level markers (loot containers and
    /// buried caches): below 4x they always clotted into noise.</summary>
    public const double LootZoomGate = 4.0;

    /// <summary>
    /// The overlay (Compact) squeezes the whole map into a small window, so icon
    /// sizes tuned against the main window's ~700 px map area dwarf the
    /// buildings there. Every marker class multiplies its side length by the
    /// canvas short side relative to that reference. Deep zoom still grows the
    /// icons through the zoom curve; this only fixes the overview footprint.
    /// </summary>
    private double CompactShrink =>
        !Compact ? 1.0
                 : Math.Clamp(Math.Min(Bounds.Width, Bounds.Height) / 700.0, 0.30, 1.0);

    /// <summary>
    /// Icon side length for a marker class. Pure sqrt(zoom) scaling only covers
    /// ~1:1.6 across the whole 0.35x–32x zoom range, so the old clamps kept
    /// icons near their full size even when the map itself shrank to a postage
    /// stamp — at fit-zoom the extract/transit glyphs dwarfed the buildings.
    /// This scales the base size with a gentler power of the zoom and caps it at
    /// the old upper bound: small zoom => clearly smaller glyphs, deep zoom =>
    /// same readable maximum as before.
    /// </summary>
    private double MarkerIcon(double baseSize, double maxSize, double minSize = 9)
        => Math.Clamp(baseSize * Math.Pow(_zoom, 0.45) * CompactShrink,
                      minSize * CompactShrink, maxSize);

    private bool ExtractVisible(ExtractPoint e) => Options.ExtractFilter switch
    {
        "pmc" => e.Faction is "pmc" or "shared",
        "scav" => e.Faction is "scav" or "shared",
        "shared" => e.Faction is "shared",
        _ => true,
    };

    private void DrawExtracts(DrawingContext context)
    {
        if (_map is null || _zoom < 0.5) return;
        // Diamond fallback + artwork icon both follow the shared zoom curve.
        var size = Math.Clamp(4.0 * Math.Pow(_zoom, 0.45), 4.0, 11);
        var diamond = Diamond(size);
        var iconSide = MarkerIcon(17.0, 32, 10);
        foreach (var e in _map.Extracts)
        {
            if (!ExtractVisible(e)) continue;
            var s = Project(e.X, e.Z);
            if (!OnScreen(s, 40)) continue;

            var brush = e.Faction == "scav" ? ExtractScav : e.Faction == "shared" ? ExtractShared : ExtractPmc;
            var pen = e.Faction == "scav" ? ExtractScavPen : e.Faction == "shared" ? ExtractSharedPen : ExtractPmcPen;
            var icon = LoadCached("legend", e.Faction == "scav" ? "extract-scav"
                : e.Faction == "shared" ? "extract-shared" : "extract-pmc");
            if (icon is not null)
            {
                context.DrawImage(icon, new Rect(s.X - iconSide / 2, s.Y - iconSide / 2, iconSide, iconSide));
            }
            else
            {
                using (context.PushTransform(Matrix.CreateTranslation(s.X, s.Y)))
                    context.DrawGeometry(ExtractFill, pen, diamond);
            }

            if (_zoom >= 1.15 && !Compact)
            {
                var label = Text(e.Name, 10.5, brush);
                var pos = new Point(s.X - label.Width / 2, s.Y + iconSide / 2 + 3);
                context.DrawText(Text(e.Name, 10.5, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
                context.DrawText(label, pos);
            }
        }
    }

    private void DrawBosses(DrawingContext context)
    {
        if (_map is null || _zoom < 0.7) return;
        // ONE avatar per boss at its primary (densest) spawn; the translucent
        // circle is the roam range covering all raw points. A carpet of the
        // same portrait told nothing the circle doesn't show better.
        var placed = new List<Rect>();
        foreach (var boss in _map.Bosses)
        {
            var pts = boss.Points;
            if (pts.Count == 0) continue;
            var primary = boss.Primary ?? pts[0];
            var cx = primary.X;
            var cz = primary.Z;
            var radiusGame = pts.Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz)));
            // Range: cover all points, minimum 30 m (avatar-scale); cap at
            // 170 m so one stray upstream point can never paint a map blob.
            var rangeM = Math.Clamp(radiusGame, 30.0, 170.0);

            var centre = Project(cx, cz);
            if (!OnScreen(centre, 220)) continue;

            var mPerPx = Math.Abs(_map.Affine[0]);
            var rangePx = rangeM * mPerPx * Scale;
            context.DrawEllipse(BossHalo, BossRingPen, centre, rangePx, rangePx);

            // Avatar rides the circle centre.
            var r = Math.Clamp(9.0 * Math.Sqrt(_zoom), 9, 20);
            var avatar = LoadCircle("bosses", boss.AvatarSlug, (int)Math.Round(r * 2));
            if (avatar is not null)
            {
                var side = (int)Math.Round(r * 2);
                context.DrawImage(avatar, new Rect(0, 0, side, side),
                    new Rect(centre.X - r, centre.Y - r, r * 2, r * 2));
                context.DrawEllipse(null, BossRingPen, centre, r, r);
            }
            else
            {
                context.DrawEllipse(BossHalo, null, centre, r + 7, r + 7);
                context.DrawEllipse(BossBrush, BossEmptyPen, centre, r, r);
                var initial = string.IsNullOrEmpty(boss.Name) ? "?" : boss.Name[..1].ToUpperInvariant();
                var itxt = Text(initial, r, BossInitial);
                context.DrawText(itxt, new Point(centre.X - itxt.Width / 2, centre.Y - itxt.Height / 2));
            }

            if (Compact || _zoom < 0.85) continue;
            var showLabel = labelled.Add(boss.Mob);
            if (!showLabel) continue;
            var size = _zoom >= 1.7 ? 10.5 : 9;
            var txt = Text(boss.Name, size, BossBrush);
            var pos = new Point(centre.X + rangePx + 4, centre.Y - txt.Height / 2);
            var box = new Rect(pos.X, pos.Y, txt.Width + 2, txt.Height + 2);
            var guard = 0;
            while (guard++ < 12 && placed.Any(b => b.Intersects(box)))
                box = box.Translate(new Vector(0, box.Height + 1));
            placed.Add(box);
            context.DrawText(Text(boss.Name, size, LabelShadow), new Point(box.X + 1, box.Y + 1));
            context.DrawText(txt, new Point(box.X, box.Y));
        }
    }

    private readonly HashSet<string> labelled = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IBrush CanvasBackground = Palette.Brush(Palette.HazardGlyph);
    private static readonly IBrush CompactBackground = Palette.Brush(Palette.CompactBg);

    private static readonly IBrush MinefieldFill = Palette.Brush(Palette.MinefieldFill);
    private static readonly IBrush MinefieldStroke = Palette.Brush(Palette.MinefieldStroke);
    private static readonly IBrush SniperFill = Palette.Brush(Palette.SniperFill);
    private static readonly IBrush SniperStroke = Palette.Brush(Palette.SniperStroke);
    private static readonly IBrush HazardFill = Palette.Brush(Palette.HazardFill);
    private static readonly IBrush HazardStroke = Palette.Brush(Palette.HazardStroke);
    private static readonly IBrush HazardGlyph = Palette.Brush(Palette.HazardGlyph);

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
                context.DrawGeometry(fill, CachedPen(stroke, 1.4 / Math.Max(0.0001, Scale)), geometry);
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
        context.DrawEllipse(HazardStroke, CachedPen(HazardGlyph, 1.2), s, r, r);

        var bang = Text("!", r * 1.7, HazardGlyph);
        context.DrawText(bang, new Point(s.X - bang.Width / 2, s.Y - bang.Height / 2));

        if (_zoom < 1.15 || Compact) return;
        var label = Text("危险区", 10.5, HazardStroke);
        var pos = new Point(s.X - label.Width / 2, s.Y + r + 3);
        context.DrawText(Text("危险区", 10.5, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
        context.DrawText(label, pos);
    }

    private void DrawSpawns(DrawingContext context)
    {
        if (_map is null || _zoom < 1.2) return;
        // Spawn clusters (Woods/Customs pour dozens of rings on one field) read
        // as noise at a glance. Shrink the dots and thin the ring so a dense
        // group blends into a soft texture instead of a row of bright circles;
        // the legend swatch stays the reference colour.
        var r = Math.Clamp(2.2 * Math.Sqrt(_zoom), 2.0, 3.4);
        foreach (var sp in _map.Spawns)
        {
            var s = Project(sp.X, sp.Z);
            if (!OnScreen(s, 20)) continue;
            var pen = sp.Side == "scav" ? SpawnScavPen : SpawnPmcPen;
            context.DrawEllipse(SpawnFill, pen, s, r, r);
        }
    }

    private void DrawTransits(DrawingContext context)
    {
        if (_map is null || _zoom < 0.5) return;
        // Same curve as extracts: tiny at overview zoom, full size when close.
        var iconSide = MarkerIcon(17.0, 30, 10);
        var transitIcon = LoadCached("legend", "transit");
        foreach (var t in _map.Transits)
        {
            var s = Project(t.X, t.Z);
            if (!OnScreen(s, 30)) continue;
            if (transitIcon is not null)
                context.DrawImage(transitIcon, new Rect(s.X - iconSide / 2, s.Y - iconSide / 2, iconSide, iconSide));
            else
                context.DrawRectangle(TransitFill, TransitPen,
                    new Rect(s.X - iconSide * 0.28, s.Y - iconSide * 0.28, iconSide * 0.56, iconSide * 0.56));

            if (_zoom < 1.1 || Compact) continue;
            // Dest stores the raw map key ("reserve") - print the localized
            // name so the label matches every other map reference.
            var dest = MapNames.Display(t.Dest);
            var label = Text(dest, 10, TransitBrush);
            var pos = new Point(s.X - label.Width / 2, s.Y + iconSide / 2 + 3);
            context.DrawText(Text(dest, 10, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
            context.DrawText(label, pos);
        }
    }

    /// <summary>
    /// BTR taxi stops (BTR 站点). A wheel glyph: ring plus three spokes, so it
    /// reads apart from the transit square at a glance.
    /// </summary>
    private void DrawBtrs(DrawingContext context)
    {
        if (_map is null || _map.Btrs.Count == 0 || _zoom < 0.5) return;
        // Same curve as extracts/transits: shrinks with the overview zoom.
        var iconSide = MarkerIcon(19.0, 30, 11);
        var btrIcon = LoadCached("legend", "btr-model");
        var showLabel = !Compact && _zoom >= 1.1;
        foreach (var stop in _map.Btrs)
        {
            var s = Project(stop.X, stop.Z);
            if (!OnScreen(s, 30)) continue;

            // Same toolbox artwork as the legend row, ringed so it reads on the
            // light rooftop areas too.
            if (btrIcon is not null)
            {
                context.DrawEllipse(TransitFill, TransitPen, s, iconSide * 0.62, iconSide * 0.62);
                context.DrawImage(btrIcon, new Rect(s.X - iconSide / 2, s.Y - iconSide / 2, iconSide, iconSide));
            }
            else
            {
                // Fallback wheel glyph when the artwork is missing.
                var r = Math.Clamp(6.0 * Math.Sqrt(_zoom), 5.5, 10);
                context.DrawEllipse(TransitFill, TransitPen, s, r, r);
                context.DrawLine(TransitPen, s, new Point(s.X + r * 0.7, s.Y - r * 0.4));
                context.DrawLine(TransitPen, s, new Point(s.X - r * 0.7, s.Y - r * 0.4));
                context.DrawLine(TransitPen, s, new Point(s.X, s.Y + r * 0.8));
            }

            if (!showLabel) continue;
            var label = Text(stop.Name, 10, TransitBrush);
            var pos = new Point(s.X - label.Width / 2, s.Y + iconSide / 2 + 3);
            context.DrawText(Text(stop.Name, 10, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
            context.DrawText(label, pos);
        }
    }

    /// <summary>
    /// Loot container markers (物资点). Sourced from tarkov.dev's lootContainer
    /// table; each kind (weapon-box, safe, jacket, drawer, medcase, ...) has its
    /// own colour and can be switched off individually from the legend.
    /// Only rendered at 4x zoom and beyond: at smaller scales the icons always
    /// clotted into noise no matter how they were thinned, so they stay hidden
    /// entirely until the user is close enough for room-level reading.
    /// </summary>
    private void DrawLoot(DrawingContext context)
    {
        if (_map is null || _map.Loot.Count == 0) return;
        if (_zoom < LootZoomGate) return;

        // Same artwork the legend rows use, so what you toggle is what you see.
        var iconSide = Math.Clamp(16.0 * Math.Sqrt(_zoom), 13, 22);
        var hidden = Options.LootKindsHidden;

        // Kind→bitmap resolved once per frame (6082 markers share ~20 kinds).
        var iconCache = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _map.Loot)
        {
            if (hidden.Count > 0 && hidden.Contains(item.Kind)) continue;
            var s = Project(item.X, item.Z);
            if (!OnScreen(s, 20)) continue;

            if (!iconCache.TryGetValue(item.Kind, out var icon))
            {
                icon = LoadCached("legend", LootKindIconFor(item.Kind));
                iconCache[item.Kind] = icon;
            }
            if (icon is not null)
            {
                context.DrawImage(icon, new Rect(s.X - iconSide / 2, s.Y - iconSide / 2, iconSide, iconSide));
            }
            else
            {
                context.DrawEllipse(LootBrushFor(item.Kind),
                    CachedPen(MarkerShadow, 1.2), s, iconSide * 0.3, iconSide * 0.3);
            }
        }
    }

    /// <summary>Legend icon stem per loot kind — mirrors MainWindow.LootKindIcon
    /// (kept in sync by datacheck: every stem must exist in Assets/legend).</summary>
    private static string LootKindIconFor(string kind) => kind switch
    {
        "duffle-bag" => "loot-duffle-bag",
        "drawer" or "filing-cabinet" => "loot-drawer",
        "weapon-box" or "weapon-rack" or "plastic-suitcase" => "loot-weapon-box",
        "toolbox" => "loot-toolbox",
        "jacket" => "loot-jacket",
        "pc-block" => "loot-pc-block",
        "grenade-box" => "loot-grenade-box",
        "dead-scav" or "civilian-body" or "scav-body" or "lab-technician-body" or "pmc-body" => "loot-dead-scav",
        "technical-supply-crate" => "loot-technical-crate",
        "medical-supply-crate" => "loot-medical-crate",
        "medcase" or "medbag" => "loot-medbox",
        "safe" or "bank-safe" or "cash-register" or "bank-cash-register" => "loot-safe",
        "ration-supply-crate" => "loot-ration-crate",
        "wooden-crate" or "wooden-ammo-box" => "loot-ammo-box",
        "shturmans-stash" => "boss-shturman",
        _ => "loot-toolbox",
    };

    /// <summary>Fill colour per loot container kind (stable, distinguishable).</summary>
    public static IBrush LootBrushFor(string kind) => kind switch
    {
        "weapon-box" or "weapon-rack" => Palette.Brush(Palette.LootWeapon),
        "jacket" => Palette.Brush(Palette.LootJacket),
        "drawer" or "filing-cabinet" => Palette.Brush(Palette.LootDrawer),
        "safe" or "bank-safe" => Palette.Brush(Palette.LootSafe),
        "medcase" or "medbag" => Palette.Brush(Palette.LootMedical),
        "toolbox" => Palette.Brush(Palette.LootTool),
        "wooden-crate" or "wooden-ammo-box" => Palette.Brush(Palette.LootCrate),
        "dead-scav" or "civilian-body" or "pmc-body" => Palette.Brush(Palette.LootBody),
        _ => Palette.Brush(Palette.LootOther),
    };

    /// <summary>
    /// Buried stash spots (物资埋藏点). Same ammo-box artwork as the legend row
    /// (the amber ring was dropped: it doubled the marker's footprint and made
    /// caches shout over every other icon).
    /// Shares the loot zoom gate: caches are room-level detail too, and a field
    /// of 60+ amber boxes at fit-zoom was noise.
    /// </summary>
    private void DrawCaches(DrawingContext context)
    {
        if (_map is null) return;
        if (_zoom < LootZoomGate) return;
        if (_map.Caches.Count == 0) return;

        // Match the loot-container icon size (16 * sqrt(zoom)) so caches don't
        // read as a bigger class of marker than regular loot.
        var iconSide = Math.Clamp(16.0 * Math.Sqrt(_zoom), 13, 22);
        var cacheIcon = LoadCached("legend", "loot-ammo-box");
        var showLabel = !Compact;   // gate is already 4x: always readable there
        foreach (var c in _map.Caches)
        {
            var s = Project(c.X, c.Z);
            if (!OnScreen(s, 24)) continue;

            if (cacheIcon is not null)
            {
                context.DrawImage(cacheIcon, new Rect(s.X - iconSide / 2, s.Y - iconSide / 2, iconSide, iconSide));
            }
            else
            {
                var r = Math.Clamp(5.2 * Math.Sqrt(_zoom), 4.5, 9);
                context.DrawEllipse(CacheFill, CachePen, s, r, r);
                context.DrawLine(CachePen,
                    new Point(s.X - r * 0.45, s.Y + r * 0.45),
                    new Point(s.X + r * 0.45, s.Y - r * 0.45));
            }

            if (!showLabel) continue;
            var cacheLabel = Text("埋藏点", 9.5, CacheStroke);
            var pos = new Point(s.X - cacheLabel.Width / 2, s.Y + iconSide / 2 + 3);
            context.DrawText(Text("埋藏点", 9.5, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
            context.DrawText(cacheLabel, pos);
        }
    }

    /// <summary>
    /// Puzzle items (解谜道具). Always drawn: they are the main reason to open
    /// the Labyrinth map. Labels are de-overlapped, because dozens of identical
    /// "钥匙"/"布偶" captions stacked into an unreadable block otherwise.
    /// </summary>
    private void DrawPois(DrawingContext context)
    {
        if (_map is null || _map.Pois.Count == 0) return;

        var iconSide = MarkerIcon(15.0, 26, 9);
        var placed = new List<Rect>();
        var showLabel = !Compact && _zoom >= 0.85;
        // Same artwork per kind as the legend's 道具 rows.
        var iconCache = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in _map.Pois)
        {
            if (PoiKindsHidden.Contains(p.Kind)) continue;
            var s = Project(p.X, p.Z);
            if (!OnScreen(s, 30)) continue;

            if (!iconCache.TryGetValue(p.Kind, out var icon))
            {
                icon = LoadCached("legend", PoiKindIconFor(p.Kind));
                iconCache[p.Kind] = icon;
            }
            if (icon is not null)
            {
                context.DrawImage(icon, new Rect(s.X - iconSide / 2, s.Y - iconSide / 2, iconSide, iconSide));
            }
            else
            {
                context.DrawEllipse(CacheFill, CachePen, s, iconSide * 0.3, iconSide * 0.3);
                context.DrawLine(CachePen, new Point(s.X - iconSide * 0.3, s.Y), new Point(s.X + iconSide * 0.3, s.Y));
                context.DrawLine(CachePen, new Point(s.X, s.Y - iconSide * 0.3), new Point(s.X, s.Y + iconSide * 0.3));
            }

            if (!showLabel || string.IsNullOrWhiteSpace(p.Text)) continue;

            var label = Text(p.Text, 10, CacheStroke);
            var pos = new Point(s.X - label.Width / 2, s.Y + iconSide / 2 + 2);
            var box = new Rect(pos.X - 2, pos.Y - 1, label.Width + 4, label.Height + 2);
            // Skip captions that would collide with one already drawn this
            // frame; the icon dot still marks the item.
            if (placed.Any(q => q.Intersects(box))) continue;
            placed.Add(box);

            context.DrawText(Text(p.Text, 10, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
            context.DrawText(label, pos);
        }
    }

    /// <summary>Legend icon stem per POI kind — mirrors MainWindow.PoiKindIcon.</summary>
    private static string PoiKindIconFor(string kind) => kind switch
    {
        "钥匙" => "key-cultist-1",
        "解除陷阱" => "lock",
        "喷灯" or "阀门手轮" => "loot-toolbox",
        _ => "lock",
    };

    private void DrawQuestMarkers(DrawingContext context)
    {
        if (_map is null || QuestMarkers.Count == 0) return;

        // Tracked tasks only — a handful of markers at most — so the avatar is
        // drawn at every zoom ("常驻") instead of degrading to a dot when the
        // user zooms out and losing the task on the map.
        var radius = Compact ? 11.0 : Math.Clamp(11 * Math.Sqrt(_zoom), 10, 16);
        // Task names ride along with the avatar from the moment the avatar is
        // drawn — a bare portrait does not tell the user WHICH task it marks.
        // At far zoom the names stack into noise, so keep those gated.
        var showName = !Compact && _zoom >= 0.55;
        var seen = new HashSet<(string Quest, int X, int Y)>();
        foreach (var marker in QuestMarkers)
        {
            var s = Project(marker.X, marker.Z);
            if (!OnScreen(s, 70)) continue;

            // The same quest often has several objectives at the same spot.
            if (!seen.Add((marker.QuestId, (int)Math.Round(s.X / 4), (int)Math.Round(s.Y / 4))))
                continue;

            _markerHits.Add((marker, new Rect(s.X - radius, s.Y - radius, radius * 2, radius * 2)));

            context.DrawEllipse(MarkerShadow, null, new Point(s.X + 1, s.Y + 2), radius + 1, radius + 1);
            context.DrawEllipse(QuestMarkerFill, QuestMarkerPen, s, radius, radius);

            var avatar = LoadCircle("traders", marker.TraderSlug, (int)Math.Round((radius - 2) * 2));
            if (avatar is not null)
            {
                var side = (int)Math.Round((radius - 2) * 2);
                var rect = new Rect(s.X - radius + 2, s.Y - radius + 2, radius * 2 - 4, radius * 2 - 4);
                context.DrawImage(avatar, new Rect(0, 0, side, side), rect);
            }
            else
            {
                var initial = string.IsNullOrEmpty(marker.TraderName) ? "?" : marker.TraderName[..1].ToUpperInvariant();
                var txt = Text(initial, radius, BossInitial);
                context.DrawText(txt, new Point(s.X - txt.Width / 2, s.Y - txt.Height / 2));
            }

            if (showName)
            {
                // Avatar left, task name right of it, vertically centred —
                // readable at every zoom where the avatar itself is visible.
                var txt = Text(marker.QuestName, 10.5, QuestLabelBrush);
                var pos = new Point(s.X + radius + 4, s.Y - txt.Height / 2);
                context.DrawText(Text(marker.QuestName, 10.5, LabelShadow), new Point(pos.X + 1, pos.Y + 1));
                context.DrawText(txt, pos);
            }
        }
    }

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
    /// Circular avatar thumbnail, rasterised once per (slug, pixel size). This
    /// replaces a geometry clip layer per boss/marker per frame.
    /// </summary>
    private Bitmap? LoadCircle(string folder, string slug, int pixelSize)
    {
        if (string.IsNullOrWhiteSpace(slug) || pixelSize <= 0) return null;
        pixelSize = Math.Clamp(pixelSize, 8, 96);
        var key = (folder, slug, pixelSize);
        if (_circleCache.TryGetValue(key, out var cached)) return cached;

        var source = LoadCached(folder, slug);
        if (source is null)
        {
            _circleCache[key] = null;
            return null;
        }

        Bitmap circle;
        try
        {
            var target = new RenderTargetBitmap(new PixelSize(pixelSize, pixelSize), new Vector(96, 96));
            using (var ctx = target.CreateDrawingContext())
            {
                using (ctx.PushGeometryClip(new EllipseGeometry(new Rect(0, 0, pixelSize, pixelSize))))
                {
                    ctx.DrawImage(source,
                        new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height),
                        new Rect(0, 0, pixelSize, pixelSize));
                }
            }
            circle = target;
        }
        catch
        {
            circle = source;
        }

        _circleCache[key] = circle;
        return circle;
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
            var text = Text(label.Text, fontSize, LabelBrush);
            var pos = new Point(s.X - text.Width / 2, s.Y - text.Height / 2);
            context.DrawText(Text(label.Text, fontSize, LabelShadow), new Point(pos.X + 1.4, pos.Y + 1.4));
            context.DrawText(text, pos);
        }
    }

    private void DrawFocusPin(DrawingContext context)
    {
        if (_map is null) return;

        // Secondary objective pins of the same jump: same crosshair styling,
        // but numbered instead of labelled so they never fight for space.
        if (_focusPins is { Count: > 1 })
        {
            for (var i = 1; i < _focusPins.Count; i++)
            {
                var (px, pz, _) = _focusPins[i];
                var ps = Project(px, pz);
                if (!OnScreen(ps, 80)) continue;
                context.DrawEllipse(FocusHalo, null, ps, 13, 13);
                context.DrawEllipse(null, AccentPen, ps, 8, 8);
                var num = Text((i + 1).ToString(CultureInfo.InvariantCulture), 9, QuestLabelBrush);
                context.DrawText(num, new Point(ps.X - num.Width / 2, ps.Y - num.Height / 2));
            }
        }

        if (_focusPin is not { } pin) return;
        var s = Project(pin.X, pin.Z);
        if (!OnScreen(s, 80)) return;

        // Pulsing-looking target: a hollow ring plus crosshair, so a jumped-to
        // objective is obvious even when several quest markers overlap it.
        context.DrawEllipse(FocusHalo, null, s, 17, 17);
        context.DrawEllipse(null, AccentPen, s, 11, 11);
        context.DrawLine(AccentPen, new Point(s.X - 18, s.Y), new Point(s.X - 5, s.Y));
        context.DrawLine(AccentPen, new Point(s.X + 5, s.Y), new Point(s.X + 18, s.Y));
        context.DrawLine(AccentPen, new Point(s.X, s.Y - 18), new Point(s.X, s.Y - 5));
        context.DrawLine(AccentPen, new Point(s.X, s.Y + 5), new Point(s.X, s.Y + 18));

        if (Compact || _zoom < 0.8 || string.IsNullOrWhiteSpace(pin.Label)) return;
        var txt = Text(pin.Label, 11, QuestLabelBrush);
        var box = new Rect(s.X + 16, s.Y - txt.Height / 2 - 4, txt.Width + 12, txt.Height + 8);
        context.DrawRectangle(PanelBrush, BorderPen, box, 4, 4);
        context.DrawText(txt, new Point(box.X + 6, box.Y + 4));
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
            context.DrawEllipse(PinShadow, null, new Point(s.X + 1, s.Y + 2), r + 2, r + 2);
            context.DrawEllipse(PinBrush, active ? ActivePinPen : PinPen, s, r, r);
            if (active || _zoom >= 1.8)
            {
                var txt = Text(index.ToString(CultureInfo.InvariantCulture), 9, Brushes.White);
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
            context.DrawGeometry(PlayerCone, PlayerConePen, geo);
        }

        context.DrawEllipse(PlayerHalo, null, s, radius + 8, radius + 8);
        context.DrawEllipse(PlayerDark, PlayerPen, s, radius, radius);
        context.DrawEllipse(PlayerBrush, null, s, radius - 3, radius - 3);

        if (large)
        {
            var txt = Text($"你  {pin.X:0}, {pin.Z:0}", 11, PlayerBrush);
            var box = new Rect(s.X + 14, s.Y - 30, txt.Width + 14, txt.Height + 8);
            context.DrawRectangle(PanelBrush, PlayerPanelPen, new RoundedRect(box, 5));
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
        var label = Text($"{nice:0} m", 11, MutedBrush);
        context.DrawRectangle(PanelBrush, null, new RoundedRect(new Rect(x - 8, y - 22, barWidth + 42, 40), 6));
        context.DrawLine(AccentPen, new Point(x, y), new Point(x + barWidth, y));
        context.DrawLine(AccentPen, new Point(x, y - 4), new Point(x, y + 4));
        context.DrawLine(AccentPen, new Point(x + barWidth, y - 4), new Point(x + barWidth, y + 4));
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
        context.DrawEllipse(PanelBrush, BorderPen, center, 19, 19);
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
        var n = Text("N", 9, MutedBrush);
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
        // Anchor invariant: the image point under the cursor stays under the
        // cursor. The required origin after zoom is screen - before * newScale;
        // _pan is the offset from the default (fit-box-centred) origin, and
        // origin moves 1:1 with _pan, so shift _pan by the origin difference.
        var before = ScreenToImage(screen);
        _zoom = Math.Clamp(_zoom * factor, 0.35, 32);
        EnsureLayout();
        var required = new Vector(screen.X - before.X * Scale, screen.Y - before.Y * Scale);
        _pan += required - _origin;
        EnsureLayout();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Wheel zoom anchored on an explicit control point. The mini-map routes its
    /// wheel gesture through this (its canvas is hit-test-invisible, so the
    /// built-in handler never fires), keeping the map spot under the cursor
    /// fixed instead of snapping the view back to the player.
    /// </summary>
    public void ZoomAtPoint(Point screen, double factor) => ZoomAt(screen, factor);

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
        SetInteractiveQuality(true);
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
            SetInteractiveQuality(false);
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