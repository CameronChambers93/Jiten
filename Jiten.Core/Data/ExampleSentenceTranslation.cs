namespace Jiten.Core.Data;

/// <summary>
/// A machine translation of an example sentence, kept so each sentence costs at most one AI call. Translations taken
/// from English subtitles aren't stored here; they're read from <see cref="DeckEnglishTrack"/> on request.
/// </summary>
public class ExampleSentenceTranslation
{
    public long SentenceId { get; set; }

    /// <summary>Denormalised from the sentence so a title's translations can be counted and cleared without a join.</summary>
    public int DeckId { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Translation provider id, e.g. "claude".</summary>
    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}
