namespace Jiten.Core.Data;

public class ExampleSentence
{
    public long SentenceId { get; set; }
    /// <summary>Null for a line mined from a game or the web, unless its game has a deck.</summary>
    public int? DeckId { get; set; }
    public required string Text { get; set; }

    /// <summary>
    /// Position i.e. id of the sentence it appears in
    /// </summary>
    public int Position { get; set; }
    public float Difficulty { get; set; }

    /// <summary>Packed resolved tokens, see <see cref="ExampleSentenceTokens"/>.</summary>
    public required byte[] Tokens { get; set; }

    public required int[] WordKeys { get; set; }

    /// <summary>The <see cref="Sentence"/> holding this text. SentenceId is this row's own id, the occurrence's.</summary>
    public long? CentralSentenceId { get; set; }

    /// <summary>
    /// A reparse dropped this line but a user mined it, so the row stays for its media. Hidden by a query filter and
    /// by its empty WordKeys, which is what hides it from the raw SQL readers.
    /// </summary>
    public bool RemovedFromDeck { get; set; }

    /// <summary>The user who mined a deckless line; such a line is private to them. No foreign key: users are in another schema.</summary>
    public string? OwnerId { get; set; }

    /// <summary>The name a game line opened with (<c>name「…」</c>), moved out so the text matches the same line elsewhere.</summary>
    public string? Speaker { get; set; }

    public string? SourceTitle { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>Stored media, for lines with no episode to cut it from.</summary>
    public string? AudioPath { get; set; }

    public string? AudioContentType { get; set; }
    public string? PicturePath { get; set; }
    public string? PictureContentType { get; set; }

    public Deck? Deck { get; set; }
}
