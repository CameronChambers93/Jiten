using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <summary>An Anki note as the browser read it through AnkiConnect, with its media files when the note has them.</summary>
public record ImportNoteDto(
    long AnkiNoteId,
    string ModelName,
    string DeckName,
    List<string> Tags,
    string Expression,
    string Reading,
    string Sentence,
    string? English,
    string? PreviousLine,
    string? Url,
    string? MiscInfo,
    string? AudioFilename,
    string? AudioBase64,
    string? PictureFilename,
    string? PictureBase64);

public record ImportNotesRequest(List<ImportNoteDto> Notes);

/// <param name="Status">"imported", "already" (the note was imported before) or "failed".</param>
public record ImportNoteResult(long AnkiNoteId, string Status, long? MinedWordId, bool Matched, string? Origin, string? Error);

public record MinedWordSummaryDto(long MinedWordId, int? WordId, byte? ReadingIndex, string Expression, string Reading, int Sentences, int Notes,
                                  int Flagged, string? MainSentence, DateTime UpdatedAt);

public record MinedWordsPage(List<MinedWordSummaryDto> Words, int Total);

public record MinedNoteDto(long AnkiNoteId, string ModelName, string DeckName, DateTime ImportedAt);

/// <param name="AudioUrl">The stored copy's URL; null for Jiten sentences (cut from the episode on demand) and sentences without audio.</param>
public record MinedSentenceDto(long MinedSentenceId, string Text, string? English, string? EnglishOrigin, string Origin, long? ExampleSentenceId,
                               string? SourceTitle, string? SourceUrl, string? PreviousLine, DateTime? MinedAt, string? AudioUrl, string? PictureUrl,
                               bool IsMain, int SortOrder, DateTime? FlaggedAt);

