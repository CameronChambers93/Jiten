using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core;

/// <summary>Gives every mined sentence an occurrence and lists the ones the occurrence can't reproduce, which will keep their text as an override.</summary>
public static class MinedLineMigrator
{
    public static async Task Run(IDbContextFactory<JitenDbContext> jitenFactory, UserDbContext user, bool dryRun)
    {
        await using var jiten = await jitenFactory.CreateDbContextAsync();

        var sentences = await user.MinedSentences.OrderBy(s => s.MinedSentenceId).ToListAsync();
        var linked = sentences.Where(s => s.ExampleSentenceId != null).Select(s => s.ExampleSentenceId!.Value).ToList();
        var texts = await jiten.ExampleSentences.IgnoreQueryFilters().Where(s => linked.Contains(s.SentenceId))
                               .ToDictionaryAsync(s => s.SentenceId, s => s.Text);

        int created = 0, reproduced = 0, overrides = 0, unmarked = 0, speakers = 0;
        foreach (var sentence in sentences)
        {
            var line = MinedLine.From(sentence.Text, MinedLineStore.PlainText);
            if (line.Speaker != null)
            {
                speakers++;
                Console.WriteLine($"speaker  {sentence.MinedSentenceId}: {line.Speaker} | {line.Text}");
            }

            if (sentence.ExampleSentenceId is { } id && texts.TryGetValue(id, out var deckText) && deckText != line.Speaker + line.Text)
                Console.WriteLine($"deck     {sentence.MinedSentenceId}: occurrence has {deckText}");

            if (line.TargetPosition == null) unmarked++;
            if (line.ToHtml() == sentence.Text) reproduced++;
            else
            {
                overrides++;
                Console.WriteLine($"override {sentence.MinedSentenceId}: {sentence.Text}\n      -> {line.ToHtml()}");
            }

            if (dryRun) continue;

            sentence.TargetPosition = line.TargetPosition;
            sentence.TargetLength = line.TargetLength;
            if (sentence.ExampleSentenceId == null)
            {
                sentence.ExampleSentenceId = await MinedLineStore.EnsureOccurrenceAsync(jiten, sentence.UserId, line, sentence, default);
                created++;
            }
        }

        if (!dryRun) await user.SaveChangesAsync();
        Console.WriteLine($"{sentences.Count} sentences: {reproduced} reproduced exactly, {overrides} keep an override, {unmarked} with no marked word, " +
                          $"{speakers} with a speaker, {created} occurrences linked{(dryRun ? " (dry run, nothing written)" : "")}.");
    }
}
