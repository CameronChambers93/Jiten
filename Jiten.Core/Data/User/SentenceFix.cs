namespace Jiten.Core.Data.User;

/// <summary>
/// One user's own version of a sentence occurrence: what they see wherever the line shows for them, and on their cards.
/// A null part is "as the shared line". Submitting it (admins, for now) moves it to the shared line and drops this row.
/// </summary>
public class SentenceFix
{
    public long SentenceFixId { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>The occurrence: an <c>ExampleSentences</c> id, across schemas like <see cref="SentenceFlag.ExampleSentenceId"/>.</summary>
    public long ExampleSentenceId { get; set; }

    /// <summary>The corrected Japanese; null keeps the line's.</summary>
    public string? Text { get; set; }

    /// <summary><see cref="Text"/> parsed (see <c>ExampleSentenceTokens</c>), for furigana and the target word; set with it.</summary>
    public byte[]? Tokens { get; set; }

    public string? English { get; set; }

    /// <summary>The clip, in subtitle time, padding included: both set or neither.</summary>
    public int? StartMs { get; set; }

    public int? EndMs { get; set; }

    /// <summary>The screenshot's frame, in subtitle time.</summary>
    public int? PictureMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When someone else's fix of the line was approved while the user, in "ask me" mode (<see cref="ApprovedFixMode"/>),
    /// had this one; their to-do list asks which to keep until they answer.
    /// </summary>
    public DateTime? ApprovedFixAt { get; set; }

    public bool IsEmpty => Text == null && English == null && StartMs == null && PictureMs == null;
}

/// <summary>What happens to a user's own fix of a line when someone else's fix of it is approved.</summary>
public static class ApprovedFixMode
{
    /// <summary>Their fix stays as it is (the default).</summary>
    public const string Keep = "keep";

    /// <summary>The parts the approval fixed are dropped from their fix, so they see the approved ones.</summary>
    public const string Use = "use";

    /// <summary>Their to-do list asks them.</summary>
    public const string Ask = "ask";

    public static bool IsValid(string? mode) => mode is Keep or Use or Ask;
}
