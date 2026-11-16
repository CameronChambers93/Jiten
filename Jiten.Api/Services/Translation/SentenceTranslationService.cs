using System.Linq.Expressions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services.Translation;

public static class TranslationOrigin
{
    public const string Subtitles = "subtitles";
    public const string Ai = "ai";

    /// <summary>A fix's English, the user's own or the shared one. Cards store it as "user", like English typed on the card.</summary>
    public const string Fix = "user";
}

/// <param name="Origin">"subtitles", "ai" or "user" (a fix).</param>
/// <param name="Source">For subtitles, the release they came from.</param>
public record SentenceTranslationDto(string Text, string Origin, string? Provider, string? Model, string? Source);

/// <summary>
/// The English for an example sentence: the English subtitle cues over its Japanese cues when the episode has an English
/// track, else a cached AI translation, else (when allowed) a new one from the active AI provider.
/// </summary>
public class SentenceTranslationService(
    JitenDbContext context,
    SentenceCueLookup cueLookup,
    IMemoryCache cache,
    TranslationSettingsStore settingsStore,
    IEnumerable<ITranslationProvider> providers,
    ILogger<SentenceTranslationService> logger)
{
    private record EnglishTrackCache(TranslationCueAligner Aligner, string Source);

    private static readonly TimeSpan EnglishTtl = TimeSpan.FromMinutes(30);

    public static void InvalidateEnglish(IMemoryCache cache, int deckId) => cache.Remove($"english-cues:{deckId}");

    /// <param name="fixedText">The caller's own text for the line, if they fixed it: the AI translates that text instead, cached
    /// under its own Sentences row. Subtitle English still comes first, since it matches the audio.</param>
    public async Task<SentenceTranslationDto?> GetAsync(long sentenceId, bool allowAi, CancellationToken ct, string? fixedText = null)
    {
        var cues = await cueLookup.FindAsync(sentenceId, ct);
        if (await FromSubtitlesAsync(cues, ct) is { } fromSubtitles) return fromSubtitles;

        var cached = fixedText != null
            ? await CachedForTextAsync(fixedText, ct)
            : (await CachedAsync(s => s.SentenceId == sentenceId, ct)).FirstOrDefault().Translation;
        if (cached != null) return Ai(cached);

        return allowAi ? await TranslateWithAiAsync(sentenceId, cues, ct, fixedText) : null;
    }

    /// <summary>Subtitle and cached translations only, for showing many sentences at once; <paramref name="fixedTexts"/> as in <see cref="GetAsync"/>.</summary>
    public async Task<Dictionary<long, SentenceTranslationDto>> GetManyAsync(IReadOnlyCollection<long> sentenceIds, CancellationToken ct,
                                                                            IReadOnlyDictionary<long, string>? fixedTexts = null)
    {
        var result = new Dictionary<long, SentenceTranslationDto>();
        foreach (var id in sentenceIds)
            if (await FromSubtitlesAsync(await cueLookup.FindAsync(id, ct), ct) is { } fromSubtitles)
                result[id] = fromSubtitles;

        var missing = sentenceIds.Where(id => !result.ContainsKey(id) && fixedTexts?.ContainsKey(id) != true).ToList();
        foreach (var (id, t) in await CachedAsync(s => missing.Contains(s.SentenceId), ct))
            result[id] = Ai(t);

        var wanted = (fixedTexts ?? new Dictionary<long, string>()).Where(p => sentenceIds.Contains(p.Key) && !result.ContainsKey(p.Key)).ToList();
        if (wanted.Count > 0)
        {
            var rows = await CentralSentenceRows.FindManyAsync(context, wanted.Select(p => p.Value).Distinct().ToList(), ct);
            var rowIds = rows.Values.Distinct().ToList();
            var cached = await context.ExampleSentenceTranslations.AsNoTracking().Where(t => rowIds.Contains(t.CentralSentenceId))
                                      .ToDictionaryAsync(t => t.CentralSentenceId, ct);
            foreach (var (id, text) in wanted)
                if (rows.GetValueOrDefault(text) is { } row && cached.GetValueOrDefault(row) is { } t)
                    result[id] = Ai(t);
        }

        return result;
    }

    private static SentenceTranslationDto Ai(ExampleSentenceTranslation t) => new(t.Text, TranslationOrigin.Ai, t.Provider, t.Model, null);

    /// <summary>Whether a sentence would be translated from subtitles (used to count what a title still needs from the AI).</summary>
    public async Task<bool> HasSubtitleEnglishAsync(long sentenceId, CancellationToken ct) =>
        await FromSubtitlesAsync(await cueLookup.FindAsync(sentenceId, ct), ct) != null;

    public record DeckCoverage(int Sentences, int FromSubtitles, int CachedAi, List<long> NeedAi);

    /// <summary>How an episode's example sentences would be translated today: from subtitles, from the cache, or by the AI.</summary>
    public async Task<DeckCoverage> CoverageAsync(int deckId, CancellationToken ct)
    {
        var sentences = await context.ExampleSentences.AsNoTracking().Where(s => s.DeckId == deckId)
                                     .Select(s => new { s.SentenceId, Text = s.SourceText ?? s.Text, s.Position }).ToListAsync(ct);
        var sentenceCount = await context.Decks.AsNoTracking().Where(d => d.DeckId == deckId).Select(d => d.SentenceCount).FirstOrDefaultAsync(ct);
        var cached = (await CachedAsync(s => s.DeckId == deckId, ct)).Select(c => c.SentenceId).ToHashSet();
        var matcher = await cueLookup.MatcherAsync(deckId, ct);
        var english = await EnglishAsync(deckId, ct);

        int fromSubtitles = 0, cachedAi = 0;
        var needAi = new List<long>();
        foreach (var sentence in sentences)
        {
            var relative = sentenceCount > 0 ? Math.Clamp(sentence.Position / (double)sentenceCount, 0, 1) : 0;
            if (english != null && matcher?.Locate(sentence.Text, relative) is { } match
                                && english.Aligner.TextFor(matcher.Cues[match.First].StartMs, matcher.Cues[match.Last].EndMs, match.From, match.To) != null)
                fromSubtitles++;
            else if (cached.Contains(sentence.SentenceId))
                cachedAi++;
            else
                needAi.Add(sentence.SentenceId);
        }

        return new DeckCoverage(sentences.Count, fromSubtitles, cachedAi, needAi);
    }

    /// <summary>The configured AI provider and its settings, or null when none is set up.</summary>
    public async Task<(ITranslationProvider Provider, ProviderConfig Config)?> ActiveProviderAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var provider = providers.FirstOrDefault(p => p.Id == settings.ActiveProvider);
        if (provider == null) return null;

        var config = settingsStore.Config(settings.Providers, provider.Id, provider.Fields);
        return config.HasSecrets ? (provider, config) : null;
    }

    /// <summary>Translates with the active AI provider and caches the result; throws when no provider is set up.</summary>
    public async Task<SentenceTranslationDto> TranslateWithAiAsync(long sentenceId, SentenceCues? cues, CancellationToken ct, string? fixedText = null)
    {
        var active = await ActiveProviderAsync(ct) ?? throw new TranslationProviderException("No AI translation provider is set up.");
        var settings = await settingsStore.GetAsync(ct);

        var sentence = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                    .Where(s => s.SentenceId == sentenceId && s.DeckId != null)
                                    .Select(s => new
                                    {
                                        s.CentralSentenceId, s.Text,
                                        Title = s.Deck!.ParentDeck != null ? s.Deck.ParentDeck.OriginalTitle : s.Deck.OriginalTitle
                                    })
                                    .FirstOrDefaultAsync(ct)
                       ?? throw new KeyNotFoundException($"Sentence {sentenceId} not found");

        var text = fixedText ?? sentence.Text;
        var (before, own, after) = SplitContext(cues, settings.ContextLines);
        // A sentence that starts partway through a cue: the cue's earlier text is context too.
        var ownText = string.Concat(own);
        var at = ownText.IndexOf(sentence.Text, StringComparison.Ordinal);
        var directBefore = at > 0 ? [..before, ownText[..at].Trim()] : before;
        var prompt = TranslationPrompt.Render(settings.EffectivePrompt, sentence.Title, [..before, ..own, ..after], text);
        var result = await active.Provider.TranslateAsync(active.Config, new TranslationRequest(prompt, text, directBefore, after), ct);
        await using var hold = fixedText != null ? await JitenHelper.HoldSentenceRowsAsync(context, ct) : null;
        var centralId = fixedText != null ? await CentralSentenceRows.EnsureAsync(context, fixedText, ct) : sentence.CentralSentenceId!.Value;

        context.ExampleSentenceTranslations.Add(new ExampleSentenceTranslation
        {
            CentralSentenceId = centralId, Text = result.Text,
            Provider = active.Provider.Id, Model = result.Model, CreatedAt = DateTime.UtcNow
        });
        try
        {
            await context.SaveChangesAsync(ct);
            if (hold != null) await hold.CommitAsync(ct);
        }
        catch (DbUpdateException e)
        {
            // Another request translated the same sentence first; keep its translation.
            logger.LogDebug(e, "Translation for sentence {SentenceId} was already saved", sentenceId);
            context.ChangeTracker.Clear();
        }

        return new SentenceTranslationDto(result.Text, TranslationOrigin.Ai, active.Provider.Id, result.Model, null);
    }

    /// <summary>
    /// The subtitle lines around a sentence: up to <paramref name="lines"/> before its first cue, its own cues, and up to
    /// <paramref name="lines"/> after its last.
    /// </summary>
    public static (List<string> Before, List<string> Own, List<string> After) SplitContext(SentenceCues? cues, int lines)
    {
        if (cues == null) return ([], [], []);
        var start = Math.Max(0, cues.First - lines);
        return (cues.Cues.Skip(start).Take(cues.First - start).Select(c => c.Text).ToList(),
                cues.Cues.Skip(cues.First).Take(cues.Last - cues.First + 1).Select(c => c.Text).ToList(),
                cues.Cues.Skip(cues.Last + 1).Take(lines).Select(c => c.Text).ToList());
    }

    /// <summary>The cached translations of deck occurrences' texts; a private line's id finds nothing.</summary>
    private async Task<List<(long SentenceId, ExampleSentenceTranslation Translation)>> CachedAsync(Expression<Func<ExampleSentence, bool>> occurrences, CancellationToken ct)
    {
        var rows = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters().Where(s => s.DeckId != null).Where(occurrences)
                                .Join(context.ExampleSentenceTranslations.AsNoTracking(), s => s.CentralSentenceId, t => (long?)t.CentralSentenceId,
                                      (s, t) => new { s.SentenceId, Translation = t })
                                .ToListAsync(ct);
        return rows.Select(r => (r.SentenceId, r.Translation)).ToList();
    }

    /// <summary>The cached translation of a text that may have no line of its own (a user's fixed text).</summary>
    private async Task<ExampleSentenceTranslation?> CachedForTextAsync(string text, CancellationToken ct) =>
        await CentralSentenceRows.FindAsync(context, text, ct) is { } id
            ? await context.ExampleSentenceTranslations.AsNoTracking().FirstOrDefaultAsync(t => t.CentralSentenceId == id, ct)
            : null;

    private async Task<SentenceTranslationDto?> FromSubtitlesAsync(SentenceCues? cues, CancellationToken ct)
    {
        if (cues == null) return null;

        var english = await EnglishAsync(cues.DeckId, ct);
        var text = english?.Aligner.TextFor(cues.StartMs, cues.EndMs, cues.From, cues.To);
        return text == null ? null : new SentenceTranslationDto(text, TranslationOrigin.Subtitles, null, null, english!.Source);
    }

    /// <summary>
    /// The subtitle English for a clip in subtitle time, as the fix screen suggests it: the English over the Japanese cues
    /// the clip mostly covers (so its padding doesn't pull in a neighbour's line), else over the clip itself.
    /// </summary>
    public async Task<string?> EnglishForRangeAsync(int deckId, int startMs, int endMs, CancellationToken ct)
    {
        var english = await EnglishAsync(deckId, ct);
        if (english == null) return null;

        var covered = (await cueLookup.MatcherAsync(deckId, ct))?.Cues
                      .Where(c => Math.Min(c.EndMs, endMs) - Math.Max(c.StartMs, startMs) >= 0.5 * (c.EndMs - c.StartMs))
                      .ToList();
        return covered is { Count: > 0 }
            ? english.Aligner.TextFor(covered[0].StartMs, covered[^1].EndMs)
            : english.Aligner.TextFor(startMs, endMs);
    }

    private Task<EnglishTrackCache?> EnglishAsync(int deckId, CancellationToken ct) =>
        cache.GetOrCreateAsync($"english-cues:{deckId}", async entry =>
        {
            entry.SlidingExpiration = EnglishTtl;
            var track = await context.DeckEnglishTracks.AsNoTracking().FirstOrDefaultAsync(t => t.DeckId == deckId, ct);
            if (track == null) return null;
            var japanese = (await cueLookup.MatcherAsync(deckId, ct))?.Cues;
            return new EnglishTrackCache(new TranslationCueAligner(track.GetCues(), track.OffsetMs, japanese), track.Source);
        });
}
