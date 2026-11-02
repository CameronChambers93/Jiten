using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <param name="Status">new, learning, young, mature or settled.</param>
/// <param name="Position">Where the word first appears in the sentence.</param>
public record SentenceDetailWordDto(WordSummaryDto Word, string Status, int Position, int Length);

public record SentenceContextLineDto(int StartMs, string Text);

/// <param name="Before">The subtitle lines just before the sentence; empty when its deck has no timed cues.</param>
/// <param name="Elsewhere">The same text in other places the user can see.</param>
public record SentenceDetailDto(
    ExampleSentenceDto Sentence,
    List<SentenceDetailWordDto> Words,
    List<SentenceContextLineDto> Before,
    List<SentenceContextLineDto> After,
    List<ExampleSentenceDto> Elsewhere);

/// <summary>One sentence occurrence with its words and surrounding lines, for its own page.</summary>
public class SentenceDetailService(
    JitenDbContext context,
    ICurrentUserService currentUser,
    IExampleSentenceQueryService exampleSentences,
    ISentenceStatsService sentenceStats,
    SentenceCueLookup cueLookup)
{
    private const int ContextLines = 3;
    private const int MaxElsewhere = 20;

    /// <summary>Null when the sentence doesn't exist or the user can't see it (the query filter hides private lines).</summary>
    /// <param name="wordId">The word to mark in the sentence; 0 marks none.</param>
    public async Task<SentenceDetailDto?> GetAsync(long sentenceId, int wordId, int readingIndex, CancellationToken ct)
    {
        var row = await context.ExampleSentences.AsNoTracking()
                               .Where(s => s.SentenceId == sentenceId)
                               .Select(s => new { s.Tokens, s.CentralSentenceId })
                               .FirstOrDefaultAsync(ct);
        if (row == null) return null;

        var sentence = (await exampleSentences.BuildDtosAsync([sentenceId], wordId, readingIndex)).FirstOrDefault();
        if (sentence == null) return null;

        List<long> elsewhereIds = row.CentralSentenceId == null
            ? []
            : await context.ExampleSentences.AsNoTracking()
                           .Where(s => s.CentralSentenceId == row.CentralSentenceId && s.SentenceId != sentenceId)
                           .OrderBy(s => s.SentenceId)
                           .Select(s => s.SentenceId)
                           .Take(MaxElsewhere)
                           .ToListAsync(ct);

        var (before, after) = await ContextAsync(sentenceId, ct);
        return new SentenceDetailDto(sentence, await WordsAsync(row.Tokens), before, after,
                                     await exampleSentences.BuildDtosAsync(elsewhereIds, 0, 0));
    }

    private async Task<List<SentenceDetailWordDto>> WordsAsync(byte[] tokens)
    {
        var words = ExampleSentenceTokens.Decode(tokens)
                                         .Where(t => !t.IsFunctionWord && !SentenceComprehension.IsNameEntry(t.WordId))
                                         .OrderBy(t => t.Position)
                                         .DistinctBy(t => t.WordKey)
                                         .ToList();
        var summaries = await sentenceStats.LoadWordSummariesAsync(words.Select(t => t.WordKey));
        var states = await currentUser.GetKnownWordsState(words.Select(t => (t.WordId, t.ReadingIndex)));

        return words.Where(t => summaries.ContainsKey(t.WordKey))
                    .Select(t =>
                    {
                        var status = SentenceComprehension.MiningStatusOf(states.GetValueOrDefault((t.WordId, t.ReadingIndex)) ?? []);
                        return new SentenceDetailWordDto(summaries[t.WordKey], status.ToString().ToLowerInvariant(), t.Position, t.Length);
                    })
                    .ToList();
    }

    private async Task<(List<SentenceContextLineDto> Before, List<SentenceContextLineDto> After)> ContextAsync(long sentenceId, CancellationToken ct)
    {
        var cues = await cueLookup.FindAsync(sentenceId, ct);
        if (cues == null) return ([], []);

        var before = cues.Cues.Take(cues.First).TakeLast(ContextLines);
        var after = cues.Cues.Skip(cues.Last + 1).Take(ContextLines);
        return (before.Select(Line).ToList(), after.Select(Line).ToList());

        static SentenceContextLineDto Line(SubtitleCue cue) => new(cue.StartMs, cue.Text);
    }
}
