using Jiten.Core;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

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
public class SentenceMediaService(JitenDbContext context, IMemoryCache cache, IConfiguration configuration) : ISentenceMediaService
{
    private const int PictureHeight = 480;

    /// <summary>ffmpeg decodes whole GOPs of 1080p HEVC; a few at once is plenty for one person mining.</summary>
    private static readonly SemaphoreSlim FfmpegSlots = new(3);

    private static readonly TimeSpan MatcherTtl = TimeSpan.FromMinutes(30);

    public async Task<SentenceClip?> ResolveAsync(long sentenceId, CancellationToken ct)
    {
        var sentence = await context.ExampleSentences.AsNoTracking()
                                    .Where(s => s.SentenceId == sentenceId)
                                    .Select(s => new { s.DeckId, s.Text, s.Position, s.Deck!.SentenceCount })
                                    .FirstOrDefaultAsync(ct);
        if (sentence == null) return null;

        var source = await context.DeckVideoSources.AsNoTracking().FirstOrDefaultAsync(v => v.DeckId == sentence.DeckId, ct);
        if (source == null || !IsUnderMediaRoot(source.VideoPath)) return null;

        var matcher = await cache.GetOrCreateAsync($"sentence-cues:{sentence.DeckId}", async entry =>
        {
            entry.SlidingExpiration = MatcherTtl;
            var cuesJson = await context.DeckSubtitleTracks.AsNoTracking()
                                        .Where(t => t.DeckId == sentence.DeckId)
                                        .Select(t => t.CuesJson)
                                        .FirstOrDefaultAsync(ct);
            return cuesJson == null ? null : new SentenceCueMatcher(new Core.Data.DeckSubtitleTrack { CuesJson = cuesJson }.GetCues());
        });

        var relative = sentence.SentenceCount > 0 ? Math.Clamp(sentence.Position / (double)sentence.SentenceCount, 0, 1) : 0;
        var cues = matcher?.Find(sentence.Text, relative);
        if (cues == null) return null;

        return new SentenceClip(source.VideoPath, source.AudioStreamIndex,
                                cues.Value.First.StartMs + source.SubtitleOffsetMs, cues.Value.Last.EndMs + source.SubtitleOffsetMs);
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
