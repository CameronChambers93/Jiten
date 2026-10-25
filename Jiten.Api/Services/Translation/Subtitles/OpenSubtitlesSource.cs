using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services.Translation.Subtitles;

/// <summary>
/// OpenSubtitles (opensubtitles.com REST API): strong for films and live action, thin for anime. Searching needs only the
/// API key; downloads need a login and count against the account's daily quota.
/// </summary>
public class OpenSubtitlesSource(IHttpClientFactory httpClientFactory, IMemoryCache cache) : ISubtitleSource
{
    private const string ApiUrl = "https://api.opensubtitles.com/api/v1/";
    private const string UserAgent = "n1miner v1.0";

    public string Id => "opensubtitles";
    public string Name => "OpenSubtitles";

    public IReadOnlyList<SettingField> Fields { get; } =
    [
        new("apiKey", "API key", SettingFieldKind.Secret, Help: "opensubtitles.com → profile → API consumers."),
        new("username", "Username", SettingFieldKind.Text, Help: "The account username, not the email address."),
        new("password", "Password", SettingFieldKind.Secret, ShowHint: false),
    ];

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleSearch search, ProviderConfig config, CancellationToken ct)
    {
        var features = await Send(config, HttpMethod.Get, $"features?query={Uri.EscapeDataString(search.Query)}", null, null, ct);
        var show = features["data"]?.AsArray()
                       .FirstOrDefault(f => f?["attributes"]?["feature_type"]?.GetValue<string>() == "Tvshow");
        var showId = show?["id"]?.ToString();
        if (showId == null) return [];

        var episodes = search.Episodes.Select(e => e.Episode).ToHashSet();
        var candidates = new List<SubtitleCandidate>();
        for (int page = 1; page <= 5; page++)
        {
            var path = $"subtitles?languages=en&page={page}&parent_feature_id={showId}" + (search.Season is { } s ? $"&season_number={s}" : "");
            var json = await Send(config, HttpMethod.Get, path, null, null, ct);
            foreach (var sub in json["data"]?.AsArray() ?? [])
            {
                var attributes = sub?["attributes"];
                var feature = attributes?["feature_details"];
                if (feature?["episode_number"]?.GetValue<int?>() is not { } episode || !episodes.Contains(episode)) continue;
                if (attributes?["files"]?.AsArray().FirstOrDefault()?["file_id"]?.ToString() is not { } fileId) continue;

                var note = attributes["hearing_impaired"]?.GetValue<bool>() == true ? "hearing impaired" : null;
                candidates.Add(new SubtitleCandidate(fileId, episode, attributes["release"]?.ToString() ?? "?",
                                                     attributes["download_count"]?.GetValue<int?>(), note));
            }

            if (page >= (json["total_pages"]?.GetValue<int>() ?? 1)) break;
        }

        return candidates;
    }

    public async Task<SubtitleFile> DownloadAsync(string candidateId, ProviderConfig config, CancellationToken ct)
    {
        if (!long.TryParse(candidateId, out var fileId)) throw new SubtitleSourceException("Bad OpenSubtitles file id.");

        var token = await LoginAsync(config, ct);
        var link = await Send(config, HttpMethod.Post, "download", new { file_id = fileId }, token, ct);
        var url = link["link"]?.GetValue<string>() ?? throw new SubtitleSourceException($"OpenSubtitles: {link["message"]}");

        var content = await httpClientFactory.CreateClient().GetStringAsync(url, ct);
        return new SubtitleFile(link["file_name"]?.GetValue<string>() ?? $"{fileId}.srt", content);
    }

    private async Task<string> LoginAsync(ProviderConfig config, CancellationToken ct)
    {
        var username = config.Get("username");
        if (username.Length == 0) throw new SubtitleSourceException("OpenSubtitles downloads need the account username and password.");

        var key = $"opensubtitles-token:{username}";
        if (cache.TryGetValue(key, out string? token) && token != null) return token;

        var login = await Send(config, HttpMethod.Post, "login", new { username, password = config.Get("password") }, null, ct);
        token = login["token"]?.GetValue<string>() ?? throw new SubtitleSourceException($"OpenSubtitles login failed: {login["message"]}");
        cache.Set(key, token, TimeSpan.FromHours(12));
        return token;
    }

    private async Task<JsonNode> Send(ProviderConfig config, HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ApiUrl + path);
        request.Headers.Add("Api-Key", config.Get("apiKey"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try
        {
            json = JsonNode.Parse(content);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || json == null)
            throw new SubtitleSourceException($"OpenSubtitles: {json?["message"]?.ToString() ?? $"HTTP {(int)response.StatusCode}"}");
        return json;
    }
}
