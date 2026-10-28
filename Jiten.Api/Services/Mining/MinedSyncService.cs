using System.Net;
using System.Text;
using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <param name="Ready">False while the note is too new to fold: GSM may still be adding its audio and screenshot.</param>
public record FoldNoteDto(long AnkiNoteId, string DeckName, DateTime CreatedAt, bool Ready, string? Sentence);

/// <param name="Keep">The note that stays: the oldest, which carries the review history.</param>
public record FoldCandidateDto(long MinedWordId, string Expression, string Reading, long Keep, List<FoldNoteDto> Notes);

public record FoldRequest(long MinedWordId, long KeepAnkiNoteId, List<long> FoldedAnkiNoteIds);

/// <summary>A flag tag read from an Anki note: <c>sentence-fix::&lt;minedSentenceId&gt;</c>.</summary>
public record SentenceFlagDto(long AnkiNoteId, long MinedSentenceId);

public record FlagsRequest(List<SentenceFlagDto> Flags);

/// <summary>What Sync writes to a word's note: the full sentence list for the card's script to pick from.</summary>
public record NoteSentencesDto(long AnkiNoteId, string SentencesHtml);

/// <summary>
/// Sync between the sentence store and Anki. The browser does the AnkiConnect half (reading tags, deleting folded notes,
/// writing fields); this decides what to do and records it.
/// </summary>
public class MinedSyncService(UserDbContext userContext)
{
    public const string FlagTagPrefix = "sentence-fix::";

    /// <summary>How old a note must be before it can be folded, so GSM has finished enhancing it.</summary>
    public static readonly TimeSpan FoldDelay = TimeSpan.FromMinutes(5);

    public async Task<List<FoldCandidateDto>> FoldCandidatesAsync(string userId, CancellationToken ct)
    {
        var words = await userContext.MinedWords.AsNoTracking().AsSplitQuery()
                                     .Where(w => w.UserId == userId && w.Notes.Count > 1)
                                     .Include(w => w.Notes).Include(w => w.Sentences)
                                     .OrderBy(w => w.Expression)
                                     .ToListAsync(ct);
        var now = DateTime.UtcNow;

        return words.Select(w =>
        {
            var notes = w.Notes.OrderBy(n => n.AnkiNoteId).ToList();
            return new FoldCandidateDto(w.MinedWordId, w.Expression, w.Reading, notes[0].AnkiNoteId, notes.Select(n =>
            {
                var created = CreatedAt(n.AnkiNoteId);
                var sentence = w.Sentences.FirstOrDefault(s => s.SourceAnkiNoteId == n.AnkiNoteId)?.Text;
                return new FoldNoteDto(n.AnkiNoteId, n.DeckName, created, now - created >= FoldDelay, sentence);
            }).ToList());
        }).ToList();
    }

