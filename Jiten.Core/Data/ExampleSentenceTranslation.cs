namespace Jiten.Core.Data;

/// <summary>
/// A machine translation of a sentence's text, kept so each text costs at most one AI call. Translations taken
/// from English subtitles aren't stored here; they're read from <see cref="DeckEnglishTrack"/> on request.
/// </summary>
public class ExampleSentenceTranslation
{
    public long CentralSentenceId { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Translation provider id, e.g. "claude".</summary>
    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}
