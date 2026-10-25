using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services.Translation.Subtitles;

public record EpisodeEnglishInfo(int DeckId, int Episode, string Title, bool HasJapaneseCues, string? EnglishSource, int? EnglishOffsetMs,
                                 int? EnglishCueCount, DateTime? SavedAt);

public record SubtitleSourceInfo(string Id, string Name, bool Configured);

public record TitleEnglishInfo(string Query, int? Season, List<SubtitleSourceInfo> Sources, List<EpisodeEnglishInfo> Episodes);

public record PreviewRequestItem(int DeckId, string SourceId, string CandidateId, string Release);

public record PreviewPair(string Japanese, string? English);

/// <param name="Coverage">Share of the English cue time that falls on Japanese cues once shifted (near 1 when aligned).</param>
public record EnglishPreview(int DeckId, string? Token, string Release, int CueCount, int OffsetMs, double Coverage, double CoverageUnshifted,
                             List<PreviewPair> Samples, string? Error);

public record SaveRequestItem(int DeckId, string Token, int? OffsetMs);

/// <summary>Finds, previews and saves English subtitle tracks for a title's episodes.</summary>
public partial class EnglishSubtitleService(
    JitenDbContext context,
    IEnumerable<ISubtitleSource> sources,
    TranslationSettingsStore settingsStore,
    SentenceCueLookup cueLookup,
    IMemoryCache cache)
{
    private const int SampleCount = 6;
    private static readonly TimeSpan PreviewTtl = TimeSpan.FromHours(1);

    private record PendingTrack(int DeckId, string CuesJson, string Source, int OffsetMs);

    public IEnumerable<ISubtitleSource> Sources => sources;

    public async Task<TitleEnglishInfo?> InfoAsync(int parentDeckId, CancellationToken ct)
    {
        var parent = await context.Decks.AsNoTracking().Where(d => d.DeckId == parentDeckId)
                                  .Select(d => new { d.OriginalTitle, d.RomajiTitle, d.EnglishTitle }).FirstOrDefaultAsync(ct);
        if (parent == null) return null;

        var children = await context.Decks.AsNoTracking().Where(d => d.ParentDeckId == parentDeckId).OrderBy(d => d.DeckOrder)
                                    .Select(d => new { d.DeckId, d.OriginalTitle }).ToListAsync(ct);
        var ids = children.Select(c => c.DeckId).ToList();
        var withCues = await context.DeckSubtitleTracks.AsNoTracking().Where(t => ids.Contains(t.DeckId)).Select(t => t.DeckId).ToListAsync(ct);
        var english = await context.DeckEnglishTracks.AsNoTracking().Where(t => ids.Contains(t.DeckId)).ToDictionaryAsync(t => t.DeckId, ct);

        var episodes = children.Select((c, i) =>
        {
            var track = english.GetValueOrDefault(c.DeckId);
            return new EpisodeEnglishInfo(c.DeckId, i + 1, c.OriginalTitle, withCues.Contains(c.DeckId), track?.Source, track?.OffsetMs,
                                          track?.GetCues().Count, track?.SavedAt);
        }).ToList();

        var settings = await settingsStore.GetAsync(ct);
        var sourceInfos = sources.Select(s => new SubtitleSourceInfo(s.Id, s.Name, settingsStore.Config(settings.SubtitleSources, s.Id, s.Fields).HasSecrets))
                                 .ToList();
        var (query, season) = GuessQuery(parent.EnglishTitle ?? parent.RomajiTitle ?? parent.OriginalTitle, parent.OriginalTitle);
        return new TitleEnglishInfo(query, season, sourceInfos, episodes);
    }

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(int parentDeckId, string sourceId, string query, int? season, CancellationToken ct)
    {
        var info = await InfoAsync(parentDeckId, ct) ?? throw new KeyNotFoundException("Title not found");
        var (source, config) = await SourceAsync(sourceId, ct);
        var episodes = info.Episodes.Where(e => e.HasJapaneseCues).Select(e => new EpisodeRef(e.DeckId, e.Episode)).ToList();
        return await source.SearchAsync(new SubtitleSearch(query.Trim(), season, episodes), config, ct);
    }

    /// <summary>Downloads each pick, detects its offset against the episode's Japanese cues and pairs a few sentences.</summary>
    public async Task<List<EnglishPreview>> PreviewAsync(IReadOnlyList<PreviewRequestItem> items, CancellationToken ct)
    {
        var previews = new List<EnglishPreview>();
        foreach (var item in items)
        {
            try
            {
                previews.Add(await PreviewOneAsync(item, ct));
            }
            catch (Exception e) when (e is SubtitleSourceException or HttpRequestException or InvalidDataException)
            {
                previews.Add(new EnglishPreview(item.DeckId, null, item.Release, 0, 0, 0, 0, [], e.Message));
            }
        }

        return previews;
    }

    public async Task<int> SaveAsync(IReadOnlyList<SaveRequestItem> items, CancellationToken ct)
    {
        var saved = 0;
        foreach (var item in items)
        {
            if (!cache.TryGetValue($"english-preview:{item.Token}", out PendingTrack? pending) || pending == null || pending.DeckId != item.DeckId)
                continue;

            var track = await context.DeckEnglishTracks.FindAsync([item.DeckId], ct);
            if (track == null)
            {
                track = new DeckEnglishTrack { DeckId = item.DeckId };
                context.DeckEnglishTracks.Add(track);
            }

            track.CuesJson = pending.CuesJson;
            track.OffsetMs = item.OffsetMs ?? pending.OffsetMs;
            track.Source = pending.Source.Length > 500 ? pending.Source[..500] : pending.Source;
            track.SavedAt = DateTime.UtcNow;
            saved++;
        }

        await context.SaveChangesAsync(ct);
        foreach (var item in items)
            SentenceTranslationService.InvalidateEnglish(cache, item.DeckId);
        return saved;
    }

    public async Task<bool> DeleteAsync(int deckId, CancellationToken ct)
    {
        var deleted = await context.DeckEnglishTracks.Where(t => t.DeckId == deckId).ExecuteDeleteAsync(ct);
        SentenceTranslationService.InvalidateEnglish(cache, deckId);
        return deleted > 0;
    }

    private async Task<EnglishPreview> PreviewOneAsync(PreviewRequestItem item, CancellationToken ct)
    {
        var (source, config) = await SourceAsync(item.SourceId, ct);
        var file = await source.DownloadAsync(item.CandidateId, config, ct);
        var cues = EnglishSubtitleParser.Parse(file.Content, Path.GetExtension(file.FileName));
        if (cues.Count == 0) throw new SubtitleSourceException($"No dialogue lines in {file.FileName}.");

        var matcher = await cueLookup.MatcherAsync(item.DeckId, ct) ?? throw new SubtitleSourceException("This episode has no timed Japanese subtitles.");
        var japanese = matcher.Cues;
        var detected = SubtitleOffsetDetector.DetectAgainstCues(japanese.Select(c => (c.StartMs, c.EndMs)), cues.Select(c => (c.StartMs, c.EndMs)));

        var aligner = new TranslationCueAligner(cues, detected.OffsetMs);
        var samples = await SamplesAsync(item.DeckId, aligner, ct);

        var token = Guid.NewGuid().ToString("N");
        var json = DeckSubtitleTrack.FromItems(cues.Select(c => new SubtitleItem(c.StartMs, c.EndMs, c.Text))).CuesJson;
        cache.Set($"english-preview:{token}", new PendingTrack(item.DeckId, json, $"{source.Name}: {item.Release}", detected.OffsetMs), PreviewTtl);

        return new EnglishPreview(item.DeckId, token, item.Release, cues.Count, detected.OffsetMs,
                                  TranslationCueAligner.Coverage(japanese, cues, detected.OffsetMs),
                                  TranslationCueAligner.Coverage(japanese, cues, 0), samples, null);
    }

    /// <summary>A few of the episode's example sentences, spread through it, with the English the track would give them.</summary>
    private async Task<List<PreviewPair>> SamplesAsync(int deckId, TranslationCueAligner aligner, CancellationToken ct)
    {
        var ids = await context.ExampleSentences.AsNoTracking().Where(s => s.DeckId == deckId).OrderBy(s => s.Position)
                               .Select(s => s.SentenceId).ToListAsync(ct);
        var pairs = new List<PreviewPair>();
        for (int i = 0; i < SampleCount && ids.Count > 0; i++)
        {
            var cues = await cueLookup.FindAsync(ids[(int)((i + 0.5) * ids.Count / SampleCount)], ct);
            if (cues != null) pairs.Add(new PreviewPair(cues.Text, aligner.TextFor(cues.StartMs, cues.EndMs, cues.From, cues.To)));
        }

        return pairs;
    }

    private async Task<(ISubtitleSource Source, ProviderConfig Config)> SourceAsync(string sourceId, CancellationToken ct)
    {
        var source = sources.FirstOrDefault(s => s.Id == sourceId) ?? throw new SubtitleSourceException($"Unknown subtitle source {sourceId}.");
        var settings = await settingsStore.GetAsync(ct);
        var config = settingsStore.Config(settings.SubtitleSources, source.Id, source.Fields);
        if (!config.HasSecrets) throw new SubtitleSourceException($"{source.Name} needs its API key; set it on the translation settings page.");
        return (source, config);
    }

    [GeneratedRegex(@"\s*(?:(\d+)(?:st|nd|rd|th)\s+season|season\s+(\d+)|\bs(\d+)\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonInTitle();

    [GeneratedRegex(@"第\s*(\d+)\s*期")]
    private static partial Regex JapaneseSeason();

    /// <summary>
    /// The search text and season from a title like "Mr. Osomatsu 2nd season" or "おそ松さん 第2期": sources index shows by
    /// their base name with a season number.
    /// </summary>
    public static (string Query, int? Season) GuessQuery(string title, string originalTitle)
    {
        var match = SeasonInTitle().Match(title);
        int? season = null;
        if (match.Success)
            season = int.Parse(match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);
        else if (JapaneseSeason().Match(originalTitle) is { Success: true } japanese)
            season = int.Parse(japanese.Groups[1].Value);

        var query = match.Success ? SeasonInTitle().Replace(title, " ").Trim() : title.Trim();
        return (query, season);
    }
}
