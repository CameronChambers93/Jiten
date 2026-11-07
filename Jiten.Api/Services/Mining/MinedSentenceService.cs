using System.Text.RegularExpressions;
using Jiten.Api.Dtos;
using Hangfire;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <summary>An Anki note as the browser read it through AnkiConnect, with its media files when the note has them.</summary>
/// <param name="Text">Every field of the note, which decides between entries sharing the word and reading.</param>
public record ImportNoteDto(
    long AnkiNoteId,
    string ModelName,
    string DeckName,
    List<string> Tags,
    string Expression,
    string Reading,
    string Sentence,
    string? Furigana,
    string? English,
    string? PreviousLine,
    string? Url,
    string? MiscInfo,
    string? AudioFilename,
    string? AudioBase64,
    string? PictureFilename,
    string? PictureBase64,
    string? Text = null,
    string? VideoFilename = null);

public record ImportNotesRequest(List<ImportNoteDto> Notes);

/// <param name="Status">"imported", "already" (the note was imported before), "sentence" (it was, without a sentence of its own, and now has it) or "failed".</param>
/// <param name="WordMatch">How the note's word was chosen, when this import chose it (<see cref="MinedWordMatch"/>).</param>
/// <param name="Moved">A stored note checked for the first time turned out to be another word, and moved to it.</param>
public record ImportNoteResult(long AnkiNoteId, string Status, long? MinedWordId, bool Matched, string? Origin, string? Error,
                               string? WordMatch = null, bool Moved = false);

public record MinedWordSummaryDto(long MinedWordId, int? WordId, byte? ReadingIndex, string Expression, string Reading, int Sentences, int Notes,
                                  int Flagged, string? MainSentence, DateTime UpdatedAt, int Guessed = 0);

public record MinedWordsPage(List<MinedWordSummaryDto> Words, int Total);

/// <param name="WordMatch">How the note's word was chosen (<see cref="MinedWordMatch"/>); null until checked.</param>
public record MinedNoteDto(long AnkiNoteId, string ModelName, string DeckName, DateTime ImportedAt, string? WordMatch);

/// <param name="WordId">The entry the note is on now.</param>
public record NoteCandidatesDto(int? WordId, byte? ReadingIndex, string? WordMatch, List<WordCandidateDto> Candidates);

public record SetNoteWordRequest(int WordId, byte ReadingIndex);

/// <param name="MinedWordId">The word the note is on now.</param>
public record SetNoteWordResult(long MinedWordId, bool Moved);

/// <param name="AudioUrl">The stored copy's URL; null for Jiten sentences (cut from the episode on demand) and sentences without audio.</param>
public record MinedSentenceDto(long MinedSentenceId, string Text, string? English, string? EnglishOrigin, string Origin, long? ExampleSentenceId,
                               string? SourceTitle, string? SourceUrl, string? PreviousLine, DateTime? MinedAt, string? AudioUrl, string? PictureUrl,
                               bool IsMain, int SortOrder, bool Flagged);

public record MinedWordDto(long MinedWordId, int? WordId, byte? ReadingIndex, string Expression, string Reading, List<MinedNoteDto> Notes,
                           List<MinedSentenceDto> Sentences);

/// <summary>One of Jiten's example sentences to add to a word's existing Anki note, its media already stored in Anki.</summary>
/// <param name="AnkiNoteId">A note of the word, as found in Anki; the sentence goes to the word, so any of its notes will do.</param>
/// <param name="Sentence">The sentence as card HTML, the word in bold.</param>
/// <param name="EnglishOrigin">"subtitles" or "ai".</param>
public record AddSentenceRequest(long AnkiNoteId, long ExampleSentenceId, string Sentence, string? Furigana, string? English, string? EnglishOrigin,
                                 string? SourceTitle, string? AudioFilename, string? PictureFilename, string? VideoFilename = null);

/// <summary>
/// A note the browser just created in Anki for one of Jiten's example sentences, its fields and media already written. The
/// sentence is in the store's form, as <see cref="AddSentenceRequest"/> sends it, before the target template.
/// </summary>
public record CreatedNoteRequest(long AnkiNoteId, string ModelName, string DeckName, int? WordId, byte? ReadingIndex, string Expression, string Reading,
                                 long ExampleSentenceId, string Sentence, string? Furigana, string? English, string? EnglishOrigin, string? SourceTitle,
                                 string? AudioFilename, string? PictureFilename, string? VideoFilename = null)
{
    public AddSentenceRequest AsSentence() =>
        new(AnkiNoteId, ExampleSentenceId, Sentence, Furigana, English, EnglishOrigin, SourceTitle, AudioFilename, PictureFilename, VideoFilename);
}

