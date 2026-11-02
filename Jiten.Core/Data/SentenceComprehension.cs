namespace Jiten.Core.Data;

/// <summary>How far along a word is, for mining. Settled words (mastered, redundant, blacklisted) are never targets.</summary>
public enum MiningStatus
{
    New,
    Learning,
    Young,
    Mature,
    Settled
}

/// <summary>Which sentences count for mining: the target's status, and how well every other content word must be known.</summary>
public sealed record MiningRule(IReadOnlySet<MiningStatus> Targets, bool OthersMature)
{
    public static readonly MiningRule Default = new(new HashSet<MiningStatus> { MiningStatus.New, MiningStatus.Learning }, false);

    public string CacheKey => $"{string.Join(',', Targets.Order())}:{OthersMature}";
}

public static class SentenceComprehension
{
    /// <summary>Learning cards (Due with no review) and plain New do not count; a blacklisted word is one the user chose not to learn.</summary>
    public static bool IsKnown(IReadOnlyCollection<KnownState> states) =>
        states.Any(s => s is KnownState.Young or KnownState.Mature or KnownState.Mastered
                             or KnownState.Redundant or KnownState.Blacklisted);

    public static MiningStatus MiningStatusOf(IReadOnlyCollection<KnownState> states)
    {
        if (states.Any(s => s is KnownState.Mastered or KnownState.Redundant or KnownState.Blacklisted)) return MiningStatus.Settled;
        if (states.Contains(KnownState.Mature)) return MiningStatus.Mature;
        if (states.Contains(KnownState.Young)) return MiningStatus.Young;
        return states.Any(s => s is KnownState.Due or KnownState.Suspended) ? MiningStatus.Learning : MiningStatus.New;
    }

    /// <summary>
    /// The words a sentence can be mined for under the rule, each at its first occurrence: a word of a wanted status whose
    /// fellow content words are all known well enough. A sentence with no weak word gives one target per wanted word.
    /// </summary>
    public static List<(SentenceToken Token, MiningStatus Status)> MiningTargets(IEnumerable<SentenceToken> tokens, Func<int, MiningStatus> statusOf, MiningRule rule)
    {
        var words = tokens.Where(t => !t.IsFunctionWord && !IsNameEntry(t.WordId))
                          .DistinctBy(t => t.WordKey)
                          .Select(t => (Token: t, Status: statusOf(t.WordKey)))
                          .ToList();
        var weak = words.Where(w => !IsSolid(w.Status, rule.OthersMature)).ToList();
        if (weak.Count > 1) return [];
        return (weak.Count == 1 ? weak : words).Where(w => rule.Targets.Contains(w.Status)).ToList();
    }

    private static bool IsSolid(MiningStatus status, bool othersMature) =>
        status is MiningStatus.Settled or MiningStatus.Mature || status == MiningStatus.Young && !othersMature;

    /// <summary>JMnedict entries; custom entries from 8,000,000 up are vocabulary and still count.</summary>
    public static bool IsNameEntry(int wordId) => wordId is >= 5_000_000 and < 8_000_000;

    /// <summary>Every occurrence of an unknown content word other than the target; particles, auxiliaries and names never count.</summary>
    public static IEnumerable<SentenceToken> UnknownTokens(IEnumerable<SentenceToken> tokens, int targetWordId, Func<int, byte, bool> isKnown) =>
        tokens.Where(t => !t.IsFunctionWord && t.WordId != targetWordId && !IsNameEntry(t.WordId) && !isKnown(t.WordId, t.ReadingIndex));

    /// <summary>Distinct unknown content words other than the target word, as <see cref="UnknownTokens"/> finds them.</summary>
    public static int CountUnknown(IEnumerable<SentenceToken> tokens, int targetWordId, Func<int, byte, bool> isKnown) =>
        UnknownTokens(tokens, targetWordId, isKnown).Select(t => t.WordKey).Distinct().Count();
}
