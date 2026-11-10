namespace Jiten.Core.Media;

/// <summary>
/// Finds the constant shift that best lines subtitle cues up with speech in the audio. Subtitles from another release
/// (e.g. a streaming rip for a Blu-ray video) are usually off by a fixed amount per episode.
/// </summary>
public static class SubtitleOffsetDetector
{
    internal const int FrameMs = 10;
    private const int SamplesPerFrame = 8000 * FrameMs / 1000;

    /// <summary>
    /// Frames louder than this share of all frames count as speech; dialogue-heavy anime is speaking about half the time.
    /// </summary>
    private const double SpeechPercentile = 0.55;

    public record Result(int OffsetMs, double Score, double ScoreAtZero);

    /// <param name="pcm8k">Mono 8 kHz s16le audio, see <see cref="VideoMedia.DecodePcm8k"/>.</param>
    /// <param name="cues">Cue start/end times in subtitle time.</param>
    /// <param name="maxShiftMs">Largest shift tried in either direction.</param>
    public static Result Detect(byte[] pcm8k, IEnumerable<(int StartMs, int EndMs)> cues, int maxShiftMs = 30_000) =>
        ActivityTrack.FromSpeech(pcm8k).Best(cues, maxShiftMs);

    /// <summary>
    /// Finds the constant shift that best lines cues up with another track's cues (e.g. an English fansub against the
    /// Japanese subtitles), in the same way as against speech: the reference cues stand in for the speech frames.
    /// </summary>
    public static Result DetectAgainstCues(IEnumerable<(int StartMs, int EndMs)> reference, IEnumerable<(int StartMs, int EndMs)> cues,
                                           int maxShiftMs = 120_000) =>
        ActivityTrack.FromCues(reference, maxShiftMs).Best(cues, maxShiftMs);

    internal static List<(int Start, int End)> MergeIntervals(IEnumerable<(int Start, int End)> intervals)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var (start, end) in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }

        return merged;
    }

    /// <summary>
    /// The frames a reference is active in (speech in the audio, or another track's cues), scored against cues at any
    /// shift. Built once, so many cue sets (windows of one episode) can be fitted against it cheaply.
    /// </summary>
    public sealed class ActivityTrack
    {
        // Prefix sums of the centred signal: the score of a shift is the sum over covered frames, O(cues) per shift.
        private readonly double[] _prefix;

        public int Frames { get; }

        private ActivityTrack(bool[] active)
        {
            Frames = active.Length;
            _prefix = new double[Frames + 1];
            if (Frames == 0) return;
            var activeShare = active.Count(a => a) / (double)Frames;
            for (int f = 0; f < Frames; f++)
                _prefix[f + 1] = _prefix[f] + (active[f] ? 1 : 0) - activeShare;
        }

        /// <summary>Speech frames of mono 8 kHz s16le audio: frames louder than most.</summary>
        public static ActivityTrack FromSpeech(byte[] pcm8k)
        {
            var frames = pcm8k.Length / 2 / SamplesPerFrame;
            if (frames == 0) return new ActivityTrack([]);

            var energy = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                for (int i = 0; i < SamplesPerFrame; i++)
                {
                    double s = BitConverter.ToInt16(pcm8k, (f * SamplesPerFrame + i) * 2);
                    sum += s * s;
                }

                energy[f] = sum / SamplesPerFrame;
            }

            var sorted = (double[])energy.Clone();
            Array.Sort(sorted);
            var threshold = sorted[(int)(frames * SpeechPercentile)];
            return new ActivityTrack(energy.Select(e => e > threshold).ToArray());
        }

        /// <summary>Frames covered by reference cues, with <paramref name="padMs"/> of room after the last one.</summary>
        public static ActivityTrack FromCues(IEnumerable<(int StartMs, int EndMs)> reference, int padMs = 120_000)
        {
            var referenceFrames = MergeIntervals(reference.Select(c => (c.StartMs / FrameMs, c.EndMs / FrameMs)));
            if (referenceFrames.Count == 0) return new ActivityTrack([]);

            // Room on both sides so cues shifted before the first or past the last reference cue still score.
            var covered = new bool[referenceFrames[^1].End + padMs / FrameMs];
            foreach (var (start, end) in referenceFrames)
                Array.Fill(covered, true, start, end - start);
            return new ActivityTrack(covered);
        }

        /// <summary>How much cue time lands on active frames at this shift, net of chance, in frames.</summary>
        public double Score(IEnumerable<(int StartMs, int EndMs)> cues, int shiftMs) =>
            Score(MergeIntervals(cues.Select(c => (c.StartMs / FrameMs, c.EndMs / FrameMs))), shiftMs / FrameMs);

        /// <summary>The shift in [-maxShiftMs, maxShiftMs] that scores best; on a tie the unshifted cues win.</summary>
        public Result Best(IEnumerable<(int StartMs, int EndMs)> cues, int maxShiftMs) =>
            Best(cues, -maxShiftMs, maxShiftMs);

        /// <summary>The shift in [minShiftMs, maxShiftMs] that scores best; on a tie zero wins if in range, else the lowest.</summary>
        public Result Best(IEnumerable<(int StartMs, int EndMs)> cues, int minShiftMs, int maxShiftMs)
        {
            var intervals = MergeIntervals(cues.Select(c => (c.StartMs / FrameMs, c.EndMs / FrameMs)));
            if (Frames == 0) return new Result(0, 0, 0);

            int min = minShiftMs / FrameMs, max = maxShiftMs / FrameMs;
            int best = min <= 0 && max >= 0 ? 0 : min;
            double bestScore = Score(intervals, best);
            for (int shift = min; shift <= max; shift++)
            {
                var score = Score(intervals, shift);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = shift;
                }
            }

            return new Result(best * FrameMs, bestScore, Score(intervals, 0));
        }

        private double Score(List<(int Start, int End)> intervals, int shift)
        {
            double score = 0;
            foreach (var (start, end) in intervals)
            {
                int s = Math.Clamp(start + shift, 0, Frames), e = Math.Clamp(end + shift, 0, Frames);
                score += _prefix[e] - _prefix[s];
            }

            return score;
        }
    }
}