/// <param name="AnkiNoteId">The word's kept note (the oldest), which <paramref name="SentencesHtml"/> is for.</param>
/// <param name="Added">False when the word already had the sentence; nothing changed.</param>
/// <param name="Sentences">The word's sentences, counting this one.</param>
/// <param name="Write">What to write to the kept note now; null when its note type has no setup or nothing to write.</param>
public record AddSentenceResult(long AnkiNoteId, long MinedSentenceId, bool Added, int Sentences, NoteWriteDto? Write);

/// <summary>Where an imported note's sentence came from, read from its tags, media file names and URL.</summary>
public record DetectedOrigin(string Origin, long? ExampleSentenceId, string? SourceTitle, string? SourceUrl)
{
    private static readonly Regex JitenMedia = new(@"^jiten_(\d+)\.", RegexOptions.Compiled);

    public static DetectedOrigin From(ImportNoteDto note)
    {
        var misc = MinedSentenceService.PlainText(note.MiscInfo ?? "");
        var url = Uri.TryCreate(MinedSentenceService.LinkOf(note.Url ?? ""), UriKind.Absolute, out var parsed) ? parsed : null;

        // Jiten names the media it adds after the example sentence it cut it from.
        foreach (var file in new[] { note.AudioFilename, note.PictureFilename })
            if (file != null && JitenMedia.Match(file) is { Success: true } match)
                return new DetectedOrigin(MinedSentenceOrigin.Jiten, long.Parse(match.Groups[1].Value), misc.Length > 0 ? misc : null, null);

        // GSM tags its notes with the OBS scene, which names the game.
        var game = note.Tags.FirstOrDefault(t => t.StartsWith("Game::", StringComparison.OrdinalIgnoreCase));
        if (game != null) return new DetectedOrigin(MinedSentenceOrigin.Game, null, game["Game::".Length..].Replace('_', ' '), url?.ToString());

        var local = url == null || url.IsFile || url.IsLoopback || url.Host == "jiten.local";
        if (url != null && !local) return new DetectedOrigin(MinedSentenceOrigin.Web, null, url.Host, url.ToString());
        if (misc.Equals("Texthooker", StringComparison.OrdinalIgnoreCase) || url?.AbsolutePath.Contains("texthooker", StringComparison.OrdinalIgnoreCase) == true)
            return new DetectedOrigin(MinedSentenceOrigin.Game, null, null, null);

        return new DetectedOrigin(MinedSentenceOrigin.Other, null, misc.Length > 0 ? misc : null, url?.IsFile == true ? url.ToString() : null);
    }
}

