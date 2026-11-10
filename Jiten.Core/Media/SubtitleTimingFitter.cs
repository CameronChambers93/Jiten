namespace Jiten.Core.Media;

/// <summary>A run of cues that share one offset onto the video, from <see cref="FirstCue"/> (an index into the sorted cues).</summary>
/// <param name="SearchFromMs">For a segment after the first: where the change was looked for, in subtitle time. Against
/// audio the placement within this range is uncertain.</param>
public sealed record TimingSegment(int FirstCue, int StartMs, int OffsetMs, int SearchFromMs = 0, int SearchToMs = 0);

/// <summary>One window's own best offset, and how the whole-episode offset scored there.</summary>
public sealed record TimingWindow(int StartMs, int EndMs, int CueCount, int OffsetMs, double Score, double ScoreAtWhole);

/// <param name="OffsetMs">The offset stored for the episode: the first segment's.</param>
/// <param name="WholeOffsetMs">The single offset that fits the episode as a whole.</param>
public sealed record TimingFit(int OffsetMs, int WholeOffsetMs, double Score, double ScoreAtZero,
                               IReadOnlyList<TimingSegment> Segments, IReadOnlyList<TimingWindow> Windows)
{
    /// <summary>True when the offset changes partway through, so the cues need retiming.</summary>
    public bool Retimed => Segments.Count > 1;

    /// <summary>
    /// The cues, sorted, with each segment after the first moved by its offset's difference from the first, so one
    /// offset (<see cref="OffsetMs"/>) maps them all onto the video.
    /// </summary>
    public List<SubtitleItem> Apply(IEnumerable<SubtitleItem> cues)
    {
        var sorted = SubtitleTimingFitter.Sort(cues);
        var result = new List<SubtitleItem>(sorted.Count);
        for (int s = 0; s < Segments.Count; s++)
        {
            var end = s + 1 < Segments.Count ? Segments[s + 1].FirstCue : sorted.Count;
            var shift = Segments[s].OffsetMs - OffsetMs;
            for (int i = Segments[s].FirstCue; i < end; i++)
                result.Add(sorted[i] with { StartMs = sorted[i].StartMs + shift, EndMs = sorted[i].EndMs + shift });
        }

        return result;
    }
}

/// <summary>
/// Fits subtitle cues to a video in windows, so a release whose breaks differ in length from the video's (Death Note
/// E08: Netflix subtitles on a Kitsune rip, 7.88 s shorter at 12:46) is caught: one offset for the whole episode can
/// look fine while half of it is off. Where the windows' offsets change, the episode is split into segments, the
/// split put at the cue that fits both sides best.
/// </summary>
public static partial class SubtitleTimingFitter
{
    public const int WindowMs = 180_000;
    public const int MaxShiftMs = 30_000;

    /// <summary>Windows with fewer cues aren't fitted on their own: too few to tell offsets apart.</summary>
    private const int MinWindowCues = 8;

    /// <summary>Offsets closer than this are the same offset.</summary>
    private const int SameOffsetMs = 1000;

    /// <summary>
    /// A window must fit its own offset better than the current one by this share of its cue time. Fits against audio
    /// score low (music and effects count as speech), so this is small; Death Note E08's first window after the break
    /// gained 2%.
    /// </summary>
    private const double MinGainShare = 0.015;

    /// <summary>Best fits this close to the search limit are treated as no fit.</summary>
    private const int EdgeMs = 1000;

    /// <summary>Segments are refined within this distance of the offset their windows found.</summary>
    private const int RefineMs = 2000;

    /// <summary>A silence in the subtitles at least this long can hold a cut break.</summary>
    private const int MinBreakGapMs = 4000;

    internal static List<SubtitleItem> Sort(IEnumerable<SubtitleItem> cues) =>
        cues.Where(c => c.EndMs > c.StartMs).OrderBy(c => c.StartMs).ToList();

    /// <summary>
    /// A cue that only describes a sound, like （鐘の音） or ♪～: closed-caption subtitles (Netflix ja[cc]) are full of
    /// them, and neither speech nor English lines sit under them, so they say nothing about the offset. Death Note E25
    /// opens with two minutes of them, which the English fitted to a stray shift. Against cues they are left out of the
    /// scoring, and still move with their segment.
    /// </summary>
    public static bool IsSoundOnly(string text) =>
        SoundDescription().Replace(text, "").Trim(' ', '　', '\n', '\r', '♪', '～', '~', '・', '…').Length == 0;

