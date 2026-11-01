namespace Jiten.Core.Data;

public class ExampleSentence
{
    public long SentenceId { get; set; }
    public int DeckId { get; set; }
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

    public Deck? Deck { get; set; }
}