/// <summary>
/// n1miner's sentence store: the user's mined words and their sentences, imported from Anki notes. GSM's media is copied
/// into Jiten's storage, since a game has no video to cut it from again.
/// </summary>
public class MinedSentenceService(
    UserDbContext userContext,
    MinedSyncService sync,
    SentenceFlagService sentenceFlags,
    IDbContextFactory<JitenDbContext> contextFactory,
    ICdnService cdn,
    IBackgroundJobClient backgroundJobs,
    WordMatchService wordMatch,
    ILogger<MinedSentenceService> logger)
{
    public const int MaxNotesPerImport = 20;
    public const long MaxMediaBytes = 5L * 1024 * 1024;
    public const int PageSize = 50;

    private static readonly TimeSpan MediaUrlTtl = TimeSpan.FromHours(6);

    public async Task<List<ImportNoteResult>> ImportAsync(string userId, IReadOnlyList<ImportNoteDto> notes, CancellationToken ct)
    {
        notes = notes.DistinctBy(n => n.AnkiNoteId).ToList();
        var noteIds = notes.Select(n => n.AnkiNoteId).ToList();
        var known = await userContext.MinedNotes.Where(n => n.UserId == userId && noteIds.Contains(n.AnkiNoteId)).ToDictionaryAsync(n => n.AnkiNoteId, ct);

        // New notes have their word chosen, and stored notes never checked are checked once (when the browser sent their text).
        var toMatch = notes.Where(n => !known.TryGetValue(n.AnkiNoteId, out var stored) || (stored.WordMatch == null && n.Text != null)).ToList();
        var matched = await wordMatch.MatchAsync(toMatch.Select(n => new WordMatchRequest(PlainText(n.Expression), PlainText(n.Reading), n.Text)).ToList(), ct);
        var matchOf = toMatch.Select((n, i) => (n.AnkiNoteId, Match: matched[i])).ToDictionary(x => x.AnkiNoteId, x => x.Match);

        var results = new List<ImportNoteResult>();
        foreach (var note in notes)
        {
            if (known.TryGetValue(note.AnkiNoteId, out var storedNote))
            {
                // Tracked again if an earlier note's failure cleared the context.
                storedNote = await userContext.MinedNotes.FirstAsync(n => n.MinedNoteId == storedNote.MinedNoteId, ct);
                string? checkedAs = null;
                var moved = false;
                try
                {
                    if (matchOf.TryGetValue(note.AnkiNoteId, out var match))
                    {
                        moved = await CheckStoredNoteAsync(userId, storedNote, match, ct);
                        checkedAs = storedNote.WordMatch;
                    }

                    // A note recorded before its note type mapped a sentence (Core 2000's notes, synced for their list only) gets
                    // its own sentence now, first in the rotation.
                    var ownSentence = await userContext.MinedSentences.AnyAsync(s => s.UserId == userId && s.SourceAnkiNoteId == note.AnkiNoteId, ct);
                    if (ownSentence || PlainText(note.Sentence).Length == 0)
                    {
                        results.Add(new ImportNoteResult(note.AnkiNoteId, "already", storedNote.MinedWordId, false, null, null, checkedAs, moved));
                        continue;
                    }

                    var knownWord = await userContext.MinedWords.FirstAsync(w => w.MinedWordId == storedNote.MinedWordId, ct);
                    var ownOrigin = DetectedOrigin.From(note);
                    await AddSentenceAsync(userId, knownWord, note, ownOrigin, ct, first: true);
                    knownWord.UpdatedAt = DateTime.UtcNow;
                    await userContext.SaveChangesAsync(ct);
                    results.Add(new ImportNoteResult(note.AnkiNoteId, "sentence", storedNote.MinedWordId, true, ownOrigin.Origin, null, checkedAs, moved));
                }
                catch (Exception e) when (e is ArgumentException or FormatException or DbUpdateException)
                {
                    userContext.ChangeTracker.Clear();
                    logger.LogWarning(e, "Couldn't import the sentence of Anki note {NoteId}", note.AnkiNoteId);
                    results.Add(new ImportNoteResult(note.AnkiNoteId, "failed", storedNote.MinedWordId, false, null,
                                                     e is DbUpdateException ? "Couldn't save the note's sentence." : e.Message));
                }

                continue;
            }

            try
            {
                var expression = PlainText(note.Expression);
                if (expression.Length == 0) throw new ArgumentException("The note has no word.");
                var reading = PlainText(note.Reading);
                var match = matchOf[note.AnkiNoteId];

                var word = await FindOrCreateWordAsync(userId, expression, reading, match?.WordId, match?.ReadingIndex, ct);
                word.UpdatedAt = DateTime.UtcNow;
                var status = match?.Status ?? MinedWordMatch.None;
                userContext.MinedNotes.Add(new MinedNote
                {
                    MinedWordId = word.MinedWordId, UserId = userId, AnkiNoteId = note.AnkiNoteId,
                    ModelName = Truncate(note.ModelName, 200), DeckName = Truncate(note.DeckName, 200), WordMatch = status
                });

                var origin = DetectedOrigin.From(note);
                if (PlainText(note.Sentence).Length > 0)
                    await AddSentenceAsync(userId, word, note, origin, ct);

                await userContext.SaveChangesAsync(ct);
                results.Add(new ImportNoteResult(note.AnkiNoteId, "imported", word.MinedWordId, match != null, origin.Origin, null, status));
            }
            catch (Exception e) when (e is ArgumentException or FormatException or DbUpdateException)
            {
                userContext.ChangeTracker.Clear();
                logger.LogWarning(e, "Couldn't import Anki note {NoteId}", note.AnkiNoteId);
                results.Add(new ImportNoteResult(note.AnkiNoteId, "failed", null, false, null, e is DbUpdateException ? "Couldn't save the note." : e.Message));
            }
        }

        if (results.Any(r => r.Status is "imported" or "sentence"))
            backgroundJobs.Enqueue<SentenceParseJob>(job => job.ParsePending(CancellationToken.None));
        return results;
    }

    /// <summary>
    /// Records how a stored note's word is matched, the first time it's checked, and moves the note to the matched entry's
    /// word when that's another one. A note whose word has a fold under way is left unchecked until the fold is done.
    /// Returns whether the note moved.
    /// </summary>
    private async Task<bool> CheckStoredNoteAsync(string userId, MinedNote note, WordMatchResult? match, CancellationToken ct)
    {
        if (match == null)
        {
            // No entry fits now; a word that has one keeps it, as it was matched (or mined) before.
            note.WordMatch = MinedWordMatch.None;
            await userContext.SaveChangesAsync(ct);
            return false;
        }

        await using var transaction = await userContext.Database.BeginTransactionAsync(ct);
        var outcome = await MoveNoteAsync(userId, note, match.WordId, match.ReadingIndex, ct);
        if (outcome == null) return false;
        note.WordMatch = match.Status;
        await userContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return outcome.Value;
    }

    /// <summary>
    /// Moves a note to the mined word of another entry (made if the user has none), with the sentences that came from it.
    /// When it was the word's last note, everything on the word goes with it and the word is removed. Returns whether it
    /// moved (false when it's on that entry already), or null when its word has a fold under way.
    /// </summary>
    private async Task<bool?> MoveNoteAsync(string userId, MinedNote note, int wordId, byte readingIndex, CancellationToken ct)
    {
        var source = await userContext.MinedWords.FirstAsync(w => w.MinedWordId == note.MinedWordId, ct);
        if (source.WordId == wordId && source.ReadingIndex == readingIndex) return false;
        var notes = await userContext.MinedNotes.Where(n => n.MinedWordId == source.MinedWordId).ToListAsync(ct);
        if (notes.Any(n => n.FoldingInto != null)) return null;
        if (await userContext.MinedNotes.AnyAsync(n => n.UserId == userId && n.FoldingInto != null && userContext.MinedWords.Any(
                                                      w => w.MinedWordId == n.MinedWordId && w.WordId == wordId && w.ReadingIndex == readingIndex), ct))
            return null;

        var last = notes.All(n => n.MinedNoteId == note.MinedNoteId);
        var target = await FindOrCreateWordAsync(userId, source.Expression, source.Reading, wordId, readingIndex, ct);
        var moving = await userContext.MinedSentences.Where(s => s.MinedWordId == source.MinedWordId && (last || s.SourceAnkiNoteId == note.AnkiNoteId))
                                      .OrderBy(s => s.SortOrder).ToListAsync(ct);
        var order = await userContext.MinedSentences.Where(s => s.MinedWordId == target.MinedWordId).MaxAsync(s => (int?)s.SortOrder, ct) ?? -1;
        foreach (var sentence in moving)
        {
            sentence.MinedWordId = target.MinedWordId;
            // The target mark stays: the word is where it was in the sentence, whichever entry it is.
            sentence.SortOrder = ++order;
        }

        note.MinedWordId = target.MinedWordId;
        (note.FieldsSentenceId, note.FieldsHash) = (null, null);
        target.UpdatedAt = DateTime.UtcNow;
        source.UpdatedAt = DateTime.UtcNow;
        await userContext.SaveChangesAsync(ct);

        if (last)
        {
            userContext.MinedWords.Remove(source);
            await userContext.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <summary>The entries a stored note's word could be, with the one it's on now; null when the note isn't stored.</summary>
    public async Task<NoteCandidatesDto?> NoteCandidatesAsync(string userId, long ankiNoteId, CancellationToken ct)
    {
        var note = await userContext.MinedNotes.AsNoTracking().FirstOrDefaultAsync(n => n.UserId == userId && n.AnkiNoteId == ankiNoteId, ct);
        if (note == null) return null;
        var word = await userContext.MinedWords.AsNoTracking().FirstAsync(w => w.MinedWordId == note.MinedWordId, ct);
        var candidates = await wordMatch.CandidatesAsync(word.Expression, word.Reading, ct);
        return new NoteCandidatesDto(word.WordId, word.ReadingIndex, note.WordMatch, candidates);
    }

    /// <summary>
    /// Puts a stored note on the entry the user picked (or confirms the one it's on), moving it with its sentences when
    /// that's another word. Null when the note isn't stored; throws when the entry isn't one of its candidates or a fold
    /// is under way.
    /// </summary>
    public async Task<SetNoteWordResult?> SetNoteWordAsync(string userId, long ankiNoteId, int wordId, byte readingIndex, CancellationToken ct)
    {
        var note = await userContext.MinedNotes.FirstOrDefaultAsync(n => n.UserId == userId && n.AnkiNoteId == ankiNoteId, ct);
        if (note == null) return null;
        var word = await userContext.MinedWords.AsNoTracking().FirstAsync(w => w.MinedWordId == note.MinedWordId, ct);
        var candidates = await wordMatch.CandidatesAsync(word.Expression, word.Reading, ct);
        if (!candidates.Any(c => c.WordId == wordId && c.ReadingIndex == readingIndex) && !(word.WordId == wordId && word.ReadingIndex == readingIndex))
            throw new ArgumentException("That entry isn't one this note's word could be.");

        await using var transaction = await userContext.Database.BeginTransactionAsync(ct);
        var moved = await MoveNoteAsync(userId, note, wordId, readingIndex, ct)
                    ?? throw new InvalidOperationException("A fold of this word or that one is under way; Sync finishes it first.");
        note.WordMatch = MinedWordMatch.Chosen;
        await userContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SetNoteWordResult(note.MinedWordId, moved);
    }

    /// <summary>
    /// Adds one of Jiten's example sentences to the word an Anki note belongs to, unless the word has it already. Null when
    /// the note isn't in the store; the browser imports it first.
    /// </summary>
    public async Task<AddSentenceResult?> AddToNoteAsync(string userId, AddSentenceRequest request, CancellationToken ct)
    {
        var note = await userContext.MinedNotes.AsNoTracking()
                                    .FirstOrDefaultAsync(n => n.UserId == userId && n.AnkiNoteId == request.AnkiNoteId, ct);
        if (note == null) return null;

        var text = PlainText(request.Sentence);
        if (text.Length == 0) throw new ArgumentException("The sentence is empty.");

        var word = await userContext.MinedWords.AsSplitQuery().Include(w => w.Notes).Include(w => w.Sentences)
                                    .FirstAsync(w => w.MinedWordId == note.MinedWordId, ct);
        await sync.FillAsync(userId, word.Sentences, ct);
        var sentence = word.Sentences.FirstOrDefault(s => s.ExampleSentenceId == request.ExampleSentenceId || PlainText(s.Shown) == text);
        var added = sentence == null;
        if (sentence == null)
        {
            sentence = NewJitenSentence(userId, word, request, null);
            await SaveNewSentenceAsync(word, sentence, ct);
        }

        var write = added ? (await sync.NoteWritesAsync(userId, word.MinedWordId, ct)).FirstOrDefault() : null;
        var kept = write?.AnkiNoteId ?? word.Notes.Where(n => n.FoldingInto == null).Min(n => n.AnkiNoteId);
        return new AddSentenceResult(kept, sentence.MinedSentenceId, added, word.Sentences.Count, write);
    }

    /// <summary>
    /// Records a note the browser just created for one of Jiten's example sentences, as an import of it would, but from the
    /// sentence as Jiten built it rather than as read back from the note. Its main fields are recorded as written, so Sync
    /// leaves them until the word's main sentence changes. A note already in the store is left as it is.
    /// </summary>
    public async Task<AddSentenceResult> RecordCreatedAsync(string userId, CreatedNoteRequest request, CancellationToken ct)
    {
        var text = PlainText(request.Sentence);
        if (text.Length == 0) throw new ArgumentException("The sentence is empty.");
        var expression = PlainText(request.Expression);
        if (expression.Length == 0) throw new ArgumentException("The note has no word.");

        var existing = await userContext.MinedNotes.AsNoTracking()
                                        .FirstOrDefaultAsync(n => n.UserId == userId && n.AnkiNoteId == request.AnkiNoteId, ct);
        if (existing != null)
        {
            // Recorded before (a retry), or imported by a Sync that ran in between.
            var known = await userContext.MinedWords.AsNoTracking().AsSplitQuery().Include(w => w.Notes).Include(w => w.Sentences)
                                         .FirstAsync(w => w.MinedWordId == existing.MinedWordId, ct);
            var own = known.Sentences.FirstOrDefault(s => s.SourceAnkiNoteId == request.AnkiNoteId) ?? known.Sentences.MinBy(s => s.SortOrder);
            var keptNote = known.Notes.Where(n => n.FoldingInto == null).Select(n => (long?)n.AnkiNoteId).Min() ?? request.AnkiNoteId;
            return new AddSentenceResult(keptNote, own?.MinedSentenceId ?? 0, false, known.Sentences.Count, null);
        }

        var word = await FindOrCreateWordAsync(userId, expression, PlainText(request.Reading), request.WordId, request.ReadingIndex, ct);
        await userContext.Entry(word).Collection(w => w.Notes).LoadAsync(ct);
        await userContext.Entry(word).Collection(w => w.Sentences).LoadAsync(ct);
        var note = new MinedNote
        {
            MinedWordId = word.MinedWordId, UserId = userId, AnkiNoteId = request.AnkiNoteId,
            ModelName = Truncate(request.ModelName, 200), DeckName = Truncate(request.DeckName, 200),
            // Mined for a known entry; without one, the next Sync checks it like an imported note.
            WordMatch = request.WordId != null ? MinedWordMatch.Mined : null
        };
        word.Notes.Add(note);

        // A word already in Anki may have this line already ("Create new" on it); the new note shows that sentence.
        await sync.FillAsync(userId, word.Sentences, ct);
        var sentence = word.Sentences.FirstOrDefault(s => s.ExampleSentenceId == request.ExampleSentenceId || PlainText(s.Shown) == text);
        var added = sentence == null;
        if (sentence == null)
        {
            sentence = NewJitenSentence(userId, word, request.AsSentence(), request.AnkiNoteId);
            await SaveNewSentenceAsync(word, sentence, ct);
        }
        else await userContext.SaveChangesAsync(ct);

        // The source title is the line's, not the row's: read it as Sync will, so the hash is the one Sync computes.
        (sentence.SourceTitle, sentence.SourceUrl) = (null, null);
        await sync.FillAsync(userId, [sentence], ct);
        var main = MinedSyncService.MainFields(sentence, await sync.TargetTemplateAsync(userId, ct));
        note.FieldsSentenceId = sentence.MinedSentenceId;
        note.FieldsHash = main.Hash;
        await userContext.SaveChangesAsync(ct);

        var write = (await sync.NoteWritesAsync(userId, word.MinedWordId, ct)).FirstOrDefault();
        var kept = write?.AnkiNoteId ?? word.Notes.Where(n => n.FoldingInto == null).Min(n => n.AnkiNoteId);
        // Sync leaves the new note's main fields alone while they show the word's main sentence. Then they come along as
        // Sync renders them, so the browser corrects any it wrote differently (the line may carry an earlier source title).
        // When the main sentence is another one, the write already holds it, as Sync would write it.
        if (kept == request.AnkiNoteId && write?.Main == null && await sync.UpdatesMainFieldsAsync(userId, note.ModelName, ct))
            write = (write ?? new NoteWriteDto(request.AnkiNoteId, note.ModelName, null, null)) with { Main = main };
        return new AddSentenceResult(kept, sentence.MinedSentenceId, added, word.Sentences.Count, write);
    }

    /// <summary>One of Jiten's example sentences for a word, last in its rotation, as both ways of mining it store it.</summary>
    private static MinedSentence NewJitenSentence(string userId, MinedWord word, AddSentenceRequest request, long? sourceAnkiNoteId)
    {
        var english = PlainText(request.English ?? "").Length > 0 ? Truncate(request.English!.Trim(), 4000) : null;
        return new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = userId,
            Text = Truncate(request.Sentence.Trim(), 4000),
            Furigana = PlainText(request.Furigana ?? "").Length > 0 ? Truncate(request.Furigana!.Trim(), 8000) : null,
            English = english,
            EnglishOrigin = english != null && request.EnglishOrigin is "subtitles" or "ai" ? request.EnglishOrigin : null,
            Origin = MinedSentenceOrigin.Jiten, ExampleSentenceId = request.ExampleSentenceId,
            SourceTitle = string.IsNullOrWhiteSpace(request.SourceTitle) ? null : Truncate(request.SourceTitle.Trim(), 300),
            MinedAt = DateTime.UtcNow,
            AnkiAudioFilename = string.IsNullOrWhiteSpace(request.AudioFilename) ? null : Truncate(request.AudioFilename, 500),
            AnkiPictureFilename = string.IsNullOrWhiteSpace(request.PictureFilename) ? null : Truncate(request.PictureFilename, 500),
            AnkiVideoFilename = string.IsNullOrWhiteSpace(request.VideoFilename) ? null : Truncate(request.VideoFilename, 300),
            SourceAnkiNoteId = sourceAnkiNoteId,
            SortOrder = word.Sentences.Count == 0 ? 0 : word.Sentences.Max(s => s.SortOrder) + 1,
        };
    }

    private async Task SaveNewSentenceAsync(MinedWord word, MinedSentence sentence, CancellationToken ct)
    {
        word.Sentences.Add(sentence);
        word.UpdatedAt = DateTime.UtcNow;
        await userContext.SaveChangesAsync(ct);

        await using var jiten = await contextFactory.CreateDbContextAsync(ct);
        if (sentence.ExampleSentenceId is { } line) await MinedLineStore.SetDeckSourceAsync(jiten, line, sentence.SourceTitle, ct);
    }

    /// <summary>The card's word fields for words picked out of a sentence: written form, furigana and first meaning.</summary>
    public async Task<List<WordSummaryDto>> WordSummariesAsync(IReadOnlyList<(int WordId, byte ReadingIndex)> words, CancellationToken ct)
    {
        var wordIds = words.Select(w => w.WordId).Distinct().ToList();
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var forms = await WordFormHelper.LoadWordForms(context, wordIds);
        var definitions = await context.JMDictWords.AsNoTracking()
                                       .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                       .Where(w => wordIds.Contains(w.WordId))
                                       .ToDictionaryAsync(w => w.WordId, ct);

        return words.Select(w => (w, Form: forms.GetValueOrDefault((w.WordId, (short)w.ReadingIndex))))
                    .Where(x => x.Form != null)
                    .Select(x => new WordSummaryDto
                    {
                        WordId = x.w.WordId, ReadingIndex = x.w.ReadingIndex, Reading = x.Form!.Text, ReadingFurigana = x.Form.RubyText,
                        MainDefinition = definitions.GetValueOrDefault(x.w.WordId)?.Definitions.FirstOrDefault()?.EnglishMeanings.FirstOrDefault(),
                    })
                    .ToList();
    }

    public Task<List<long>> NoteIdsAsync(string userId, CancellationToken ct) =>
        userContext.MinedNotes.AsNoTracking().Where(n => n.UserId == userId).Select(n => n.AnkiNoteId).ToListAsync(ct);

    /// <summary>Notes in the store that none of their word's sentences came from, such as notes synced for their list only.</summary>
    public Task<List<long>> NoteIdsWithoutSentenceAsync(string userId, CancellationToken ct) =>
        userContext.MinedNotes.AsNoTracking()
                   .Where(n => n.UserId == userId && n.WordMatch != MinedWordMatch.Mined
                               && !userContext.MinedSentences.Any(s => s.UserId == userId && s.SourceAnkiNoteId == n.AnkiNoteId))
                   .Select(n => n.AnkiNoteId).ToListAsync(ct);

    /// <summary>Notes whose word was never checked against the other entries sharing its word and reading.</summary>
    public Task<List<long>> UncheckedNoteIdsAsync(string userId, CancellationToken ct) =>
        userContext.MinedNotes.AsNoTracking().Where(n => n.UserId == userId && n.WordMatch == null).Select(n => n.AnkiNoteId).ToListAsync(ct);

    /// <param name="guessed">Only words with a note whose word was guessed.</param>
    public async Task<MinedWordsPage> ListAsync(string userId, string? search, int page, CancellationToken ct, bool guessed = false)
    {
        var query = userContext.MinedWords.AsNoTracking().Where(w => w.UserId == userId);
        if (guessed) query = query.Where(w => w.Notes.Any(n => n.WordMatch == MinedWordMatch.Guessed));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(w => w.Expression.Contains(term) || w.Reading.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var words = await query.OrderByDescending(w => w.UpdatedAt).ThenByDescending(w => w.MinedWordId)
                               .Skip(Math.Max(page, 0) * PageSize).Take(PageSize)
                               .Select(w => new MinedWordSummaryDto(
                                   w.MinedWordId, w.WordId, w.ReadingIndex, w.Expression, w.Reading,
                                   w.Sentences.Count, w.Notes.Count,
                                   w.Sentences.Count(s => userContext.SentenceFlags.Any(f => f.UserId == userId && f.ResolvedAt == null
                                                                                             && f.ExampleSentenceId == s.ExampleSentenceId)),
                                   null, w.UpdatedAt, w.Notes.Count(n => n.WordMatch == MinedWordMatch.Guessed)))
                               .ToListAsync(ct);

        var ids = words.Select(w => w.MinedWordId).ToList();
        var mains = await userContext.MinedSentences.AsNoTracking().Where(s => ids.Contains(s.MinedWordId) && !userContext.MinedSentences.Any(o => o.MinedWordId == s.MinedWordId && o.SortOrder < s.SortOrder)).ToListAsync(ct);
        await sync.FillAsync(userId, mains, ct);
        var shown = mains.ToLookup(s => s.MinedWordId, s => s.Shown);
        return new MinedWordsPage(words.Select(w => w with { MainSentence = shown[w.MinedWordId].FirstOrDefault() }).ToList(), total);
    }

    public async Task<MinedWordDto?> GetAsync(string userId, long minedWordId, CancellationToken ct)
    {
        var word = await userContext.MinedWords.AsNoTracking().AsSplitQuery()
                                    .Include(w => w.Notes).Include(w => w.Sentences)
                                    .FirstOrDefaultAsync(w => w.UserId == userId && w.MinedWordId == minedWordId, ct);
        if (word == null) return null;
        await sync.FillAsync(userId, word.Sentences, ct);
        var main = word.Sentences.MinBy(s => s.SortOrder);
        var flagged = await sentenceFlags.OpenSentenceIdsAsync(userId, ct);

        return new MinedWordDto(
            word.MinedWordId, word.WordId, word.ReadingIndex, word.Expression, word.Reading,
            word.Notes.OrderBy(n => n.AnkiNoteId).Select(n => new MinedNoteDto(n.AnkiNoteId, n.ModelName, n.DeckName, n.ImportedAt, n.WordMatch)).ToList(),
            word.Sentences.OrderBy(s => s.SortOrder).Select(s => new MinedSentenceDto(
                s.MinedSentenceId, s.Shown, s.English, s.EnglishOrigin, s.Origin, s.ExampleSentenceId, s.SourceTitle, s.SourceUrl, s.PreviousLine,
                s.MinedAt, s.AudioPath == null ? null : cdn.GetSignedUrl(s.AudioPath, MediaUrlTtl),
                s.PicturePath == null ? null : cdn.GetSignedUrl(s.PicturePath, MediaUrlTtl), s == main, s.SortOrder,
                s.ExampleSentenceId is { } id && flagged.Contains(id))).ToList());
    }

    private async Task<MinedWord> FindOrCreateWordAsync(string userId, string expression, string reading, int? wordId, byte? readingIndex,
                                                        CancellationToken ct)
    {
        var word = wordId != null
            ? await userContext.MinedWords.FirstOrDefaultAsync(w => w.UserId == userId && w.WordId == wordId && w.ReadingIndex == readingIndex, ct)
            : await userContext.MinedWords.FirstOrDefaultAsync(w => w.UserId == userId && w.WordId == null && w.Expression == expression
                                                                    && w.Reading == reading, ct);
        if (word != null) return word;

        word = new MinedWord
        {
            UserId = userId, WordId = wordId, ReadingIndex = readingIndex,
            Expression = Truncate(expression, 100), Reading = Truncate(reading, 100)
        };
        userContext.MinedWords.Add(word);
        await userContext.SaveChangesAsync(ct);
        return word;
    }

    /// <param name="first">Put the sentence before the word's others, as its main sentence, instead of after them.</param>
    private async Task AddSentenceAsync(string userId, MinedWord word, ImportNoteDto note, DetectedOrigin origin, CancellationToken ct, bool first = false)
    {
        var orders = userContext.MinedSentences.Where(s => s.MinedWordId == word.MinedWordId).Select(s => (int?)s.SortOrder);
        var existing = first ? await orders.MinAsync(ct) : await orders.MaxAsync(ct);
        var english = PlainText(note.English ?? "").Length > 0 ? note.English!.Trim() : null;
        var previous = PlainText(note.PreviousLine ?? "");

        var sentence = new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = userId,
            Text = Truncate(TargetWordMarkup.ToStored(note.Sentence.Trim()), 4000),
            Furigana = PlainText(note.Furigana ?? "").Length > 0 ? Truncate(TargetWordMarkup.ToStored(note.Furigana!.Trim()), 8000) : null,
            English = english == null ? null : Truncate(english, 4000), EnglishOrigin = english == null ? null : "anki",
            Origin = origin.Origin, ExampleSentenceId = origin.ExampleSentenceId,
            SourceTitle = origin.SourceTitle == null ? null : Truncate(origin.SourceTitle, 300),
            SourceUrl = origin.SourceUrl == null ? null : Truncate(origin.SourceUrl, 2000),
            PreviousLine = previous.Length > 0 ? Truncate(previous, 4000) : null,
            MinedAt = DateTimeOffset.FromUnixTimeMilliseconds(note.AnkiNoteId).UtcDateTime,
            AnkiAudioFilename = note.AudioFilename, AnkiPictureFilename = note.PictureFilename,
            AnkiVideoFilename = string.IsNullOrWhiteSpace(note.VideoFilename) ? null : Truncate(note.VideoFilename, 300),
            SourceAnkiNoteId = note.AnkiNoteId,
            SortOrder = first ? (existing ?? 1) - 1 : (existing ?? -1) + 1,
        };

        // A Jiten sentence's media is cut again from the episode; anything else only exists as the copy on the note.
        if (origin.Origin != MinedSentenceOrigin.Jiten)
        {
            (sentence.AudioPath, sentence.AudioContentType) = await StoreAsync(userId, note.AudioBase64, CardMediaKind.Audio, ct);
            (sentence.PicturePath, sentence.PictureContentType) = await StoreAsync(userId, note.PictureBase64, CardMediaKind.Image, ct);
        }

        var line = MinedLine.From(sentence.Text!, PlainText);
        (sentence.TargetPosition, sentence.TargetLength) = (line.TargetPosition, line.TargetLength);
        await using var jiten = await contextFactory.CreateDbContextAsync(ct);
        if (sentence.ExampleSentenceId is { } deckLine) await MinedLineStore.SetDeckSourceAsync(jiten, deckLine, sentence.SourceTitle, ct);
        else
        {
            sentence.ExampleSentenceId = await MinedLineStore.EnsureOccurrenceAsync(jiten, userId, line, sentence, ct);
        }

        // The occurrence now holds this text, unless the note's markup can't be rebuilt from it.
        await MinedLineStore.FillAsync(jiten, userId, [sentence], ct);
        if (sentence.OccurrenceHtml == sentence.Text) sentence.Text = null;

        userContext.MinedSentences.Add(sentence);
    }

    private async Task<(string? Path, string? ContentType)> StoreAsync(string userId, string? base64, CardMediaKind kind, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(base64)) return (null, null);

        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length > MaxMediaBytes) throw new ArgumentException($"A media file is over {MaxMediaBytes / 1024 / 1024} MB.");
        var sniff = CardMediaSniffer.Detect(bytes);
        if (sniff == null || sniff.Kind != kind) throw new ArgumentException($"The note's {kind.ToString().ToLowerInvariant()} isn't a supported file.");

        var path = $"mined-media/{userId}/{Guid.NewGuid():N}.{sniff.Extension}";
        ct.ThrowIfCancellationRequested();
        await cdn.UploadFile(bytes, path, secure: true);
        return (path, sniff.ContentType);
    }

    /// <summary>A field's text without tags or entities.</summary>
    public static string PlainText(string html) => MinedLineStore.PlainText(html);

    /// <summary>A URL field's link: the anchor's href, or the text when it holds a bare URL.</summary>
    public static string LinkOf(string field)
    {
        var href = Regex.Match(field, "href=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return href.Success ? System.Net.WebUtility.HtmlDecode(href.Groups[1].Value).Trim() : PlainText(field);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
