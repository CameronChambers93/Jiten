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

    public bool IsEmpty => Text == null && English == null && StartMs == null && PictureMs == null;
}
