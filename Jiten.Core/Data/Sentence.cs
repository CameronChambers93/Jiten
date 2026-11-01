namespace Jiten.Core.Data;

/// <summary>
/// A sentence's text, stored once however many places it appears in; each appearance is an <see cref="ExampleSentence"/>.
/// Rows are written with the SQL in <see cref="JitenHelper"/>, because jiten.normalize_sentence decides what counts as the same text.
/// </summary>
public class Sentence
{
    public long Id { get; set; }

    /// <summary>SHA-256 of Text, the sentence's identity.</summary>
    public required byte[] TextHash { get; set; }

    /// <summary>The normalized text: tags stripped, trimmed, NFC.</summary>
    public required string Text { get; set; }

    /// <summary>The parsed words (see <see cref="ExampleSentenceTokens"/>); null until parsed. Only private lines are parsed here so far, a deck's tokens are on its occurrences.</summary>
    public byte[]? Tokens { get; set; }

    public int[]? WordKeys { get; set; }
}
