using Jiten.Core;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>Where an example sentence is spoken in its deck's linked video, in video time.</summary>
public record SentenceClip(string VideoPath, int AudioStreamIndex, int StartMs, int EndMs);

public interface ISentenceMediaService
{
    /// <summary>Null when the sentence doesn't exist, its deck has no linked video, or no cue matches it.</summary>
    Task<SentenceClip?> ResolveAsync(long sentenceId, CancellationToken ct);

    Task<byte[]> CutAudioAsync(SentenceClip clip, int padStartMs, int padEndMs, CancellationToken ct);

    Task<byte[]> GrabPictureAsync(SentenceClip clip, CancellationToken ct);
}

/// <summary>Cuts card media out of the local videos linked with <c>--link-video</c>.</summary>
public class SentenceMediaService(JitenDbContext context, SentenceCueLookup cueLookup, IConfiguration configuration) : ISentenceMediaService
{
    private const int PictureHeight = 480;

    /// <summary>ffmpeg decodes whole GOPs of 1080p HEVC; a few at once is plenty for one person mining.</summary>
    private static readonly SemaphoreSlim FfmpegSlots = new(3);

    public async Task<SentenceClip?> ResolveAsync(long sentenceId, CancellationToken ct)
    {
        var cues = await cueLookup.FindAsync(sentenceId, ct);
        if (cues == null) return null;

        var source = await context.DeckVideoSources.AsNoTracking().FirstOrDefaultAsync(v => v.DeckId == cues.DeckId, ct);
        if (source == null || !IsUnderMediaRoot(source.VideoPath)) return null;

        return new SentenceClip(source.VideoPath, source.AudioStreamIndex, cues.StartMs + source.SubtitleOffsetMs, cues.EndMs + source.SubtitleOffsetMs);
    }

    public Task<byte[]> CutAudioAsync(SentenceClip clip, int padStartMs, int padEndMs, CancellationToken ct) =>
        WithSlot(() => VideoMedia.CutAudioMp3(clip.VideoPath, clip.AudioStreamIndex, clip.StartMs - padStartMs, clip.EndMs + padEndMs, ct), ct);

    public Task<byte[]> GrabPictureAsync(SentenceClip clip, CancellationToken ct) =>
        WithSlot(() => VideoMedia.GrabFrameJpeg(clip.VideoPath, (clip.StartMs + clip.EndMs) / 2, PictureHeight, ct), ct);

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

    /// <summary>Video paths come from metadata.json; only files inside the media library mount are ever opened.</summary>
    private bool IsUnderMediaRoot(string videoPath)
    {
        var root = Path.GetFullPath(configuration["MediaRoot"] ?? "/media").TrimEnd('/') + "/";
        return Path.GetFullPath(videoPath).StartsWith(root, StringComparison.Ordinal);
    }
}
