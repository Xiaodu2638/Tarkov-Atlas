using System.Text.Json;
using Avalonia.Media.Imaging;
using TarkovAtlas.Models;

namespace TarkovAtlas.Services;

public sealed class MapRepository
{
    private readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap?> _layerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
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
        var manifestPath = Path.Combine(AppPaths.MapsDir, "mapdata.json");
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

        LoadLayers();
        LoadHazards();
    }

    /// <summary>Attach per-floor artwork listed in layers/index.json.</summary>
    private void LoadLayers()
    {
        try
        {
            var path = Path.Combine(AppPaths.MapsDir, "layers", "index.json");
            if (!File.Exists(path)) return;
            var index = JsonSerializer.Deserialize(File.ReadAllText(path),
                AppJsonContext.Default.DictionaryStringListMapLayer);
            if (index is null) return;
            foreach (var map in _maps)
            {
                if (index.TryGetValue(map.Key, out var layers) && layers.Count > 0)
                    map.Layers = layers.OrderBy(l => l.Order).ToList();
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
            var path = Path.Combine(AppPaths.MapsDir, "hazards.json");
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

    /// <summary>Load (and memoise) one floor overlay image. Null when missing.</summary>
    public Bitmap? GetLayerBitmap(MapLayer layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Image)) return null;
        lock (_gate)
        {
            if (_layerCache.TryGetValue(layer.Image, out var cached))
                return cached;

            var path = Path.Combine(AppPaths.MapsDir, layer.Image);
            if (!File.Exists(path))
            {
                _layerCache[layer.Image] = null;
                return null;
            }

            Bitmap bitmap;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                bitmap = new Bitmap(stream);

            _layerCache[layer.Image] = bitmap;
            return bitmap;
        }
    }

    }
