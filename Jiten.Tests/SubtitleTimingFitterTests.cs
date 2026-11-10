using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Media;

namespace Jiten.Tests;

public class SubtitleTimingFitterTests
{
    /// <summary>Irregular dialogue in video time: lines of 0.8-4 s with pauses of 0.3-3 s, and a silent break.</summary>
    private static List<SubtitleItem> VideoDialogue(int lengthMs, int breakAtMs, int breakMs, int seed = 7)
    {
        var rng = new Random(seed);
        var cues = new List<SubtitleItem>();
        var t = 2000;
        while (t < lengthMs)
        {
            if (t >= breakAtMs && t < breakAtMs + breakMs) t = breakAtMs + breakMs;
            var len = rng.Next(800, 4000);
            if (t < breakAtMs && t + len > breakAtMs) len = breakAtMs - t;
            if (len > 300) cues.Add(new SubtitleItem(t, t + len, $"line {cues.Count}"));
            t += len + rng.Next(300, 3000);
        }

        return cues;
    }

    /// <summary>
    /// The same lines on the subtitle release's clock: a constant offset, and a break shorter by
    /// <paramref name="shorterBreakMs"/> (so every line after it is that much earlier).
    /// </summary>
    private static List<SubtitleItem> Release(List<SubtitleItem> video, int offsetMs, int breakAtMs, int shorterBreakMs) =>
        video.Select(c =>
        {
            var shift = offsetMs + (c.StartMs >= breakAtMs ? shorterBreakMs : 0);
            return c with { StartMs = c.StartMs - shift, EndMs = c.EndMs - shift };
        }).ToList();

    /// <summary>Mono 8 kHz s16le: loud noise while a line is spoken, quiet noise otherwise.</summary>
    private static byte[] Speech(List<SubtitleItem> video, int lengthMs)
    {
        var rng = new Random(3);
        var samples = lengthMs * 8;
        var pcm = new byte[samples * 2];
        var speaking = new bool[samples];
        foreach (var c in video)
            for (int i = c.StartMs * 8; i < Math.Min(c.EndMs * 8, samples); i++) speaking[i] = true;
        for (int i = 0; i < samples; i++)
        {
            var amplitude = speaking[i] ? 8000 : 300;
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), (short)rng.Next(-amplitude, amplitude));
        }

        return pcm;
    }

    [Fact]
    public void A_shorter_break_in_the_subtitles_splits_the_episode_and_retimes_the_cues_after_it()
    {
        // Death Note E08's shape: +4620 before the break, +12500 after it (7.88 s shorter in the subtitles)
        const int length = 24 * 60_000, breakAt = 12 * 60_000 + 46_000, breakMs = 20_000;
        var video = VideoDialogue(length, breakAt, breakMs);
        var subs = Release(video, 4620, breakAt, 7880);
        var track = SubtitleOffsetDetector.ActivityTrack.FromCues(video.Select(c => (c.StartMs, c.EndMs)));

        var fit = SubtitleTimingFitter.Fit(track, subs);

        fit.Retimed.Should().BeTrue();
        fit.Segments.Should().HaveCount(2);
        fit.OffsetMs.Should().BeCloseTo(4620, 20);
        fit.Segments[1].OffsetMs.Should().BeCloseTo(12500, 20);
        // The split is the first line after the break
        var firstAfter = video.FindIndex(c => c.StartMs >= breakAt);
        fit.Segments[1].FirstCue.Should().Be(firstAfter);

        // With the one stored offset, every retimed cue lands on its line in the video
        var retimed = fit.Apply(subs);
        retimed.Select(c => c.StartMs + fit.OffsetMs).Should().Equal(video.Select(c => c.StartMs).ToList(),
                                                                     (a, b) => Math.Abs(a - b) <= 20);
    }

    [Fact]
    public void The_break_is_found_from_the_audio_too()
    {
        const int length = 10 * 60_000, breakAt = 5 * 60_000, breakMs = 15_000;
        var video = VideoDialogue(length, breakAt, breakMs, seed: 11);
        var subs = Release(video, 3000, breakAt, 6000);
        var track = SubtitleOffsetDetector.ActivityTrack.FromSpeech(Speech(video, length));

        var fit = SubtitleTimingFitter.Fit(track, subs, windowMs: 120_000);

        fit.Segments.Select(s => s.OffsetMs).Should().Equal([3000, 9000], (a, b) => Math.Abs(a - b) <= 30);
        fit.Segments[1].FirstCue.Should().Be(video.FindIndex(c => c.StartMs >= breakAt));
    }

    [Fact]
    public void A_constant_offset_gives_one_segment_and_leaves_the_cues_alone()
    {
        const int length = 10 * 60_000;
        var video = VideoDialogue(length, breakAtMs: 5 * 60_000, breakMs: 15_000, seed: 5);
        var subs = Release(video, 4650, int.MaxValue, 0);
        var track = SubtitleOffsetDetector.ActivityTrack.FromSpeech(Speech(video, length));

        var fit = SubtitleTimingFitter.Fit(track, subs, windowMs: 120_000);

        fit.Retimed.Should().BeFalse();
        fit.OffsetMs.Should().BeCloseTo(4650, 30);
        fit.OffsetMs.Should().Be(fit.WholeOffsetMs);
        fit.Windows.Should().OnlyContain(w => Math.Abs(w.OffsetMs - 4650) <= 30);
        fit.Apply(subs).Should().Equal(subs);
    }

    [Fact]
    public void The_whole_episode_fit_is_unchanged_by_the_refactor()
    {
        // Detect and DetectAgainstCues keep their old results: a constant shift is found exactly
        var video = VideoDialogue(5 * 60_000, int.MaxValue, 0, seed: 2);
        var subs = Release(video, 2500, int.MaxValue, 0);

        var result = SubtitleOffsetDetector.DetectAgainstCues(video.Select(c => (c.StartMs, c.EndMs)), subs.Select(c => (c.StartMs, c.EndMs)));

        result.OffsetMs.Should().Be(2500);
        result.Score.Should().BeGreaterThan(result.ScoreAtZero);
    }

    [Theory]
    [InlineData("（鐘の音）", true)]
    [InlineData("（子供たちのはしゃぎ声）", true)]
    [InlineData("♪～", true)]
    [InlineData("(笑い声) [拍手]", true)]
    [InlineData("（ワタリ）どうしました？　竜崎(りゅうざき)", false)]
    [InlineData("どうしたんだ？", false)]
    public void Sound_descriptions_are_told_from_speech(string text, bool soundOnly) =>
        SubtitleTimingFitter.IsSoundOnly(text).Should().Be(soundOnly);

    [Fact]
    public void Sound_only_cues_with_nothing_under_them_dont_start_a_segment()
    {
        // Death Note E25: two minutes of （…） cues open the episode; the English has nothing there
        const int length = 12 * 60_000;
        var video = VideoDialogue(length, int.MaxValue, 0, seed: 9).Where(c => c.StartMs > 200_000).ToList();
        var reference = SubtitleOffsetDetector.ActivityTrack.FromCues(video.Select(c => (c.StartMs, c.EndMs)));
        var sounds = Enumerable.Range(0, 30).Select(i => new SubtitleItem(5_000 + i * 6_000, 9_000 + i * 6_000, "（鐘の音）"));
        var subs = Release(video, 5000, int.MaxValue, 0).Concat(sounds).ToList();

        var fit = SubtitleTimingFitter.Fit(reference, subs, ignoreSoundOnly: true);

        fit.Segments.Should().ContainSingle();
        fit.OffsetMs.Should().BeCloseTo(5000, 20);
    }
}
