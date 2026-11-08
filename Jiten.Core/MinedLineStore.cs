using System.Net;
using System.Text.RegularExpressions;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Core;

/// <summary>Stores mined lines as occurrences. A line's text goes in the central table at once; the occurrence stays private to its owner.</summary>
public static class MinedLineStore
{
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

    private static readonly byte[] NoTokens = ExampleSentenceTokens.Encode([]);

    /// <summary>A field's text without tags or entities.</summary>
    public static string PlainText(string html) =>
        WebUtility.HtmlDecode(Tags.Replace(html.Replace("<br>", " "), "")).Replace(' ', ' ').Trim();

    /// <summary>Fills in what a user's sentences read from their occurrences. A private line of another user is never read.</summary>
    /// <param name="fixedTexts">The user's own text for fixed lines, by occurrence; it replaces the line's, with the word marked where
    /// the same text is found in it.</param>
    public static async Task FillAsync(JitenDbContext context, string userId, IReadOnlyCollection<MinedSentence> sentences, CancellationToken ct,
                                       IReadOnlyDictionary<long, string>? fixedTexts = null)
    {
        var ids = sentences.Where(s => s.ExampleSentenceId != null).Select(s => s.ExampleSentenceId!.Value).Distinct().ToList();
        var occurrences = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                       .Where(s => ids.Contains(s.SentenceId) && (s.OwnerId == null || s.OwnerId == userId))
                                       .ToDictionaryAsync(s => s.SentenceId, ct);

        foreach (var sentence in sentences)
        {
            if (sentence.ExampleSentenceId is not { } id || !occurrences.TryGetValue(id, out var occurrence)) continue;
            sentence.OccurrenceHtml = fixedTexts?.GetValueOrDefault(id) is { } fixedText
                ? new MinedLine(occurrence.Text, occurrence.Speaker, sentence.TargetPosition, sentence.TargetLength).MovedTo(occurrence.Text, fixedText).ToHtml()
                : new MinedLine(occurrence.Text, occurrence.Speaker, sentence.TargetPosition, sentence.TargetLength).ToHtml();
            sentence.SourceTitle = occurrence.SourceTitle;
            sentence.SourceUrl = occurrence.SourceUrl;
            sentence.AudioPath = occurrence.AudioPath;
            sentence.AudioContentType = occurrence.AudioContentType;
            sentence.PicturePath = occurrence.PicturePath;
            sentence.PictureContentType = occurrence.PictureContentType;
        }
    }

    /// <summary>Labels a deck's line with where it was mined from, the first time.</summary>
    public static Task SetDeckSourceAsync(JitenDbContext context, long sentenceId, string? sourceTitle, CancellationToken ct) =>
        context.ExampleSentences.IgnoreQueryFilters()
               .Where(s => s.SentenceId == sentenceId && s.DeckId != null && s.SourceTitle == null)
               .ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceTitle, sourceTitle), ct);

    /// <summary>The user's occurrence of this line from this source, added if it's new. Media it doesn't have yet is filled in.</summary>
    public static async Task<long> EnsureOccurrenceAsync(JitenDbContext context, string userId, MinedLine line, MinedSentence media, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        var centralId = (await JitenHelper.EnsureCentralSentences(conn, [line.Text]))[0];

        var occurrence = await context.ExampleSentences.IgnoreQueryFilters()
                                      .FirstOrDefaultAsync(s => s.OwnerId == userId && s.DeckId == null && s.CentralSentenceId == centralId
                                                                && s.SourceTitle == media.SourceTitle, ct);
        if (occurrence == null)
        {
            occurrence = new ExampleSentence
            {
                Text = line.Text, Tokens = NoTokens, WordKeys = [], CentralSentenceId = centralId, OwnerId = userId, Speaker = line.Speaker,
                SourceTitle = media.SourceTitle, SourceUrl = media.SourceUrl
            };
            context.ExampleSentences.Add(occurrence);
        }

        if (occurrence.AudioPath == null) (occurrence.AudioPath, occurrence.AudioContentType) = (media.AudioPath, media.AudioContentType);
        if (occurrence.PicturePath == null) (occurrence.PicturePath, occurrence.PictureContentType) = (media.PicturePath, media.PictureContentType);
        await context.SaveChangesAsync(ct);
        return occurrence.SentenceId;
    }
}
