using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.SpeechBoundaries;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class ReparseJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IBackgroundJobClient backgroundJobs,
    Jiten.Api.Services.IPendingEmbeddingQueue pendingEmbeddingQueue,
    Jiten.Api.Services.IIndexNowService indexNow,
    Jiten.Api.Services.Mining.ParseCorrectionService corrections,
    ILogger<ReparseJob> logger)
{
    // Parses after the first, when fixes were submitted to the title while it parsed
    private const int MaxExtraParses = 2;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    // Single-deck reparse: notifies IndexNow. Kept as a 1-arg overload so in-flight Hangfire jobs
    // serialized before this change still resolve.
    [Queue("reparse")]
    public Task Reparse(int deckId) => Reparse(deckId, notifyIndexNow: true);

    // Bulk reparse endpoints pass notifyIndexNow: false — never mass-submit the catalogue to IndexNow.
    [Queue("reparse")]
    public async Task Reparse(int deckId, bool notifyIndexNow)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var deck = await context.Decks.AsNoTracking()
                                .Include(d => d.RawText).Include(d => d.ExampleSentences)
                                .Include(d => d.Children).ThenInclude(deck => deck.RawText)
                                .Include(deck => deck.ExampleSentences)
                                .Include(d => d.DictionaryEntries)
                                .FirstOrDefaultAsync(d => d.DeckId == deckId);
        if (deck == null)
            throw new Exception($"Deck with ID {deckId} not found.");

        if (deck.RawText == null && deck.Children.Count == 0)
            throw new Exception($"Deck with ID {deckId} has no raw text to reparse.");

        // Load dictionary entries: own entries, or parent's if this is a child
        var dictEntries = deck.DictionaryEntries.Count > 0
            ? deck.DictionaryEntries
            : deck.ParentDeckId != null
                ? await context.Set<DeckDictionaryEntry>().Where(e => e.DeckId == deck.ParentDeckId).ToListAsync()
                : null;

        var children = deck.Children.ToList();

        // Submitted text fixes go back into their lines before parsing, so the fixed words count and each fixed line keeps its row.
        // A fix submitted while the title parses would be lost when the save puts back the parsed text, so then it parses again;
        // while it saves, Submit is refused (ReparseSaves).
        List<int> episodeIds = children.Count == 0 ? [deck.DeckId] : children.Select(c => c.DeckId).ToList();
        var newest = await corrections.NewestIdAsync(episodeIds);
        Dictionary<int, List<(LineCorrection Correction, string UserId)>> corrected;
        var unmatched = new List<LineCorrection>();
        for (var parse = 0; ; parse++)
        {
            corrected = await corrections.LoadAsync(episodeIds);
            unmatched.Clear();
            await ParseAsync();

            ReparseSaves.Enter(episodeIds);
            long now;
            try
            {
                now = await corrections.NewestIdAsync(episodeIds);
            }
            catch
            {
                ReparseSaves.Exit(episodeIds);
                throw;
            }

            if (now == newest) break;
            ReparseSaves.Exit(episodeIds);
            if (parse == MaxExtraParses)
            {
                // Saving now would replace the newest fixed lines with new rows of their old text, so it waits for the fixes to settle
                logger.LogWarning("Deck {DeckId} got more sentence fixes during each of {Count} parses; not saved, trying again in {Delay}",
                                  deckId, parse + 1, RetryDelay);
                backgroundJobs.Schedule<ReparseJob>(job => job.Reparse(deckId, notifyIndexNow), RetryDelay);
                return;
            }

            logger.LogInformation("Deck {DeckId} got sentence fixes while it parsed; parsing again", deckId);
            newest = now;
        }

        try
        {
            var userBySentence = corrected.Values.SelectMany(l => l).ToDictionary(c => c.Correction.SentenceId, c => c.UserId);
            if (children.Count > 0)
            {
                deck.Children = children;
                await deck.AddChildDeckWords(context);
            }

            deck.LastUpdate = DateTime.UtcNow;

            // Before saving: the flag keeps a line whose fix found no place, rather than the save deleting it
            await corrections.ReportUnmatchedAsync(deck.DeckId, unmatched, userBySentence);

            await JitenHelper.InsertDeck(contextFactory, deck, [], true);
        }
        finally
        {
            ReparseSaves.Exit(episodeIds);
        }

        QueueStatsComputationForDeckTree(deck);
        backgroundJobs.Enqueue<DifficultyComputationJob>(job => job.ComputeDeckDifficulty(deck.DeckId, false));

        // Re-embed the affected parent deck for vocabulary similarity (periodic sweep batches these).
        await pendingEmbeddingQueue.AddAsync(deck.ParentDeckId ?? deck.DeckId);

        // Notify IndexNow of the top-level deck page (skipped for bulk reparses to respect Bing's quota).
        if (notifyIndexNow)
            await indexNow.SubmitDeckAsync(deck.ParentDeckId ?? deck.DeckId);
        return;

        List<LineCorrection>? CorrectionsFor(int episodeId) => corrected.GetValueOrDefault(episodeId)?.Select(c => c.Correction).ToList();

        async Task ParseAsync()
        {
            if (deck.Children.Count == 0)
            {
                Deck newDeck = await Parser.Parser.ParseTextToDeck(contextFactory, deck.RawText!.RawText, true, true, deck.MediaType,
                    dictionaryEntries: dictEntries, speechBoundaries: deck.RawText.SpeechBoundaries,
                    lineCorrections: CorrectionsFor(deck.DeckId), unmatchedCorrections: unmatched);
                deck.CharacterCount = newDeck.CharacterCount;
                deck.WordCount = newDeck.WordCount;
                deck.UniqueWordCount = newDeck.UniqueWordCount;
                deck.UniqueWordUsedOnceCount = newDeck.UniqueWordUsedOnceCount;
                deck.UniqueKanjiCount = newDeck.UniqueKanjiCount;
                deck.UniqueKanjiUsedOnceCount = newDeck.UniqueKanjiUsedOnceCount;
                deck.SentenceCount = newDeck.SentenceCount;
                deck.DeckWords = newDeck.DeckWords;
                deck.ExampleSentences = newDeck.ExampleSentences;
                deck.SentenceProfile = newDeck.SentenceProfile;
                deck.DialoguePercentage = newDeck.DialoguePercentage;
                deck.RawText.SpeechBoundaries = newDeck.RawText?.SpeechBoundaries ?? deck.RawText.SpeechBoundaries;

                if (deck.MediaType is MediaType.Manga or MediaType.Audio)
                    deck.SentenceCount = 0;
            }
            else
            {
                // Validate all children have raw text
                foreach (var child in children)
                {
                    if (child.RawText == null)
                        throw new Exception($"Child deck with ID {child.DeckId} has no raw text to reparse.");
                }


                var texts = children.Select(c => c.RawText!.RawText).ToList();
                var newDecks = await Parser.Parser.ParseTextsToDeck(
                    contextFactory,
                    texts,
                    storeRawText: true,
                    predictDifficulty: true,
                    deck.MediaType,
                    dictionaryEntries: dictEntries,
                    speechBoundaries: children.Select(c => c.RawText!.SpeechBoundaries).ToList(),
                    lineCorrections: children.Select(c => (IReadOnlyList<LineCorrection>?)CorrectionsFor(c.DeckId)).ToList(),
                    unmatchedCorrections: unmatched);

                // Copy properties back to original deck objects
                for (int i = 0; i < children.Count; i++)
                {
                    var original = children[i];
                    var parsed = newDecks[i];

                    original.CharacterCount = parsed.CharacterCount;
                    original.WordCount = parsed.WordCount;
                    original.UniqueWordCount = parsed.UniqueWordCount;
                    original.UniqueWordUsedOnceCount = parsed.UniqueWordUsedOnceCount;
                    original.UniqueKanjiCount = parsed.UniqueKanjiCount;
                    original.UniqueKanjiUsedOnceCount = parsed.UniqueKanjiUsedOnceCount;
                    original.SentenceCount = parsed.SentenceCount;
                    original.DeckWords = parsed.DeckWords;
                    original.ExampleSentences = parsed.ExampleSentences;
                    original.SentenceProfile = parsed.SentenceProfile;
                    original.DialoguePercentage = parsed.DialoguePercentage;
                    original.RawText!.SpeechBoundaries = parsed.RawText?.SpeechBoundaries ?? original.RawText.SpeechBoundaries;

                    if (original.MediaType is MediaType.Manga or MediaType.Audio)
                        original.SentenceCount = 0;
                }
            }
        }
    }

    private void QueueStatsComputationForDeckTree(Deck deck)
    {
        backgroundJobs.Enqueue<StatsComputationJob>(job => job.ComputeDeckCoverageStats(deck.DeckId));
        foreach (var child in deck.Children)
            QueueStatsComputationForDeckTree(child);
    }
}

/// <summary>
/// Episodes whose reparse is saving. A fix submitted to one of their lines then would be overwritten by the save, so
/// <see cref="Services.SentenceFixService.ApplyToSharedAsync"/> refuses it. In memory: reparses run in the API's own Hangfire server.
/// </summary>
public static class ReparseSaves
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Saving = new();

    public static void Enter(IEnumerable<int> deckIds)
    {
        foreach (var id in deckIds) Saving.AddOrUpdate(id, 1, (_, n) => n + 1);
    }

    public static void Exit(IEnumerable<int> deckIds)
    {
        foreach (var id in deckIds)
            if (Saving.AddOrUpdate(id, 0, (_, n) => n - 1) <= 0) Saving.TryRemove(new KeyValuePair<int, int>(id, 0));
    }

    public static bool IsSaving(int deckId) => Saving.ContainsKey(deckId);
}
