using System.Diagnostics;
using TarkovAtlas.Models;
using TarkovAtlas.Services;

// Screenshot filename parsing + "read then delete" behaviour.
internal static class ShotChecks
{
    private static int _failures;

    public static async Task<int> Main()
    {
        var svc = new ScreenshotService();
        var dir = Path.Combine(AppContext.BaseDirectory, "shot-test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        // Real format produced by the game.
        var real = "2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png";
        var path = Path.Combine(dir, real);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var info = svc.Parse(path);
        Check("parses real filename", info is not null);
        if (info is not null)
        {
            Check("x parsed", Math.Abs(info.X - 7.86) < 1e-6, info.X.ToString());
            Check("y parsed", Math.Abs(info.Y - 38.06) < 1e-6, info.Y.ToString());
            Check("z parsed", Math.Abs(info.Z + 27.57) < 1e-6, info.Z.ToString());
            Check("timestamp parsed", info.CapturedAt.Year == 2026 && info.CapturedAt.Month == 9,
                info.CapturedAt.ToString("u"));
            Check("heading is a valid angle", info.HeadingDegrees is >= 0 and < 360,
                info.HeadingDegrees.ToString("0.0"));
        }

        // Negative / decimal coordinates and the (1) de-duplication suffix.
        var neg = Path.Combine(dir, "2026-01-02[03-04]_-123.456, -7.5, 890.25_0, 0, 0, 1_5.5 (1).png");
        File.WriteAllBytes(neg, new byte[] { 1 });
        var n = svc.Parse(neg);
        Check("parses negative coords", n is not null && Math.Abs(n.X + 123.456) < 1e-6 && Math.Abs(n.Z - 890.25) < 1e-6,
            n is null ? "null" : $"{n.X},{n.Z}");

        // Loose fallback for older/renamed files: parseable on request, but never
        // imported by the folder scan and never deleted.
        var loose = Path.Combine(dir, "random 12.5, -3.25, 44.0 shot.png");
        File.WriteAllBytes(loose, new byte[] { 1 });
        var l = svc.Parse(loose);
        Check("loose fallback parses", l is not null && Math.Abs(l.X - 12.5) < 1e-6, l?.X.ToString() ?? "null");

        // Non-screenshot names must be rejected, not guessed.
        var junk = Path.Combine(dir, "not a screenshot.png");
        File.WriteAllBytes(junk, new byte[] { 1 });
        Check("rejects unrelated names", svc.Parse(junk) is null);

        // Scanning only returns files in the exact game format.
        var scanned = svc.Scan(dir);
        Check("scan returns only valid shots", scanned.Count == 2, scanned.Count.ToString());
        Check("scan skips loose names", scanned.All(s => s.FileName != Path.GetFileName(loose)));

        // Read-then-delete removes the file.
        Check("file exists before import", File.Exists(path));
        var deleted = svc.TryDeleteAfterImport(path);
        Check("delete after import succeeds", deleted);
        Check("file is gone", !File.Exists(path));
        Check("delete is idempotent", !svc.TryDeleteAfterImport(path));

        // A loose-named file is the user's picture: never delete it.
        Check("loose name is not deleted", !svc.TryDeleteAfterImport(loose) && File.Exists(loose));

        // Import ledger (path + creation time identity): a capture is fresh
        // once; more bytes appended under the SAME token never re-fire (the
        // old FileTime-based key churned every poll while the game streamed
        // the PNG, which is what made pins jump and the folder feel dead).
        var ledger = new ScreenshotImportLedger();
        var t1 = new DateTime(2026, 10, 30, 9, 45, 45, DateTimeKind.Utc);
        var shot = Path.Combine(dir, "2026-10-30[17-45]_1.0, 2.0, 3.0_0, 0, 0, 1_5.0 (0).png");
        ScreenshotInfo ShotAt(DateTime created) =>
            new() { FilePath = shot, FileName = Path.GetFileName(shot), CreatedUtc = created, FileTime = created };
        Check("ledger: new capture is fresh", ledger.ShouldImport(ShotAt(t1)));
        ledger.MarkImported(ShotAt(t1));
        Check("ledger: same token skipped", !ledger.ShouldImport(ShotAt(t1)));
        var t2 = t1.AddSeconds(30);
        Check("ledger: recreated name is fresh", ledger.ShouldImport(ShotAt(t2)));

        var diffLedger = new ScreenshotImportLedger();
        var one = new[] { ShotAt(t1) };
        Check("diff: first pass yields fresh", diffLedger.Diff(one).Count == 1);
        Check("diff: second pass is quiet", diffLedger.Diff(one).Count == 0);
        Check("diff: new token yields fresh", diffLedger.Diff(new[] { ShotAt(t2) }).Count == 1);
        Check("diff: vanished file forgotten", diffLedger.Diff(Array.Empty<ScreenshotInfo>()).Count == 0);
        Check("diff: name back after vanish yields fresh", diffLedger.Diff(one).Count == 1);

        // Latest-wins pin store: a new capture on the same map REPLACES the
        // old pin (no accumulating trail), a different map keeps its own.
        var shots = new Dictionary<string, List<ScreenshotInfo>>(StringComparer.OrdinalIgnoreCase);
        ScreenshotService.RememberLatest(shots, new ScreenshotInfo { MapKey = "customs", X = 1 });
        ScreenshotService.RememberLatest(shots, new ScreenshotInfo { MapKey = "customs", X = 2 });
        ScreenshotService.RememberLatest(shots, new ScreenshotInfo { MapKey = "woods", X = 3 });
        Check("latest-wins: one pin per map", shots.Count == 2 &&
              shots["customs"].Count == 1 && shots["woods"].Count == 1);
        Check("latest-wins: newest coordinates kept",
              Math.Abs(shots["customs"][0].X - 2) < 1e-9, shots["customs"][0].X.ToString());

        // End-to-end watcher: the file appears -> callback within one poll;
        // the game keeps streaming bytes -> still exactly one callback.
        var watchDir = Path.Combine(dir, "watch");
        Directory.CreateDirectory(watchDir);
        var received = new List<ScreenshotInfo>();
        var wsvc = new ScreenshotService();
        wsvc.StartWatching(watchDir, i => { lock (received) received.Add(i); });
        var live = Path.Combine(watchDir, "2026-10-30[19-00]_10.5, 1.0, -20.25_0, 0, 0, 1_12.0.png");
        File.WriteAllBytes(live, new byte[1024]);
        var got = false;
        var wait = Stopwatch.StartNew();
        for (var i = 0; i < 40 && !got; i++)
        {
            await Task.Delay(100);
            lock (received) got = received.Count > 0;
        }
        wait.Stop();
        Check("watcher: new capture arrives fast", got, $"+{wait.ElapsedMilliseconds}ms");
        for (var i = 0; i < 10; i++)
        {
            using var fs = new FileStream(live, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(new byte[256]);
            await Task.Delay(150);
        }
        await Task.Delay(1500);
        int finalCount;
        lock (received) finalCount = received.Count;
        Check("watcher: one callback per capture", finalCount == 1, $"{finalCount} callbacks");
        wsvc.Dispose();
        // The poll timer's in-flight callback may still hold a directory
        // handle; give it one cycle to finish before wiping the test tree.
        await Task.Delay(ScreenshotService.PollInterval + TimeSpan.FromMilliseconds(300));
        try { Directory.Delete(dir, true); }
        catch (Exception ex) { Check("test dir cleaned up", false, ex.Message); }

        Console.WriteLine(_failures == 0 ? "\nALL SCREENSHOT CHECKS PASSED" : $"\n{_failures} SCREENSHOT CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }
    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : "  (" + detail + ")")}");
    }
}
