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
        Check("mapdata.json exists", File.Exists(Path.Combine(assets, "maps", "mapdata.json")));
        Check("quests.json exists", File.Exists(Path.Combine(assets, "quests.json")));

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(assets, "maps", "mapdata.json")),
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

        // Projection must round-trip through the inverse for every map. With a
        // spline correction the mapping is intentionally non-invertible-by-affine,
        // so only the plain-affine maps must round-trip exactly.
        foreach (var map in manifest.Maps.Where(m => m.Spline is null))
        {
            var px = map.GameToImage(map.MinX, map.MinZ);
            var back = map.ImageToGame(px.X, px.Y);
            var ok = Math.Abs(back.X - map.MinX) < 0.5 && Math.Abs(back.Z - map.MinZ) < 0.5;
            Check($"affine roundtrip {map.Key}", ok, $"{back.X:0.0},{back.Z:0.0} vs {map.MinX:0.0},{map.MinZ:0.0}");

            // Every corner must land inside the image bounds. Only meaningful for
            // an axis-aligned affine: a rotated image is a tilted rectangle, so
            // the corners of its world-space bbox fall outside it by design. The
            // spline also warps the frame locally, so it skips this check too.
            if (Math.Abs(map.Affine[1]) < 1e-9 && Math.Abs(map.Affine[3]) < 1e-9 && map.Spline is null)
            {
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
        }

        // ---- geometry consistency ------------------------------------------
        // The bitmap rectangle (via the affine) and the declared bounds must
        // describe the same world area, and every marker has to land inside both.
        var knownGeometryDefects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in manifest.Maps)
        {
            var known = knownGeometryDefects.Contains(map.Key);
            var (dx, dz) = BoundsMismatch(map);
            Report(known, $"image rect matches bounds {map.Key}", dx <= 0.5 && dz <= 0.5,
                $"{dx:0.0} m on x, {dz:0.0} m on z");

            // The view fits Content, so a wrong rect would crop or letterbox the
            // map. It is a pixel rect and must exist inside the canvas.
            var contentOk = map.HasContent
                && map.ContentMinX >= -1 && map.ContentMinZ >= -1
                && map.ContentMaxX <= map.Width + 1 && map.ContentMaxZ <= map.Height + 1;
            Check($"content rect inside canvas {map.Key}", contentOk,
                $"{map.ContentMinX:0},{map.ContentMinZ:0} .. {map.ContentMaxX:0},{map.ContentMaxZ:0} of {map.Width}x{map.Height}");

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
        var layersDir = Path.Combine(assets, "maps", "layers");
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
                    // An empty image is the map's own artwork: the ground/base
                    // level needs no extra PNG, so nothing to verify on disk.
                    if (string.IsNullOrWhiteSpace(layer.Image)) continue;
                    var path = Path.Combine(assets, "maps", layer.Image.Replace('/', Path.DirectorySeparatorChar));
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
        var hazardsPath = Path.Combine(assets, "maps", "hazards.json");
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

        // ---- floor coverage regressions -----------------------------------
        // The Lab, Interchange and Factory are the maps users reported as
        // missing levels. All need a base-artwork entry plus their overlays.
        foreach (var key in new[] { "the-lab", "interchange", "factory" })
        {
            var ok = layerIndex is not null
                     && layerIndex.TryGetValue(key, out var floors)
                     && floors.Count >= 3
                     && floors.Any(l => string.IsNullOrWhiteSpace(l.Image));
            Check($"{key} has >=3 floors incl. base artwork", ok,
                layerIndex is not null && layerIndex.TryGetValue(key, out var listed)
                    ? $"{listed.Count} floors"
                    : "no layer entry");
        }

        // ---- boss data regressions ----------------------------------------
        foreach (var map in manifest.Maps)
        {
            var dupes = map.Bosses.GroupBy(b => b.Mob, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            Check($"bosses deduped {map.Key}", dupes.Count == 0, string.Join(",", dupes));

            var badName = map.Bosses.Where(b =>
                b.Name.Equals("PMC", StringComparison.OrdinalIgnoreCase)
                || b.Name.Equals("Boss", StringComparison.OrdinalIgnoreCase)
                || TraderInfo.IsRawId(b.Name)
                || string.IsNullOrWhiteSpace(b.Name)).ToList();
            Check($"boss names resolved {map.Key}", badName.Count == 0,
                string.Join(",", badName.Select(b => b.Mob + "=" + b.Name)));

            // Every boss point must sit in the map it is attached to.
            var stray = map.Bosses.SelectMany(b => b.Points.Select(p => (b.Mob, p)))
                .Count(x => !map.Contains(x.p.X, x.p.Z, 200));
            Check($"boss points inside {map.Key}", stray == 0, stray.ToString());
        }

        // ---- puzzle item points (解谜道具) --------------------------------
        var labyrinth = manifest.Maps.FirstOrDefault(k => k.Key == "the-labyrinth");
        Check("labyrinth puzzle items present",
            labyrinth is not null && labyrinth.Pois.Count > 0,
            labyrinth?.Pois.Count.ToString() ?? "no map");
        if (labyrinth is not null)
        {
            var outOfBounds = labyrinth.Pois.Count(p => !labyrinth.Contains(p.X, p.Z, 1));
            Check("labyrinth puzzle items inside map", outOfBounds == 0, outOfBounds.ToString());

            var blank = labyrinth.Pois.Count(p => string.IsNullOrWhiteSpace(p.Text));
            Check("labyrinth puzzle items labelled", blank == 0, blank.ToString());
        }

        // Lighthouse: the mansion guards are rogues (exUsecFree), never a
        // "USEC" boss. The old aggregation emitted a boss literally called
        // USEC from five duplicate exUsecFree rows.
        var lighthouse = manifest.Maps.FirstOrDefault(m => m.Key == "lighthouse");
        if (lighthouse is not null)
        {
            var rogue = lighthouse.Bosses.FirstOrDefault(b =>
                b.Mob.Equals("exUsecFree", StringComparison.OrdinalIgnoreCase));
            Check("lighthouse rogue boss present", rogue is not null && rogue.Name == "游荡者",
                rogue?.Name ?? "missing");
            var usecBoss = lighthouse.Bosses.Any(b =>
                b.Name.Equals("USEC", StringComparison.OrdinalIgnoreCase)
                || b.Mob.Equals("ExUsec", StringComparison.OrdinalIgnoreCase));
            Check("lighthouse has no USEC boss", !usecBoss,
                string.Join(",", lighthouse.Bosses.Select(b => b.Mob + "/" + b.Name)));

            // Every Lighthouse boss must have portrait art; a missing file used
            // to leave a blank initial in the boss rail.
            var missingAvatars = lighthouse.Bosses
                .Where(b => !File.Exists(Path.Combine(assets, "bosses", b.AvatarSlug + ".png")))
                .Select(b => b.Mob + "->" + b.AvatarSlug)
                .ToList();
            Check("lighthouse boss avatars exist", missingAvatars.Count == 0,
                string.Join(",", missingAvatars));

            // "流氓" (thug) is the wrong translation for the rogue mob; the
            // Chinese wiki and in-game name are 游荡者.
            var mislabelled = manifest.Maps
                .Where(m => m.Bosses.Any(b => b.Name == "流氓"))
                .Select(m => m.Key)
                .ToList();
            Check("no boss named 流氓", mislabelled.Count == 0, string.Join(",", mislabelled));
        }

        // Shturman spawns on Woods are all in the Sawmill, not at USEC Camp or
        // Scav Town (upstream mixes ZoneWoodCutter entries from those areas).
        var woods = manifest.Maps.FirstOrDefault(m => m.Key == "woods");
        var shturman = woods?.Bosses.FirstOrDefault(b =>
            b.Mob.Equals("bossKojaniy", StringComparison.OrdinalIgnoreCase));
        Check("woods Shturman present", shturman is not null);
        if (shturman is not null)
        {
            var far = shturman.Points
                .Where(p => Math.Sqrt(Math.Pow(p.X - 10, 2) + Math.Pow(p.Z + 3, 2)) > 260)
                .ToList();
            Check("Shturman points at Sawmill", far.Count == 0 && shturman.Points.Count > 0,
                $"{far.Count} far of {shturman.Points.Count}");
        }

        // ---- story data regressions ---------------------------------------
        var storyPath = Path.Combine(assets, "story.json");
        Check("story.json exists", File.Exists(storyPath));
        StoryDatabase? story = null;
        try
        {
            if (File.Exists(storyPath))
                story = JsonSerializer.Deserialize(File.ReadAllText(storyPath),
                    AppJsonContext.Default.StoryDatabase);
        }
        catch (Exception ex)
        {
            Check("story.json parses", false, ex.Message);
        }
        Check("story.json parsed", story is not null);
        if (story is not null)
        {
            Check("story has 10 chapters", story.Chapters.Count == 10, story.Chapters.Count.ToString());
            Check("story chapter ids unique",
                story.Chapters.Select(c => c.Id).Distinct().Count() == story.Chapters.Count);
            Check("story chapter titles localized",
                story.Chapters.All(c => !string.IsNullOrWhiteSpace(c.NameZh)
                                        && !TraderInfo.IsRawId(c.NameZh)));

            var objectives = story.Chapters.SelectMany(c => c.Objectives).ToList();
            Check("story objectives populated", objectives.Count > 200, objectives.Count.ToString());
            var cjk = objectives.Count(o => o.Text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF));
            Check("story objective text is Chinese", cjk >= objectives.Count * 0.9,
                $"{cjk}/{objectives.Count}");

            var unknownStoryMap = objectives
                .Where(o => o.HasLocation && !byKey.ContainsKey(o.Map))
                .Select(o => o.Map)
                .Distinct()
                .ToList();
            Check("story maps resolve", unknownStoryMap.Count == 0, string.Join(",", unknownStoryMap));
            Check("story map names present",
                objectives.Where(o => o.HasLocation).All(o => !string.IsNullOrWhiteSpace(o.MapName)));

            var badStoryCoords = objectives
                .Where(o => o.HasCoordinates
                            && (!byKey.TryGetValue(o.Map, out var owner)
                                || !owner.Contains(o.X, o.Z, 200)))
                .Select(o => $"{o.Map}@{o.X:0},{o.Z:0}")
                .Take(5)
                .ToList();
            Check("story coordinates inside their maps", badStoryCoords.Count == 0,
                string.Join("; ", badStoryCoords));
            Check("story calibrated markers exist",
                objectives.Count(o => o.HasCoordinates) > 20,
                objectives.Count(o => o.HasCoordinates).ToString());
        }

        // ---- XAML resource keys -------------------------------------------
        // A missing StaticResource key only fails at runtime, when the window is
        // constructed. Catch it here instead: every key referenced from the
        // markup must also be declared in App.axaml.
        {
            var projectDir = Path.GetDirectoryName(AssetsRoot()) ?? ".";
            var xamlFiles = Directory.Exists(projectDir)
                ? Directory.GetFiles(projectDir, "*.axaml")
                : Array.Empty<string>();

            var declared = new HashSet<string>(StringComparer.Ordinal);
            var referenced = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var file in xamlFiles)
            {
                var text = File.ReadAllText(file);
                foreach (var key in MatchAll(text, "x:Key=\"([^\"]+)\""))
                    declared.Add(key);
                foreach (var key in MatchAll(text, "StaticResource\\s+([A-Za-z0-9_.]+)"))
                    referenced[key] = Path.GetFileName(file);
            }

            var missing = referenced
                .Where(kv => !declared.Contains(kv.Key))
                .Select(kv => $"{kv.Key} (used in {kv.Value})")
                .OrderBy(x => x)
                .ToList();

            Check("xaml resource keys resolve", missing.Count == 0, string.Join("; ", missing));
        }

        if (_known > 0)
            Console.WriteLine($"\n{_known} known defect(s) reported above - see AGENTS.md section 7");
        Console.WriteLine(_failures == 0 ? "\nALL CHECKS PASSED" : $"\n{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Every first capture group of <paramref name="pattern"/> in <paramref name="text"/>.</summary>
    private static IEnumerable<string> MatchAll(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.Matches(text, pattern)
            .Select(m => m.Groups[1].Value);

    /// <summary>Assets directory used by the app, resolved from the test binary location.</summary>
    private static string AssetsRoot()
    {
        var project = ProjectDir();
        return Path.Combine(project, "Assets");
    }

    /// <summary>The TarkovAtlas project directory (…/default-workspace/TarkovAtlas).</summary>
    private static string ProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "TarkovAtlas", "App.axaml");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "TarkovAtlas");
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>How far the image rectangle, mapped back to game space, misses the declared bounds.</summary>
    private static (double Dx, double Dz) BoundsMismatch(MapDefinition map)
    {
        // All four corners: with a rotated affine the diagonal pair does not
        // cover the image rect, so the game-space extent needs every corner.
        var xs = new List<double>();
        var zs = new List<double>();
        foreach (var (px, py) in new[] { (0.0, 0.0), (map.Width, 0.0), (0.0, map.Height), (map.Width, map.Height) })
        {
            var g = map.ImageToGame(px, py);
            xs.Add(g.X);
            zs.Add(g.Z);
        }
        var dx = Math.Max(Math.Abs(xs.Min() - map.MinX), Math.Abs(xs.Max() - map.MaxX));
        var dz = Math.Max(Math.Abs(zs.Min() - map.MinZ), Math.Abs(zs.Max() - map.MaxZ));
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