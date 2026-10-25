using Jiten.Core.Data;

namespace Jiten.Core.Media;

/// <summary>
/// Picks the translation cues spoken over a span of the Japanese track. Fansub lines split and merge differently from the
/// Japanese ones, so a translation cue counts when most of it falls inside the span, it starts inside the span, or it covers most of
/// the span.
/// </summary>
public sealed class TranslationCueAligner(IReadOnlyList<SubtitleCue> cues, int offsetMs)
{
    private const double MostOf = 0.5;
    private const int LateStartMs = 500;

    /// <summary>The joined text of the translation cues over [startMs, endMs] in Japanese-track time, or null when none.</summary>
    /// <param name="from">For a sentence that is only some lines of a multi-line Japanese cue, where those lines start (0 to 1).</param>
    /// <param name="to">Where those lines end (0 to 1).</param>
    /// <remarks>
    /// A cue's lines are usually different speakers, and timing can't split one cue, so the translation cues found for the
    /// whole cue are shared out in order: the sentence on the second of two lines gets the later half of them.
    /// </remarks>
    public string? TextFor(int startMs, int endMs, double from = 0, double to = 1)
    {
        if (endMs <= startMs) return null;

        var picked = new List<string>();
        foreach (var cue in cues)
        {
            int start = cue.StartMs + offsetMs, end = cue.EndMs + offsetMs;
            if (start >= endMs) break;
            if (end <= startMs) continue;

            var overlap = Math.Min(end, endMs) - Math.Max(start, startMs);
            if (overlap >= MostOf * (end - start) || overlap >= MostOf * (endMs - startMs) || StartsWithin(start, startMs, endMs))
                if (picked.Count == 0 || picked[^1] != cue.Text)
                    picked.Add(cue.Text);
        }

        if (picked.Count > 1 && (from > 0 || to < 1))
        {
            var share = picked.Where((_, i) => (i + 0.5) / picked.Count is var centre && centre >= from && centre <= to).ToList();
            picked = share.Count > 0 ? share : [picked[Math.Clamp((int)((from + to) / 2 * picked.Count), 0, picked.Count - 1)]];
        }

        return picked.Count == 0 ? null : string.Join(" ", picked);
    }

    /// <summary>
    /// A translation line that starts during the span is spoken in it even when it runs on past the end (fansubs often hold
    /// a line longer than the Japanese cue); one starting in the span's last stretch is more likely the next line.
    /// </summary>
    private static bool StartsWithin(int start, int startMs, int endMs) =>
        start >= startMs && start < endMs - Math.Min(LateStartMs, (endMs - startMs) / 4);

    /// <summary>Share of the translation's cue time that falls on Japanese cues once shifted: near 1 when the tracks line up.</summary>
    public static double Coverage(IReadOnlyList<SubtitleCue> reference, IReadOnlyList<SubtitleCue> cues, int offsetMs)
    {
        long total = 0, covered = 0;
        int r = 0;
        foreach (var cue in cues)
        {
            int start = cue.StartMs + offsetMs, end = cue.EndMs + offsetMs;
            total += end - start;
            while (r < reference.Count && reference[r].EndMs <= start) r++;
            for (int i = r; i < reference.Count && reference[i].StartMs < end; i++)
                covered += Math.Max(0, Math.Min(end, reference[i].EndMs) - Math.Max(start, reference[i].StartMs));
        }

        return total == 0 ? 0 : Math.Min(1, covered / (double)total);
    }
}
