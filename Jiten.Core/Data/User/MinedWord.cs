namespace Jiten.Core.Data.User;

/// <summary>
/// A word's sentence list (n1miner's sentence store), whether or not the word has an Anki note: sentences saved in Jiten
/// and those imported from or mined into Anki. Jiten is the source of truth for the sentences and writes them out to the
/// word's Anki note, when it has one.
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

    /// <summary>Set while a fold is under way: the note being kept. The note is deleted in Anki, then the fold is confirmed.</summary>
    public long? FoldingInto { get; set; }

    /// <summary>The sentence Sync last wrote to the note's main fields; null while they hold the note's own sentence.</summary>
    public long? FieldsSentenceId { get; set; }

    /// <summary>A hash of what Sync last wrote to the main fields, so a changed sentence is written again.</summary>
    public string? FieldsHash { get; set; }

    /// <summary>How the note's word was chosen (<see cref="MinedWordMatch"/>); null until the note has been checked.</summary>
    public string? WordMatch { get; set; }
}

/// <summary>How a note's JMDict entry was chosen. Only <see cref="Guessed"/> is shown for review.</summary>
public static class MinedWordMatch
{
    /// <summary>No entry fits the word and reading: a new note has no WordId; a stored one keeps the word it was on.</summary>
    public const string None = "none";

    /// <summary>Only one entry fits the word and reading.</summary>
    public const string Single = "single";

    /// <summary>Several fit, and the glosses of exactly one appear in the note's text.</summary>
    public const string Gloss = "gloss";

    /// <summary>Several fit and the note's text didn't settle it: the most common one was taken.</summary>
    public const string Guessed = "guessed";

    /// <summary>Recorded by the mine button, which knows the entry it mined for.</summary>
    public const string Mined = "mined";

    /// <summary>The user picked or confirmed the entry.</summary>
    public const string Chosen = "chosen";

    /// <summary>Statuses Jiten decided itself, which a later check may change; the others are never redone.</summary>
    public static bool IsAutomatic(string? status) => status is null or None or Single or Gloss or Guessed;
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

    /// <summary>The user's own text as card HTML, the word in bold; null to use the occurrence's. See <see cref="Shown"/>.</summary>
    public string? Text { get; set; }

    /// <summary>The sentence as card HTML, once <see cref="MinedLineStore.FillAsync"/> has read its occurrence.</summary>
    public string Shown => Text ?? OccurrenceHtml ?? "";

    /// <summary>Read from the occurrence, not stored here.</summary>
    public string? OccurrenceHtml { get; set; }

    /// <summary>The sentence with readings, as Lapis+'s SentenceFurigana holds it (bracket or ruby furigana).</summary>
    public string? Furigana { get; set; }

    public string? English { get; set; }

    /// <summary>Where the English came from: "subtitles", "ai", "anki" (already on the note) or "user".</summary>
    public string? EnglishOrigin { get; set; }

    /// <summary>A <see cref="MinedSentenceOrigin"/> value.</summary>
    public string Origin { get; set; } = MinedSentenceOrigin.Other;

    /// <summary>The occurrence this sentence uses: an example sentence, or the user's private line.</summary>
    public long? ExampleSentenceId { get; set; }

    /// <summary>Where the target word is in the occurrence's text; null when the note didn't mark it.</summary>
    public int? TargetPosition { get; set; }

    public int? TargetLength { get; set; }

    /// <summary>
    /// The occurrence's text changed (moved to a whole-cue line by a re-ingest), so the stored text, furigana and English
    /// are rebuilt from it by the next Sync.
    /// </summary>
    public bool NeedsRebuild { get; set; }

    /// <summary>
    /// Kept for Jiten only: the sentence is in the word's list but Sync doesn't write it to the word's Anki card. Mining
    /// it to Anki clears this, with the card's parts (English, furigana, media) built then.
    /// </summary>
    public bool JitenOnly { get; set; }

    /// <summary>The game, show or site it came from, as known (GSM's Game:: tag, the card's source, the page's host). Stored on the occurrence, like the media.</summary>
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

    /// <summary>The video clip's file name in Anki's collection; only Jiten sentences cut from a linked video have one.</summary>
    public string? AnkiVideoFilename { get; set; }

    /// <summary>The Anki note it was imported from.</summary>
    public long? SourceAnkiNoteId { get; set; }

    /// <summary>The first sentence of a word is its main one, shown in the note's main Sentence fields.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
