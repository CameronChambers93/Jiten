using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <param name="Ready">False while the note is too new to fold: GSM may still be adding its audio and screenshot.</param>
/// <param name="WordMatch">How the note's word was chosen; folding a guessed one asks first.</param>
public record FoldNoteDto(long AnkiNoteId, string DeckName, DateTime CreatedAt, bool Ready, string? Sentence, string? WordMatch = null);

/// <param name="Keep">The note that stays: the oldest, which carries the review history.</param>
public record FoldCandidateDto(long MinedWordId, string Expression, string Reading, long Keep, List<FoldNoteDto> Notes);

public record FoldRequest(long MinedWordId, long KeepAnkiNoteId, List<long> FoldedAnkiNoteIds);

/// <summary>A fold recorded before its notes were deleted in Anki, and not yet confirmed.</summary>
public record PendingFoldDto(long MinedWordId, long Keep, List<long> Folded);

public record FoldsDto(List<FoldCandidateDto> Candidates, List<PendingFoldDto> Pending);

/// <param name="GoneAnkiNoteIds">The folded notes Anki no longer has, when only some were deleted; null for all.</param>
public record ConfirmFoldRequest(List<long>? GoneAnkiNoteIds);

/// <summary>
/// A flag tag read from an Anki note: <c>sentence-fix::&lt;minedSentenceId&gt;</c>, or the plain <c>jiten-flag</c> tag
/// (no id), which flags the sentence in the note's main fields.
/// </summary>
public record SentenceFlagDto(long AnkiNoteId, long? MinedSentenceId);

public record FlagsRequest(List<SentenceFlagDto> Flags);

public record MinedSettingsRequest(string TargetWordTemplate);

/// <summary>
/// The word's main sentence, for a note whose setup updates its main fields. The browser writes each part to the fields
/// with that role, then reports the note written with the id and hash.
/// </summary>
public record MainFieldsDto(long MinedSentenceId, string Hash, string Sentence, string SentencePlain, string? Furigana, string? English,
                            string? AudioFilename, string? PictureFilename, string? Source);

/// <summary>
/// What Sync writes to a word's kept note: the sentence list (in the setup's format; null when it has none) and the main
/// fields (null when they don't need writing).
/// </summary>
public record NoteWriteDto(long AnkiNoteId, string ModelName, string? SentencesHtml, MainFieldsDto? Main);

public record MainWrittenDto(long AnkiNoteId, long MinedSentenceId, string Hash);

public record MainWrittenRequest(List<MainWrittenDto> Notes);

