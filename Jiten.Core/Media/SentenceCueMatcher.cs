using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core.Data;

namespace Jiten.Core.Media;

/// <summary>
/// Finds the subtitle cues an example sentence was taken from. Sentences only keep their text and index, and the parser
/// rewrites punctuation and drops furigana and speaker labels, so both sides are reduced to letters and digits first.
/// </summary>
/// <param name="From">For a sentence inside one cue, where its lines start in the cue, 0 to 1 (by line, as a cue's lines are
/// usually different speakers); otherwise 0.</param>
/// <param name="To">Where its lines end, 0 to 1; otherwise 1.</param>
public readonly record struct CueMatch(int First, int Last, double From, double To);

public sealed partial class SentenceCueMatcher
{
    /// <summary>Shortest prefix tried when the whole sentence isn't found (it may span a cue the cleaner changed).</summary>
    private const int MinPrefix = 6;

    private readonly List<SubtitleCue> _cues;
    private readonly string _text;
    private readonly int[] _cueAt;

    /// <summary>For each character of <see cref="_text"/>, the line of its cue it came from.</summary>
    private readonly int[] _lineAt;

    private readonly int[] _lineCount;

    public IReadOnlyList<SubtitleCue> Cues => _cues;

    public SentenceCueMatcher(List<SubtitleCue> cues)
    {
        _cues = cues;
        var text = new StringBuilder();
        var cueAt = new List<int>();
        var lineAt = new List<int>();
        _lineCount = new int[cues.Count];
        for (int i = 0; i < cues.Count; i++)
        {
            // Lines are counted only when they hold text: a speaker label on its own line, like "（トト子）", isn't one.
            var line = 0;
            foreach (var normalised in cues[i].Text.Split('\n').Select(Normalise).Where(l => l.Length > 0))
            {
                text.Append(normalised);
                cueAt.AddRange(Enumerable.Repeat(i, normalised.Length));
                lineAt.AddRange(Enumerable.Repeat(line++, normalised.Length));
            }

            _lineCount[i] = Math.Max(line, 1);
        }

        _text = text.ToString();
        _cueAt = cueAt.ToArray();
        _lineAt = lineAt.ToArray();
    }

    /// <summary>The first and last cue holding the sentence, or null when it isn't found.</summary>
    /// <param name="relativePosition">Where the sentence sits in the text, 0 to 1; picks between repeated lines.</param>
    public (SubtitleCue First, SubtitleCue Last)? Find(string sentence, double relativePosition) =>
        FindIndices(sentence, relativePosition) is (int first, int last) ? (_cues[first], _cues[last]) : null;

    /// <summary>The indices of the first and last cue holding the sentence, or null when it isn't found.</summary>
    public (int First, int Last)? FindIndices(string sentence, double relativePosition) =>
        Locate(sentence, relativePosition) is { } match ? (match.First, match.Last) : null;

    /// <summary>Where the sentence is: its first and last cue, and for a sentence inside one cue, which of its lines.</summary>
    public CueMatch? Locate(string sentence, double relativePosition)
    {
        var needle = Normalise(sentence);
        if (needle.Length == 0 || _text.Length == 0) return null;

        var hits = Occurrences(needle);
        var prefixOnly = false;
        if (hits.Count == 0 && needle.Length > MinPrefix)
        {
            needle = needle[..MinPrefix];
            hits = Occurrences(needle);
            prefixOnly = true;
        }

        if (hits.Count == 0) return null;

        var expected = relativePosition * _text.Length;
        var start = hits.MinBy(h => Math.Abs(h - expected));
        var end = start + needle.Length - 1;
        int first = _cueAt[start], last = _cueAt[end];
        return first == last
            // Matched by prefix only, the sentence may run on past the matched line: then it ends with the cue.
            ? new CueMatch(first, last, _lineAt[start] / (double)_lineCount[first], prefixOnly ? 1 : (_lineAt[end] + 1) / (double)_lineCount[first])
            : new CueMatch(first, last, 0, 1);
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
