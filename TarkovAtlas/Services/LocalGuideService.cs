using System.IO;
using Avalonia.Media.Imaging;

namespace TarkovAtlas.Services;

/// <summary>
/// Local task-guide images bundled under Assets/guides/{questId}/{n}.webp.
/// Sourced from the local toolbox asset dump (hand-annotated location maps);
/// page order matches the toolbox numbering. Falls back to the remote wiki
/// scraper when a quest has no bundled images.
/// </summary>
public sealed class LocalGuideService
{
    private static string Root => Path.Combine(AppPaths.AssetsDir, "guides");

    private HashSet<string>? _questIdsWithGuides;

    /// <summary>One-time scan of Assets/guides; O(1) lookups afterwards.</summary>
    private HashSet<string> QuestIdsWithGuides
    {
        get
        {
            if (_questIdsWithGuides is not null) return _questIdsWithGuides;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (var dir in Directory.EnumerateDirectories(Root))
                    {
                        // a folder only counts when it actually ships pages
                        if (Directory.EnumerateFiles(dir, "*.webp").Any())
                            set.Add(Path.GetFileName(dir));
                    }
                }
            }
            catch
            {
                // unreadable tree = no guides; the button then disables
            }
            _questIdsWithGuides = set;
            return set;
        }
    }

    /// <summary>Whether the quest ships with local guide pages (no I/O).</summary>
    public bool HasLocalImages(string questId) =>
        !string.IsNullOrWhiteSpace(questId) && QuestIdsWithGuides.Contains(questId);

    /// <summary>Bundled guide pages for a quest, absolute paths in page order.</summary>
    public IReadOnlyList<string> GetLocalImages(string questId)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(questId)) return result;
        var dir = Path.Combine(Root, questId);
        if (!Directory.Exists(dir)) return result;

        try
        {
            result.AddRange(Directory.EnumerateFiles(dir, "*.webp")
                .OrderBy(f => PageIndex(Path.GetFileName(f)))
                .Select(f => Path.GetFullPath(f)));
        }
        catch
        {
            result.Clear();
        }
        return result;
    }

    private static int PageIndex(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        return int.TryParse(stem, out var i) ? i : int.MaxValue;
    }
}