public record MinedWordDto(long MinedWordId, int? WordId, byte? ReadingIndex, string Expression, string Reading, List<MinedNoteDto> Notes,
                           List<MinedSentenceDto> Sentences);

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
    IDbContextFactory<JitenDbContext> contextFactory,
    ICdnService cdn,
    ILogger<MinedSentenceService> logger)
{
    public const int MaxNotesPerImport = 20;
    public const long MaxMediaBytes = 5L * 1024 * 1024;
    public const int PageSize = 50;

    private static readonly TimeSpan MediaUrlTtl = TimeSpan.FromHours(6);

    public async Task<List<ImportNoteResult>> ImportAsync(string userId, IReadOnlyList<ImportNoteDto> notes, CancellationToken ct)
    {
        var noteIds = notes.Select(n => n.AnkiNoteId).ToList();
        var known = (await userContext.MinedNotes.Where(n => n.UserId == userId && noteIds.Contains(n.AnkiNoteId))
                                      .Select(n => new { n.AnkiNoteId, n.MinedWordId }).ToListAsync(ct))
                    .ToDictionary(n => n.AnkiNoteId, n => n.MinedWordId);

        var pairs = notes.Select(n => (Word: PlainText(n.Expression), Reading: PlainText(n.Reading)))
                         .Where(p => p.Word.Length > 0).Distinct().ToList();
        var lookup = pairs.Count == 0
            ? new Dictionary<(string Word, string Reading), Core.Data.DeckWord>()
            : await Parser.Parser.GetWordsDirectLookupByReading(contextFactory, pairs);

        var results = new List<ImportNoteResult>();
        foreach (var note in notes)
        {
            if (known.TryGetValue(note.AnkiNoteId, out var existingWordId))
            {
                results.Add(new ImportNoteResult(note.AnkiNoteId, "already", existingWordId, false, null, null));
                continue;
            }

            try
            {
                var expression = PlainText(note.Expression);
                if (expression.Length == 0) throw new ArgumentException("The note has no word.");
                var reading = PlainText(note.Reading);
                var match = lookup.GetValueOrDefault((expression, reading));

                var word = await FindOrCreateWordAsync(userId, expression, reading, match?.WordId, match?.ReadingIndex, ct);
                word.UpdatedAt = DateTime.UtcNow;
                userContext.MinedNotes.Add(new MinedNote
                {
                    MinedWordId = word.MinedWordId, UserId = userId, AnkiNoteId = note.AnkiNoteId,
                    ModelName = Truncate(note.ModelName, 200), DeckName = Truncate(note.DeckName, 200)
                });

                var origin = DetectedOrigin.From(note);
                if (PlainText(note.Sentence).Length > 0)
                    await AddSentenceAsync(userId, word, note, origin, ct);

                await userContext.SaveChangesAsync(ct);
                results.Add(new ImportNoteResult(note.AnkiNoteId, "imported", word.MinedWordId, match != null, origin.Origin, null));
            }
            catch (Exception e) when (e is ArgumentException or FormatException or DbUpdateException)
            {
                userContext.ChangeTracker.Clear();
                logger.LogWarning(e, "Couldn't import Anki note {NoteId}", note.AnkiNoteId);
                results.Add(new ImportNoteResult(note.AnkiNoteId, "failed", null, false, null, e is DbUpdateException ? "Couldn't save the note." : e.Message));
            }
        }

        return results;
    }

    public async Task<MinedWordsPage> ListAsync(string userId, string? search, int page, CancellationToken ct)
    {
        var query = userContext.MinedWords.AsNoTracking().Where(w => w.UserId == userId);
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
                                   w.Sentences.Count, w.Notes.Count, w.Sentences.Count(s => s.FlaggedAt != null),
                                   w.Sentences.Where(s => s.IsMain).Select(s => s.Text).FirstOrDefault(), w.UpdatedAt))
                               .ToListAsync(ct);
        return new MinedWordsPage(words, total);
    }

    public async Task<MinedWordDto?> GetAsync(string userId, long minedWordId, CancellationToken ct)
    {
        var word = await userContext.MinedWords.AsNoTracking().AsSplitQuery()
                                    .Include(w => w.Notes).Include(w => w.Sentences)
                                    .FirstOrDefaultAsync(w => w.UserId == userId && w.MinedWordId == minedWordId, ct);
        if (word == null) return null;

        return new MinedWordDto(
            word.MinedWordId, word.WordId, word.ReadingIndex, word.Expression, word.Reading,
            word.Notes.OrderBy(n => n.AnkiNoteId).Select(n => new MinedNoteDto(n.AnkiNoteId, n.ModelName, n.DeckName, n.ImportedAt)).ToList(),
            word.Sentences.OrderBy(s => s.SortOrder).Select(s => new MinedSentenceDto(
                s.MinedSentenceId, s.Text, s.English, s.EnglishOrigin, s.Origin, s.ExampleSentenceId, s.SourceTitle, s.SourceUrl, s.PreviousLine,
                s.MinedAt, s.AudioPath == null ? null : cdn.GetSignedUrl(s.AudioPath, MediaUrlTtl),
                s.PicturePath == null ? null : cdn.GetSignedUrl(s.PicturePath, MediaUrlTtl), s.IsMain, s.SortOrder, s.FlaggedAt)).ToList());
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

    private async Task AddSentenceAsync(string userId, MinedWord word, ImportNoteDto note, DetectedOrigin origin, CancellationToken ct)
    {
        var existing = await userContext.MinedSentences.Where(s => s.MinedWordId == word.MinedWordId)
                                        .Select(s => (int?)s.SortOrder).MaxAsync(ct);
        var english = PlainText(note.English ?? "").Length > 0 ? note.English!.Trim() : null;
        var previous = PlainText(note.PreviousLine ?? "");

        var sentence = new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = userId,
            Text = Truncate(note.Sentence.Trim(), 4000),
            English = english == null ? null : Truncate(english, 4000), EnglishOrigin = english == null ? null : "anki",
            Origin = origin.Origin, ExampleSentenceId = origin.ExampleSentenceId,
            SourceTitle = origin.SourceTitle == null ? null : Truncate(origin.SourceTitle, 300),
            SourceUrl = origin.SourceUrl == null ? null : Truncate(origin.SourceUrl, 2000),
            PreviousLine = previous.Length > 0 ? Truncate(previous, 4000) : null,
            MinedAt = DateTimeOffset.FromUnixTimeMilliseconds(note.AnkiNoteId).UtcDateTime,
            AnkiAudioFilename = note.AudioFilename, AnkiPictureFilename = note.PictureFilename,
            SourceAnkiNoteId = note.AnkiNoteId,
            IsMain = existing == null,
            SortOrder = (existing ?? -1) + 1,
        };

        // A Jiten sentence's media is cut again from the episode; anything else only exists as the copy on the note.
        if (origin.Origin != MinedSentenceOrigin.Jiten)
        {
            (sentence.AudioPath, sentence.AudioContentType) = await StoreAsync(userId, note.AudioBase64, CardMediaKind.Audio, ct);
            (sentence.PicturePath, sentence.PictureContentType) = await StoreAsync(userId, note.PictureBase64, CardMediaKind.Image, ct);
        }

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

    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

    /// <summary>A field's text without tags or entities.</summary>
    public static string PlainText(string html) =>
        System.Net.WebUtility.HtmlDecode(Tags.Replace(html.Replace("<br>", " "), "")).Replace(' ', ' ').Trim();

    /// <summary>A URL field's link: the anchor's href, or the text when it holds a bare URL.</summary>
    public static string LinkOf(string field)
    {
        var href = Regex.Match(field, "href=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return href.Success ? System.Net.WebUtility.HtmlDecode(href.Groups[1].Value).Trim() : PlainText(field);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