    [System.Text.RegularExpressions.GeneratedRegex(@"（[^）]*）|\([^)]*\)|［[^］]*］|\[[^\]]*\]|〈[^〉]*〉")]
    private static partial System.Text.RegularExpressions.Regex SoundDescription();

    /// <param name="ignoreSoundOnly">
    /// Leave sound-only cues out of the scoring: right against another track's cues (English has no line under
    /// （鐘の音）), wrong against audio, where the sound they describe is there and helps the fit.
    /// </param>
    public static TimingFit Fit(SubtitleOffsetDetector.ActivityTrack track, IEnumerable<SubtitleItem> cues,
                                int windowMs = WindowMs, int maxShiftMs = MaxShiftMs, bool ignoreSoundOnly = false)
    {
        var sorted = Sort(cues);
        // Ignored sound-only cues get no length, so they score nothing anywhere
        var spans = sorted.Select(c => ignoreSoundOnly && IsSoundOnly(c.Text) ? (StartMs: c.StartMs, EndMs: c.StartMs) : (StartMs: c.StartMs, EndMs: c.EndMs)).ToList();
        var whole = track.Best(spans, maxShiftMs);
        if (sorted.Count == 0)
            return new TimingFit(whole.OffsetMs, whole.OffsetMs, whole.Score, whole.ScoreAtZero, [new TimingSegment(0, 0, whole.OffsetMs)], []);

        // Windows by cue start: [First, End) cue indices
        var windows = new List<(int First, int End, TimingWindow Fit)>();
        var first = 0;
        for (var w0 = sorted[0].StartMs; first < sorted.Count; w0 += windowMs)
        {
            var end = first;
            while (end < sorted.Count && sorted[end].StartMs < w0 + windowMs) end++;
            if (spans.Skip(first).Take(end - first).Count(s => s.EndMs > s.StartMs) >= MinWindowCues)
            {
                var part = spans.GetRange(first, end - first);
                var local = track.Best(part, maxShiftMs);
                windows.Add((first, end, new TimingWindow(w0, w0 + windowMs, end - first, local.OffsetMs, local.Score,
                                                          track.Score(part, whole.OffsetMs))));
            }

            first = end;
        }

        // Walk the windows: two neighbours that both clearly prefer another offset, and agree on it, start a segment.
        // One window alone isn't enough: the opening song or the next-episode preview often fits some stray shift.
        var seeds = new List<(int Window, int OffsetMs)> { (0, whole.OffsetMs) };
        for (int i = 0; i + 1 < windows.Count; i++)
        {
            var current = seeds[^1].OffsetMs;
            var (a, b) = (windows[i], windows[i + 1]);
            if (!Prefers(track, spans, a, current, maxShiftMs) || !Prefers(track, spans, b, current, maxShiftMs) ||
                Math.Abs(a.Fit.OffsetMs - b.Fit.OffsetMs) > SameOffsetMs)
                continue;

            var offset = (a.Fit.OffsetMs + b.Fit.OffsetMs) / 2;
            if (i == 0)
                seeds[0] = (0, offset);
            else
                seeds.Add((i, offset));
            i++;
        }

        if (seeds.Count <= 1)
            return new TimingFit(whole.OffsetMs, whole.OffsetMs, whole.Score, whole.ScoreAtZero, [new TimingSegment(0, sorted[0].StartMs, whole.OffsetMs)],
                                 windows.Select(w => w.Fit).ToList());

        // Segment boundaries in cue indices: first at the first seed window's start, then placed between windows
        var starts = new List<int> { 0 };
        var searched = new List<(int From, int To)> { (0, 0) };
        var offsets = seeds.Select(s => s.OffsetMs).ToList();
        for (int s = 1; s < seeds.Count; s++)
        {
            // The break is after the last window that still fits the previous offset (windows between may be too noisy
            // to say: Death Note E11's audio), and before the end of the first window that fits the new one
            var last = seeds[s].Window - 1;
            while (last > seeds[s - 1].Window && Math.Abs(windows[last].Fit.OffsetMs - offsets[s - 1]) > SameOffsetMs) last--;
            var lo = windows[last].First;
            var hi = windows[seeds[s].Window].End;
            var from = Math.Max(lo, starts[^1] + 1);
            starts.Add(BestSplit(track, sorted, spans, from, hi, offsets[s - 1], offsets[s]));
            searched.Add((sorted[from].StartMs, sorted[hi - 1].EndMs));
        }

        // Refine each segment's offset over all its cues, near the window's
        for (int s = 0; s < starts.Count; s++)
        {
            var end = s + 1 < starts.Count ? starts[s + 1] : sorted.Count;
            var part = spans.GetRange(starts[s], end - starts[s]);
            offsets[s] = track.Best(part, offsets[s] - RefineMs, offsets[s] + RefineMs).OffsetMs;
        }

        var segments = new List<TimingSegment>();
        for (int s = 0; s < starts.Count; s++)
        {
            // Neighbours that came out at the same offset after refining are one segment
            if (segments.Count > 0 && Math.Abs(segments[^1].OffsetMs - offsets[s]) < SameOffsetMs) continue;
            segments.Add(new TimingSegment(starts[s], sorted[starts[s]].StartMs, offsets[s], searched[s].From, searched[s].To));
        }

        var score = Enumerable.Range(0, segments.Count).Sum(s =>
        {
            var end = s + 1 < segments.Count ? segments[s + 1].FirstCue : sorted.Count;
            return track.Score(spans.GetRange(segments[s].FirstCue, end - segments[s].FirstCue), segments[s].OffsetMs);
        });
        return new TimingFit(segments[0].OffsetMs, whole.OffsetMs, score, whole.ScoreAtZero, segments, windows.Select(w => w.Fit).ToList());
    }

