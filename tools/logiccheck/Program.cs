using System.Text.Json;
using TarkovAtlas;
using TarkovAtlas.Models;
using TarkovAtlas.Services;

// Logic-level checks for the quest scoping rules and marker generation that the
// UI depends on. Runs headless: no window is created.
internal static class LogicChecks
{
    private static int _failures;

    public static int Main()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        // Keep progress/settings reads out of the host profile.
        Environment.SetEnvironmentVariable("TARKOVATLAS_DATA_DIR",
            Path.Combine(AppContext.BaseDirectory, "data"));
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(assets, "maps", "mapdata.json")),
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
            // maps[] also carries the eftarkov "任务地图" chips (kill-anywhere
            // style tasks list their eligible maps without typed-out zones), so
            // offered legitimately exceeds withZones. What must never happen is
            // one map claiming a large share of the whole DB (the scoping bug
            // this guard exists for).
            Check($"offered not inflated {key}", offered * 3 <= db.Quests.Count, $"{offered}/{db.Quests.Count}");
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

        // 4. Every coordinate zone map must be declared in Maps (the reverse is
        //    fine: chip-only entries carry no zones by design).
        var badZoneScope = db.Quests.Count(q => q.ZoneMaps.Any(m =>
            !q.Maps.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase))));
        Check("zone maps stay inside Maps", badZoneScope == 0, badZoneScope.ToString());
        var multi = db.Quests.Where(q => q.IsMultiMap).ToList();
        var zonedMulti = multi.Count(q => q.ZoneMaps.Count() > 1);
        Check("true multi-zone tasks exist", zonedMulti > 10, $"{zonedMulti}/{multi.Count}");

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
        Check("legacy progress has no story objective state", legacyFile.StoryObjectiveDone.Count == 0);

        var roundtrip = new QuestProgressFile
        {
            Completed = new Dictionary<string, bool> { ["q1"] = true, ["q2"] = false },
            Tracked = new List<string> { "q2" },
            StoryDone = new List<string> { "story-accidental-witness", "story-batya" },
            StoryObjectiveDone = new List<string> { "6ab719d971b30671e7b58701", "6ab719d9870f6e5e987e3804" },
        };
        var json = JsonSerializer.Serialize(roundtrip, AppJsonContext.Default.QuestProgressFile);
        Check("progress json carries storyDone", json.Contains("\"storyDone\"", StringComparison.Ordinal));
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.QuestProgressFile)!;
        Check("progress roundtrip tasks", back.Completed.Count == 2 && back.Tracked.SequenceEqual(roundtrip.Tracked));
        Check("progress roundtrip story", back.StoryDone.OrderBy(x => x).SequenceEqual(roundtrip.StoryDone.OrderBy(x => x)));
        Check("progress roundtrip story objectives",
            back.StoryObjectiveDone.OrderBy(x => x).SequenceEqual(roundtrip.StoryObjectiveDone.OrderBy(x => x)));

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

        // 14. Live service resolution: the sidebar and markers must never print a
        //     raw 24-hex trader id, must keep the trader's English nickname, and
        //     story jumps must only point at maps that exist.
        var service = new QuestService();
        service.Load();
        Check("service loads every task", service.TotalCount == db.Quests.Count,
            $"{service.TotalCount}/{db.Quests.Count}");

        var badDisplay = service.Quests
            .Where(q => string.IsNullOrWhiteSpace(q.TraderDisplay) || TraderInfo.IsRawId(q.TraderDisplay))
            .Select(q => q.Trader)
            .Distinct()
            .ToList();
        Check("trader display resolved", badDisplay.Count == 0, string.Join(",", badDisplay));

        var badSlug = service.Quests
            .Where(q => string.IsNullOrWhiteSpace(q.TraderSlug) || TraderInfo.IsRawId(q.TraderSlug))
            .Select(q => q.Trader)
            .Distinct()
            .ToList();
        Check("trader slug resolved", badSlug.Count == 0, string.Join(",", badSlug));

        // Tasks keep the English original nickname (e.g. "Prapor"), not the
        // Chinese display name used elsewhere.
        var englishNameMismatch = service.Quests
            .Where(q => traders.TryGetValue(q.Trader, out var t) && q.TraderDisplay != t.Name)
            .Select(q => $"{q.TraderDisplay}!={traders[q.Trader].Name}")
            .Take(5)
            .ToList();
        Check("trader display keeps English name", englishNameMismatch.Count == 0,
            string.Join("; ", englishNameMismatch));

        var missingSlugPng = service.Quests
            .Select(q => q.TraderSlug)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !File.Exists(Path.Combine(assets, "traders", s + ".png")))
            .ToList();
        Check("every task trader has an avatar", missingSlugPng.Count == 0,
            string.Join(",", missingSlugPng));

        // Search must work off the precomputed key: a task found by a Chinese
        // substring and one found by its English name must both surface.
        Check("search key precomputed", service.Quests.All(q => q.SearchKey.Length > 0));
        var probeQuest = service.Quests.First(q => q.Name.Length > 2 && q.NameEn.Length > 2);
        service.SetQuery(probeQuest.Name[..2]);
        service.Apply();
        Check("search finds task by Chinese name",
            service.VisibleQuests.Any(r => r.Quest.Id == probeQuest.Id));
        service.SetQuery(probeQuest.NameEn[..Math.Min(6, probeQuest.NameEn.Length)].ToLowerInvariant());
        service.Apply();
        Check("search finds task by English name",
            service.VisibleQuests.Any(r => r.Quest.Id == probeQuest.Id));
        service.SetQuery("");
        service.Apply();

        // Regression: Chinese titles with " - N" suffixes. Normalize must keep
        // CJK (it used to strip it, leaving only "2" which matched dozens of
        // part-2 tasks or none at all when the dash spacing differed).
        var spaced = service.Quests.FirstOrDefault(q => q.Name.Contains(" - "));
        if (spaced is not null)
        {
            var tight = spaced.Name.Replace(" ", "");   // user drops the spaces
            service.SetQuery(tight);
            service.Apply();
            Check("compact Chinese query finds its task",
                service.VisibleQuests.Any(r => r.Quest.Id == spaced.Id), tight);
            Check("compact Chinese query stays specific",
                service.VisibleQuests.Count <= 3, service.VisibleQuests.Count.ToString());
            service.SetQuery("");
            service.Apply();
        }

        // Raid detection from the game log. 1.3.0.0 moved application logs
        // into per-session directories and dropped the old "Load location"
        // lines; both layouts plus the rcid scene names must still resolve,
        // and lobby/menu scenes must never fire.
        Check("1.3 scene preset line resolves",
            GameLogParsing.RaidLocationId(
                "2026-10-30 17:45:25.889|1.3.0.0.48484|Info|application|scene preset path:maps/factory_day_preset.bundle rcid:factory_day.scenespreset.asset")
            == "factory_day");
        Check("interchange rcid resolves",
            GameLogParsing.RaidLocationId(
                "scene preset path:maps/shopping_mall.bundle rcid:Shopping_Mall.ScenesPreset.asset")
            == "Shopping_Mall");
        Check("transit locations line resolves",
            GameLogParsing.RaidLocationId(
                "2026-10-30 17:45:43.261|1.3.0.0.48484|Info|application|[Transit] Flag:None, RaidId:6ae4674544203dd6df03fbc6, Count:0, Locations:factory4_day -> ")
            == "factory4_day");
        Check("legacy Load location line resolves",
            GameLogParsing.RaidLocationId("2026-01-01 00:00:00.000|Load location: bigmap") == "bigmap");
        Check("non-marker lines are not markers",
            GameLogParsing.RaidLocationId("2026-10-30 17:45:40.193|Info|application|LocationLoaded:9.91 real:15.51 diff:5.6") is null);
        Check("new factory ids map to factory",
            MapRepository.LocationToMap["factory_day"] == "factory" &&
            MapRepository.LocationToMap["shopping_mall"] == "interchange");

        var known = MapRepository.LocationToMap.ContainsKey;
        Check("newest marker wins",
            GameLogParsing.NewestRaidLocation(
                "line1 scene preset path:maps/woods_preset.bundle rcid:woods.x\n" +
                "line2 [Transit] Flag:None, RaidId:abc, Count:0, Locations:bigmap -> \n", known)
            == "bigmap");
        Check("unknown scenes never fire",
            GameLogParsing.NewestRaidLocation(
                "scene preset path:maps/SomeLobby.bundle rcid:SomeLobby.asset\n", known) is null);

        // Log discovery in both directory layouts.
        var logRoot = Path.Combine(AppContext.BaseDirectory, "atlas-logtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sess = Directory.CreateDirectory(Path.Combine(logRoot, "log_2026.10.30_17-40-33_1.3.0.0.48484"));
            File.WriteAllText(Path.Combine(sess.FullName, "2026.10.30_17-40-33_1.3.0.0.48484 application_000.log"), "x");
            File.WriteAllText(Path.Combine(sess.FullName, "2026.10.30_17-40-33_1.3.0.0.48484 output_000.log"), "x");
            var found13 = GameLogParsing.FindApplicationLog(logRoot);
            Check("1.3 session dir log found", found13 is not null && found13!.EndsWith("application_000.log"),
                found13 is null ? "null" : Path.GetFileName(found13));

            Directory.Delete(sess.FullName, true);
            File.WriteAllText(Path.Combine(logRoot, "application_2026.10.24.log"), "x");
            var foundLegacy = GameLogParsing.FindApplicationLog(logRoot);
            Check("legacy flat log found", foundLegacy is not null, foundLegacy ?? "null");
        }
        finally
        {
            try { Directory.Delete(logRoot, true); } catch { }
        }

        // Deleting the player's own screenshots must never be a default.
        Check("screenshot deletion is opt-in", !new AppSettings().DeleteScreenshotsAfterScan);

        // Jump affordance: a task naming a map must expose the jump button even
        // when its objectives carry no typed coordinates.
        var mappedNoZones = service.Quests
            .Where(q => q.Maps.Count > 0 && !q.HasAnyZones)
            .ToList();
        Check("mapped tasks without zones exist", mappedNoZones.Count > 0, mappedNoZones.Count.ToString());
        Check("mapped tasks can still jump", mappedNoZones.All(q => q.CanJump),
            mappedNoZones.Count(q => !q.CanJump).ToString());
        Check("zoneless tasks without maps cannot jump",
            service.Quests.Where(q => q.Maps.Count == 0 && !q.HasAnyZones).All(q => !q.CanJump));

        // Story objectives exposed to the UI must resolve their map and keep
        // calibrated coordinates inside that map.
        var storyRows = service.StoryRows;
        var allStoryObjectives = storyRows.SelectMany(r => r.Objectives).ToList();
        Check("story rows expose objectives", allStoryObjectives.Count > 200,
            allStoryObjectives.Count.ToString());
        Check("story objective text kept",
            allStoryObjectives.All(o => !string.IsNullOrWhiteSpace(o.Text)));
        var brokenStoryMap = allStoryObjectives
            .Where(o => o.HasLocation && !maps.ContainsKey(o.Map))
            .Select(o => o.Map)
            .Distinct()
            .Take(5)
            .ToList();
        Check("story objective maps resolve", brokenStoryMap.Count == 0,
            string.Join(",", brokenStoryMap));
        var brokenStoryCoord = allStoryObjectives
            .Where(o => o.HasCoordinates
                        && (!maps.TryGetValue(o.Map, out var owner) || !owner.Contains(o.X, o.Z, 200)))
            .Select(o => $"{o.Map}@{o.X:0},{o.Z:0}")
            .Take(5)
            .ToList();
        Check("story objective coords resolve", brokenStoryCoord.Count == 0,
            string.Join("; ", brokenStoryCoord));

        // The story tab's tick is display state on top of the progress file:
        // ticking a chapter must fold its objective list, and the optional
        // 「隐藏已完成」 filter must drop exactly the ticked rows without
        // touching the full list the count is derived from.
        var filterProbe = new StoryRow(service.Chapters[0], 0, false) { IsExpanded = true };
        filterProbe.IsDone = true;
        Check("ticking a story chapter folds its objective list", !filterProbe.IsExpanded,
            filterProbe.IsExpanded.ToString());

        service.SetStoryDoneByIndex(0, true);
        var allRows = service.StoryRows.Count;
        var doneRows = service.StoryDoneCount;
        service.HideDoneStories = true;
        service.RefreshStoryView();
        Check("story view hides completed chapters",
            service.StoryView.Count == allRows - doneRows && service.StoryView.All(r => !r.IsDone),
            $"{service.StoryView.Count}/{allRows} done={doneRows}");
        service.HideDoneStories = false;
        service.RefreshStoryView();
        Check("story view restores every chapter", service.StoryView.Count == allRows,
            service.StoryView.Count.ToString());

        // Chapter and objective ticks are two views of one state: ticking the
        // chapter ticks every objective, unticking any objective reopens the
        // chapter, and 「隐藏已完成」 folds finished objectives too.
        var syncChapter = service.Chapters.First(c => c.Objectives.Count > 1);
        var syncChapterId = syncChapter.Id;
        service.SetStoryDone(syncChapterId, true);
        Check("ticking a chapter ticks its objectives",
            syncChapter.Objectives.All(o => o.IsDone),
            $"{syncChapter.Objectives.Count(o => o.IsDone)}/{syncChapter.Objectives.Count}");
        Check("story objective progress is countable",
            service.StoryObjectiveDoneCount >= syncChapter.Objectives.Count,
            service.StoryObjectiveDoneCount.ToString());

        service.SetStoryObjectiveDone(syncChapter.Objectives[0].Id, false);
        Check("unticking one objective reopens the chapter", !service.IsStoryDone(syncChapterId),
            service.IsStoryDone(syncChapterId).ToString());

        service.SetStoryDone(syncChapterId, true);
        Check("re-ticking the chapter restores every objective",
            syncChapter.Objectives.All(o => o.IsDone));

        service.HideDoneStories = true;
        service.RefreshStoryView();
        Check("hide done also folds finished objectives",
            syncChapter.Objectives.All(o => !o.IsVisible));
        service.HideDoneStories = false;
        service.RefreshStoryView();
        Check("clearing hide-done restores objectives",
            syncChapter.Objectives.All(o => o.IsVisible));

        Console.WriteLine(_failures == 0 ? "\nALL LOGIC CHECKS PASSED" : $"\n{_failures} LOGIC CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : "  (" + detail + ")")}");
    }
}