namespace Jiten.Core.Data;

/// <summary>
/// A fix submitted to a shared line, kept so parsing can apply it again once decks keep their raw text (sentence flags
/// step 3). Append-only: the line's own columns hold the fix in force.
/// </summary>
public class SentenceCorrection
{
    public long SentenceCorrectionId { get; set; }
    public long ExampleSentenceId { get; set; }
    public int? DeckId { get; set; }

    /// <summary>The line's position in its deck when it was fixed, to find it again near there.</summary>
    public int Position { get; set; }

    /// <summary>The subtitle's text, as the parser found it.</summary>
    public required string SourceText { get; set; }

    public string? Text { get; set; }
    public string? English { get; set; }
    public int? StartMs { get; set; }
    public int? EndMs { get; set; }
    public int? PictureMs { get; set; }

    /// <summary>Who submitted it. No foreign key: users are in another schema.</summary>
    public required string UserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
