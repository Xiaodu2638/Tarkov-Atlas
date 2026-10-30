using TarkovAtlas.Services;

// Screenshot filename parsing + "read then delete" behaviour.
internal static class ShotChecks
{
    private static int _failures;

    public static int Main()
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

        // Import ledger: same path+time is one import; a SAME name coming back
        // with a newer write time is a fresh capture (minute-precision names
        // plus delete-after-import) and must import again.
        var ledger = new ScreenshotImportLedger();
        var t1 = new DateTime(2026, 10, 30, 17, 45, 0);
        var t2 = t1.AddMinutes(1);
        var shot = Path.Combine(dir, "2026-10-30[17-45]_1.0, 2.0, 3.0_0, 0, 0, 1_5.0 (0).png");
        Check("ledger: new path imports", ledger.ShouldImport(shot, t1));
        ledger.MarkImported(shot, t1);
        Check("ledger: same path+time skipped", !ledger.ShouldImport(shot, t1));
        Check("ledger: same path newer time imports", ledger.ShouldImport(shot, t2));
        ledger.MarkImported(shot, t2);
        Check("ledger: other path imports",
            ledger.ShouldImport(shot + "-2.png", t1));

        try { Directory.Delete(dir, true); } catch { }

        Console.WriteLine(_failures == 0 ? "\nALL SCREENSHOT CHECKS PASSED" : $"\n{_failures} SCREENSHOT CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : "  (" + detail + ")")}");
    }
}
