using Hangfire;
using Jiten.Api.Services;
using Jiten.Api.Services.Translation;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Jobs;

/// <summary>Progress of a title's "translate all sentences" run, kept in memory for the admin page to poll.</summary>
/// <param name="Queued">Waiting behind other titles of a bulk run.</param>
/// <param name="QuotaReached">The provider's quota ran out: the run stopped and can be started again later.</param>
public record TranslationJobProgress(int Total, int Done, int Failed, string? LastError, bool Running, DateTime StartedAt,
                                     bool Queued = false, bool QuotaReached = false);

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

    private static readonly TimeSpan ProgressLifetime = TimeSpan.FromDays(1);

    public static string ProgressKey(int parentDeckId) => $"translation-job:{parentDeckId}";

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task TranslateTitle(int parentDeckId, CancellationToken ct)
    {
        try
        {
            await RunTitleAsync(parentDeckId, ct);
        }
        finally
        {
            if (cache.Get<TranslationJobProgress>(ProgressKey(parentDeckId)) is { Running: true } p)
                cache.Set(ProgressKey(parentDeckId), p with { Running = false, LastError = p.LastError ?? "The run stopped unexpectedly." }, ProgressLifetime);
        }
    }

    /// <summary>Translates several titles one after another; a used-up quota stops the rest, which keep a "quota reached" state.</summary>
    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task TranslateTitles(List<int> parentDeckIds, CancellationToken ct)
    {
        var i = 0;
        try
        {
            for (; i < parentDeckIds.Count; i++)
                if (!await RunTitleAsync(parentDeckIds[i], ct))
                {
                    foreach (var rest in parentDeckIds.Skip(i + 1))
                        cache.Set(ProgressKey(rest), new TranslationJobProgress(0, 0, 0, NotStartedQuota, false, DateTime.UtcNow, QuotaReached: true),
                                  ProgressLifetime);
                    return;
                }
        }
        finally
        {
            // A crash or cancel must not leave titles showing "Waiting" or "Translating" for a day
            foreach (var id in parentDeckIds.Skip(i))
                if (cache.Get<TranslationJobProgress>(ProgressKey(id)) is { Running: true } p)
                    cache.Set(ProgressKey(id), p with { Running = false, Queued = false, LastError = p.LastError ?? "The run stopped unexpectedly." },
                              ProgressLifetime);
        }
    }

    public const string NotStartedQuota = "Not started: the provider's quota is used up.";

    /// <summary>Whether a bulk run is going: one runs at a time, since the job holds a lock for its whole run.</summary>
    public static bool AnyRunning(IMemoryCache cache, IEnumerable<int> titleIds) =>
        titleIds.Any(id => cache.Get<TranslationJobProgress>(ProgressKey(id)) is { Running: true });

    /// <summary>Marks a title as waiting in a bulk run, so the admin page shows it before its turn comes.</summary>
    public static void MarkQueued(IMemoryCache cache, int parentDeckId) =>
        cache.Set(ProgressKey(parentDeckId), new TranslationJobProgress(0, 0, 0, null, true, DateTime.UtcNow, Queued: true), ProgressLifetime);

    /// <returns>False when the provider's quota ran out.</returns>
    private async Task<bool> RunTitleAsync(int parentDeckId, CancellationToken ct)
    {
        var deckIds = await context.Decks.AsNoTracking().Where(d => d.ParentDeckId == parentDeckId || d.DeckId == parentDeckId)
                                   .Select(d => d.DeckId).ToListAsync(ct);
        var needAi = new List<long>();
        foreach (var deckId in deckIds)
            needAi.AddRange((await translations.CoverageAsync(deckId, ct)).NeedAi);
        var todo = await DistinctTextsAsync(context, needAi, ct);

        var progress = new TranslationJobProgress(todo.Count, 0, 0, null, true, DateTime.UtcNow);
        cache.Set(ProgressKey(parentDeckId), progress, ProgressLifetime);

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
            catch (TranslationQuotaException e)
            {
                logger.LogWarning("Translating deck {DeckId} stopped: {Message}", parentDeckId, e.Message);
                progress = progress with { LastError = e.Message, QuotaReached = true };
                break;
            }
            catch (Exception e) when (e is TranslationProviderException or HttpRequestException or DbUpdateException or KeyNotFoundException)
            {
                logger.LogWarning(e, "Translating sentence {SentenceId} failed", sentenceId);
                progress = progress with { Failed = progress.Failed + 1, LastError = e.Message };
                if (++consecutiveFailures >= MaxConsecutiveFailures) break;
            }

            cache.Set(ProgressKey(parentDeckId), progress, ProgressLifetime);
        }

        cache.Set(ProgressKey(parentDeckId), progress with { Running = false }, ProgressLifetime);
        logger.LogInformation("Translated {Done} of {Total} sentences of deck {DeckId} ({Failed} failed)", progress.Done, progress.Total, parentDeckId,
                              progress.Failed);
        return !progress.QuotaReached;
    }

    /// <summary>
    /// One line per distinct text: translations are cached per text, so the other lines with it are covered by the first.
    /// Paying the provider per line would spend a character quota many times over on repeated lines.
    /// </summary>
    public static async Task<List<long>> DistinctTextsAsync(JitenDbContext context, List<long> sentenceIds, CancellationToken ct)
    {
        var rows = await context.ExampleSentences.AsNoTracking().Where(s => sentenceIds.Contains(s.SentenceId))
                                .Select(s => new { s.SentenceId, s.CentralSentenceId }).ToListAsync(ct);
        var central = rows.ToDictionary(r => r.SentenceId, r => r.CentralSentenceId);
        var seen = new HashSet<long>();
        return sentenceIds.Where(id => central.GetValueOrDefault(id) is not { } c || seen.Add(c)).ToList();
    }
}
