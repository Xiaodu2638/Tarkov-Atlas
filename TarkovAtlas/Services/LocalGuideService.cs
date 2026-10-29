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
