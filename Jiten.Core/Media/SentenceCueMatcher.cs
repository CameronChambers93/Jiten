using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core.Data;

namespace Jiten.Core.Media;

/// <summary>
/// Finds the subtitle cues an example sentence was taken from. Sentences only keep their text and index, and the parser
/// rewrites punctuation and drops furigana and speaker labels, so both sides are reduced to letters and digits first.
/// </summary>
public sealed partial class SentenceCueMatcher
{
    /// <summary>Shortest prefix tried when the whole sentence isn't found (it may span a cue the cleaner changed).</summary>
    private const int MinPrefix = 6;

    private readonly List<SubtitleCue> _cues;
    private readonly string _text;
    private readonly int[] _cueAt;

    public SentenceCueMatcher(List<SubtitleCue> cues)
    {
        _cues = cues;
        var text = new StringBuilder();
        var cueAt = new List<int>();
        for (int i = 0; i < cues.Count; i++)
        {
            var normalised = Normalise(cues[i].Text);
            text.Append(normalised);
            cueAt.AddRange(Enumerable.Repeat(i, normalised.Length));
        }

        _text = text.ToString();
        _cueAt = cueAt.ToArray();
    }

    /// <summary>The first and last cue holding the sentence, or null when it isn't found.</summary>
    /// <param name="relativePosition">Where the sentence sits in the text, 0 to 1; picks between repeated lines.</param>
    public (SubtitleCue First, SubtitleCue Last)? Find(string sentence, double relativePosition)
    {
        var needle = Normalise(sentence);
        if (needle.Length == 0 || _text.Length == 0) return null;

        var hits = Occurrences(needle);
        if (hits.Count == 0 && needle.Length > MinPrefix)
        {
            needle = needle[..MinPrefix];
            hits = Occurrences(needle);
        }

        if (hits.Count == 0) return null;

        var expected = relativePosition * _text.Length;
        var start = hits.MinBy(h => Math.Abs(h - expected));
        return (_cues[_cueAt[start]], _cues[_cueAt[start + needle.Length - 1]]);
    }

    private List<int> Occurrences(string needle)
    {
        var hits = new List<int>();
        for (int at = _text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = _text.IndexOf(needle, at + 1, StringComparison.Ordinal))
            hits.Add(at);
        return hits;
    }

    [GeneratedRegex(@"\(.*?\)|（.*?）")]
    private static partial Regex Parenthesised();

    /// <summary>
    /// NFKC, without parenthesised text (furigana, speaker labels), keeping only letters and digits. ー is dropped too: the
    /// parser writes it where subtitles have 〜 (お〜い → おーい).
    /// </summary>
    public static string Normalise(string text)
    {
        var stripped = Parenthesised().Replace(text.Normalize(NormalizationForm.FormKC), "");
        var result = new StringBuilder(stripped.Length);
        foreach (var c in stripped)
            if (char.IsLetterOrDigit(c) && c != 'ー')
                result.Append(c);
        return result.ToString();
    }
}
