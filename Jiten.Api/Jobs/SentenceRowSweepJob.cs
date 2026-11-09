using Hangfire;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>
/// Deletes the sentence rows nothing uses any more: lines a reparse removed from their deck, once nobody has them mined,
/// flagged or submitted, and Sentences rows no line points at, with their cached AI English, unless a user's own fix
/// has their text. Holds <see cref="JitenHelper.SentenceRowsLockKey"/> exclusively, so no deck save, or code that has
/// resolved a row but not yet saved what points at it, is in progress.
/// </summary>
public class SentenceRowSweepJob(IDbContextFactory<JitenDbContext> contextFactory, ILogger<SentenceRowSweepJob> logger)
{
    private const int LockAttempts = 60;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(5);

    [Queue("default")]
    public async Task Sweep()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = 0");

        // Tried rather than waited for: a waiting exclusive request would queue every new Submit, Sync and deck save behind it
        for (var attempt = 1; ; attempt++)
        {
            if (await context.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({JitenHelper.SentenceRowsLockKey}) AS \"Value\"").SingleAsync())
                break;
            if (attempt == LockAttempts)
            {
                logger.LogWarning("Sentence row sweep: rows were in use for {Minutes} minutes; skipped until the next run",
                                  (int)(LockAttempts * LockRetryDelay).TotalMinutes);
                return;
            }

            await Task.Delay(LockRetryDelay);
        }

        // The rule the deck save uses for a dropped line
        var lines = await context.Database.ExecuteSqlRawAsync(@"
            DELETE FROM jiten.""ExampleSentences"" es
            WHERE es.""RemovedFromDeck""
              AND NOT EXISTS (SELECT 1 FROM ""user"".""MinedSentences"" m WHERE m.""ExampleSentenceId"" = es.""SentenceId"")
              AND NOT EXISTS (SELECT 1 FROM ""user"".""SentenceFlags"" f WHERE f.""ExampleSentenceId"" = es.""SentenceId"" AND f.""ResolvedAt"" IS NULL)
              AND NOT EXISTS (SELECT 1 FROM jiten.""SentenceSubmissions"" s WHERE s.""ExampleSentenceId"" = es.""SentenceId"" AND s.""Status"" = 'pending')");

        // A user's own fix of a line that's gone can't be seen; its text would otherwise keep its row
        var fixes = await context.Database.ExecuteSqlRawAsync(@"
            DELETE FROM ""user"".""SentenceFixes"" f
            WHERE NOT EXISTS (SELECT 1 FROM jiten.""ExampleSentences"" es WHERE es.""SentenceId"" = f.""ExampleSentenceId"")");

        // A user's own fixed text keeps its row, which holds the AI English of their text
        var rows = await context.Database.ExecuteSqlRawAsync(@"
            WITH fixed AS (
                SELECT DISTINCT sha256(convert_to(jiten.normalize_sentence(f.""Text""), 'UTF8')) AS hash
                FROM ""user"".""SentenceFixes"" f WHERE f.""Text"" IS NOT NULL)
            DELETE FROM jiten.""Sentences"" c
            WHERE NOT EXISTS (SELECT 1 FROM jiten.""ExampleSentences"" e WHERE e.""CentralSentenceId"" = c.""Id"")
              AND NOT EXISTS (SELECT 1 FROM fixed WHERE fixed.hash = c.""TextHash"")");

        await transaction.CommitAsync();
        logger.LogInformation("Sentence row sweep: deleted {Lines} removed lines, {Fixes} fixes of lines that are gone and {Rows} unused sentence rows",
                              lines, fixes, rows);
    }
}
