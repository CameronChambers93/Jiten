using System.Text;
using Jiten.Core.Data;

namespace Jiten.Parser.SpeechBoundaries;

/// <summary>A submitted text fix of one stored line: its original text, the fixed text, and where the line was.</summary>
public record LineCorrection(long SentenceId, string SourceText, string Text, int Position);

/// <summary>A fix <see cref="LineCorrector.Apply"/> put in, with the position its line has in this parse.</summary>
public record AppliedCorrection(LineCorrection Correction, int Position);

/// <summary>
/// Puts submitted text fixes back into a deck's lines before they're parsed (sentence flags step 3), so a reparse counts the
/// fixed words and the fixed line keeps its row. Works on the cue assembler's output, one line per stored sentence.
/// </summary>
public static class LineCorrector
{
    /// <summary>How far, in lines, a fix may land from where it was made; past this it counts as not found.</summary>
    public const int MaxDistance = 50;

    /// <summary>
    /// Replaces each correction's line in <paramref name="assembledText"/> (lines separated by \n): the line whose
    /// <see cref="Key"/> matches the correction's source text, nearest its position first and at most
    /// <see cref="MaxDistance"/> away. A line takes at most one correction. Returns the text and the corrections that found
    /// no line. <paramref name="applied"/> gets the rest, with their lines' positions.
    /// </summary>
    public static (string Text, List<LineCorrection> Unmatched) Apply(string assembledText, IReadOnlyList<LineCorrection> corrections,
                                                                    ICollection<AppliedCorrection>? applied = null)
    {
        if (corrections.Count == 0) return (assembledText, []);

        var lines = assembledText.Split('\n');
        var lineIndices = new Dictionary<string, List<int>>();
        var sentence = 0;
        var sentenceOfLine = new int[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            // Positions count stored sentences, and a line with no Japanese (♪～, Latin only) stores none
            sentenceOfLine[i] = sentence;
            var key = Key(FuriganaHintExtractor.Strip(lines[i]));
            if (key.Length == 0) continue;
            sentence++;
            if (!lineIndices.TryGetValue(key, out var list)) lineIndices[key] = list = [];
            list.Add(i);
        }

        // Nearest pairs first, as the occurrence matcher pairs rows, so two fixes of the same text don't swap lines
        var candidates = corrections
                         .SelectMany(c => lineIndices.GetValueOrDefault(Key(c.SourceText)) ?? [], (c, line) => (Correction: c, Line: line))
                         .Where(p => Math.Abs((long)sentenceOfLine[p.Line] - p.Correction.Position) <= MaxDistance)
                         .OrderBy(p => Math.Abs((long)sentenceOfLine[p.Line] - p.Correction.Position))
                         .ThenBy(p => p.Line);

        var done = new HashSet<LineCorrection>();
        var taken = new HashSet<int>();
        foreach (var (correction, line) in candidates)
        {
            if (done.Contains(correction) || !taken.Add(line)) continue;
            lines[line] = correction.Text.Replace("\r", "").Replace('\n', '　');
            done.Add(correction);
            applied?.Add(new AppliedCorrection(correction, sentenceOfLine[line]));
        }

        return (string.Join('\n', lines), corrections.Where(c => !done.Contains(c)).ToList());
    }

    /// <summary>
    /// Gives each applied fix's parsed sentence the fix's <see cref="LineCorrection.SentenceId"/>, so the save keeps the line's
    /// row even when the parser changed the fixed text (「！！」 is stored as 「！」) and its text no longer pairs it. The
    /// sentence is the one whose <see cref="Key"/> matches the fixed text's, nearest its line's position (the same one,
    /// unless the parse dropped a line). A fixed text with no key (only punctuation or Latin) isn't pinned.
    /// </summary>
    public static void PinRows(IReadOnlyList<AppliedCorrection> applied, IReadOnlyList<ExampleSentence> sentences)
    {
        if (applied.Count == 0) return;

        var byKey = sentences.ToLookup(s => Key(s.Text));
        var candidates = applied
                         .Select(a => (Applied: a, Key: Key(FuriganaHintExtractor.Strip(a.Correction.Text))))
                         .Where(a => a.Key.Length > 0)
                         .SelectMany(a => byKey[a.Key], (a, s) => (a.Applied.Correction, a.Applied.Position, Sentence: s))
                         .Where(p => Math.Abs((long)p.Sentence.Position - p.Position) <= MaxDistance)
                         .OrderBy(p => Math.Abs((long)p.Sentence.Position - p.Position))
                         .ThenBy(p => p.Sentence.Position);

        var pinned = new HashSet<LineCorrection>();
        var taken = new HashSet<ExampleSentence>(ReferenceEqualityComparer.Instance);
        foreach (var (correction, _, sentence) in candidates)
        {
            if (pinned.Contains(correction) || !taken.Add(sentence)) continue;
            sentence.SentenceId = correction.SentenceId;
            pinned.Add(correction);
        }
    }

    /// <summary>
    /// The text's kana, kanji and digits, after width folding: what survives the analyser's cleaning, so a stored line
    /// that lost its Latin text or punctuation still finds its subtitle line.
    /// </summary>
    public static string Key(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or '々' or >= '一' and <= '鿿'
                or >= '㐀' and <= '䶿' or >= '0' and <= '9')
                sb.Append(c);
        }

        return sb.ToString();
    }
}
