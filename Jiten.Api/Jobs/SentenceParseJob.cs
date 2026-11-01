using Hangfire;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>
/// Parses the texts of private lines, which arrive from Sync without tokens. The tokens go on the central row, so a private
/// occurrence keeps its empty WordKeys and stays out of word lookups. Marks the target word on mined sentences whose note didn't.
/// </summary>
public class SentenceParseJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    UserDbContext userContext,
    ILogger<SentenceParseJob> logger)
{
    private const int BatchSize = 100;

    [Queue("parse")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 1)]
    public async Task ParsePending(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        int parsed = 0, marked = 0;

        while (true)
        {
            var batch = await context.Sentences.IgnoreQueryFilters()
                                     .Where(s => s.Tokens == null && context.ExampleSentences.Any(e => e.CentralSentenceId == s.Id && e.OwnerId != null))
                                     .OrderBy(s => s.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;

            var paragraphs = await ParagraphParser.ParseAsync(contextFactory, batch.Select(s => s.Text).ToArray());
            var tokensOf = new Dictionary<long, IReadOnlyList<SentenceToken>>();
            for (var i = 0; i < batch.Count; i++)
            {
                var tokens = Locate(batch[i].Text, paragraphs[i]);
                batch[i].Tokens = ExampleSentenceTokens.Encode(tokens);
                batch[i].WordKeys = ExampleSentenceTokens.WordKeys(tokens, Random.Shared.Next(ExampleSentenceTokens.FineBucketCount));
                tokensOf[batch[i].Id] = tokens;
            }

            marked += await MarkTargetsAsync(context, batch, tokensOf, ct);
            await context.SaveChangesAsync(ct);
            parsed += batch.Count;
        }

        if (parsed > 0) logger.LogInformation("Parsed {Parsed} private lines and marked the word on {Marked} mined sentences", parsed, marked);
    }

    /// <summary>The parsed words with their places in the text; a word that can't be placed or encoded is left out.</summary>
    private static List<SentenceToken> Locate(string text, IEnumerable<DeckWord> words)
    {
        var tokens = new List<SentenceToken>();
        var from = 0;
        foreach (var word in words)
        {
            var (position, length) = TokenPositionHelper.FindTokenInSource(text, word.OriginalText, from);
            if (position < 0) continue;
            from = position + length;
            if (!ExampleSentenceTokens.CanEncode(word.WordId, position, length)) continue;

            tokens.Add(new SentenceToken(word.WordId, word.ReadingIndex, (byte)position, (byte)length, IsTarget: false,
                                         IsFunctionWord: word.PartsOfSpeech.Any(p => p is PartOfSpeech.Particle or PartOfSpeech.Auxiliary)));
        }

        return tokens;
    }

    private async Task<int> MarkTargetsAsync(JitenDbContext context, List<Sentence> batch, Dictionary<long, IReadOnlyList<SentenceToken>> tokensOf,
                                             CancellationToken ct)
    {
        var centralIds = batch.Select(s => s.Id).ToList();
        var occurrences = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                       .Where(e => e.CentralSentenceId != null && centralIds.Contains(e.CentralSentenceId.Value) && e.OwnerId != null)
                                       .Select(e => new { e.SentenceId, e.Text, CentralSentenceId = e.CentralSentenceId!.Value })
                                       .ToDictionaryAsync(e => e.SentenceId, ct);
        var occurrenceIds = occurrences.Keys.ToList();

        var rows = await userContext.MinedSentences
                                    .Where(s => s.TargetPosition == null && s.Text == null && s.ExampleSentenceId != null
                                                && occurrenceIds.Contains(s.ExampleSentenceId.Value))
                                    .Join(userContext.MinedWords, s => s.MinedWordId, w => w.MinedWordId, (s, w) => new { Sentence = s, w.WordId, w.Expression })
                                    .ToListAsync(ct);

        var marked = 0;
        foreach (var row in rows)
        {
            var occurrence = occurrences[row.Sentence.ExampleSentenceId!.Value];
            if (MinedTarget.Find(occurrence.Text, tokensOf[occurrence.CentralSentenceId], row.WordId, row.Expression) is not var (position, length)) continue;
            (row.Sentence.TargetPosition, row.Sentence.TargetLength) = (position, length);
            marked++;
        }

        await userContext.SaveChangesAsync(ct);
        return marked;
    }
}