/// <summary>
/// Sync between the sentence store and Anki. The browser does the AnkiConnect half (reading tags, deleting folded notes,
/// writing fields); this decides what to do and records it.
/// </summary>
public class MinedSyncService(UserDbContext userContext, IDbContextFactory<JitenDbContext> contextFactory, SentenceFlagService sentenceFlags,
                              AnkiSyncSetupService setups)
{
    public const string FlagTagPrefix = "sentence-fix::";
    public const string PlainFlagTag = "jiten-flag";

    /// <summary>How old a note must be before it can be folded, so GSM has finished enhancing it.</summary>
    public static readonly TimeSpan FoldDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Words on more than one note of note types that fold duplicates, and the folds recorded but not yet confirmed. Notes
    /// being folded aren't candidates.
    /// </summary>
    public async Task<FoldsDto> FoldsAsync(string userId, CancellationToken ct)
    {
        var folding = (await setups.SetupsAsync(userId, ct)).Where(s => s.FoldDuplicates).Select(s => s.ModelName).ToHashSet();
        var words = await userContext.MinedWords.AsNoTracking().AsSplitQuery()
                                     .Where(w => w.UserId == userId && w.Notes.Count > 1)
                                     .Include(w => w.Notes).Include(w => w.Sentences)
                                     .OrderBy(w => w.Expression)
                                     .ToListAsync(ct);
        await FillAsync(userId, words.SelectMany(w => w.Sentences).ToList(), ct);
        var now = DateTime.UtcNow;

        var candidates = new List<FoldCandidateDto>();
        foreach (var w in words)
        {
            var notes = w.Notes.Where(n => n.FoldingInto == null && folding.Contains(n.ModelName)).OrderBy(n => n.AnkiNoteId).ToList();
            if (notes.Count < 2 || w.Notes.Any(n => n.FoldingInto != null)) continue;
            candidates.Add(new FoldCandidateDto(w.MinedWordId, w.Expression, w.Reading, notes[0].AnkiNoteId, notes.Select(n =>
            {
                var created = CreatedAt(n.AnkiNoteId);
                var sentence = w.Sentences.FirstOrDefault(s => s.SourceAnkiNoteId == n.AnkiNoteId)?.Shown;
                return new FoldNoteDto(n.AnkiNoteId, n.DeckName, created, now - created >= FoldDelay, sentence, n.WordMatch);
            }).ToList()));
        }

        var pending = words.Where(w => w.Notes.Any(n => n.FoldingInto != null))
                           .Select(w => new PendingFoldDto(w.MinedWordId, w.Notes.First(n => n.FoldingInto != null).FoldingInto!.Value,
                                                           w.Notes.Where(n => n.FoldingInto != null).Select(n => n.AnkiNoteId).ToList()))
                           .ToList();
        return new FoldsDto(candidates, pending);
    }

    /// <summary>
    /// Records a fold the user confirmed, before the browser deletes the folded notes in Anki, so a failure in between
    /// leaves a pending fold rather than deleted notes Jiten still lists. False when the notes aren't the word's.
    /// </summary>
    public async Task<bool> BeginFoldAsync(string userId, FoldRequest request, CancellationToken ct)
    {
        var notes = await userContext.MinedNotes.Where(n => n.UserId == userId && n.MinedWordId == request.MinedWordId).ToListAsync(ct);
        if (notes.All(n => n.AnkiNoteId != request.KeepAnkiNoteId) || request.FoldedAnkiNoteIds.Count == 0
                                                                    || request.FoldedAnkiNoteIds.Contains(request.KeepAnkiNoteId)) return false;

        // A pending fold may already have deleted its notes in Anki; it's finished (or dropped) by Sync before another starts.
        if (notes.Any(n => n.FoldingInto != null)) return false;

        var folded = notes.Where(n => request.FoldedAnkiNoteIds.Contains(n.AnkiNoteId)).ToList();
        if (folded.Count != request.FoldedAnkiNoteIds.Distinct().Count()) return false;

        foreach (var note in folded) note.FoldingInto = request.KeepAnkiNoteId;
        await userContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Completes a pending fold once its notes are gone from Anki: the notes are dropped and their sentences stay with the
    /// word. <paramref name="gone"/> names the notes Anki no longer has when only some were deleted; the rest stay
    /// notes of the word. Returns whether a fold was pending, and what to write to the kept note straight away.
    /// </summary>
    public async Task<(bool Confirmed, NoteWriteDto? Write)> ConfirmFoldAsync(string userId, long minedWordId, IReadOnlyCollection<long>? gone,
                                                                            CancellationToken ct)
    {
        var notes = await userContext.MinedNotes.Where(n => n.UserId == userId && n.MinedWordId == minedWordId).ToListAsync(ct);
        var pending = notes.Where(n => n.FoldingInto != null).ToList();
        if (pending.Count == 0) return (false, null);
        var keep = pending[0].FoldingInto!.Value;
        var folded = gone == null ? pending : pending.Where(n => gone.Contains(n.AnkiNoteId)).ToList();
        foreach (var note in pending.Except(folded)) note.FoldingInto = null;

        userContext.MinedNotes.RemoveRange(folded);

        // A note mined twice from the same line brings the same sentence twice; keep one copy, preferring the kept note's.
        var sentences = await userContext.MinedSentences.Where(s => s.MinedWordId == minedWordId).ToListAsync(ct);
        await FillAsync(userId, sentences, ct);
        foreach (var copies in sentences.GroupBy(s => MinedSentenceService.PlainText(s.Shown)).Where(g => g.Count() > 1))
        {
            var kept = copies.OrderByDescending(s => s.SourceAnkiNoteId == keep).ThenBy(s => s.SortOrder).First();
            kept.SortOrder = copies.Min(s => s.SortOrder);
            userContext.MinedSentences.RemoveRange(copies.Where(s => s != kept));
        }

        var word = await userContext.MinedWords.FirstAsync(w => w.MinedWordId == minedWordId, ct);
        word.UpdatedAt = DateTime.UtcNow;
        await userContext.SaveChangesAsync(ct);
        return (true, (await NoteWritesAsync(userId, minedWordId, ct)).FirstOrDefault());
    }

    /// <summary>Drops a pending fold whose notes are still in Anki (the delete never happened); the word is a candidate again.</summary>
    public async Task<bool> CancelFoldAsync(string userId, long minedWordId, CancellationToken ct) =>
        await userContext.MinedNotes.Where(n => n.UserId == userId && n.MinedWordId == minedWordId && n.FoldingInto != null)
                         .ExecuteUpdateAsync(u => u.SetProperty(n => n.FoldingInto, (long?)null), ct) > 0;

    /// <summary>
    /// Opens a flag, with no reason yet, on each flagged sentence's occurrence; returns the flags that named a sentence on
    /// that note's word (or, for the plain tag, found the note's main sentence), so their tags can be removed.
    /// </summary>
    public async Task<List<SentenceFlagDto>> ApplyFlagsAsync(string userId, IReadOnlyList<SentenceFlagDto> flags, CancellationToken ct)
    {
        var noteIds = flags.Select(f => f.AnkiNoteId).Distinct().ToList();
        var notes = await userContext.MinedNotes.AsNoTracking().Where(n => n.UserId == userId && noteIds.Contains(n.AnkiNoteId))
                                     .ToDictionaryAsync(n => n.AnkiNoteId, ct);
        var wordIds = notes.Values.Select(n => n.MinedWordId).Distinct().ToList();
        var words = await userContext.MinedWords.AsNoTracking().AsSplitQuery().Where(w => wordIds.Contains(w.MinedWordId))
                                     .Include(w => w.Notes).Include(w => w.Sentences).ToDictionaryAsync(w => w.MinedWordId, ct);
        var open = await sentenceFlags.OpenSentenceIdsAsync(userId, ct);

        var applied = new List<SentenceFlagDto>();
        foreach (var flag in flags)
        {
            if (!notes.TryGetValue(flag.AnkiNoteId, out var note)) continue;
            var word = words[note.MinedWordId];
            var sentence = flag.MinedSentenceId is { } id
                ? word.Sentences.FirstOrDefault(s => s.MinedSentenceId == id)
                : ShownOn(word, note, open);
            if (sentence?.ExampleSentenceId is not { } occurrence) continue;
            if (open.Add(occurrence)) userContext.SentenceFlags.Add(new SentenceFlag { UserId = userId, ExampleSentenceId = occurrence });
            applied.Add(flag);
        }

        await userContext.SaveChangesAsync(ct);
        return applied;
    }

    /// <summary>
    /// The sentence a note's main fields show: what Sync last wrote there, else the note's own sentence, else the word's
    /// main one.
    /// </summary>
    public static MinedSentence? ShownOn(MinedWord word, MinedNote note, IReadOnlySet<long> flagged) =>
        word.Sentences.FirstOrDefault(s => s.MinedSentenceId == note.FieldsSentenceId)
        ?? word.Sentences.FirstOrDefault(s => s.SourceAnkiNoteId == note.AnkiNoteId)
        ?? InRotation(word.Sentences, flagged).OrderBy(s => s.SortOrder).FirstOrDefault();

    /// <summary>
    /// What to write to each word's kept note (the oldest), as its note type's setup asks: the sentence list, and the main
    /// fields when the word's main sentence isn't the one they hold. Notes of note types without a setup are left alone.
    /// </summary>
    public async Task<List<NoteWriteDto>> NoteWritesAsync(string userId, long? minedWordId, CancellationToken ct)
    {
        var bySetup = (await setups.SetupsAsync(userId, ct)).ToDictionary(s => s.ModelName);
        var template = await TargetTemplateAsync(userId, ct);
        var query = userContext.MinedWords.AsNoTracking().AsSplitQuery()
                               .Where(w => w.UserId == userId && w.Notes.Any() && w.Sentences.Any());
        if (minedWordId != null) query = query.Where(w => w.MinedWordId == minedWordId);
        var words = await query.Include(w => w.Notes).Include(w => w.Sentences).ToListAsync(ct);
        await FillAsync(userId, words.SelectMany(w => w.Sentences).ToList(), ct);
        var flagged = await sentenceFlags.OpenSentenceIdsAsync(userId, ct);

        var writes = new List<NoteWriteDto>();
        foreach (var w in words)
        {
            var note = KeptNote(w, bySetup);
            if (note == null) continue;
            var setup = bySetup[note.ModelName];
            var rotation = InRotation(w.Sentences, flagged);

            var list = setup.SentenceList switch
            {
                AnkiSentenceListFormat.Script => RenderSentences(note.AnkiNoteId, rotation, template),
                AnkiSentenceListFormat.Plain => RenderPlainList(rotation, template),
                _ => null,
            };
            var main = setup.UpdateMainFields ? MainToWrite(w, note, rotation, template) : null;
            if (list != null || main != null) writes.Add(new NoteWriteDto(note.AnkiNoteId, note.ModelName, list, main));
        }

        return writes;
    }

    /// <summary>Whether Sync updates the main fields of the note type's notes.</summary>
    public async Task<bool> UpdatesMainFieldsAsync(string userId, string modelName, CancellationToken ct) =>
        (await setups.SetupsAsync(userId, ct)).Any(s => s.ModelName == modelName && s.UpdateMainFields);

    /// <summary>
    /// The note Sync writes a word's sentences to: the oldest note of a note type that has a setup, leaving out notes
    /// being folded away. Null when none of the word's notes is synced.
    /// </summary>
    public static MinedNote? KeptNote(MinedWord word, IReadOnlyDictionary<string, AnkiNoteTypeSetup> setups) =>
        word.Notes.Where(n => n.FoldingInto == null && setups.ContainsKey(n.ModelName)).MinBy(n => n.AnkiNoteId);

    /// <summary>The main fields for the word's main sentence, or null when the note already shows it as it is now.</summary>
    public static MainFieldsDto? MainToWrite(MinedWord word, MinedNote note, IReadOnlyCollection<MinedSentence> rotation, string template)
    {
        var main = rotation.OrderBy(s => s.SortOrder).First();
        var fields = MainFields(main, template);
        if (note.FieldsSentenceId == null)
        {
            // Until Sync writes them, the fields hold the note's own sentence as Anki has it, which stays while it's the main one.
            var own = word.Sentences.FirstOrDefault(s => s.SourceAnkiNoteId == note.AnkiNoteId);
            if (own?.MinedSentenceId == main.MinedSentenceId) return null;
        }
        else if (note.FieldsSentenceId == main.MinedSentenceId && note.FieldsHash == fields.Hash) return null;

        return fields;
    }

    public static MainFieldsDto MainFields(MinedSentence s, string template)
    {
        var sentence = TargetWordMarkup.Apply(s.Shown, template);
        var plain = MinedSentenceService.PlainText(s.Shown);
        var furigana = string.IsNullOrEmpty(s.Furigana) ? null : TargetWordMarkup.Apply(s.Furigana, template);
        var parts = string.Join("\u0001", sentence, plain, furigana, s.English, s.AnkiAudioFilename, s.AnkiPictureFilename, s.SourceTitle);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts)))[..32];
        return new MainFieldsDto(s.MinedSentenceId, hash, sentence, plain, furigana, s.English, s.AnkiAudioFilename, s.AnkiPictureFilename, s.SourceTitle);
    }

    /// <summary>Records the main fields the browser wrote; notes or sentences that aren't the user's are skipped.</summary>
    public async Task MainWrittenAsync(string userId, IReadOnlyList<MainWrittenDto> written, CancellationToken ct)
    {
        var ids = written.Select(w => w.AnkiNoteId).ToList();
        var notes = await userContext.MinedNotes.Where(n => n.UserId == userId && ids.Contains(n.AnkiNoteId)).ToDictionaryAsync(n => n.AnkiNoteId, ct);
        var sentenceIds = written.Select(w => w.MinedSentenceId).ToList();
        var wordOf = await userContext.MinedSentences.AsNoTracking().Where(s => s.UserId == userId && sentenceIds.Contains(s.MinedSentenceId))
                                      .ToDictionaryAsync(s => s.MinedSentenceId, s => s.MinedWordId, ct);
        foreach (var w in written)
        {
            if (!notes.TryGetValue(w.AnkiNoteId, out var note) || wordOf.GetValueOrDefault(w.MinedSentenceId) != note.MinedWordId) continue;
            note.FieldsSentenceId = w.MinedSentenceId;
            note.FieldsHash = w.Hash.Length > 64 ? w.Hash[..64] : w.Hash;
        }

        await userContext.SaveChangesAsync(ct);
    }

    /// <summary>The sentences a card may show: the unflagged ones, or the main one if all are flagged, so the card still works.</summary>
    public static List<MinedSentence> InRotation(IReadOnlyCollection<MinedSentence> sentences, IReadOnlySet<long> flagged)
    {
        var kept = sentences.Where(s => s.ExampleSentenceId is not { } id || !flagged.Contains(id)).ToList();
        return kept.Count > 0 ? kept : sentences.OrderBy(s => s.SortOrder).Take(1).ToList();
    }

    public async Task FillAsync(string userId, IReadOnlyCollection<MinedSentence> sentences, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await MinedLineStore.FillAsync(context, userId, sentences, ct);
    }

    public async Task<string> TargetTemplateAsync(string userId, CancellationToken ct) =>
        await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.TargetWordTemplate).FirstOrDefaultAsync(ct)
        ?? TargetWordMarkup.Default;

    public async Task SaveTargetTemplateAsync(string userId, string template, CancellationToken ct)
    {
        var settings = await userContext.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (settings == null) userContext.UserSettings.Add(settings = new UserSettings { UserId = userId });
        settings.TargetWordTemplate = template;
        await userContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The Sentences field: one block per sentence for Lapis+'s script, which shows a random one on each review. Media are
    /// real <c>&lt;audio src&gt;</c> / <c>&lt;img src&gt;</c> references (not file names in data), so Anki's Check Media
    /// counts the files as used; the template keeps the blocks hidden. The note's id is on the list, since a template
    /// can't otherwise know it and the card's flag button tags the note through AnkiConnect.
    /// </summary>
    public static string RenderSentences(long ankiNoteId, IEnumerable<MinedSentence> sentences, string targetTemplate = TargetWordMarkup.Default)
    {
        var html = new StringBuilder($"<div class=\"jiten-sentences\" data-note=\"{ankiNoteId}\">");
        var ordered = sentences.OrderBy(s => s.SortOrder).ToList();
        foreach (var s in ordered)
        {
            html.Append($"<div class=\"jiten-sentence\" data-id=\"{s.MinedSentenceId}\" data-origin=\"{Attr(s.Origin)}\"");
            if (s == ordered[0]) html.Append(" data-main=\"1\"");
            if (!string.IsNullOrEmpty(s.SourceTitle)) html.Append($" data-source=\"{Attr(s.SourceTitle)}\"");
            html.Append('>');
            html.Append($"<div class=\"jiten-sentence-text\">{TargetWordMarkup.Apply(s.Shown, targetTemplate)}</div>");
            if (!string.IsNullOrEmpty(s.Furigana)) html.Append($"<div class=\"jiten-sentence-furigana\">{TargetWordMarkup.Apply(s.Furigana, targetTemplate)}</div>");
            if (!string.IsNullOrEmpty(s.English)) html.Append($"<div class=\"jiten-sentence-english\">{s.English}</div>");
            if (!string.IsNullOrEmpty(s.AnkiAudioFilename))
                html.Append($"<audio class=\"jiten-sentence-audio\" src=\"{Attr(s.AnkiAudioFilename)}\" preload=\"none\"></audio>");
            if (!string.IsNullOrEmpty(s.AnkiPictureFilename))
                html.Append($"<img class=\"jiten-sentence-picture\" src=\"{Attr(s.AnkiPictureFilename)}\" alt=\"\" loading=\"lazy\">");
            html.Append("</div>");
        }

        return html.Append("</div>").ToString();
    }

    /// <summary>The plain sentence list: every sentence with its English, for cards without the rotation script.</summary>
    public static string RenderPlainList(IEnumerable<MinedSentence> sentences, string targetTemplate = TargetWordMarkup.Default)
    {
        var html = new StringBuilder("<ol class=\"jiten-sentence-list\">");
        foreach (var s in sentences.OrderBy(s => s.SortOrder))
        {
            html.Append("<li>").Append(TargetWordMarkup.Apply(s.Shown, targetTemplate));
            if (!string.IsNullOrEmpty(s.English)) html.Append("<br><small>").Append(s.English).Append("</small>");
            html.Append("</li>");
        }

        return html.Append("</ol>").ToString();
    }

    private static string Attr(string value) => WebUtility.HtmlEncode(value);

    private static DateTime CreatedAt(long ankiNoteId) => DateTimeOffset.FromUnixTimeMilliseconds(ankiNoteId).UtcDateTime;
}