    /// <summary>
    /// Whether a window fits its own offset clearly better than <paramref name="currentMs"/>. A best fit at the edge of
    /// the search means nothing fitted (music, or speech without subtitles), so it never counts.
    /// </summary>
    private static bool Prefers(SubtitleOffsetDetector.ActivityTrack track, List<(int StartMs, int EndMs)> spans,
                                (int First, int End, TimingWindow Fit) w, int currentMs, int maxShiftMs)
    {
        if (Math.Abs(w.Fit.OffsetMs - currentMs) <= SameOffsetMs) return false;
        if (Math.Abs(w.Fit.OffsetMs) >= maxShiftMs - EdgeMs) return false;
        var part = spans.GetRange(w.First, w.End - w.First);
        var cueFrames = part.Sum(c => (c.EndMs - c.StartMs) / (double)SubtitleOffsetDetector.FrameMs);
        return w.Fit.Score - track.Score(part, currentMs) > MinGainShare * cueFrames;
    }

    /// <summary>
    /// The cue index in [lo, hi) where switching from offset a to b fits the cues in [lo, hi) best. Only cues after a
    /// long silence in the subtitles are candidates when there are any: the offset changes where a break was cut
    /// shorter (Death Note: 10-47 s gaps), and per-cue scores against audio are too noisy to place it alone (E11 put
    /// it 96 s late, after a 0.7 s gap).
    /// </summary>
    private static int BestSplit(SubtitleOffsetDetector.ActivityTrack track, List<SubtitleItem> sorted, List<(int StartMs, int EndMs)> spans,
                                 int lo, int hi, int a, int b)
    {
        // Per-cue scores at each offset, then prefix sums: before the split at a, from it on at b
        var n = hi - lo;
        var atA = new double[n + 1];
        var atB = new double[n + 1];
        for (int i = 0; i < n; i++)
        {
            atA[i + 1] = atA[i] + track.Score([spans[lo + i]], a);
            atB[i + 1] = atB[i] + track.Score([spans[lo + i]], b);
        }

        // Silence before each cue: from the latest end of any earlier cue (cues overlap)
        var gapBefore = new int[n];
        var lastEnd = sorted.Take(lo).Select(c => c.EndMs).DefaultIfEmpty(0).Max();
        for (int i = 0; i < n; i++)
        {
            gapBefore[i] = sorted[lo + i].StartMs - lastEnd;
            lastEnd = Math.Max(lastEnd, sorted[lo + i].EndMs);
        }

        var atGaps = Enumerable.Range(1, Math.Max(n - 1, 0)).Where(k => gapBefore[k] >= MinBreakGapMs).ToList();
        int best = lo;
        double bestScore = double.MinValue;
        foreach (var k in atGaps.Count > 0 ? atGaps : Enumerable.Range(1, Math.Max(n - 1, 0)))
        {
            var score = atA[k] + (atB[n] - atB[k]);
            if (score > bestScore)
            {
                bestScore = score;
                best = lo + k;
            }
        }

        return best;
    }
}
