namespace Jiten.Core.Data.User;

/// <summary>
/// A word the user mined into Anki, with the sentences Jiten keeps for it (n1miner's sentence store). Jiten is the
/// source of truth for the sentences and writes them out to the word's Anki note.
/// </summary>
public class MinedWord
{
    public long MinedWordId { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>The JMDict word, or null when the note's word has no match (names, slang); then only the text is known.</summary>
    public int? WordId { get; set; }

    public byte? ReadingIndex { get; set; }

    /// <summary>The word as written on the note (Expression).</summary>
    public required string Expression { get; set; }

    /// <summary>Its kana reading (ExpressionReading).</summary>
    public string Reading { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<MinedNote> Notes { get; set; } = [];
    public List<MinedSentence> Sentences { get; set; } = [];
}

/// <summary>An Anki note of a mined word. A word can have several until duplicates are folded into one.</summary>
public class MinedNote
{
    public long MinedNoteId { get; set; }
    public long MinedWordId { get; set; }
    public string UserId { get; set; } = default!;
    public long AnkiNoteId { get; set; }
    public string ModelName { get; set; } = "";
    public string DeckName { get; set; } = "";
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

public static class MinedSentenceOrigin
{
    /// <summary>One of Jiten's example sentences; media is cut again from the episode on demand.</summary>
    public const string Jiten = "jiten";

    /// <summary>A game line captured by GSM; its media is the copy taken on import.</summary>
    public const string Game = "game";

    /// <summary>Text looked up on a web page (or document) with Yomitan.</summary>
    public const string Web = "web";

    public const string Other = "other";
}

/// <summary>One sentence of a mined word, as shown on the word's Anki card.</summary>
public class MinedSentence
{
    public long MinedSentenceId { get; set; }
    public long MinedWordId { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>The sentence as card HTML, the word in bold.</summary>
    public required string Text { get; set; }

    /// <summary>The sentence with readings, as Lapis+'s SentenceFurigana holds it (bracket or ruby furigana).</summary>
    public string? Furigana { get; set; }

    public string? English { get; set; }

    /// <summary>Where the English came from: "subtitles", "ai", "anki" (already on the note) or "user".</summary>
    public string? EnglishOrigin { get; set; }

    /// <summary>A <see cref="MinedSentenceOrigin"/> value.</summary>
    public string Origin { get; set; } = MinedSentenceOrigin.Other;

    /// <summary>For <see cref="MinedSentenceOrigin.Jiten"/>, the example sentence it came from.</summary>
    public long? ExampleSentenceId { get; set; }

    /// <summary>The game, show or site it came from, as known (GSM's Game:: tag, the card's source, the page's host).</summary>
    public string? SourceTitle { get; set; }

    public string? SourceUrl { get; set; }

    /// <summary>The line shown before it, when known.</summary>
    public string? PreviousLine { get; set; }

    /// <summary>When it was mined (the Anki note's creation time for imported notes).</summary>
    public DateTime? MinedAt { get; set; }

    /// <summary>Copied audio, under the user's mined-media storage; null for Jiten sentences (cut on demand) or none.</summary>
    public string? AudioPath { get; set; }

    public string? AudioContentType { get; set; }
    public string? PicturePath { get; set; }
    public string? PictureContentType { get; set; }

    /// <summary>The media's file names in Anki's collection, so writing back doesn't upload a second copy.</summary>
    public string? AnkiAudioFilename { get; set; }

    public string? AnkiPictureFilename { get; set; }

    /// <summary>The Anki note it was imported from.</summary>
    public long? SourceAnkiNoteId { get; set; }

    /// <summary>The sentence shown in the note's main Sentence fields.</summary>
    public bool IsMain { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Set when the user flags the sentence from a card as needing an edit.</summary>
    public DateTime? FlaggedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
