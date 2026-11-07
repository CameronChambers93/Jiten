using System.Collections;
using System.Text;

namespace Jiten.Parser.SpeechBoundaries;

/// <summary>
/// Turns subtitle text with cue breaks (a blank line after each cue) into one line per unit the audio is cut from: one or
/// more whole cues, never part of one. The speech-break model decides only whether a sentence runs on past a cue end; a
/// unit holds at most <see cref="MaxCuesPerLine"/> cues. A cue whose text already ends a sentence (。！？…」』) always
/// ends its unit; the model is asked only about cues without such an end, as anime subtitles usually are. Lines of one cue that are separate sentences are joined with a
/// full-width space; a sentence wrapped over two lines is joined directly.
/// </summary>
public static class CueLineAssembler
{
    public const int MaxCuesPerLine = 3;

    private const char Separator = '　';
    private static readonly char[] SentenceEnders = ['。', '！', '？', '…', '」', '』'];

    /// <summary>Text from <c>SubtitleExtractor.ExtractWithCueBreaks</c> carries its cue ends as blank lines.</summary>
    public static bool HasCueBreaks(string text) => text.Replace("\r", "").Contains("\n\n", StringComparison.Ordinal);

    /// <summary>One unit per output line, and the model's bitmap over the raw lines (stored, as for the plain assembler).</summary>
    public static (string Text, byte[] Boundaries) Prepare(string rawText, byte[]? storedBoundaries, SpeechBoundaryModel model)
    {
        var lines = rawText.Replace("\r", "").Split('\n');
        var bits = SpeechTextAssembler.FromBytes(storedBoundaries, lines.Length) ?? SpeechTextAssembler.DetectBoundaries(lines, model);
        return (Assemble(lines, bits), SpeechTextAssembler.ToBytes(bits));
    }

    public static string Assemble(IReadOnlyList<string> rawLines, BitArray sentenceEnds)
    {
        var sb = new StringBuilder();
        var unit = new StringBuilder();
        var cues = 0;
        var lastInCue = -1;
        var lastText = "";

        for (int i = 0; i < rawLines.Count; i++)
        {
            if (rawLines[i].Trim().Length == 0)
            {
                // A blank line ends the cue before it
                if (lastInCue < 0) continue;
                cues++;
                var sentenceEndsHere = EndsSentence(lastText) || (lastInCue < sentenceEnds.Length && sentenceEnds[lastInCue]);
                if (sentenceEndsHere || cues >= MaxCuesPerLine)
                    Flush();
                lastInCue = -1;
                continue;
            }

            var text = SpeechLineCleaner.ToSentenceText(SpeechLineCleaner.Clean(rawLines[i]));
            if (text.Length == 0) continue;

            // Another line of the same cue: a new sentence gets a space, a wrapped one joins on
            if (lastInCue >= 0 && unit.Length > 0 && lastInCue < sentenceEnds.Length && sentenceEnds[lastInCue])
                unit.Append(Separator);
            unit.Append(text);
            lastInCue = i;
            lastText = text;
        }

        Flush();
        return sb.ToString();

        static bool EndsSentence(string text) => text.Length > 0 && Array.IndexOf(SentenceEnders, text[^1]) >= 0;

        void Flush()
        {
            if (unit.Length > 0)
            {
                if (Array.IndexOf(SentenceEnders, unit[^1]) < 0)
                    unit.Append('。');
                sb.Append(unit).Append('\n');
                unit.Clear();
            }

            cues = 0;
        }
    }
}
