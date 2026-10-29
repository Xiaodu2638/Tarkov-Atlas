using System.Net.Http;
using System.Text.RegularExpressions;

namespace TarkovAtlas.Services;

/// <summary>
/// Pulls the annotated "location" pictures out of a task page on 大傻哥
/// (eftarkov.com). The page renders a cover image plus one or more
/// /uploads/infoImg/... pictures under the guide section; only the latter are
/// the actual location hints, so the cover is deliberately skipped.
/// </summary>
public sealed partial class TaskGuideService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static TaskGuideService()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    public static string TaskPageUrl(string questId) => "https://www.eftarkov.com/task/" + questId;

    /// <summary>
    /// Download the task page and return the guide pictures in page order.
    /// Never throws: on any failure an empty list comes back so the caller can
    /// fall back to the cover image.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLocationImagesAsync(string questId)
    {
        if (string.IsNullOrWhiteSpace(questId)) return Array.Empty<string>();

        try
        {
            var html = await Http.GetStringAsync(TaskPageUrl(questId));
            return ExtractInfoImages(html);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Extract absolute /uploads/infoImg/... urls, de-duplicated, in order.</summary>
    internal static IReadOnlyList<string> ExtractInfoImages(string html)
    {
        if (string.IsNullOrEmpty(html)) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (Match match in InfoImageRegex().Matches(html))
        {
            var raw = match.Groups[1].Value.Replace("&amp;", "&").Trim();
            if (raw.Length == 0) continue;

            var absolute = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : "https://www.eftarkov.com" + (raw.StartsWith('/') ? raw : "/" + raw);

            // Only ever hand an https URL to the downloader / shell. The list
            // comes from a remote page, so it is untrusted input.
            if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps) continue;

            if (seen.Add(absolute)) result.Add(absolute);
        }

        return result;
    }

    /// <summary>Matches src="/uploads/infoImg/xxx.webp" (also absolute urls).</summary>
    [GeneratedRegex(@"src=[""']((?:https?://[^""']+)?/uploads/infoImg/[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex InfoImageRegex();
}
