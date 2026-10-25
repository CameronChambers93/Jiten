namespace Jiten.Core.Media;

/// <summary>
/// Finds the constant shift that best lines subtitle cues up with speech in the audio. Subtitles from another release
/// (e.g. a streaming rip for a Blu-ray video) are usually off by a fixed amount per episode.
/// </summary>
public static class SubtitleOffsetDetector
{
    private const int FrameMs = 10;
    private const int SamplesPerFrame = 8000 * FrameMs / 1000;

    /// <summary>
    /// Frames louder than this share of all frames count as speech; dialogue-heavy anime is speaking about half the time.
    /// </summary>
    private const double SpeechPercentile = 0.55;

    public record Result(int OffsetMs, double Score, double ScoreAtZero);

    /// <param name="pcm8k">Mono 8 kHz s16le audio, see <see cref="VideoMedia.DecodePcm8k"/>.</param>
    /// <param name="cues">Cue start/end times in subtitle time.</param>
    /// <param name="maxShiftMs">Largest shift tried in either direction.</param>
    public static Result Detect(byte[] pcm8k, IEnumerable<(int StartMs, int EndMs)> cues, int maxShiftMs = 30_000)
    {
        var samples = pcm8k.Length / 2;
        var frames = samples / SamplesPerFrame;
        if (frames == 0) return new Result(0, 0, 0);

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
        return BestShift(energy.Select(e => e > threshold).ToArray(), cues, maxShiftMs);
    }

    /// <summary>
    /// Finds the constant shift that best lines cues up with another track's cues (e.g. an English fansub against the
    /// Japanese subtitles), in the same way as against speech: the reference cues stand in for the speech frames.
    /// </summary>
    public static Result DetectAgainstCues(IEnumerable<(int StartMs, int EndMs)> reference, IEnumerable<(int StartMs, int EndMs)> cues,
                                           int maxShiftMs = 120_000)
    {
        var referenceFrames = MergeIntervals(reference.Select(c => (c.StartMs / FrameMs, c.EndMs / FrameMs)));
        if (referenceFrames.Count == 0) return new Result(0, 0, 0);

        // Room on both sides so cues shifted before the first or past the last reference cue still score.
        var frames = referenceFrames[^1].End + maxShiftMs / FrameMs;
        var covered = new bool[frames];
        foreach (var (start, end) in referenceFrames)
            Array.Fill(covered, true, start, end - start);

        return BestShift(covered, cues, maxShiftMs);
    }

    /// <summary>The shift (in frames, returned in ms) that puts the most cue time on active frames, net of chance.</summary>
    private static Result BestShift(bool[] active, IEnumerable<(int StartMs, int EndMs)> cues, int maxShiftMs)
    {
        var frames = active.Length;
        if (frames == 0) return new Result(0, 0, 0);
        var activeShare = active.Count(a => a) / (double)frames;

        // Prefix sums of the centred signal: the score of a shift is the sum over covered frames, O(cues) per shift.
        var prefix = new double[frames + 1];
        for (int f = 0; f < frames; f++)
            prefix[f + 1] = prefix[f] + (active[f] ? 1 : 0) - activeShare;

        var intervals = MergeIntervals(cues.Select(c => (c.StartMs / FrameMs, c.EndMs / FrameMs)));

        double Score(int shift)
        {
            double score = 0;
            foreach (var (start, end) in intervals)
            {
                int s = Math.Clamp(start + shift, 0, frames), e = Math.Clamp(end + shift, 0, frames);
                score += prefix[e] - prefix[s];
            }

            return score;
        }

        var maxShift = maxShiftMs / FrameMs;
        int best = 0;
        double bestScore = Score(0);
        for (int shift = -maxShift; shift <= maxShift; shift++)
        {
            var score = Score(shift);
            if (score > bestScore)
            {
                bestScore = score;
                best = shift;
            }
        }

        return new Result(best * FrameMs, bestScore, Score(0));
    }

    private static List<(int Start, int End)> MergeIntervals(IEnumerable<(int Start, int End)> intervals)
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
}
