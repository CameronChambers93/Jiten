using Hangfire;
using Jiten.Api.Services;
using Jiten.Api.Services.Translation;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Jobs;

/// <summary>Progress of a title's "translate all sentences" run, kept in memory for the admin page to poll.</summary>
public record TranslationJobProgress(int Total, int Done, int Failed, string? LastError, bool Running, DateTime StartedAt);

/// <summary>Translates every example sentence of a title that has no subtitle English yet, with the active AI provider.</summary>
public class SentenceTranslationJob(
    JitenDbContext context,
    SentenceTranslationService translations,
    SentenceCueLookup cueLookup,
    IMemoryCache cache,
    ILogger<SentenceTranslationJob> logger)
{
    /// <summary>Stop after this many failures in a row: the key, quota or provider is likely broken, not the lines.</summary>
    private const int MaxConsecutiveFailures = 5;

    public static string ProgressKey(int parentDeckId) => $"translation-job:{parentDeckId}";

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task TranslateTitle(int parentDeckId, CancellationToken ct)
    {
        var deckIds = await context.Decks.AsNoTracking().Where(d => d.ParentDeckId == parentDeckId || d.DeckId == parentDeckId)
                                   .Select(d => d.DeckId).ToListAsync(ct);
        var todo = new List<long>();
        foreach (var deckId in deckIds)
            todo.AddRange((await translations.CoverageAsync(deckId, ct)).NeedAi);

        var progress = new TranslationJobProgress(todo.Count, 0, 0, null, true, DateTime.UtcNow);
        cache.Set(ProgressKey(parentDeckId), progress, TimeSpan.FromDays(1));

        var consecutiveFailures = 0;
        foreach (var sentenceId in todo)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await translations.TranslateWithAiAsync(sentenceId, await cueLookup.FindAsync(sentenceId, ct), ct);
                progress = progress with { Done = progress.Done + 1 };
                consecutiveFailures = 0;
            }
            catch (Exception e) when (e is TranslationProviderException or HttpRequestException)
            {
                logger.LogWarning(e, "Translating sentence {SentenceId} failed", sentenceId);
                progress = progress with { Failed = progress.Failed + 1, LastError = e.Message };
                if (++consecutiveFailures >= MaxConsecutiveFailures) break;
            }

            cache.Set(ProgressKey(parentDeckId), progress, TimeSpan.FromDays(1));
        }

        cache.Set(ProgressKey(parentDeckId), progress with { Running = false }, TimeSpan.FromDays(1));
        logger.LogInformation("Translated {Done} of {Total} sentences of deck {DeckId} ({Failed} failed)", progress.Done, progress.Total, parentDeckId,
                              progress.Failed);
    }
}
