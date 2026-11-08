using Jiten.Api.Helpers;
using Jiten.Api.Services.Mining;
using Jiten.Api.Services.Translation;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Api.Services;

/// <summary>One version of a line's fixable parts; a null part isn't set at that level.</summary>
/// <param name="StartMs">The clip in subtitle time, padding included.</param>
public record FixPartsDto(string? Text, string? English, int? StartMs, int? EndMs, int? PictureMs);

/// <summary>A subtitle cue near the line, for picking the clip.</summary>
/// <param name="InClip">Most of the cue is inside the line's clip as the user sees it now.</param>
public record FixCueDto(int StartMs, int EndMs, string Text, string? English, bool InClip);

/// <summary>Another public line with the same text, which Submit can fix too.</summary>
public record SameTextLineDto(long SentenceId, string? Title, string? Episode);

/// <summary>One of the user's mined sentences on the line.</summary>
/// <param name="Removable">False when it's the only sentence the word's Anki card shows.</param>
public record FixMinedDto(long MinedSentenceId, string Expression, bool Removable);

public record FixFlagDto(long SentenceFlagId, string? Reason, string? Note);

/// <summary>Everything the fix screen shows for a line.</summary>
/// <param name="Text">The line's text as the user sees it now.</param>
/// <param name="Original">What the parser and subtitles give, before any fix: the clip is the cues with the default padding.</param>
/// <param name="Shared">The submitted fix, when there is one.</param>
/// <param name="Mine">The user's own fix, when there is one.</param>
/// <param name="HasVideo">The line has an episode with a linked video, so its clip and frame can be fixed.</param>
/// <param name="HasPage">The line is in a deck, so it has a sentence page (private lines don't).</param>
public record SentenceFixEditorDto(long SentenceId, string Text, string? Title, string? Episode, int? DeckId, bool HasVideo, bool HasPage,
                                   FixPartsDto Original, FixPartsDto? Shared, FixPartsDto? Mine, List<FixCueDto> Cues,
                                   List<SameTextLineDto> SameText, List<FixMinedDto> Mined, FixFlagDto? Flag);

/// <summary>The user's fix as a whole; a null part is "as the shared line". All null resets it.</summary>
public record SaveSentenceFixRequest(string? Text, string? English, int? StartMs, int? EndMs, int? PictureMs);

/// <param name="SameTextIds">Other lines with the same text to give the fixed text too.</param>
public record SubmitSentenceFixRequest(List<long>? SameTextIds);

public class SentenceFixException(string message) : Exception(message);

