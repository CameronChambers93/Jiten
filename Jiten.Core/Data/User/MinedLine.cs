using System.Text.RegularExpressions;

namespace Jiten.Core.Data.User;

/// <summary>A mined sentence as an occurrence stores it: plain text without its speaker, and where the note marked the word.</summary>
public readonly record struct MinedLine(string Text, string? Speaker, int? TargetPosition, int? TargetLength)
{
    private const int MaxSpeakerLength = 12;

    private static readonly Regex Spoken = new("^([^「」、。！？\\s]{1," + MaxSpeakerLength + "})(「[^「」]*」)$", RegexOptions.Compiled);
    private static readonly Regex Marked = new("<b>(.*?)</b>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <param name="html">The stored field, the word in <c>&lt;b&gt;</c>.</param>
    /// <param name="plainText">Turns a field into plain text.</param>
    public static MinedLine From(string html, Func<string, string> plainText)
    {
        var text = plainText(html);
        string? speaker = null;
        if (Spoken.Match(text) is { Success: true } spoken)
        {
            speaker = spoken.Groups[1].Value;
            text = spoken.Groups[2].Value;
        }

        var mark = Marked.Match(html);
        if (!mark.Success) return new MinedLine(text, speaker, null, null);

        var word = plainText(mark.Groups[1].Value);
        if (word.Length == 0) return new MinedLine(text, speaker, null, null);

        var expected = plainText(html[..mark.Index] + "\u0001").TrimEnd('\u0001').Length - (speaker?.Length ?? 0);
        if (expected >= 0 && string.CompareOrdinal(text, expected, word, 0, word.Length) == 0)
            return new MinedLine(text, speaker, expected, word.Length);

        var found = text.IndexOf(word, StringComparison.Ordinal);
        return found < 0 ? new MinedLine(text, speaker, null, null) : new MinedLine(text, speaker, found, word.Length);
    }

    /// <summary>The line as a card field shows it, the word in <c>&lt;b&gt;</c>.</summary>
    public string ToHtml()
    {
        var html = TargetPosition is { } start && TargetLength is { } length && start + length <= Text.Length
            ? $"{Text[..start]}<b>{Text.Substring(start, length)}</b>{Text[(start + length)..]}"
            : Text;
        return Speaker + html;
    }
}
