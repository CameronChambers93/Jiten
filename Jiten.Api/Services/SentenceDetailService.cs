using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <param name="Status">new, learning, young, mature or settled.</param>
/// <param name="Position">Where the word first appears in the sentence.</param>
public record SentenceDetailWordDto(WordSummaryDto Word, string Status, int Position, int Length);

public record SentenceContextLineDto(int StartMs, string Text);

/// <summary>A line of the sentence's clip: the sentence itself, or other speech its audio holds.</summary>
/// <param name="StartMs">The start of the line's cue.</param>
/// <param name="Text">The subtitle line; for the sentence, its own text.</param>
/// <param name="SentenceId">For another line, the deck's example sentence with the same text, when there is one.</param>
public record SentenceClipLineDto(int StartMs, string Text, bool IsSentence, long? SentenceId);

/// <param name="Before">The subtitle lines just before the sentence's clip; empty when its deck has no timed cues.</param>
/// <param name="Clip">The lines of the sentence's cues in order, when they hold other speech besides the sentence; otherwise
/// empty.</param>
/// <param name="Elsewhere">The same text in other places the user can see.</param>
public record SentenceDetailDto(
    ExampleSentenceDto Sentence,
    List<SentenceDetailWordDto> Words,
    List<SentenceContextLineDto> Before,
    List<SentenceClipLineDto> Clip,
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

    /// <summary>How far, in sentence positions, a clip's other line is looked for among the deck's example sentences.</summary>
    private const int ClipLinkWindow = 10;

    /// <summary>Null when the sentence doesn't exist or the user can't see it (the query filter hides private lines).</summary>
    /// <param name="wordId">The word to mark in the sentence; 0 marks none.</param>
    public async Task<SentenceDetailDto?> GetAsync(long sentenceId, int wordId, int readingIndex, CancellationToken ct)
    {
        var row = await context.ExampleSentences.AsNoTracking()
                               .Where(s => s.SentenceId == sentenceId)
                               .Select(s => new { s.Tokens, s.CentralSentenceId, s.DeckId, s.Position })
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

        var cues = await cueLookup.FindAsync(sentenceId, ct);
        List<SentenceContextLineDto> before = [], after = [];
        List<SentenceClipLineDto> clip = [];
        if (cues != null)
        {
            before = cues.Cues.Take(cues.First).TakeLast(ContextLines).Select(Line).ToList();
            after = cues.Cues.Skip(cues.Last + 1).Take(ContextLines).Select(Line).ToList();
            clip = await ClipAsync(cues, sentenceId, row.DeckId, row.Position, ct);
        }

        return new SentenceDetailDto(sentence, await WordsAsync(row.Tokens), before, clip, after,
                                     await exampleSentences.BuildDtosAsync(elsewhereIds, 0, 0));

        static SentenceContextLineDto Line(SubtitleCue cue) => new(cue.StartMs, cue.Text);
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

    /// <summary>
    /// The clip's lines: those of the first cue before the sentence starts, the sentence, and those of the last cue after
    /// it ends. Empty when there are no others, as the clip is then just the sentence.
    /// </summary>
    private async Task<List<SentenceClipLineDto>> ClipAsync(SentenceCues cues, long sentenceId, int? deckId, int position, CancellationToken ct)
    {
        var clip = new List<SentenceClipLineDto>();
        for (var i = cues.First; i <= cues.Last; i++)
        {
            var lines = SentenceCueMatcher.Lines(cues.Cues[i].Text);
            for (var j = 0; j < lines.Count; j++)
            {
                var other = (i == cues.First && j < cues.FirstLine) || (i == cues.Last && j > cues.LastLine);
                if (other)
                    clip.Add(new SentenceClipLineDto(cues.Cues[i].StartMs, lines[j].Trim(), false, null));
                else if (clip.LastOrDefault()?.IsSentence != true)
                    clip.Add(new SentenceClipLineDto(cues.Cues[i].StartMs, cues.Text, true, null));
            }
        }

        if (clip.All(l => l.IsSentence)) return [];

        // Lines the parser kept as example sentences link to them: the nearest in position with the same text.
        var nearby = await context.ExampleSentences.AsNoTracking()
                                  .Where(s => s.DeckId == deckId && s.SentenceId != sentenceId && s.Position >= position - ClipLinkWindow &&
                                              s.Position <= position + ClipLinkWindow)
                                  .Select(s => new { s.SentenceId, s.Text, s.Position })
                                  .ToListAsync(ct);
        var byText = nearby.GroupBy(s => SentenceCueMatcher.Normalise(s.Text))
                           .ToDictionary(g => g.Key, g => g.MinBy(s => Math.Abs(s.Position - position))!.SentenceId);

        return clip.Select(l => l.IsSentence ? l : l with { SentenceId = byText.TryGetValue(SentenceCueMatcher.Normalise(l.Text), out var id) ? id : null })
                   .ToList();
    }
}