/// <summary>
/// Users' fixes of sentences: the fix screen's data, saving and resetting a user's own fix, and (admins, for now)
/// submitting it to the shared line. See the sentence flags and fixes design, build step 2.
/// </summary>
public class SentenceFixService(
    JitenDbContext context,
    UserDbContext userContext,
    SentenceFixStore store,
    SentenceCueLookup cueLookup,
    SentenceTranslationService translations,
    ISentenceTextParser parser,
    SentenceFlagService flags,
    ILogger<SentenceFixService> logger)
{
    public const int MaxTextLength = 1000;
    public const int MaxEnglishLength = 2000;
    public const int MaxClipMs = 30_000;

    /// <summary>The padding a card's clip gets by default, shown as the original clip's.</summary>
    private const int DefaultPadMs = 250;

    private const int NearbyCues = 3;
    private const int MaxSameText = 20;

    /// <summary>The line, when the user can see it: a deck's line, or their own private one.</summary>
    private Task<ExampleSentence?> LineAsync(string userId, long sentenceId, CancellationToken ct) =>
        context.ExampleSentences.IgnoreQueryFilters().Include(s => s.Deck).ThenInclude(d => d!.ParentDeck)
               .FirstOrDefaultAsync(s => s.SentenceId == sentenceId && (s.OwnerId == null || s.OwnerId == userId), ct);

    /// <summary>The fix screen's data; null when the user can't see the line.</summary>
    public async Task<SentenceFixEditorDto?> EditorAsync(string userId, long sentenceId, CancellationToken ct)
    {
        var line = await LineAsync(userId, sentenceId, ct);
        if (line == null) return null;

        var mine = await userContext.SentenceFixes.AsNoTracking().FirstOrDefaultAsync(f => f.UserId == userId && f.ExampleSentenceId == sentenceId, ct);
        var fix = await store.ForUserAsync(userId, sentenceId, ct);
        var hasVideo = line.DeckId is { } deckId && await context.DeckVideoSources.AsNoTracking().AnyAsync(v => v.DeckId == deckId, ct);

        var cues = line.DeckId == null ? null : await cueLookup.FindAsync(sentenceId, ct);
        var originalEnglish = line.DeckId == null ? null : (await translations.GetAsync(sentenceId, allowAi: false, ct))?.Text;
        var original = new FixPartsDto(line.SourceText ?? line.Text, originalEnglish,
                                       cues == null ? null : Math.Max(0, cues.StartMs - DefaultPadMs), cues == null ? null : cues.EndMs + DefaultPadMs,
                                       cues == null ? null : (cues.StartMs + cues.EndMs) / 2);

        var shared = line.SourceText != null || line.English != null || line.StartMs != null || line.PictureMs != null
            ? new FixPartsDto(line.SourceText != null ? line.Text : null, line.English, line.StartMs, line.EndMs, line.PictureMs)
            : null;

        var (title, episode) = Titles(line);
        return new SentenceFixEditorDto(
            sentenceId, fix?.Text ?? line.Text, title, episode, line.DeckId, hasVideo, line is { DeckId: not null, RemovedFromDeck: false },
            original, shared, mine == null ? null : new FixPartsDto(mine.Text, mine.English, mine.StartMs, mine.EndMs, mine.PictureMs),
            hasVideo ? await CuesAsync(line, cues, fix, ct) : [],
            await SameTextAsync(line, ct),
            await MinedAsync(userId, sentenceId, ct),
            await userContext.SentenceFlags.AsNoTracking()
                             .Where(f => f.UserId == userId && f.ExampleSentenceId == sentenceId && f.ResolvedAt == null)
                             .Select(f => new FixFlagDto(f.SentenceFlagId, f.Reason, f.Note)).FirstOrDefaultAsync(ct));
    }

    private static (string? Title, string? Episode) Titles(ExampleSentence line) =>
        line.Deck?.ParentDeck != null ? (line.Deck.ParentDeck.OriginalTitle, line.Deck.OriginalTitle) : (line.Deck?.OriginalTitle ?? line.SourceTitle, null);

    /// <summary>
    /// The cues of the line's clip as the user sees it, with <see cref="NearbyCues"/> on either side. A line no cue matches
    /// (often the line a fix is for) gets the cues around where its position in the episode puts it, with none ticked.
    /// </summary>
    private async Task<List<FixCueDto>> CuesAsync(ExampleSentence line, SentenceCues? cues, LineFix? fix, CancellationToken ct)
    {
        var deckId = line.DeckId!.Value;
        var all = (await cueLookup.MatcherAsync(deckId, ct))?.Cues;
        if (all == null || all.Count == 0) return [];

        int start, end;
        if (fix is { HasRange: true }) (start, end) = (fix.StartMs!.Value, fix.EndMs!.Value);
        else if (cues != null) (start, end) = (cues.StartMs, cues.EndMs);
        else
        {
            var relative = line.Deck is { SentenceCount: > 0 } deck ? Math.Clamp(line.Position / (double)deck.SentenceCount, 0, 1) : 0;
            var guess = all[(int)Math.Min(all.Count - 1, relative * all.Count)];
            (start, end) = (guess.StartMs, guess.StartMs);
        }

        var inClip = end > start ? Enumerable.Range(0, all.Count).Where(i => Covered(all[i], start, end)).ToList() : [];
        int first, last;
        if (inClip.Count > 0) (first, last) = (inClip[0], inClip[^1]);
        else
        {
            // A clip that covers no whole cue: centre on the nearest one
            var nearest = Enumerable.Range(0, all.Count).MinBy(i => Math.Abs(all[i].StartMs - start));
            (first, last) = (nearest, nearest);
        }

        var result = new List<FixCueDto>();
        for (var i = Math.Max(0, first - NearbyCues); i <= Math.Min(all.Count - 1, last + NearbyCues); i++)
        {
            var cue = all[i];
            result.Add(new FixCueDto(cue.StartMs, cue.EndMs, cue.Text, await translations.EnglishForRangeAsync(deckId, cue.StartMs, cue.EndMs, ct),
                                     end > start && Covered(cue, start, end)));
        }

        return result;
    }

    private static bool Covered(SubtitleCue cue, int start, int end) =>
        Math.Min(cue.EndMs, end) - Math.Max(cue.StartMs, start) >= 0.5 * Math.Max(1, cue.EndMs - cue.StartMs);

    /// <summary>Other public lines with the same text, which Submit offers to fix as well.</summary>
    private async Task<List<SameTextLineDto>> SameTextAsync(ExampleSentence line, CancellationToken ct)
    {
        if (line.CentralSentenceId == null || line.OwnerId != null) return [];
        var others = await context.ExampleSentences.AsNoTracking().Include(s => s.Deck).ThenInclude(d => d!.ParentDeck)
                                  .Where(s => s.CentralSentenceId == line.CentralSentenceId && s.SentenceId != line.SentenceId)
                                  .OrderBy(s => s.SentenceId).Take(MaxSameText).ToListAsync(ct);
        return others.Select(s =>
        {
            var (title, episode) = Titles(s);
            return new SameTextLineDto(s.SentenceId, title, episode);
        }).ToList();
    }

    private async Task<List<FixMinedDto>> MinedAsync(string userId, long sentenceId, CancellationToken ct)
    {
        var rows = await userContext.MinedSentences.AsNoTracking()
                                    .Where(s => s.UserId == userId && s.ExampleSentenceId == sentenceId)
                                    .Join(userContext.MinedWords, s => s.MinedWordId, w => w.MinedWordId, (s, w) => new { s.MinedSentenceId, w.Expression, s.MinedWordId })
                                    .ToListAsync(ct);
        var result = new List<FixMinedDto>();
        foreach (var row in rows)
            result.Add(new FixMinedDto(row.MinedSentenceId, row.Expression, await MinedSentenceService.CanRemoveAsync(userContext, row.MinedSentenceId, ct)));
        return result;
    }

    /// <summary>
    /// Saves the user's fix of a line, replacing the one they had; all parts null resets it. Saving closes their open flag
    /// on the line as fixed, and marks their mined sentences on it for Sync to rebuild when the text or English changed.
    /// False when the user can't see the line.
    /// </summary>
    /// <exception cref="SentenceFixException">A part isn't valid.</exception>
    public async Task<bool> SaveAsync(string userId, long sentenceId, SaveSentenceFixRequest request, CancellationToken ct)
    {
        var line = await LineAsync(userId, sentenceId, ct);
        if (line == null) return false;

        var text = Clean(request.Text);
        if (text?.Length > MaxTextLength) throw new SentenceFixException($"The Japanese is at most {MaxTextLength} characters.");
        if (text == line.Text) text = null;
        var english = Clean(request.English);
        if (english?.Length > MaxEnglishLength) throw new SentenceFixException($"The English is at most {MaxEnglishLength} characters.");
        if ((request.StartMs, request.EndMs, request.PictureMs) is not (null, null, null))
        {
            if (line.DeckId == null || !await context.DeckVideoSources.AsNoTracking().AnyAsync(v => v.DeckId == line.DeckId, ct))
                throw new SentenceFixException("Only lines from an episode with a linked video can have their clip or frame fixed.");
            ValidateTimes(request.StartMs, request.EndMs, request.PictureMs);
        }

        var fix = await userContext.SentenceFixes.FirstOrDefaultAsync(f => f.UserId == userId && f.ExampleSentenceId == sentenceId, ct);
        var before = (fix?.Text, fix?.English);

        if (text == null && english == null && request.StartMs == null && request.PictureMs == null)
        {
            if (fix == null) return true;
            userContext.SentenceFixes.Remove(fix);
        }
        else
        {
            if (fix == null) userContext.SentenceFixes.Add(fix = new SentenceFix { UserId = userId, ExampleSentenceId = sentenceId });
            if (text != fix.Text) fix.Tokens = text == null ? null : await ParseAsync(text, ct);
            (fix.Text, fix.English, fix.StartMs, fix.EndMs, fix.PictureMs) = (text, english, request.StartMs, request.EndMs, request.PictureMs);
            fix.UpdatedAt = DateTime.UtcNow;
            await flags.CloseAsync(userId, sentenceId, SentenceFlagResolution.Fixed, ct);
        }

        if (before != (text, english))
            await MarkRebuildAsync(userContext.MinedSentences.Where(s => s.UserId == userId && s.ExampleSentenceId == sentenceId), ct);
        await userContext.SaveChangesAsync(ct);
        return true;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateTimes(int? startMs, int? endMs, int? pictureMs)
    {
        if ((startMs == null) != (endMs == null)) throw new SentenceFixException("A clip needs both a start and an end.");
        if (startMs < 0 || pictureMs < 0) throw new SentenceFixException("Times can't be negative.");
        if (endMs <= startMs) throw new SentenceFixException("The clip has to end after it starts.");
        if (endMs - startMs > MaxClipMs) throw new SentenceFixException($"A clip lasts at most {MaxClipMs / 1000} seconds.");
    }

    /// <summary>The text's tokens; empty when the parser fails, so the fix still saves (it then has no furigana).</summary>
    private async Task<byte[]> ParseAsync(string text, CancellationToken ct)
    {
        try
        {
            return ExampleSentenceTokens.Encode(await parser.ParseAsync(text, ct));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Couldn't parse a fixed sentence");
            return ExampleSentenceTokens.Encode([]);
        }
    }

    /// <summary>Marks mined sentences for the next Sync to rebuild their text, furigana and English, as a moved line's are.</summary>
    private static Task<int> MarkRebuildAsync(IQueryable<MinedSentence> sentences, CancellationToken ct) =>
        sentences.Where(s => !s.NeedsRebuild).ExecuteUpdateAsync(u => u.SetProperty(s => s.NeedsRebuild, true), ct);

    /// <summary>
    /// Makes the user's fix the shared line's, for everyone: the text (re-parsed, the original kept in SourceText), English
    /// and times, and the text on the ticked lines with the same text too. Closes every open flag on those lines as fixed,
    /// and marks mined sentences on them for Sync. Others' own fixes stay theirs. False when the user has no fix of a line
    /// they can see, or it's a private line.
    /// </summary>
    public async Task<bool> SubmitAsync(string userId, long sentenceId, SubmitSentenceFixRequest request, CancellationToken ct)
    {
        var line = await LineAsync(userId, sentenceId, ct);
        var fix = await userContext.SentenceFixes.FirstOrDefaultAsync(f => f.UserId == userId && f.ExampleSentenceId == sentenceId, ct);
        if (line == null || line.OwnerId != null || fix == null) return false;

        var lines = new List<ExampleSentence> { line };
        if (fix.Text != null && request.SameTextIds is { Count: > 0 } sameIds && line.CentralSentenceId != null)
            lines.AddRange(await context.ExampleSentences.Where(s => sameIds.Contains(s.SentenceId) && s.SentenceId != sentenceId &&
                                                                     s.CentralSentenceId == line.CentralSentenceId).ToListAsync(ct));

        // Parsed again, since the shared line's tokens decide which words it counts for. A failure stops the Submit rather
        // than leaving the shared line without words.
        List<SentenceToken>? tokens = null;
        if (fix.Text != null)
        {
            try
            {
                tokens = await parser.ParseAsync(fix.Text, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Couldn't parse a submitted fix of sentence {SentenceId}", sentenceId);
                throw new SentenceFixException("Couldn't read the words of the new text. Try again.");
            }
        }
        long? centralId = fix.Text == null ? null : await CentralIdAsync(fix.Text, ct);
        var oldTexts = lines.ToDictionary(l => l.SentenceId, l => l.Text);
        foreach (var l in lines)
        {
            var sourceText = l.SourceText ?? l.Text;
            if (fix.Text != null)
            {
                l.SourceText = fix.Text == sourceText ? null : sourceText;
                l.Text = fix.Text;
                l.Tokens = ExampleSentenceTokens.Encode(tokens!);
                l.WordKeys = ExampleSentenceTokens.WordKeys(tokens!, ExampleSentenceTokens.FineBucketOf(l.WordKeys) ?? Random.Shared.Next(ExampleSentenceTokens.FineBucketCount));
                if (centralId != null) l.CentralSentenceId = centralId;
            }

            var isLine = l.SentenceId == sentenceId;
            if (isLine)
            {
                l.English = fix.English ?? l.English;
                // A new clip without a frame of its own shows its middle, as it did for the submitter
                if (fix.StartMs != null) (l.StartMs, l.EndMs, l.PictureMs) = (fix.StartMs, fix.EndMs, null);
                if (fix.PictureMs != null) l.PictureMs = fix.PictureMs;
            }

            context.SentenceCorrections.Add(new SentenceCorrection
            {
                ExampleSentenceId = l.SentenceId, DeckId = l.DeckId, Position = l.Position, SourceText = sourceText, Text = fix.Text, UserId = userId,
                English = isLine ? fix.English : null, StartMs = isLine ? fix.StartMs : null, EndMs = isLine ? fix.EndMs : null,
                PictureMs = isLine ? fix.PictureMs : null
            });
        }

        await context.SaveChangesAsync(ct);

        userContext.SentenceFixes.Remove(fix);
        await userContext.SaveChangesAsync(ct);

        var ids = lines.Select(l => l.SentenceId).ToList();
        foreach (var id in ids) await flags.MarkFixedAsync(id, ct);
        if (fix.Text != null) await MoveMinedWordsAsync(ids, oldTexts, fix.Text, ct);
        if (fix.Text != null || fix.English != null)
            await MarkRebuildAsync(userContext.MinedSentences.Where(s => s.ExampleSentenceId != null && ids.Contains(s.ExampleSentenceId.Value)), ct);

        logger.LogInformation("User {UserId} submitted a fix of sentence {SentenceId} ({Count} lines)", userId, sentenceId, ids.Count);
        return true;
    }

    /// <summary>The subtitle English for a clip of the line's episode. Found is false when the user can't see the line or it has no episode.</summary>
    public async Task<(bool Found, string? Text)> SuggestEnglishAsync(string userId, long sentenceId, int startMs, int endMs, CancellationToken ct)
    {
        var deckId = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                  .Where(s => s.SentenceId == sentenceId && (s.OwnerId == null || s.OwnerId == userId))
                                  .Select(s => s.DeckId).FirstOrDefaultAsync(ct);
        return deckId == null ? (false, null) : (true, await translations.EnglishForRangeAsync(deckId.Value, startMs, endMs, ct));
    }

    /// <summary>
    /// Mined sentences mark their word by its place in the line's text: a new shared text moves it to where the same word
    /// is in the new text, or unmarks it.
    /// </summary>
    private async Task MoveMinedWordsAsync(List<long> lineIds, Dictionary<long, string> oldTexts, string newText, CancellationToken ct)
    {
        var mined = await userContext.MinedSentences
                                     .Where(s => s.ExampleSentenceId != null && lineIds.Contains(s.ExampleSentenceId.Value) && s.TargetPosition != null)
                                     .ToListAsync(ct);
        foreach (var s in mined)
        {
            var moved = new MinedLine(oldTexts[s.ExampleSentenceId!.Value], null, s.TargetPosition, s.TargetLength).MovedTo(oldTexts[s.ExampleSentenceId.Value], newText);
            (s.TargetPosition, s.TargetLength) = (moved.TargetPosition, moved.TargetLength);
        }

        await userContext.SaveChangesAsync(ct);
    }

    /// <summary>The central row for a fixed text, made if it's new. Null outside Postgres (the tests), where the line keeps its row.</summary>
    private async Task<long?> CentralIdAsync(string text, CancellationToken ct)
    {
        if (context.Database.GetDbConnection() is not NpgsqlConnection conn) return null;
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        return (await JitenHelper.EnsureCentralSentences(conn, [text]))[0];
    }
}
