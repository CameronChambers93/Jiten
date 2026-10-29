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
}

/// <param name="Origin">"subtitles" or "ai".</param>
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

    public async Task<SentenceTranslationDto?> GetAsync(long sentenceId, bool allowAi, CancellationToken ct)
    {
        var cues = await cueLookup.FindAsync(sentenceId, ct);
        if (await FromSubtitlesAsync(cues, ct) is { } fromSubtitles) return fromSubtitles;

        var cached = await context.ExampleSentenceTranslations.AsNoTracking().FirstOrDefaultAsync(t => t.SentenceId == sentenceId, ct);
        if (cached != null) return new SentenceTranslationDto(cached.Text, TranslationOrigin.Ai, cached.Provider, cached.Model, null);

        return allowAi ? await TranslateWithAiAsync(sentenceId, cues, ct) : null;
    }

    /// <summary>Subtitle and cached translations only, for showing many sentences at once.</summary>
    public async Task<Dictionary<long, SentenceTranslationDto>> GetManyAsync(IReadOnlyCollection<long> sentenceIds, CancellationToken ct)
    {
        var result = new Dictionary<long, SentenceTranslationDto>();
        foreach (var id in sentenceIds)
            if (await FromSubtitlesAsync(await cueLookup.FindAsync(id, ct), ct) is { } fromSubtitles)
                result[id] = fromSubtitles;

        var missing = sentenceIds.Where(id => !result.ContainsKey(id)).ToList();
        var cached = await context.ExampleSentenceTranslations.AsNoTracking().Where(t => missing.Contains(t.SentenceId)).ToListAsync(ct);
        foreach (var t in cached)
            result[t.SentenceId] = new SentenceTranslationDto(t.Text, TranslationOrigin.Ai, t.Provider, t.Model, null);

        return result;
    }

    /// <summary>Whether a sentence would be translated from subtitles (used to count what a title still needs from the AI).</summary>
    public async Task<bool> HasSubtitleEnglishAsync(long sentenceId, CancellationToken ct) =>
        await FromSubtitlesAsync(await cueLookup.FindAsync(sentenceId, ct), ct) != null;

    public record DeckCoverage(int Sentences, int FromSubtitles, int CachedAi, List<long> NeedAi);

    /// <summary>How an episode's example sentences would be translated today: from subtitles, from the cache, or by the AI.</summary>
    public async Task<DeckCoverage> CoverageAsync(int deckId, CancellationToken ct)
    {
        var sentences = await context.ExampleSentences.AsNoTracking().Where(s => s.DeckId == deckId)
                                     .Select(s => new { s.SentenceId, s.Text, s.Position }).ToListAsync(ct);
        var sentenceCount = await context.Decks.AsNoTracking().Where(d => d.DeckId == deckId).Select(d => d.SentenceCount).FirstOrDefaultAsync(ct);
        var cached = (await context.ExampleSentenceTranslations.AsNoTracking().Where(t => t.DeckId == deckId)
                                   .Select(t => t.SentenceId).ToListAsync(ct)).ToHashSet();
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
    public async Task<SentenceTranslationDto> TranslateWithAiAsync(long sentenceId, SentenceCues? cues, CancellationToken ct)
    {
        var active = await ActiveProviderAsync(ct) ?? throw new TranslationProviderException("No AI translation provider is set up.");
        var settings = await settingsStore.GetAsync(ct);

        var sentence = await context.ExampleSentences.AsNoTracking()
                                    .Where(s => s.SentenceId == sentenceId)
                                    .Select(s => new
                                    {
                                        s.DeckId, s.Text,
                                        Title = s.Deck!.ParentDeck != null ? s.Deck.ParentDeck.OriginalTitle : s.Deck.OriginalTitle
                                    })
                                    .FirstOrDefaultAsync(ct)
                       ?? throw new KeyNotFoundException($"Sentence {sentenceId} not found");

        var contextLines = cues == null
            ? []
            : cues.Cues.Skip(Math.Max(0, cues.First - settings.ContextLines))
                  .Take(cues.Last - Math.Max(0, cues.First - settings.ContextLines) + 1 + settings.ContextLines)
                  .Select(c => c.Text)
                  .ToList();

        var prompt = TranslationPrompt.Render(settings.EffectivePrompt, sentence.Title, contextLines, sentence.Text);
        var result = await active.Provider.TranslateAsync(active.Config, prompt, ct);

        context.ExampleSentenceTranslations.Add(new ExampleSentenceTranslation
        {
            SentenceId = sentenceId, DeckId = sentence.DeckId, Text = result.Text,
            Provider = active.Provider.Id, Model = result.Model, CreatedAt = DateTime.UtcNow
        });
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            // Another request translated the same sentence first; keep its translation.
            logger.LogDebug(e, "Translation for sentence {SentenceId} was already saved", sentenceId);
            context.ChangeTracker.Clear();
        }

        return new SentenceTranslationDto(result.Text, TranslationOrigin.Ai, active.Provider.Id, result.Model, null);
    }

    private async Task<SentenceTranslationDto?> FromSubtitlesAsync(SentenceCues? cues, CancellationToken ct)
    {
        if (cues == null) return null;

        var english = await EnglishAsync(cues.DeckId, ct);
        var text = english?.Aligner.TextFor(cues.StartMs, cues.EndMs, cues.From, cues.To);
        return text == null ? null : new SentenceTranslationDto(text, TranslationOrigin.Subtitles, null, null, english!.Source);
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
