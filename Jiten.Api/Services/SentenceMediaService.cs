using Jiten.Core;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>Where an example sentence is spoken in its deck's linked video, in video time.</summary>
/// <param name="Padded">Cuts pad the clip: false for a fixed or previewed range, which already holds its padding.</param>
/// <param name="PictureMs">The screenshot's frame; null for the middle of the clip.</param>
public record SentenceClip(string VideoPath, int AudioStreamIndex, int StartMs, int EndMs, bool Padded = true, int? PictureMs = null);

/// <summary>Times that replace the cue lookup's, in subtitle time: a fix, or a range being previewed on the fix screen.</summary>
/// <param name="StartMs">Set with <paramref name="EndMs"/>, or neither.</param>
public record ClipTimes(int? StartMs, int? EndMs, int? PictureMs)
{
    public bool HasRange => StartMs != null && EndMs != null;

    public static ClipTimes? Of(LineFix? fix) => fix == null ? null : new ClipTimes(fix.StartMs, fix.EndMs, fix.EffectivePictureMs);
}

public interface ISentenceMediaService
{
    /// <summary>
    /// Null when the sentence doesn't exist, its deck has no linked video, or no cue matches it. A range in
    /// <paramref name="times"/> is used without the cue lookup, so a fixed line doesn't depend on matching its text.
    /// </summary>
    Task<SentenceClip?> ResolveAsync(long sentenceId, ClipTimes? times, CancellationToken ct);

    Task<byte[]> CutAudioAsync(SentenceClip clip, int padStartMs, int padEndMs, CancellationToken ct);

    Task<byte[]> GrabPictureAsync(SentenceClip clip, CancellationToken ct);

    /// <summary>A WebM with sound of the sentence's cues, padded on both sides like the audio.</summary>
    Task<byte[]> CutVideoAsync(SentenceClip clip, int padStartMs, int padEndMs, CancellationToken ct);
}

/// <summary>Cuts card media out of the local videos linked with <c>--link-video</c>.</summary>
public class SentenceMediaService(JitenDbContext context, SentenceCueLookup cueLookup, IConfiguration configuration) : ISentenceMediaService
{
    private const int PictureHeight = 480;

    /// <summary>ffmpeg decodes whole GOPs of 1080p HEVC; a few at once is plenty for one person mining.</summary>
    private static readonly SemaphoreSlim FfmpegSlots = new(3);

    public async Task<SentenceClip?> ResolveAsync(long sentenceId, ClipTimes? times, CancellationToken ct)
    {
        int deckId, startMs, endMs;
        var cues = times is { HasRange: true } ? null : await cueLookup.FindAsync(sentenceId, ct);
        if (cues != null) (deckId, startMs, endMs) = (cues.DeckId, cues.StartMs, cues.EndMs);
        else if (times is { HasRange: true } || times?.PictureMs != null)
        {
            // A fixed clip, or only a frame for a line no cue matches: the deck is all that's needed
            if (await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters().Where(s => s.SentenceId == sentenceId)
                             .Select(s => s.DeckId).FirstOrDefaultAsync(ct) is not { } deck) return null;
            (deckId, startMs, endMs) = (deck, times.StartMs ?? times.PictureMs!.Value, times.EndMs ?? times.PictureMs!.Value);
        }
        else return null;

        var source = await context.DeckVideoSources.AsNoTracking().FirstOrDefaultAsync(v => v.DeckId == deckId, ct);
        if (source == null || !IsUnderMediaRoot(source.VideoPath)) return null;

        var offset = source.SubtitleOffsetMs;
        return new SentenceClip(source.VideoPath, source.AudioStreamIndex, startMs + offset, endMs + offset, Padded: times is not { HasRange: true },
                                PictureMs: times?.PictureMs + offset);
    }

    public Task<byte[]> CutAudioAsync(SentenceClip clip, int padStartMs, int padEndMs, CancellationToken ct)
    {
        var (start, end) = Span(clip, padStartMs, padEndMs);
        return WithSlot(() => VideoMedia.CutAudioMp3(clip.VideoPath, clip.AudioStreamIndex, start, end, ct), ct);
    }

    public Task<byte[]> GrabPictureAsync(SentenceClip clip, CancellationToken ct) =>
        WithSlot(() => VideoMedia.GrabFrameJpeg(clip.VideoPath, Math.Max(0, clip.PictureMs ?? (clip.StartMs + clip.EndMs) / 2), PictureHeight, ct), ct);

    public Task<byte[]> CutVideoAsync(SentenceClip clip, int padStartMs, int padEndMs, CancellationToken ct)
    {
        var (start, end) = Span(clip, padStartMs, padEndMs);
        return WithSlot(() => VideoMedia.CutVideoWebm(clip.VideoPath, clip.AudioStreamIndex, start, end, PictureHeight, ct), ct);
    }

    private static (int Start, int End) Span(SentenceClip clip, int padStartMs, int padEndMs) =>
        clip.Padded ? (Math.Max(0, clip.StartMs - padStartMs), clip.EndMs + padEndMs) : (Math.Max(0, clip.StartMs), clip.EndMs);

    private static async Task<byte[]> WithSlot(Func<Task<byte[]>> work, CancellationToken ct)
    {
        await FfmpegSlots.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            FfmpegSlots.Release();
        }
    }

    /// <summary>Video paths come from metadata.json; only files inside the configured media roots are ever opened.</summary>
    private bool IsUnderMediaRoot(string videoPath) => MediaRoots.Contains(configuration, videoPath);
}
