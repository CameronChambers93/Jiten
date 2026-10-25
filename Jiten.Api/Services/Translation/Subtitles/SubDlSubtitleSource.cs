using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Jiten.Core.Media;

namespace Jiten.Api.Services.Translation.Subtitles;

/// <summary>SubDL (subdl.com): has many anime fansubs. Searches by show name; downloads are zips holding one subtitle file.</summary>
public class SubDlSubtitleSource(IHttpClientFactory httpClientFactory) : ISubtitleSource
{
    private const string ApiUrl = "https://api.subdl.com/api/v1/subtitles";
    private const string DownloadHost = "https://dl.subdl.com";
    private const int MaxPages = 5;

    public string Id => "subdl";
    public string Name => "SubDL";

    public IReadOnlyList<SettingField> Fields { get; } =
    [
        new("apiKey", "API key", SettingFieldKind.Secret, Help: "From subdl.com → account → API key (free)."),
    ];

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleSearch search, ProviderConfig config, CancellationToken ct)
    {
        var episodes = search.Episodes.Select(e => e.Episode).ToHashSet();
        var candidates = new List<SubtitleCandidate>();
        for (int page = 1; page <= MaxPages; page++)
        {
            var query = new Dictionary<string, string?>
            {
                ["api_key"] = config.Get("apiKey"), ["film_name"] = search.Query, ["type"] = "tv", ["languages"] = "EN",
                ["subs_per_page"] = "30", ["page"] = page.ToString(),
                ["season_number"] = search.Season?.ToString()
            };
            var url = ApiUrl + "?" + string.Join("&", query.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));

            using var response = await httpClientFactory.CreateClient().GetAsync(url, ct);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!response.IsSuccessStatusCode || json?["status"]?.GetValue<bool>() != true)
            {
                var error = json?["error"]?.ToString() ?? $"HTTP {(int)response.StatusCode}";
                // An empty search reports an error too; that's just no results.
                if (page == 1 && candidates.Count == 0 && error.Contains("can't find", StringComparison.OrdinalIgnoreCase)) return [];
                if (page == 1) throw new SubtitleSourceException($"SubDL: {error}");
                break;
            }

            var subtitles = json["subtitles"]?.AsArray() ?? [];
            foreach (var sub in subtitles)
            {
                if (sub?["episode"]?.GetValue<int?>() is not { } episode || !episodes.Contains(episode)) continue;
                if (search.Season != null && sub["season"]?.GetValue<int?>() is { } season && season != search.Season) continue;

                var note = string.Join(", ", new[] { sub["author"]?.ToString(), sub["hi"]?.GetValue<bool>() == true ? "hearing impaired" : null }
                                           .Where(n => !string.IsNullOrEmpty(n)));
                // The listed url carries the API key as a query parameter; downloads don't need it, and ids reach the browser.
                var path = sub["url"]!.GetValue<string>().Split('?')[0];
                candidates.Add(new SubtitleCandidate(path, episode,
                                                     sub["release_name"]?.ToString() ?? sub["name"]?.ToString() ?? "?",
                                                     null, note.Length > 0 ? note : null));
            }

            if (subtitles.Count < 30) break;
        }

        return candidates;
    }

    public async Task<SubtitleFile> DownloadAsync(string candidateId, ProviderConfig config, CancellationToken ct)
    {
        if (!candidateId.StartsWith("/subtitle/")) throw new SubtitleSourceException("Bad SubDL subtitle id.");

        using var response = await httpClientFactory.CreateClient().GetAsync(DownloadHost + candidateId, ct);
        if (!response.IsSuccessStatusCode) throw new SubtitleSourceException($"SubDL download failed: HTTP {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.Entries.Where(e => EnglishSubtitleParser.SupportedExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()))
                       .MaxBy(e => e.Length)
                    ?? throw new SubtitleSourceException("The SubDL download holds no .ass, .srt or .vtt file.");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new SubtitleFile(entry.Name, await reader.ReadToEndAsync(ct));
    }
}
