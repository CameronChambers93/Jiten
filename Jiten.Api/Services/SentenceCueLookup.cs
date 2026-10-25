using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

/// <summary>An example sentence with the Japanese cues it was taken from, in subtitle time.</summary>
/// <param name="From">See <see cref="CueMatch.From"/>.</param>
public record SentenceCues(int DeckId, string Text, IReadOnlyList<SubtitleCue> Cues, int First, int Last, double From = 0, double To = 1)
{
    public int StartMs => Cues[First].StartMs;
    public int EndMs => Cues[Last].EndMs;
}

/// <summary>Finds the subtitle cues behind example sentences, for card media and English translations.</summary>
public class SentenceCueLookup(JitenDbContext context, IMemoryCache cache)
{
    private static readonly TimeSpan MatcherTtl = TimeSpan.FromMinutes(30);

    /// <summary>Null when the sentence doesn't exist, its deck has no stored cues, or no cue matches it.</summary>
    public async Task<SentenceCues?> FindAsync(long sentenceId, CancellationToken ct)
    {
        var sentence = await context.ExampleSentences.AsNoTracking()
                                    .Where(s => s.SentenceId == sentenceId)
                                    .Select(s => new { s.DeckId, s.Text, s.Position, s.Deck!.SentenceCount })
                                    .FirstOrDefaultAsync(ct);
        if (sentence == null) return null;

        var matcher = await MatcherAsync(sentence.DeckId, ct);
        var relative = sentence.SentenceCount > 0 ? Math.Clamp(sentence.Position / (double)sentence.SentenceCount, 0, 1) : 0;
        return matcher?.Locate(sentence.Text, relative) is { } match
            ? new SentenceCues(sentence.DeckId, sentence.Text, matcher.Cues, match.First, match.Last, match.From, match.To)
            : null;
    }

    public Task<SentenceCueMatcher?> MatcherAsync(int deckId, CancellationToken ct) =>
        cache.GetOrCreateAsync($"sentence-cues:{deckId}", async entry =>
        {
            entry.SlidingExpiration = MatcherTtl;
            var cuesJson = await context.DeckSubtitleTracks.AsNoTracking()
                                        .Where(t => t.DeckId == deckId)
                                        .Select(t => t.CuesJson)
                                        .FirstOrDefaultAsync(ct);
            return cuesJson == null ? null : new SentenceCueMatcher(new DeckSubtitleTrack { CuesJson = cuesJson }.GetCues());
        });
}
