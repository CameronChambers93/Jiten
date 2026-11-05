using Jiten.Core.Data;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core;

/// <summary>
/// After a title is ingested again as whole-cue lines, moves mined sentences and open flags off the lines the re-ingest
/// removed, onto the new line of the same episode that holds their text. Mined sentences moved are marked for a rebuild.
/// Only lines of episodes with subtitle cues are looked at, and a text found on more than one line isn't guessed: it's
/// left in place and reported.
/// </summary>
public static class MinedLineMover
{
    public record Unplaced(string Kind, long Id, long OldLineId, string Text);

    /// <param name="Ambiguous">Texts found on more than one new line; they're among <paramref name="Unplaced"/>.</param>
    public record Report(int SentencesMoved, int FlagsMoved, int Ambiguous, List<Unplaced> Unplaced);

    public static async Task<Report> MoveAsync(JitenDbContext jiten, UserDbContext user, bool dryRun, CancellationToken ct = default)
    {
        // Only lines something points at: the mined sentences' and open flags' lines, of those the re-ingest removed
        var pointedAt = (await user.MinedSentences.AsNoTracking().Where(s => s.ExampleSentenceId != null).Select(s => s.ExampleSentenceId!.Value).ToListAsync(ct))
                        .Concat(await user.SentenceFlags.AsNoTracking().Where(f => f.ResolvedAt == null).Select(f => f.ExampleSentenceId).ToListAsync(ct))
                        .Distinct().ToList();
        var removed = await jiten.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                 .Where(s => pointedAt.Contains(s.SentenceId) && s.RemovedFromDeck && s.DeckId != null
                                             && jiten.DeckSubtitleTracks.Any(t => t.DeckId == s.DeckId))
                                 .Select(s => new { s.SentenceId, DeckId = s.DeckId!.Value, s.Text, s.Position })
                                 .ToDictionaryAsync(s => s.SentenceId, ct);
        var removedIds = removed.Keys.ToList();

        var sentences = await user.MinedSentences.Where(s => s.ExampleSentenceId != null && removedIds.Contains(s.ExampleSentenceId.Value)).ToListAsync(ct);
        var flags = await user.SentenceFlags.Where(f => f.ResolvedAt == null && removedIds.Contains(f.ExampleSentenceId)).ToListAsync(ct);

        var deckIds = sentences.Select(s => removed[s.ExampleSentenceId!.Value].DeckId)
                               .Concat(flags.Select(f => removed[f.ExampleSentenceId].DeckId)).Distinct().ToList();
        var lines = (await jiten.ExampleSentences.AsNoTracking()
                                .Where(s => deckIds.Contains(s.DeckId!.Value))
                                .Select(s => new { s.SentenceId, DeckId = s.DeckId!.Value, s.Text, s.Position })
                                .ToListAsync(ct))
                    .GroupBy(s => s.DeckId)
                    .ToDictionary(g => g.Key, g => g.Select(s => (s.SentenceId, s.Text, s.Position, Normalised: SentenceCueMatcher.Normalise(s.Text))).ToList());

        var unplaced = new List<Unplaced>();
        var ambiguous = 0;

        (long Id, string Text)? Find(long oldId)
        {
            var old = removed[oldId];
            var needle = SentenceCueMatcher.Normalise(old.Text);
            if (needle.Length == 0 || !lines.TryGetValue(old.DeckId, out var deckLines)) return null;
            var hits = deckLines.Where(l => l.Normalised.Contains(needle, StringComparison.Ordinal)).ToList();
            if (hits.Count == 0) return null;
            // Old and new positions count different units, so they can't pick between repeats; such a line is reported
            if (hits.Count > 1)
            {
                ambiguous++;
                return null;
            }

            return (hits[0].SentenceId, hits[0].Text);
        }

        var movedSentences = 0;
        foreach (var sentence in sentences)
        {
            var oldId = sentence.ExampleSentenceId!.Value;
            var found = Find(oldId);
            if (found == null)
            {
                unplaced.Add(new Unplaced("mined sentence", sentence.MinedSentenceId, oldId, removed[oldId].Text));
                continue;
            }

            var (newId, newText) = found.Value;

            // The word keeps its place inside the old text, wherever that text now sits in the line
            var oldText = removed[oldId].Text.TrimEnd('。', '！', '？', '!', '?', '…');
            var at = oldText.Length > 0 ? newText.IndexOf(oldText, StringComparison.Ordinal) : -1;
            if (sentence.TargetPosition is { } position)
                sentence.TargetPosition = at >= 0 ? position + at : null;
            if (sentence.TargetPosition == null) sentence.TargetLength = null;

            sentence.ExampleSentenceId = newId;
            sentence.NeedsRebuild = true;
            movedSentences++;
        }

        // A user has one open flag per line, so a second flag landing on the same line stays where it is
        var openFlags = (await user.SentenceFlags.AsNoTracking().Where(f => f.ResolvedAt == null && !removedIds.Contains(f.ExampleSentenceId))
                                   .Select(f => new { f.UserId, f.ExampleSentenceId }).ToListAsync(ct))
                        .Select(f => (f.UserId, f.ExampleSentenceId)).ToHashSet();
        var movedFlags = 0;
        foreach (var flag in flags)
        {
            var found = Find(flag.ExampleSentenceId);
            if (found == null || !openFlags.Add((flag.UserId, found.Value.Id)))
            {
                unplaced.Add(new Unplaced("flag", flag.SentenceFlagId, flag.ExampleSentenceId, removed[flag.ExampleSentenceId].Text));
                continue;
            }

            flag.ExampleSentenceId = found.Value.Id;
            movedFlags++;
        }

        if (!dryRun) await user.SaveChangesAsync(ct);
        return new Report(movedSentences, movedFlags, ambiguous, unplaced);
    }
}
