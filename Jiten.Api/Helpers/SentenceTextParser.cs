using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>Parses one line of text into placed tokens, as deck lines store them.</summary>
public interface ISentenceTextParser
{
    Task<List<SentenceToken>> ParseAsync(string text, CancellationToken ct);
}

public class SentenceTextParser(IDbContextFactory<JitenDbContext> contextFactory) : ISentenceTextParser
{
    public async Task<List<SentenceToken>> ParseAsync(string text, CancellationToken ct) =>
        Locate(text, (await ParagraphParser.ParseAsync(contextFactory, [text]))[0]);

    /// <summary>The parsed words with their places in the text; a word that can't be placed or encoded is left out.</summary>
    public static List<SentenceToken> Locate(string text, IEnumerable<DeckWord> words)
    {
        var tokens = new List<SentenceToken>();
        var from = 0;
        foreach (var word in words)
        {
            var (position, length) = TokenPositionHelper.FindTokenInSource(text, word.OriginalText, from);
            if (position < 0) continue;
            from = position + length;
            if (!ExampleSentenceTokens.CanEncode(word.WordId, position, length)) continue;

            tokens.Add(new SentenceToken(word.WordId, word.ReadingIndex, (byte)position, (byte)length, IsTarget: false,
                                         IsFunctionWord: word.PartsOfSpeech.Any(p => p is PartOfSpeech.Particle or PartOfSpeech.Auxiliary)));
        }

        return tokens;
    }
}
