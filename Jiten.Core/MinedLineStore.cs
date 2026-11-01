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
