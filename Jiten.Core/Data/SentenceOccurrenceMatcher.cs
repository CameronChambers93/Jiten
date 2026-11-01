namespace Jiten.Core.Data;

/// <summary>Pairs a deck's stored occurrences with a new parse's, so a reparse updates rows in place and their ids stay valid.</summary>
public static class SentenceOccurrenceMatcher
{
    public readonly record struct Stored(long SentenceId, long CentralSentenceId, int Position);

    public readonly record struct Incoming(long CentralSentenceId, int Position);

    /// <summary>The stored id each incoming occurrence takes, or null for a new row. Within one text, the nearest positions pair first.</summary>
    public static long?[] Match(IReadOnlyList<Stored> stored, IReadOnlyList<Incoming> incoming)
    {
        var matches = new long?[incoming.Count];
        var storedByText = stored.ToLookup(s => s.CentralSentenceId);

        foreach (var group in Enumerable.Range(0, incoming.Count).GroupBy(i => incoming[i].CentralSentenceId))
        {
            var candidates = group.SelectMany(i => storedByText[group.Key], (i, s) => (Index: i, Stored: s))
                                  .OrderBy(p => Math.Abs((long)p.Stored.Position - incoming[p.Index].Position))
                                  .ThenBy(p => p.Stored.Position)
                                  .ThenBy(p => incoming[p.Index].Position);

            var taken = new HashSet<long>();
            foreach (var (index, candidate) in candidates)
            {
                if (matches[index] != null || !taken.Add(candidate.SentenceId)) continue;
                matches[index] = candidate.SentenceId;
            }
        }

        return matches;
    }
}
