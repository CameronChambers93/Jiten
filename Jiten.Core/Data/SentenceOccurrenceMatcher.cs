namespace Jiten.Core.Data;

/// <summary>Pairs a deck's stored occurrences with a new parse's, so a reparse updates rows in place and their ids stay valid.</summary>
public static class SentenceOccurrenceMatcher
{
    public readonly record struct Stored(long SentenceId, long CentralSentenceId, int Position);

    /// <param name="PinnedId">A stored row this occurrence keeps whatever its text (a line with a submitted fix); 0 for none.</param>
    public readonly record struct Incoming(long CentralSentenceId, int Position, long PinnedId = 0);

    /// <summary>
    /// The stored id each incoming occurrence takes, or null for a new row. A pinned occurrence takes its row when the deck
    /// holds it; the rest pair within one text, the nearest positions first.
    /// </summary>
    public static long?[] Match(IReadOnlyList<Stored> stored, IReadOnlyList<Incoming> incoming)
    {
        var matches = new long?[incoming.Count];
        var storedIds = stored.Select(s => s.SentenceId).ToHashSet();
        var taken = new HashSet<long>();

        for (var i = 0; i < incoming.Count; i++)
        {
            var pinned = incoming[i].PinnedId;
            if (pinned != 0 && storedIds.Contains(pinned) && taken.Add(pinned))
                matches[i] = pinned;
        }

        var storedByText = stored.ToLookup(s => s.CentralSentenceId);
        foreach (var group in Enumerable.Range(0, incoming.Count).Where(i => matches[i] == null).GroupBy(i => incoming[i].CentralSentenceId))
        {
            var candidates = group.SelectMany(i => storedByText[group.Key], (i, s) => (Index: i, Stored: s))
                                  .OrderBy(p => Math.Abs((long)p.Stored.Position - incoming[p.Index].Position))
                                  .ThenBy(p => p.Stored.Position)
                                  .ThenBy(p => incoming[p.Index].Position);

            foreach (var (index, candidate) in candidates)
            {
                if (matches[index] != null || !taken.Add(candidate.SentenceId)) continue;
                matches[index] = candidate.SentenceId;
            }
        }

        return matches;
    }
}
