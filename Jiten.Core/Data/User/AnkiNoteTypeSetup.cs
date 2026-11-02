namespace Jiten.Core.Data.User;

/// <summary>
/// How Sync treats one Anki note type: which fields hold which roles, an optional search that narrows the notes, and the
/// actions that run on them. Note types without a setup aren't synced.
/// </summary>
public class AnkiNoteTypeSetup
{
    public long AnkiNoteTypeSetupId { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>The Anki note type (model) name.</summary>
    public required string ModelName { get; set; }

    /// <summary>An Anki search ANDed with <c>note:"ModelName"</c>, such as <c>-deck:JPMN-Examples</c>; null for every note.</summary>
    public string? Filter { get; set; }

    /// <summary>JSON object of field name → role (see <see cref="AnkiFieldRoles"/>). A role can sit on several fields.</summary>
    public string FieldRolesJson { get; set; } = "{}";

    /// <summary>The preset the roles were filled from, if any.</summary>
    public string? Preset { get; set; }

    /// <summary>The card state that makes a word known (see <see cref="AnkiKnownWordsState"/>); null when off.</summary>
    public string? KnownWords { get; set; }

    public bool ImportSentences { get; set; }
    public bool TakeFlags { get; set; }
    public bool FoldDuplicates { get; set; }
    public bool UpdateMainFields { get; set; }

    /// <summary>What Sync writes to the sentence-list field (see <see cref="AnkiSentenceListFormat"/>).</summary>
    public string SentenceList { get; set; } = AnkiSentenceListFormat.None;

    /// <summary>The rotation script version inserted into the note type's templates; null when none is.</summary>
    public int? ScriptVersion { get; set; }

    /// <summary>
    /// JSON of how mining treats this note type: adding to a word's existing card, the parts it builds, and the empty-field
    /// policies (see <c>AnkiMiningSettingsDto</c>); null for the defaults.
    /// </summary>
    public string? MiningJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether any sentence action is on: the note type's notes are mining notes.</summary>
    public bool HasSentenceActions => ImportSentences || TakeFlags || FoldDuplicates || UpdateMainFields || SentenceList != AnkiSentenceListFormat.None;
}

public static class AnkiKnownWordsState
{
    /// <summary>Reviewed at least once.</summary>
    public const string Seen = "seen";

    /// <summary>Out of learning, in review.</summary>
    public const string Review = "review";

    /// <summary>In review with an interval of 21 days or more.</summary>
    public const string Mature = "mature";

    public static readonly string[] All = [Seen, Review, Mature];
}

public static class AnkiMiningExisting
{
    /// <summary>Add the sentence to the word's card without asking.</summary>
    public const string Auto = "auto";

    /// <summary>Ask whether to add it or create a new note.</summary>
    public const string Ask = "ask";

    public static readonly string[] All = [Auto, Ask];
}

public static class AnkiSentenceListFormat
{
    /// <summary>Every sentence as data for the rotation script.</summary>
    public const string Script = "script";

    /// <summary>Every sentence as a readable list.</summary>
    public const string Plain = "plain";

    /// <summary>No list; only the main fields, when updating them is on.</summary>
    public const string None = "none";

    public static readonly string[] All = [Script, Plain, None];
}

/// <summary>What a note type's field holds, read by import and Sync and written when a card is created.</summary>
public static class AnkiFieldRoles
{
    public static readonly string[] All =
    [
        "word", "wordReading", "wordFurigana", "pitchPosition", "pitchCategories", "definitions", "mainDefinition", "frequency",
        "sentence", "sentencePlain", "sentenceFurigana", "sentenceEnglish", "sentenceAudio", "picture", "source", "url",
        "previousLine", "sentenceList",
    ];
}
