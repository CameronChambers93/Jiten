using Jiten.Core;
using Jiten.Parser.SpeechBoundaries;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

public record CorrectedTitleDto(int DeckId, string Title, int Corrections);

/// <summary>
/// Submitted text fixes at parse time (sentence flags step 3): which corrections a reparse puts back into each deck's
/// lines, which titles have corrections their last parse hasn't seen, and what happens to a fix that finds no line.
/// </summary>
public class ParseCorrectionService(IDbContextFactory<JitenDbContext> contextFactory, SentenceFlagService flags, ILogger<ParseCorrectionService> logger)
{
    /// <summary>
    /// The text fix in force for each corrected line of these decks: the latest correction with text, at the line's
    /// position now, which an earlier reparse may have moved. A line already out of the deck (an earlier reparse found no
    /// place for its fix, and its submitter was told) or gone is left out, so it isn't reported again on every reparse.
    /// </summary>
    public async Task<Dictionary<int, List<(LineCorrection Correction, string UserId)>>> LoadAsync(IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var rows = await context.SentenceCorrections.AsNoTracking()
                                .Where(c => c.DeckId != null && deckIds.Contains(c.DeckId.Value) && c.Text != null)
                                .ToListAsync(ct);

        var latest = rows.GroupBy(c => c.ExampleSentenceId).Select(g => g.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.SentenceCorrectionId).First()).ToList();
        var ids = latest.Select(c => c.ExampleSentenceId).ToList();
        var positions = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                     .Where(s => ids.Contains(s.SentenceId) && !s.RemovedFromDeck)
                                     .ToDictionaryAsync(s => s.SentenceId, s => (s.DeckId, s.Position), ct);

        return latest.Where(c => positions.TryGetValue(c.ExampleSentenceId, out var p) && p.DeckId == c.DeckId)
                     .GroupBy(c => c.DeckId!.Value)
                     .ToDictionary(g => g.Key, g => g.Select(c =>
                         (new LineCorrection(c.ExampleSentenceId, c.SourceText, c.Text!, positions[c.ExampleSentenceId].Position), c.UserId)).ToList());
    }

    /// <summary>The newest text correction of these decks' lines (0 for none), to tell whether more came in during a parse.</summary>
    public async Task<long> NewestIdAsync(IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.SentenceCorrections.AsNoTracking()
                            .Where(c => c.DeckId != null && deckIds.Contains(c.DeckId.Value) && c.Text != null)
                            .MaxAsync(c => (long?)c.SentenceCorrectionId, ct) ?? 0;
    }

    /// <summary>Back on the to-do list: a flag for whoever submitted each fix that found no line, which also keeps the line.</summary>
    public async Task ReportUnmatchedAsync(int deckId, IEnumerable<LineCorrection> unmatched, IReadOnlyDictionary<long, string> userBySentence, CancellationToken ct = default)
    {
        foreach (var c in unmatched)
        {
            if (!userBySentence.TryGetValue(c.SentenceId, out var userId)) continue;
            await flags.OpenForCorrectionAsync(userId, c.SentenceId, c.Text, ct);
            logger.LogWarning("Reparse of deck {DeckId} found no line for the fix of sentence {SentenceId}; flagged for {UserId}", deckId, c.SentenceId, userId);
        }
    }

    /// <summary>Titles (a parent, or a deck without one) with a correction newer than the corrected episode's last parse, counting lines.</summary>
    public async Task<List<CorrectedTitleDto>> CorrectedTitlesAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var rows = await (from c in context.SentenceCorrections.AsNoTracking()
                          join d in context.Decks.AsNoTracking() on c.DeckId equals d.DeckId
                          where c.Text != null
                          select new { c.ExampleSentenceId, c.CreatedAt, d.LastUpdate, TitleId = d.ParentDeckId ?? d.DeckId })
                         .ToListAsync(ct);

        var counts = rows.Where(r => new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)) > r.LastUpdate)
                         .GroupBy(r => r.TitleId).ToDictionary(g => g.Key, g => g.Select(r => r.ExampleSentenceId).Distinct().Count());
        var ids = counts.Keys.ToList();
        var titles = await context.Decks.AsNoTracking().Where(d => ids.Contains(d.DeckId))
                                  .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle })
                                  .ToListAsync(ct);

        return titles.Select(t => new CorrectedTitleDto(t.DeckId, t.RomajiTitle ?? t.OriginalTitle, counts[t.DeckId]))
                     .OrderBy(t => t.Title).ToList();
    }
}
