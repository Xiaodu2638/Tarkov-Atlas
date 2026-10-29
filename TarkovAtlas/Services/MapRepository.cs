using System.Text.Json;
using Avalonia.Media.Imaging;
using TarkovAtlas.Models;

namespace TarkovAtlas.Services;

public sealed class MapRepository
{
    private readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap?> _layerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Queue<string> _layerOrder = new();
    private readonly object _gate = new();
    private readonly List<MapDefinition> _maps = new();

    public IReadOnlyList<MapDefinition> Maps => _maps;
    public string GeneratedAt { get; private set; } = "";

    public static readonly Dictionary<string, string> LocationToMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigmap"] = "customs",
        ["customs"] = "customs",
        ["woods"] = "woods",
        ["factory4_day"] = "factory",
        ["factory4_night"] = "factory",
        ["factory4"] = "factory",
        ["factory"] = "factory",
        ["interchange"] = "interchange",
        ["shoreline"] = "shoreline",
        ["rezervbase"] = "reserve",
        ["rezerv_base"] = "reserve",
        ["reserve"] = "reserve",
        ["laboratory"] = "the-lab",
        ["the-lab"] = "the-lab",
        ["labs"] = "the-lab",
        ["lighthouse"] = "lighthouse",
        ["tarkovstreets"] = "streets-of-tarkov",
        ["city"] = "streets-of-tarkov",
        ["streets"] = "streets-of-tarkov",
        ["sandbox"] = "ground-zero",
        ["sandbox_high"] = "ground-zero",
        ["ground-zero"] = "ground-zero",
        ["labyrinth"] = "the-labyrinth",
        ["the-labyrinth"] = "the-labyrinth",
        ["terminal"] = "terminal",
        ["icebreaker"] = "icebreaker",
    };

    public void Load()
    {
        var manifestPath = AppPaths.MapDataPath;
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("地图清单不存在，请确认 Assets/Maps/mapdata.json 已随程序发布。", manifestPath);

        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize(json, AppJsonContext.Default.MapManifest)
                       ?? throw new InvalidDataException("无法解析 mapdata.json");

        GeneratedAt = manifest.GeneratedAt;
        _maps.Clear();
        _maps.AddRange(manifest.Maps.OrderBy(m => m.Order).ThenBy(m => m.Name));
        if (_maps.Count == 0)
            throw new InvalidDataException("mapdata.json 中没有地图。");

        LoadMarkers();
        LoadLayers();
        LoadHazards();
    }

    /// <summary>
    /// Attach per-map points from markers.json (the split marker file). Falls
    /// back to inline mapdata markers when the file predates the split.
    /// </summary>
    private void LoadMarkers()
    {
        if (!File.Exists(AppPaths.MarkersPath)) return;
        try
        {
            var data = JsonSerializer.Deserialize(File.ReadAllText(AppPaths.MarkersPath),
                AppJsonContext.Default.MarkersManifest);
            if (data is null) return;
            foreach (var map in _maps)
            {
                if (!data.Maps.TryGetValue(map.Key, out var pts)) continue;
                map.Extracts = pts.Extracts;
                map.Bosses = pts.Bosses;
                map.Spawns = pts.Spawns;
                map.Transits = pts.Transits;
                map.Caches = pts.Caches;
                map.Pois = pts.Pois;
            }
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    /// <summary>Attach per-map artwork from layers/index.json.</summary>
    private void LoadLayers()
    {
        try
        {
            var path = AppPaths.LayersIndexPath;
            if (!File.Exists(path)) return;
            var index = JsonSerializer.Deserialize(File.ReadAllText(path),
                AppJsonContext.Default.DictionaryStringListMapLayer);
            if (index is null) return;
            foreach (var map in _maps)
            {
                if (index.TryGetValue(map.Key, out var layers) && layers.Count > 0)
                    map.Layers = layers.OrderBy(l => l.Order).ToList();
                // The spline corrects the map's own affine, so it needs a
                // reference to it once both are loaded.
                if (map.Spline is not null) map.Spline.Affine = map.Affine;
            }
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    /// <summary>Load minefield / sniper polygons.</summary>
    private void LoadHazards()
    {
        try
        {
            var path = AppPaths.HazardsPath;
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize(File.ReadAllText(path),
                AppJsonContext.Default.DictionaryStringListHazardZone);
            if (data is null) return;
            foreach (var (key, zones) in data)
            {
                var map = FindByKey(key);
                if (map is not null) map.Hazards = zones;
            }
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    public MapDefinition? FindByKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _maps.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public MapDefinition? FindByLocationId(string? locationId)
    {
        if (string.IsNullOrWhiteSpace(locationId)) return null;
        return LocationToMap.TryGetValue(locationId, out var key) ? FindByKey(key) : null;
    }

    public Bitmap GetBitmap(MapDefinition map)
    {
        lock (_gate)
        {
            if (_bitmapCache.TryGetValue(map.Key, out var cached))
                return cached;

            var path = Path.Combine(AppPaths.MapsDir, map.Image);
            if (!File.Exists(path))
                throw new FileNotFoundException($"地图图片缺失: {path}");

            Bitmap bitmap;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                bitmap = new Bitmap(stream);

            _bitmapCache[map.Key] = bitmap;
            _cacheOrder.Enqueue(map.Key);
            while (_cacheOrder.Count > 3)
            {
                var oldKey = _cacheOrder.Dequeue();
                if (oldKey == map.Key || !_bitmapCache.Remove(oldKey, out var oldBitmap)) continue;
                _ = Task.Delay(5000).ContinueWith(_ =>
                {
                    try { oldBitmap.Dispose(); } catch { }
                });
            }

            return bitmap;
        }
    }

    /// <summary>
    /// Load (and memoise) one floor overlay image. Null when missing.
    /// </summary>
    /// <remarks>
    /// Floor images are 60-175 MB decoded, so the cache keeps the last few only.
    /// Icebreaker alone has 16 floors; keeping them all cost ~1.8 GB.
    /// </remarks>
    public Bitmap? GetLayerBitmap(MapLayer layer) => GetLayerBitmapAsync(layer).GetAwaiter().GetResult();

    /// <summary>Decode off the UI thread; the caller awaits and then paints.</summary>
    public async Task<Bitmap?> GetLayerBitmapAsync(MapLayer layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Image)) return null;
        lock (_gate)
        {
            if (_layerCache.TryGetValue(layer.Image, out var cached))
                return cached;
        }

        var path = Path.Combine(AppPaths.MapsDir, layer.Image);
        if (!File.Exists(path))
        {
            lock (_gate) _layerCache[layer.Image] = null;
            return null;
        }

        // Decode without holding the gate: this runs on a worker and the UI
        // stays responsive while a 60-175 MB PNG is read.
        Bitmap bitmap;
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            bitmap = await Task.Run(() => new Bitmap(stream)).ConfigureAwait(false);

        lock (_gate)
        {
            if (_layerCache.TryGetValue(layer.Image, out var raced))
                return raced ?? bitmap;

            _layerCache[layer.Image] = bitmap;
            _layerOrder.Enqueue(layer.Image);
            while (_layerOrder.Count > 3)
            {
                var oldKey = _layerOrder.Dequeue();
                if (oldKey == layer.Image || !_layerCache.Remove(oldKey, out var oldBitmap) || oldBitmap is null)
                    continue;
                // The canvas may still be painting the evicted bitmap this frame.
                _ = Task.Delay(5000).ContinueWith(_ =>
                {
                    try { oldBitmap.Dispose(); } catch { }
                });
            }
            return bitmap;
        }
    }

    }
