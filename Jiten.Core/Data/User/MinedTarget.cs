namespace Jiten.Core.Data.User;

/// <summary>Finds a word in a sentence whose note didn't mark it.</summary>
public static class MinedTarget
{
    /// <summary>The first token of the word, else the first place its written form appears.</summary>
    public static (int Position, int Length)? Find(string text, IEnumerable<SentenceToken> tokens, int? wordId, string expression)
    {
        foreach (var token in tokens)
            if (token.WordId == wordId) return (token.Position, token.Length);

        var found = expression.Length == 0 ? -1 : text.IndexOf(expression, StringComparison.Ordinal);
        return found < 0 ? null : (found, expression.Length);
    }
}
