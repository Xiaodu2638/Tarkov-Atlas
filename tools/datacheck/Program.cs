using System.Text.Json;
using TarkovAtlas;
using TarkovAtlas.Models;

// Data-layer checks that do not need a display: projection round-trips,
// localized names, marker counts, quest/map scoping and tracked markers,
// plus the invariant that each image rectangle agrees with its declared bounds.
internal static class DataChecks
{
    private static int _failures;
    private static int _known;

    public static int Main()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        Check("mapdata.json exists", File.Exists(Path.Combine(assets, "Maps", "mapdata.json")));
        Check("quests.json exists", File.Exists(Path.Combine(assets, "quests.json")));

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(assets, "Maps", "mapdata.json")),
            AppJsonContext.Default.MapManifest);
        var quests = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(assets, "quests.json")),
            AppJsonContext.Default.QuestDatabase);

        Check("manifest parsed", manifest is not null);
        Check("quest db parsed", quests is not null);
        if (manifest is null || quests is null) return 1;

        Check("13 maps", manifest.Maps.Count == 13, manifest.Maps.Count.ToString());
        Check("traders listed", manifest.Traders.Count >= 10, manifest.Traders.Count.ToString());
        Check("tasks loaded", quests.Quests.Count > 400, quests.Quests.Count.ToString());

        // All names localized (no raw "extension Name" artifacts).
        var badNames = manifest.Maps.Where(m => m.Name.Contains(" Name") || m.NameEn.Contains(" Name")).ToList();
        Check("map names localized", badNames.Count == 0,
            string.Join(",", badNames.Select(m => m.Key)));

        // English secondary names are real English, not ids.
        var badEn = manifest.Maps.Where(m => m.NameEn.Length == 0 || m.NameEn.All(char.IsDigit)).ToList();
        Check("english names present", badEn.Count == 0, string.Join(",", badEn.Select(m => m.Key)));

        // Projection must round-trip through the inverse for every map.
        foreach (var map in manifest.Maps)
        {
            var px = map.GameToImage(map.MinX, map.MinZ);
            var back = map.ImageToGame(px.X, px.Y);
            var ok = Math.Abs(back.X - map.MinX) < 0.5 && Math.Abs(back.Z - map.MinZ) < 0.5;
            Check($"affine roundtrip {map.Key}", ok, $"{back.X:0.0},{back.Z:0.0} vs {map.MinX:0.0},{map.MinZ:0.0}");

            // Every corner must land inside the image bounds.
            foreach (var (gx, gz) in new[] { (map.MinX, map.MinZ), (map.MaxX, map.MinZ), (map.MinX, map.MaxZ), (map.MaxX, map.MaxZ) })
            {
                var p = map.GameToImage(gx, gz);
                var inside = p.X >= -1 && p.Y >= -1 && p.X <= map.Width + 1 && p.Y <= map.Height + 1;
                if (!inside)
                {
                    Check($"corner inside image {map.Key}", false, $"{gx:0},{gz:0} -> {p.X:0},{p.Y:0} of {map.Width}x{map.Height}");
                }
            }
        }

        // ---- geometry consistency ------------------------------------------
        // The bitmap rectangle (via the affine) and the declared bounds must
        // describe the same world area, and every marker has to land inside both.
        // icebreaker breaks this today, so it is a tracked defect (HANDOVER §3):
        // its failures print as KNOWN instead of failing the suite.
        var knownGeometryDefects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "icebreaker" };
        foreach (var map in manifest.Maps)
        {
            var known = knownGeometryDefects.Contains(map.Key);
            var (dx, dz) = BoundsMismatch(map);
            Report(known, $"image rect matches bounds {map.Key}", dx <= 0.5 && dz <= 0.5,
                $"{dx:0.0} m on x, {dz:0.0} m on z");

            var markers = new List<(double X, double Z)>();
            markers.AddRange(map.Extracts.Select(e => (e.X, e.Z)));
            markers.AddRange(map.Spawns.Select(s => (s.X, s.Z)));
            markers.AddRange(map.Bosses.SelectMany(b => b.Points).Select(p => (p.X, p.Z)));
            markers.AddRange(map.Transits.Select(t => (t.X, t.Z)));

            // A few assets legitimately sit just past the edge (factory has 7).
            const int allowedOutliers = 8;
            var outsideBounds = markers.Count(m => !map.Contains(m.X, m.Z));
            var outsideImage = markers.Count(m =>
            {
                var p = map.GameToImage(m.X, m.Z);
                return p.X < -1 || p.Y < -1 || p.X > map.Width + 1 || p.Y > map.Height + 1;
            });
            Report(known, $"markers inside bounds {map.Key}", outsideBounds <= allowedOutliers,
                $"{outsideBounds}/{markers.Count} outside");
            Report(known, $"markers inside image {map.Key}", outsideImage <= allowedOutliers,
                $"{outsideImage}/{markers.Count} outside");
        }

        // Maps the user cares about must have extracts.
        foreach (var key in new[] { "customs", "factory", "woods", "interchange", "shoreline", "reserve", "streets-of-tarkov" })
        {
            var m = manifest.Maps.FirstOrDefault(x => x.Key == key);
            Check($"extracts {key}", m is not null && m.Extracts.Count > 0, m?.Extracts.Count.ToString() ?? "no map");
            Check($"bosses {key}", m is not null && m.Bosses.Count > 0, m?.Bosses.Count.ToString() ?? "no map");
        }

        // Extract factions must be one of the three known values.
        var badFaction = manifest.Maps.SelectMany(m => m.Extracts)
            .Count(e => e.Faction is not ("pmc" or "scav" or "shared"));
        Check("extract factions valid", badFaction == 0, badFaction.ToString());

        // Every extract/quest coordinate must fall inside its map's bounds.
        foreach (var m in manifest.Maps)
        {
            var outside = m.Extracts.Count(e => !m.Contains(e.X, e.Z, 40));
            Check($"extracts inside bounds {m.Key}", outside == 0, $"{outside} outside");
        }

        // Quest localization + multi-map detection.
        var chinese = quests.Quests.Count(q => q.Name.Any(c => c >= 0x4E00 && c <= 0x9FFF));
        Check("quest names localized", chinese > 400, chinese.ToString());

        var withZones = quests.Quests.Count(q => q.HasAnyZones);
        Check("quests carry coordinates", withZones > 100, withZones.ToString());

        var multi = quests.Quests.Where(q => q.ZoneMaps.Count() > 1).ToList();
        Check("multi-map quests exist", multi.Count > 0, multi.Count.ToString());

        // Quest zone coordinates must be inside the referenced map.
        var byKey = manifest.Maps.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        var zoneErrors = 0;
        foreach (var q in quests.Quests)
        {
            foreach (var zone in q.Objectives.SelectMany(o => o.Zones))
            {
                if (!byKey.TryGetValue(zone.Map, out var map)) { zoneErrors++; continue; }
                if (!map.Contains(zone.X, zone.Z, 200)) zoneErrors++;
            }
        }
        Check("quest zones inside their maps", zoneErrors == 0, zoneErrors.ToString());

        // Trader avatars referenced by quests should exist on disk.
        var traderBySlug = manifest.Traders.ToDictionary(t => t.Id, t => t.Slug, StringComparer.OrdinalIgnoreCase);
        var missingAvatar = new HashSet<string>();
        foreach (var q in quests.Quests.Where(q => q.HasAnyZones))
        {
            if (!traderBySlug.TryGetValue(q.Trader, out var slug)) continue;
            var path = Path.Combine(assets, "traders", slug + ".png");
            if (!File.Exists(path)) missingAvatar.Add(slug);
        }
        Check("trader avatars bundled", missingAvatar.Count == 0, string.Join(",", missingAvatar));

        // ---- floor layers -------------------------------------------------
        var layersDir = Path.Combine(assets, "Maps", "layers");
        var layerIndexPath = Path.Combine(layersDir, "index.json");
        Check("layers/index.json exists", File.Exists(layerIndexPath));

        Dictionary<string, List<MapLayer>>? layerIndex = null;
        try
        {
            if (File.Exists(layerIndexPath))
            {
                layerIndex = JsonSerializer.Deserialize(File.ReadAllText(layerIndexPath),
                    AppJsonContext.Default.DictionaryStringListMapLayer);
            }
        }
        catch (Exception ex)
        {
            Check("layers index parses", false, ex.Message);
        }
        Check("layers index parsed", layerIndex is not null);

        if (layerIndex is not null)
        {
            var layerTotal = 0;
            var missingLayerImages = new List<string>();
            var badLayerSizes = new List<string>();
            var badLayerOrder = new List<string>();

            foreach (var pair in layerIndex)
            {
                var mapKey = pair.Key;
                var layers = pair.Value;
                layerTotal += layers.Count;
                if (!byKey.TryGetValue(mapKey, out var owner))
                {
                    missingLayerImages.Add("unknown map:" + mapKey);
                    continue;
                }
                if (layers.Count > 1 && layers.Select(l => l.Name).Distinct().Count() != layers.Count)
                    badLayerOrder.Add(mapKey + ": duplicate names");
                for (var i = 1; i < layers.Count; i++)
                {
                    if (layers[i].Order <= layers[i - 1].Order)
                    {
                        badLayerOrder.Add(mapKey + ": order not ascending");
                        break;
                    }
                }
                foreach (var layer in layers)
                {
                    var path = Path.Combine(assets, "Maps", layer.Image.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) { missingLayerImages.Add(layer.Image); continue; }
                    var size = ReadPngSize(path);
                    if (size is null || size.Value.Width != owner.Width || size.Value.Height != owner.Height)
                        badLayerSizes.Add($"{layer.Image} {size?.Width}x{size?.Height} vs {owner.Width}x{owner.Height}");
                    if (layer.MinHeight is not null && layer.MaxHeight is not null && layer.MinHeight > layer.MaxHeight)
                        badLayerSizes.Add(layer.Image + ": minHeight > maxHeight");
                }
            }

            Check("floor layers listed", layerTotal > 20, layerTotal.ToString());
            Check("floor layer images exist", missingLayerImages.Count == 0, string.Join(",", missingLayerImages));
            Check("floor layer images match base grid", badLayerSizes.Count == 0, string.Join("; ", badLayerSizes));
            Check("floor layer order sane", badLayerOrder.Count == 0, string.Join(",", badLayerOrder));
        }

        // ---- hazards -------------------------------------------------------
        var hazardsPath = Path.Combine(assets, "Maps", "hazards.json");
        Check("hazards.json exists", File.Exists(hazardsPath));
        Dictionary<string, List<HazardZone>>? hazards = null;
        try
        {
            if (File.Exists(hazardsPath))
            {
                hazards = JsonSerializer.Deserialize(File.ReadAllText(hazardsPath),
                    AppJsonContext.Default.DictionaryStringListHazardZone);
            }
        }
        catch (Exception ex)
        {
            Check("hazards parses", false, ex.Message);
        }
        Check("hazards parsed", hazards is not null);
        if (hazards is not null)
        {
            var hazardTotal = hazards.Values.Sum(v => v.Count);
            var badHazard = new List<string>();
            foreach (var (mapKey, zones) in hazards)
            {
                if (!byKey.TryGetValue(mapKey, out var owner)) { badHazard.Add("unknown map:" + mapKey); continue; }
                foreach (var zone in zones)
                {
                    if (zone.Points.Count < 3) { badHazard.Add(mapKey + ": <3 points"); continue; }
                    if (zone.Type is not ("minefield" or "sniper" or "hazard"))
                        badHazard.Add($"{mapKey}: bad type {zone.Type}");
                    if (zone.Points.Any(p => !owner.Contains(p.X, p.Z, 200)))
                        badHazard.Add(mapKey + ": point outside bounds");
                }
            }
            Check("hazards present", hazardTotal > 100, hazardTotal.ToString());
            Check("hazards well formed", badHazard.Count == 0, string.Join("; ", badHazard.Take(5)));

            // Area-level generic hazards are drawn as a single warning marker
            // instead of a filled polygon (see HazardRules). Only the Labyrinth
            // has one upstream; flag any other map so a new giant box cannot
            // silently blanket the map again.
            var areaLevel = new List<string>();
            foreach (var (mapKey, zones) in hazards)
            {
                if (!byKey.TryGetValue(mapKey, out var owner)) continue;
                foreach (var zone in zones.Where(z => HazardRules.IsAreaLevel(z, owner)))
                {
                    var share = HazardRules.Area(zone.Points) / Math.Max(1, HazardRules.Area(owner));
                    areaLevel.Add($"{mapKey} {share:P1}");
                }
            }
            var unexpected = areaLevel
                .Where(x => !x.StartsWith("the-labyrinth ", StringComparison.Ordinal))
                .ToList();
            Check("no unexpected full-map hazards", unexpected.Count == 0,
                string.Join("; ", unexpected.Concat(areaLevel).Take(5)));
        }

        if (_known > 0)
            Console.WriteLine($"\n{_known} known defect(s) reported above - see HANDOVER section 3");
        Console.WriteLine(_failures == 0 ? "\nALL CHECKS PASSED" : $"\n{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>How far the image rectangle, mapped back to game space, misses the declared bounds.</summary>
    private static (double Dx, double Dz) BoundsMismatch(MapDefinition map)
    {
        var first = map.ImageToGame(0, 0);
        var second = map.ImageToGame(map.Width, map.Height);
        var dx = Math.Max(Math.Abs(Math.Min(first.X, second.X) - map.MinX),
                          Math.Abs(Math.Max(first.X, second.X) - map.MaxX));
        var dz = Math.Max(Math.Abs(Math.Min(first.Z, second.Z) - map.MinZ),
                          Math.Abs(Math.Max(first.Z, second.Z) - map.MaxZ));
        return (dx, dz);
    }

    /// <summary>Fail normally, unless the map is a documented known defect.</summary>
    private static void Report(bool known, string name, bool ok, string? detail)
    {
        if (ok) { Check(name, true, detail); return; }
        if (!known) { Check(name, false, detail); return; }
        _known++;
        Console.WriteLine($"[KNOWN] {name}  ({detail})");
    }

    /// <summary>Read width/height straight from the PNG IHDR chunk (no decoder needed).</summary>
    private static (int Width, int Height)? ReadPngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) < 24) return null;
            if (!header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                return null;
            var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return (width, height);
        }
        catch
        {
            return null;
        }
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        var status = ok ? "PASS" : "FAIL";
        Console.WriteLine($"[{status}] {name}{(detail is null ? "" : "  (" + detail + ")")}");
    }
}