namespace Jiten.Core.Data.User;

/// <summary>Why a sentence was flagged.</summary>
public static class SentenceFlagReason
{
    public const string Japanese = "japanese";
    public const string English = "english";
    public const string Media = "media";
    public const string Parsing = "parsing";

    public static bool IsValid(string? reason) => reason is Japanese or English or Media or Parsing;
}

/// <summary>How a flag was closed.</summary>
public static class SentenceFlagResolution
{
    /// <summary>The user closed it themselves.</summary>
    public const string Dismissed = "dismissed";

    /// <summary>An admin fixed the line, which closes every user's flag on it.</summary>
    public const string Fixed = "fixed";

    /// <summary>An admin found nothing wrong with the line ("Not a problem").</summary>
    public const string Rejected = "rejected";
}

/// <summary>
/// One user's report that a sentence is wrong. It's on the occurrence, so a line nobody mined can be flagged, and it hides
/// that line for this user only. A user has at most one open flag per occurrence.
/// </summary>
public class SentenceFlag
{
    public long SentenceFlagId { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>The occurrence: an <c>ExampleSentences</c> id, across schemas like <see cref="MinedSentence.ExampleSentenceId"/>.</summary>
    public long ExampleSentenceId { get; set; }

    /// <summary>A <see cref="SentenceFlagReason"/>; null when it came from a card and the reason is still needed.</summary>
    public string? Reason { get; set; }

    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When it was fixed or dismissed; the sentence comes back from then on.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>A <see cref="SentenceFlagResolution"/>, set with <see cref="ResolvedAt"/>.</summary>
    public string? Resolution { get; set; }
}
