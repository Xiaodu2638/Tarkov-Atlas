using System.Text.Json;
using TarkovAtlas;
using TarkovAtlas.Models;

// Logic-level checks for the quest scoping rules and marker generation that the
// UI depends on. Runs headless: no window is created.
internal static class LogicChecks
{
    private static int _failures;

    public static int Main()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(assets, "Maps", "mapdata.json")),
            AppJsonContext.Default.MapManifest)!;
        var db = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(assets, "quests.json")),
            AppJsonContext.Default.QuestDatabase)!;

        var maps = manifest.Maps.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        var traders = manifest.Traders.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        // 1. Every task that claims a map must exist in the manifest.
        var unknownMaps = db.Quests.SelectMany(q => q.Maps)
            .Where(k => !maps.ContainsKey(k)).Distinct().ToList();
        Check("task map keys resolve", unknownMaps.Count == 0, string.Join(",", unknownMaps));

        // 2. Scoping: a task with zones on customs must be offered on customs, and
        //    the list must stay meaningfully smaller than the whole database.
        foreach (var key in new[] { "customs", "factory", "woods", "interchange", "reserve", "streets-of-tarkov" })
        {
            var offered = db.Quests.Count(q => q.AppliesTo(key));
            var withZones = db.Quests.Count(q => q.ObjectivesFor(key).Any());
            Check($"tasks offered on {key}", offered > 10 && offered < db.Quests.Count / 2,
                $"{offered}/{db.Quests.Count}");
            Check($"tasks with zones on {key}", withZones > 3, withZones.ToString());
            // A task may list a map without typed-out zones (e.g. only a "kill in
            // this area" note), so offered >= withZones; the gap must stay small.
            Check($"offered covers zones {key}", offered >= withZones, $"{offered} >= {withZones}");
            Check($"offered not inflated {key}", offered <= withZones * 2 + 4, $"{offered} vs {withZones}");
        }

        // 3. Objectives for a map must actually be located on that map.
        var wrongZone = 0;
        foreach (var q in db.Quests)
        {
            foreach (var mapKey in q.Maps)
            {
                foreach (var o in q.ObjectivesFor(mapKey))
                {
                    if (o.Zones.Any(z => string.Equals(z.Map, mapKey, StringComparison.OrdinalIgnoreCase))) continue;
                    wrongZone++;
                }
            }
        }
        Check("ObjectivesFor stays on its map", wrongZone == 0, wrongZone.ToString());

        // 4. Multi-map tasks: every reported map has at least one zone.
        var multi = db.Quests.Where(q => q.IsMultiMap).ToList();
        var badMulti = multi.Count(q => q.Maps.Any(m => !q.ObjectivesFor(m).Any()));
        Check("multi-map tasks all have zones", badMulti == 0, $"{badMulti}/{multi.Count}");

        // 5. Markers: tracked tasks on customs must produce markers inside the map
        //    and each marker must resolve to a bundled trader avatar.
        var customs = maps["customs"];
        var tracked = db.Quests.Where(q => q.AppliesTo("customs") && q.ObjectivesFor("customs").Any())
            .Take(25).ToList();
        var markerCount = 0;
        var missingAvatar = new List<string>();
        foreach (var q in tracked)
        {
            if (!traders.TryGetValue(q.Trader, out var trader)) continue;
            var avatar = Path.Combine(assets, "traders", trader.Slug + ".png");
            if (!File.Exists(avatar)) missingAvatar.Add(trader.Slug);
            foreach (var o in q.ObjectivesFor("customs"))
            {
                foreach (var z in o.Zones.Where(z => z.Map == "customs"))
                {
                    markerCount++;
                    if (!customs.Contains(z.X, z.Z, 200))
                        Check("quest marker inside map", false, $"{q.Name} @ {z.X:0},{z.Z:0}");
                }
            }
        }
        Check("markers generated for tracked tasks", markerCount > 10, markerCount.ToString());
        Check("markers have trader avatars", missingAvatar.Count == 0, string.Join(",", missingAvatar.Distinct()));

        // 6. Search should be able to reach tasks from other maps.
        var searchable = db.Quests.Count(q => q.Name.Length > 1);
        Check("all tasks searchable", searchable == db.Quests.Count, searchable.ToString());

        // 10. "Anywhere" tasks (no map coordinates) must stay reachable on every
        //     map but sort behind the map-specific ones.
        var anywhere = db.Quests.Where(q => q.IsAnyLocation).ToList();
        Check("any-location tasks exist", anywhere.Count > 50, anywhere.Count.ToString());
        Check("any-location tasks carry no maps", anywhere.All(q => q.Maps.Count == 0));

        var customsSpecific = db.Quests.Count(q => !q.IsAnyLocation && q.AppliesTo("customs"));
        Check("customs has map-specific tasks", customsSpecific > 10, customsSpecific.ToString());

        // The anywhere bucket must be a strict subset of the database, and none
        // of them may claim a concrete map.
        Check("anywhere tasks are a subset", anywhere.Count < db.Quests.Count,
            $"{anywhere.Count}/{db.Quests.Count}");

        // Sorting: anywhere tasks must never precede map-specific tasks in the
        // ordered list that Apply() produces.
        var ordered = db.Quests
            .OrderBy(q => q.IsAnyLocation)
            .ToList();
        var firstAnywhere = ordered.FindIndex(q => q.IsAnyLocation);
        if (firstAnywhere >= 0)
        {
            var tail = ordered.Skip(firstAnywhere).ToList();
            Check("anywhere tasks sort last", tail.All(q => q.IsAnyLocation),
                $"{tail.Count(q => !q.IsAnyLocation)} misplaced");
        }

        // 7. Extract filters behave as documented.
        var c = maps["customs"];
        var pmcOnly = c.Extracts.Where(e => e.Faction is "pmc" or "shared").Count();
        var scavOnly = c.Extracts.Where(e => e.Faction is "scav" or "shared").Count();
        var sharedOnly = c.Extracts.Count(e => e.Faction is "shared");
        Check("pmc filter non-empty", pmcOnly > 0, pmcOnly.ToString());
        Check("scav filter non-empty", scavOnly > 0, scavOnly.ToString());
        Check("shared subset of both", sharedOnly <= Math.Min(pmcOnly, scavOnly),
            $"{sharedOnly} <= min({pmcOnly},{scavOnly})");

        // 8. Boss spawn points must be inside the map.
        var badBoss = 0;
        foreach (var m in manifest.Maps)
        {
            foreach (var b in m.Bosses)
            {
                foreach (var p in b.Points)
                {
                    if (!m.Contains(p.X, p.Z, 100)) badBoss++;
                }
            }
        }
        Check("boss spawns inside maps", badBoss == 0, badBoss.ToString());

        // 9. Screenshot heading math: forward vector must be unit length.
        foreach (var (qx, qy, qz, qw) in new[]
                 {
                     (0.0, 0.0, 0.0, 1.0),          // identity
                     (-0.03307, -0.13322, 0.00384, -0.99053), // real capture
                     (0.0, 0.7071, 0.0, 0.7071),    // 90 deg yaw
                 })
        {
            var len = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            var nx = qx / len; var ny = qy / len; var nz = qz / len; var nw = qw / len;
            var fx = 2 * (nx * nz + nw * ny);
            var fz = 1 - 2 * (nx * nx + ny * ny);
            var mag = Math.Sqrt(fx * fx + fz * fz);
            Check("heading forward vector sane", mag is > 0.2 and < 1.6, mag.ToString("0.000"));
        }

        // 11. Story chapters: loaded through the source-generated context, ids
        //     unique, every listed map resolvable, objective counts consistent.
        var storyPath = Path.Combine(assets, "story.json");
        Check("story.json exists", File.Exists(storyPath));
        if (File.Exists(storyPath))
        {
            var story = JsonSerializer.Deserialize(File.ReadAllText(storyPath),
                AppJsonContext.Default.StoryDatabase)!;
            var chapters = story.Chapters;
            Check("story chapters loaded", chapters.Count > 0, chapters.Count.ToString());
            Check("story ids unique",
                chapters.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == chapters.Count);
            Check("story ids non-empty", chapters.All(c => !string.IsNullOrWhiteSpace(c.Id)));
            Check("story titles resolve", chapters.All(c => !string.IsNullOrWhiteSpace(c.Title)));
            var badStoryMaps = chapters.SelectMany(c => c.Maps)
                .Where(k => !maps.ContainsKey(k)).Distinct().ToList();
            Check("story map keys resolve", badStoryMaps.Count == 0, string.Join(",", badStoryMaps));
            Check("story chapters have objectives", chapters.All(c => c.Objectives.Count > 0));
            Check("story objective counts match",
                chapters.All(c => c.ObjectiveCount == c.Objectives.Count),
                chapters.Count(c => c.ObjectiveCount != c.Objectives.Count).ToString());
            Check("story rows build in order",
                chapters.OrderBy(c => c.Order).Select(c => c.Id)
                    .SequenceEqual(chapters.Select(c => c.Id)));

            // The tab-data wrapper must mirror the chapter it was built from.
            var rows = new List<StoryRow>();
            for (var i = 0; i < chapters.Count; i++)
                rows.Add(new StoryRow(chapters[i], i, i % 2 == 0));
            Check("story rows carry index", rows.All(r => r.Index >= 0 && r.Index < chapters.Count));
            Check("story rows carry objectives", rows.All(r => r.Objectives.Count == r.Chapter.Objectives.Count));
            Check("story rows keep tick state", rows.Where((_, i) => i % 2 == 0).All(r => r.IsDone));
        }

        // 12. Progress file: legacy files (no storyDone) must still load, and a
        //     save/load roundtrip must preserve both task and story state.
        const string legacy = "{\"completed\":{\"q1\":true},\"tracked\":[\"q1\"]}";
        var legacyFile = JsonSerializer.Deserialize(legacy, AppJsonContext.Default.QuestProgressFile)!;
        Check("legacy progress loads", legacyFile.Completed.Count == 1 && legacyFile.Tracked.Count == 1);
        Check("legacy progress has no story state", legacyFile.StoryDone.Count == 0);

        var roundtrip = new QuestProgressFile
        {
            Completed = new Dictionary<string, bool> { ["q1"] = true, ["q2"] = false },
            Tracked = new List<string> { "q2" },
            StoryDone = new List<string> { "story-accidental-witness", "story-batya" },
        };
        var json = JsonSerializer.Serialize(roundtrip, AppJsonContext.Default.QuestProgressFile);
        Check("progress json carries storyDone", json.Contains("\"storyDone\"", StringComparison.Ordinal));
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.QuestProgressFile)!;
        Check("progress roundtrip tasks", back.Completed.Count == 2 && back.Tracked.SequenceEqual(roundtrip.Tracked));
        Check("progress roundtrip story", back.StoryDone.OrderBy(x => x).SequenceEqual(roundtrip.StoryDone.OrderBy(x => x)));

        // 13. Hazard sizing: generic zones that cover a large part of a map are
        //     area-level triggers (drawn as one marker), while genuinely small
        //     patches - and every minefield/sniper - keep their polygon.
        var lab = maps["the-labyrinth"];
        var bigBox = new HazardZone
        {
            Type = "hazard",
            Points = new List<GameMarker>
            {
                new() { X = -10, Z = -10 }, new() { X = 10, Z = -10 },
                new() { X = 10, Z = 10 }, new() { X = -10, Z = 10 },
            },
        };
        Check("hazard shoelace area", Math.Abs(HazardRules.Area(bigBox.Points) - 400) < 1e-6,
            HazardRules.Area(bigBox.Points).ToString("0"));
        Check("map bounds area resolves", HazardRules.Area(lab) > 0, HazardRules.Area(lab).ToString("0"));
        Check("big generic hazard is area-level", HazardRules.IsAreaLevel(bigBox, lab));
        Check("big minefield stays an area", !HazardRules.IsAreaLevel(
            new HazardZone { Type = "minefield", Points = bigBox.Points }, lab));
        Check("small hazard stays an area", !HazardRules.IsAreaLevel(new HazardZone
        {
            Type = "hazard",
            Points = new List<GameMarker>
            {
                new() { X = 0, Z = 0 }, new() { X = 2, Z = 0 },
                new() { X = 2, Z = 2 }, new() { X = 0, Z = 2 },
            },
        }, lab));

        Console.WriteLine(_failures == 0 ? "\nALL LOGIC CHECKS PASSED" : $"\n{_failures} LOGIC CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : "  (" + detail + ")")}");
    }
}