    /// <summary>
    /// Records a fold the user confirmed and the browser carried out (the folded notes are already deleted in Anki): the
    /// notes are dropped, and their sentences stay with the word.
    /// </summary>
    public async Task<bool> FoldAsync(string userId, FoldRequest request, CancellationToken ct)
    {
        var notes = await userContext.MinedNotes.Where(n => n.UserId == userId && n.MinedWordId == request.MinedWordId).ToListAsync(ct);
        if (notes.All(n => n.AnkiNoteId != request.KeepAnkiNoteId) || request.FoldedAnkiNoteIds.Contains(request.KeepAnkiNoteId)) return false;

        var folded = notes.Where(n => request.FoldedAnkiNoteIds.Contains(n.AnkiNoteId)).ToList();
        if (folded.Count != request.FoldedAnkiNoteIds.Distinct().Count()) return false;

        userContext.MinedNotes.RemoveRange(folded);
        var word = await userContext.MinedWords.FirstAsync(w => w.MinedWordId == request.MinedWordId, ct);
        word.UpdatedAt = DateTime.UtcNow;
        await userContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Marks flagged sentences; returns the flags that named a sentence on that note's word, so their tags can be removed.</summary>
    public async Task<List<SentenceFlagDto>> ApplyFlagsAsync(string userId, IReadOnlyList<SentenceFlagDto> flags, CancellationToken ct)
    {
        var noteIds = flags.Select(f => f.AnkiNoteId).Distinct().ToList();
        var wordByNote = await userContext.MinedNotes.AsNoTracking().Where(n => n.UserId == userId && noteIds.Contains(n.AnkiNoteId))
                                          .ToDictionaryAsync(n => n.AnkiNoteId, n => n.MinedWordId, ct);
        var sentenceIds = flags.Select(f => f.MinedSentenceId).Distinct().ToList();
        var sentences = await userContext.MinedSentences.Where(s => s.UserId == userId && sentenceIds.Contains(s.MinedSentenceId)).ToListAsync(ct);

        var applied = new List<SentenceFlagDto>();
        foreach (var flag in flags)
        {
            var sentence = sentences.FirstOrDefault(s => s.MinedSentenceId == flag.MinedSentenceId);
            if (sentence == null || wordByNote.GetValueOrDefault(flag.AnkiNoteId) != sentence.MinedWordId) continue;
            sentence.FlaggedAt ??= DateTime.UtcNow;
            sentence.UpdatedAt = DateTime.UtcNow;
            applied.Add(flag);
        }

        await userContext.SaveChangesAsync(ct);
        return applied;
    }

    /// <summary>The Sentences field for each word's kept note (the oldest), for the words that have sentences.</summary>
    public async Task<List<NoteSentencesDto>> NoteSentencesAsync(string userId, CancellationToken ct)
    {
        var words = await userContext.MinedWords.AsNoTracking().AsSplitQuery()
                                     .Where(w => w.UserId == userId && w.Notes.Any() && w.Sentences.Any())
                                     .Include(w => w.Notes).Include(w => w.Sentences)
                                     .ToListAsync(ct);
        return words.Select(w => new NoteSentencesDto(w.Notes.Min(n => n.AnkiNoteId), RenderSentences(w.Sentences))).ToList();
    }

    /// <summary>
    /// The Sentences field: one block per sentence for Lapis+'s script, which shows a random one on each review. Media are
    /// real <c>&lt;audio src&gt;</c> / <c>&lt;img src&gt;</c> references (not file names in data), so Anki's Check Media
    /// counts the files as used; the template keeps the blocks hidden.
    /// </summary>
    public static string RenderSentences(IEnumerable<MinedSentence> sentences)
    {
        var html = new StringBuilder("<div class=\"jiten-sentences\">");
        foreach (var s in sentences.OrderBy(s => s.SortOrder))
        {
            html.Append($"<div class=\"jiten-sentence\" data-id=\"{s.MinedSentenceId}\" data-origin=\"{Attr(s.Origin)}\"");
            if (s.IsMain) html.Append(" data-main=\"1\"");
            if (!string.IsNullOrEmpty(s.SourceTitle)) html.Append($" data-source=\"{Attr(s.SourceTitle)}\"");
            html.Append('>');
            html.Append($"<div class=\"jiten-sentence-text\">{s.Text}</div>");
            if (!string.IsNullOrEmpty(s.Furigana)) html.Append($"<div class=\"jiten-sentence-furigana\">{s.Furigana}</div>");
            if (!string.IsNullOrEmpty(s.English)) html.Append($"<div class=\"jiten-sentence-english\">{s.English}</div>");
            if (!string.IsNullOrEmpty(s.AnkiAudioFilename))
                html.Append($"<audio class=\"jiten-sentence-audio\" src=\"{Attr(s.AnkiAudioFilename)}\" preload=\"none\"></audio>");
            if (!string.IsNullOrEmpty(s.AnkiPictureFilename))
                html.Append($"<img class=\"jiten-sentence-picture\" src=\"{Attr(s.AnkiPictureFilename)}\" alt=\"\" loading=\"lazy\">");
            html.Append("</div>");
        }

        return html.Append("</div>").ToString();
    }

    private static string Attr(string value) => WebUtility.HtmlEncode(value);

    private static DateTime CreatedAt(long ankiNoteId) => DateTimeOffset.FromUnixTimeMilliseconds(ankiNoteId).UtcDateTime;
}
